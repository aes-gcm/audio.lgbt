using AudioLgbt;
using System.Reflection;
using System.Text.Json;

internal static class UiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Invoke(object instance, string method) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, null);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static int Run(bool render)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
        var root = Path.GetFullPath("dist/ui-review");
        Directory.CreateDirectory(root);
        var profile = Path.Combine(root, "test-preferences.json");
        // The regression owns this file; never mutate the user's preferences.
        File.WriteAllText(profile, JsonSerializer.Serialize(new { Name = "Windows · нативный клиент" }));
        var historyPath=Path.Combine(root,"test-rooms.dat");
        File.WriteAllBytes(historyPath,[]);
        var history=new RoomHistory(historyPath);
        var server=new Uri("https://audio.lgbt/");
        var first="https://audio.lgbt/#AAAAAAAAAAAAAAAAAAAAAA";
        var second="https://audio.lgbt/#AQEBAQEBAQEBAQEBAQEBAQ";
        history.Remember(server,first,true);history.Rename(history.Rooms[0].Id,"Друзья");
        history.Remember(server,second,false);history.Rename(history.Rooms[0].Id,"Вечерние звонки");
        history.Remember(server,first,false);
        Assert(history.Rooms.Count==2&&history.Rooms[0].Title=="Друзья","Repeated joins must move a room to the top without losing its title.");
        var restored=new RoomHistory(historyPath);
        Assert(restored.Rooms.Count==2&&restored.Rooms[0].Invitation==first,"History must survive restart.");
        Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(historyPath)).Contains(first),"Room keys must not be stored as plaintext.");
        restored.Remove(restored.Rooms[1].Id);Assert(new RoomHistory(historyPath).Rooms.Count==1,"History deletion must persist.");
        history.Remember(server,"https://audio.lgbt/#invalid",false);Assert(history.Rooms.Count==2,"Invalid invitation should not enter history.");
        // Restore a second card for rendering. This test profile never touches user history.
        history.Remember(server,second,false);
        var prefs = Preferences.Load(profile);
        Assert(prefs.Name == "", "Old fixture name should become an empty field.");

        using (var window = Create(prefs))
        {
            window.Show(); Application.DoEvents();
            var name = (ModernInput)Field(window, "name")!;
            Assert(name.Text == "" && name.PlaceholderText == "Гость", "Guest must be a hint, not editable text.");
            name.Text = "Саша";
            Pump(550);
            Assert(Preferences.Load(profile).Name == "Саша", "Typing a name must persist without entering a room.");
            name.Text = ""; Invoke(window, "SaveName");
            Assert(Preferences.Load(profile).Name == "", "Clearing the name must restore the Guest hint on restart.");
            name.Text = "Александр"; Invoke(window, "SaveName");
            window.PreviewRoom();
            int forms = Application.OpenForms.Count;
            var participants = (ModernButton)Field(window, "peopleCount")!;
            var placement = participants.ContentBounds();
            Assert(placement.Icon.Right + 8 <= placement.Label.Left, "Participant count overlaps the icon.");
            var tiles = (Control)Field(window, "tiles")!;
            var tile = tiles.Controls.OfType<ParticipantTile>().First();
            // Drive the production timer from decoded Opus, without opening a physical microphone.
            using (var sound = new AudioDevices(new Preferences()))
            {
                typeof(AudioDevices).GetField("mixer", Private)!.SetValue(sound,new NAudio.Wave.SampleProviders.MixingSampleProvider(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000,2)){ReadFully=true});
                window.GetType().GetField("audio", Private)!.SetValue(window,sound);
                var encoder=Concentus.OpusCodecFactory.CreateEncoder(48000,1,Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
                var pcm=Enumerable.Range(0,960).Select(i=>(short)(2400*Math.Sin(2*Math.PI*440*i/48000d))).ToArray();
                var buffer=new byte[4096];int size=encoder.Encode(pcm,960,buffer,buffer.Length);var packet=buffer[..size];
                var map=(System.Collections.IDictionary)Field(window,"peers")!;var peer=map["preview-guest"]!;
                peer.GetType().GetField("Muted")!.SetValue(peer,false);
                var remote=tiles.Controls.OfType<ParticipantTile>().Single(t=>t!=tile);remote.SetName("Мария",false);
                sound.Receive("preview-guest","mic",packet,0);sound.Receive("preview-guest","mic",packet,960);Pump(120);
                Assert((bool)Field(remote,"speaking")!,"Incoming Opus did not highlight the actual room tile.");
                Pump(650);Assert(!(bool)Field(remote,"speaking")!,"Room tile remains highlighted after packets stop.");
                window.GetType().GetField("audio", Private)!.SetValue(window,null);
            }
            var originalBounds = tile.Bounds;
            tile.SetName("Проверка речи",false); tile.SetSpeaking(true);
            using(var speakingImage = new Bitmap(tile.Width,tile.Height))
            {
                tile.DrawToBitmap(speakingImage,tile.ClientRectangle);
                var edge=speakingImage.GetPixel(tile.Width/2,2);
                Assert(edge.R>220&&edge.G>220&&edge.B>220,"Speaking outline is not visible.");
                speakingImage.Save(Path.Combine(root,"speaking-tile.png"));
            }
            tile.SetName("Проверка речи",true);Assert(!(bool)Field(tile,"speaking")!,"Muted microphone must clear speaking.");
            tile.SetName("Александр (вы)",false);tile.SetSpeaking(false);

            for (int repeat = 0; repeat < 8; repeat++)
            {
                window.GetType().GetMethod("ShowSidebar", Private)!.Invoke(window, ["chat"]);
                Application.DoEvents();
                Assert(tile.Right <= tiles.ClientSize.Width, "Participant tile exceeds room after opening the sidebar.");
                window.GetType().GetMethod("ShowSidebar", Private)!.Invoke(window, ["chat"]);
                Application.DoEvents();
                Assert(tile.Bounds == originalBounds, "Closing participants must restore tile geometry.");
            }
            Invoke(window, "ShowPeople");
            Assert(tile.Bounds == originalBounds, "Participants overlay must not change stage geometry.");
            var peopleOverlay = (ModalOverlay)Field(window, "modal")!;
            Assert(Application.OpenForms.Count == forms, "Participants opened another window.");
            Assert(Descendants(peopleOverlay).OfType<ModernSlider>().All(s => s.Maximum == 200), "Participant volume must allow 200%.");
            peopleOverlay.RequestClose();
            var settingsTimes = new List<long>();
            for (int repeat = 0; repeat < 5; repeat++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Invoke(window, "ShowSettings"); watch.Stop(); settingsTimes.Add(watch.ElapsedMilliseconds); Pump(30);
                var overlay = (ModalOverlay)Field(window, "modal")!;
                Assert(Application.OpenForms.Count == forms, "Settings opened a second window.");
                Assert(!((Control)Field(window, "body")!).Enabled, "Background must not receive input while modal is open.");
                overlay.RequestClose();
                Assert(Field(window, "modal") is null && ((Control)Field(window, "body")!).Enabled, "Closing the layer should restore the room.");
            }
            Console.WriteLine("Settings open milliseconds: " + string.Join(",",settingsTimes));
            int shareLevel = -1;
            using (var screenTile = new ParticipantTile("Test screen", value => shareLevel = value))
            {
                window.Controls.Add(screenTile); screenTile.Size = new Size(700,400);
                var expand = screenTile.Controls.OfType<ModernButton>().Single();
                Assert(expand.Right == screenTile.Width - 12 && expand.Bottom == screenTile.Height - 12, "Fullscreen belongs at the bottom right.");
                var slider = (ModernSlider)Field(screenTile,"volumeSlider")!;
                var mute = (ToolStripMenuItem)Field(screenTile,"muteItem")!;
                slider.Value = 43; Assert(shareLevel == 43,"Separate share volume was not routed.");
                mute.PerformClick(); Assert(shareLevel == 0,"Share mute failed.");
                mute.PerformClick(); Assert(shareLevel == 43,"Unmute must restore the chosen level.");
            }
            Invoke(window, "ShowInvitation");
            var invitation = (ModalOverlay)Field(window, "modal")!;
            Assert(Descendants(invitation).OfType<ModernInput>().Any(c => c.ReadOnly && c.Text.StartsWith("https://audio.lgbt/#")), "Invitation must show a selectable read-only link.");
            invitation.RequestClose();

            // Exercise the actual FPS control and its ToolStrip close lifecycle.
            var devices = new VideoDevices();
            using var share = new ShareDialog(devices, new ShareOptions(), _ => { });
            window.Controls.Add(share); share.BringToFront();
            share.PreviewCards();
            foreach (var size in new[] { new Size(852,586), new Size(700,464), new Size(852,586) })
            {
                share.Size = size; share.PerformLayout(); Application.DoEvents();
                var viewport = (ScrollViewport)Field(share,"sourceViewport")!;
                var cards = Descendants(share).OfType<SourceCard>().ToArray();
                Assert(cards.Length == 3,"Expected source fixtures.");
                foreach(var card in cards)
                {
                    var origin = viewport.PointToClient(card.PointToScreen(Point.Empty));
                    Assert(origin.Y >= 0 && origin.Y + card.Height <= viewport.ClientSize.Height,"First source row is clipped after resize.");
                }
            }
            var fps = (ModernSelect)Field(share, "fps")!;
            int committed = 0; fps.SelectionChangeCommitted += (_, _) => committed++;
            for (int repeat = 0; repeat < 12; repeat++)
            {
                fps.Open(); Application.DoEvents();
                var menu = (ContextMenuStrip)Field(fps, "dropdown")!;
                menu.Items[repeat % 3].PerformClick();
                menu.Close(ToolStripDropDownCloseReason.ItemClicked);
                Application.DoEvents();
                Assert(!menu.IsDisposed, "Closing a menu must not dispose it within ToolStrip's callback.");
                Assert(fps.SelectedIndex == repeat % 3, "FPS selection did not stick.");
            }
            Assert(committed == 12, "FPS choices should each fire exactly once.");
            fps.Open(); share.Dispose(); Application.DoEvents();
            devices.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        using (var reopened = Create(Preferences.Load(profile)))
            Assert(((ModernInput)Field(reopened, "name")!).Text == "Александр", "A new window must restore the saved name.");
        Console.WriteLine("History regression: encrypted storage, restart, rename, deduplication, deletion and invalid links passed.");
        Console.WriteLine("Layout regression: sidebar restoration, source cards on resize, icon/count spacing and share volume/mute passed.");
        Console.WriteLine("UI regression: guest hint, name persistence/restart, 5 modal cycles, invitation, 12 FPS selections and owner disposal passed.");
        if (!render) return 0;
        foreach (var mode in new[] { "home", "server", "room", "chat", "settings", "people", "compact", "compact-settings", "invite", "share", "compact-share", "history" })
        {
            using var window = mode=="history"?Create(Preferences.Load(profile),history):Create(Preferences.Load(profile));
            window.Show(); Application.DoEvents();
            string preview = mode.Replace("compact-", "");
            if(mode=="server") Invoke(window,"ShowServer");
            else if (mode is not "home" and not "history") window.PreviewRoom(preview is "chat" or "settings" or "people" or "invite" or "share" ? preview : "");
            if (mode.StartsWith("compact")) window.ClientSize = new Size(780, 600);
            Pump(100);
            if (preview == "share" && Field(window, "modal") is ModalOverlay modal && modal.Content is ShareDialog panel) panel.PreviewCards();
            Application.DoEvents();
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            if(Field(window,"modal") is ModalOverlay layer)
            {
                Console.WriteLine($"{mode}: overlay visible={layer.Visible} enabled={layer.Enabled} bounds={layer.Bounds} index={window.Controls.GetChildIndex(layer)} card={layer.Controls[0].Bounds}");
                using var overlayImage=new Bitmap(layer.Width,layer.Height);layer.DrawToBitmap(overlayImage,new Rectangle(Point.Empty,overlayImage.Size));overlayImage.Save(Path.Combine(root,mode+"-layer.png"));
                // WM_PRINT draws overlapping WinForms siblings in reverse z-order.
                // Draw the real top layer last, at its actual client origin.
                using var composite=Graphics.FromImage(bitmap);
                var origin=window.PointToScreen(Point.Empty)-new Size(window.Left,window.Top);
                composite.DrawImageUnscaled(overlayImage,origin);
            }
            bitmap.Save(Path.Combine(root, mode + ".png"));
        }
        Console.WriteLine("Rendered real controls with fixture content: " + root);
        return 0;
    }

    private static MainWindow Create(Preferences preferences,RoomHistory? history=null) => new(LaunchOptions.Parse([]), preferences,history??new RoomHistory(Path.GetFullPath("dist/ui-review/empty-test-history.dat")))
    { StartPosition = FormStartPosition.Manual, Location = new Point(-24000, -24000), ShowInTaskbar = false };
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control c in parent.Controls) { yield return c; foreach (var child in Descendants(c)) yield return child; }
    }
    private static void Pump(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        do { Application.DoEvents(); Thread.Sleep(5); } while (Environment.TickCount64 < end);
    }
}
