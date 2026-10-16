using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;

namespace AudioLgbt;

internal sealed class MainWindow : Form
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private LaunchOptions options;
    private ServerAccount account;
    private bool authorizing;
    private readonly Preferences preferences;
    private readonly RoomHistory history;
    private bool creatingRoom;
    private readonly System.Windows.Forms.Timer nameSaveTimer = new() { Interval = 400 };
    private ModalOverlay? modal;
    private Control? focusBeforeModal, peopleContent;
    private readonly ModernInput name = Theme.Input("Гость"), invitation = Theme.Input("https://audio.lgbt/#…"), chatInput = Theme.Input("Сообщение");
    private readonly Label status = Theme.Label("", muted: true), homeError = Theme.Label("", muted: true);
    private readonly Panel body = new BufferedPanel() { Dock = DockStyle.Fill }, sidebar = new RoundedPanel() { Dock = DockStyle.Right, Width = 340, Padding = new Padding(20, 12, 20, 20), Visible = false };
    private readonly Panel tiles = new BufferedPanel() { Dock = DockStyle.Fill, AutoScroll = false, Padding = Padding.Empty };
    private readonly FlowLayoutPanel controls = new BufferedFlowPanel() { Height = 78, WrapContents = false, BackColor = Theme.Surface };
    private ModernButton? peopleCount;
    private readonly System.Windows.Forms.Timer callTimer = new() { Interval = 1000 };
    private DateTime callStarted;
    private readonly System.Windows.Forms.Timer speakingTimer = new() { Interval = 100 };
    private bool announcedSpeaking;
    private long lastSpeakingAnnouncement;
    private void UpdateSpeaking()
    {
        if (!inCall) return;
        bool localSpeaking = audio?.IsSpeaking() == true;
        foreach (var peer in peers.Values)
            if (tileMap.TryGetValue(peer.Id + "/cam", out var tile))
                tile.SetSpeaking(!peer.Muted && (peer.Id == selfId ? localSpeaking : audio?.IsSpeaking(peer.Id) == true));
        long now = Environment.TickCount64;
        if (client is not null && (localSpeaking != announcedSpeaking || localSpeaking && now - lastSpeakingAnnouncement >= 750))
            if (client.Send(new { type = "speaking", speaking = localSpeaking })) { announcedSpeaking = localSpeaking; lastSpeakingAnnouncement = now; }
    }
    private readonly RichTextBox chat = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.None };
    private readonly Dictionary<string, Peer> peers = new();
    private readonly ConcurrentDictionary<string, ParticipantTile> tileMap = new();
    private readonly Dictionary<string, VideoReceiver> decoders = new();
    private readonly object decoderGate = new();
    private ClientProcess? client; private AudioDevices? audio; private VideoDevices? video;
    private Button? microphone, camera, screen, create, join, lockRoom;
    private ModernSelect? transportSelect;
    private Label? settingsFeedback;
    private bool transportBusy;
    private string key = "", selfId = "", myName = "", sideMode = "";
    private bool chatSending;
    private bool joining, inCall, closing, leaving, deviceBusy, host, locked;
    private volatile bool videoVisible = true;
    private int generation;
    public MainWindow(LaunchOptions options, Preferences? initialPreferences = null, RoomHistory? initialHistory = null)
    {
        this.preferences = initialPreferences ?? Preferences.Load();
        history = initialHistory ?? new RoomHistory();
        this.options = options; account = new ServerAccount(options.Server); Text = "audio.lgbt"; Font = Theme.Font(); BackColor = Theme.Background; ForeColor = Theme.Text;
        FormClosed += (_, _) => account.Dispose();
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(600, 520); MinimumSize = new Size(540, 500); StartPosition = FormStartPosition.CenterScreen;
        using (var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("AudioLgbt.app.ico")) { if (iconStream is not null) Icon = new Icon(iconStream); }
        HandleCreated += (_, _) => { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); };
        name.Text = preferences.Name;
        speakingTimer.Tick += (_, _) => UpdateSpeaking();
        nameSaveTimer.Tick += (_, _) => SaveName();
        name.TextChanged += (_, _) => { nameSaveTimer.Stop(); nameSaveTimer.Start(); };
        if (options.Start.Fragment.Length > 0) invitation.Text = options.Start.AbsoluteUri;
        Controls.Add(body); status.TextChanged += (_, _) => status.Visible = inCall && status.Text.Length > 0; tiles.Resize += (_, _) => LayoutTiles(); ShowHome();
        FormClosing += OnClosing;
        _ = DiscoverDevices();
        Resize += (_, _) => { videoVisible = WindowState != FormWindowState.Minimized; LayoutTiles(); };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && modal is not null) { modal.RequestClose(); e.Handled = e.SuppressKeyPress = true; return; } if (e.KeyCode == Keys.Escape && sidebar.Visible) { sidebar.Visible = false; LayoutTiles(); e.Handled = true; } if (inCall && e.Control && e.KeyCode == Keys.M) { ToggleMicrophone(); e.Handled = true; } };
    }
    private void Ui(Action action) { if (IsDisposed || closing || !IsHandleCreated) return; try { BeginInvoke(action); } catch (InvalidOperationException) { } }
    private static void DisposeChildren(Control parent) { foreach (Control child in parent.Controls.Cast<Control>().ToArray()) { parent.Controls.Remove(child); child.Dispose(); } }
    private void ClearSidebar() { chat.Parent?.Controls.Remove(chat); chatInput.Parent?.Controls.Remove(chatInput); DisposeChildren(sidebar); }
    private void ClearBody()
    {
        foreach (var reusable in new Control[] { name, invitation, homeError, tiles, sidebar, status, controls }) reusable.Parent?.Controls.Remove(reusable);
        DisposeChildren(body);
    }
    private void ShowHome()
    {
        CloseModal(); ClearBody();
        var recent = history.Rooms.Where(r=>Uri.TryCreate(r.Invitation,UriKind.Absolute,out var uri)&&LaunchOptions.IsInvitation(options.Server,uri)).ToArray();
        if(recent.Length>0 && ClientSize.Height<700)ClientSize=new Size(600,Math.Max(540,Math.Min(780,Screen.FromControl(this).WorkingArea.Height-100)));
        var layout=new Panel{Dock=DockStyle.Fill,Padding=new Padding(32,20,32,24)};
        var form=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,AutoSize=true,Padding=new Padding(40,0,40,0)};
        form.Controls.Add(Theme.Label("Ваше имя"));name.AccessibleName="Ваше имя";form.Controls.Add(name);
        invitation.PlaceholderText = options.Server.AbsoluteUri + "#…";
        create=Theme.Button("Создать комнату",true);create.Dock=DockStyle.Top;create.Margin=new Padding(0,6,0,24);create.Click+=async(_,_)=>await JoinCall(true);form.Controls.Add(create);
        form.Controls.Add(Theme.Label("Приглашение"));invitation.AccessibleName="Ссылка на комнату";form.Controls.Add(invitation);
        join=Theme.Button("Подключиться");join.Dock=DockStyle.Top;join.Click+=async(_,_)=>await JoinCall(false);form.Controls.Add(join);
        homeError.MaximumSize=new Size(430,0);homeError.Margin=new Padding(0,12,0,0);form.Controls.Add(homeError);
        if(recent.Length>0)
        {
            var area=new Panel{Dock=DockStyle.Fill,Padding=new Padding(40,18,40,0)};
            var heading=Theme.Label("Недавние комнаты",10,true);heading.Dock=DockStyle.Top;heading.AutoSize=false;heading.Height=30;
            var scroll=new ScrollViewport{Dock=DockStyle.Fill};var list=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,AutoSize=true};
            foreach(var room in recent)
            {
                var card=new RecentRoomCard(room,async()=>{if(joining)return;invitation.Text=room.Invitation;await JoinCall(false);},()=>{if(!joining)RenameRoom(room);},()=>{if(joining)return;history.Remove(room.Id);ShowHome();});list.Controls.Add(card);
            }
            scroll.Content.Controls.Add(list);area.Controls.Add(scroll);area.Controls.Add(heading);layout.Controls.Add(area);
        }
        var serverButton = Theme.Button("Сервер: " + options.Server.Authority); serverButton.Dock = DockStyle.Bottom;
        serverButton.Click += (_, _) => { if (!joining && !authorizing) ShowServer(); }; layout.Controls.Add(serverButton);
        layout.Controls.Add(form);layout.Controls.Add(new Brand{ForeColor=Theme.Text,Dock=DockStyle.Top,Height=64});
        body.Controls.Add(layout);AcceptButton=invitation.Text.Length>0?join:create;
    }
    private void RenameRoom(RecentRoom room)
    {
        var content=new TableLayoutPanel{ColumnCount=1,Padding=new Padding(0,8,0,0)};
        var title=Theme.Input("Название комнаты");title.Text=room.Title;title.AccessibleName="Название комнаты";
        var save=Theme.Button("Сохранить",true);save.Dock=DockStyle.Top;
        void Apply(){if(title.Text.Trim().Length==0)return;history.Rename(room.Id,title.Text);CloseModal();ShowHome();}
        save.Click+=(_,_)=>Apply();title.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){e.Handled=e.SuppressKeyPress=true;Apply();}};
        title.TextChanged+=(_,_)=>save.Enabled=title.Text.Trim().Length>0;
        content.Controls.Add(title);content.Controls.Add(save);ShowModal("Название комнаты",content,new Size(560,260));title.Focus();
    }
    private async void ShowServer()
    {
        var content = new TableLayoutPanel { ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
        var address = Theme.Input("https://call.example.com/"); address.Text = options.Server.AbsoluteUri; address.AccessibleName = "Адрес сервера";
        var feedback = Theme.Label("", muted: true); feedback.MaximumSize = new Size(450, 0);
        var save = Theme.Button("Подключить сервер", true); save.Dock = DockStyle.Top; save.Margin = new Padding(0, 0, 0, 12);
        var reset = Theme.Button("Основной сервер audio.lgbt"); reset.Dock = DockStyle.Top; reset.Margin = new Padding(0, 0, 0, 12); reset.Click += (_, _) => address.Text = "https://audio.lgbt/";
        var login = Theme.Button("Войти в аккаунт"); login.Dock = DockStyle.Top; login.Visible = false;
        bool busy = false;
        content.Controls.Add(Theme.Label("Адрес сервера")); content.Controls.Add(address); content.Controls.Add(save); content.Controls.Add(reset); content.Controls.Add(login); content.Controls.Add(feedback);
        ShowModal("Сервер", content, new Size(560, 430)); var overlay = modal!; overlay.CanClose = () => !busy;
        save.Click += async (_, _) =>
        {
            if (busy) return; busy = true; save.Enabled = reset.Enabled = login.Enabled = false; feedback.Text = "Проверяем сервер…";
            ServerAccount? candidate = null;
            try
            {
                var parsed = LaunchOptions.Parse(["--server", address.Text.Trim()]); candidate = new ServerAccount(parsed.Server);
                _ = await candidate.Status();
                if (closing || IsDisposed) return;
                account.Dispose(); account = candidate; candidate = null; options = parsed;
                preferences.Server = parsed.Server.AbsoluteUri; preferences.Save(); invitation.Clear(); homeError.Text = "";
                CloseModal(); ShowHome();
            }
            catch (ArgumentException e) { feedback.Text = e.Message; }
            catch { feedback.Text = "Не удалось подключиться. Укажите адрес сервера audio.lgbt с действующим HTTPS."; }
            finally { candidate?.Dispose(); busy = false; if (!save.IsDisposed) save.Enabled = reset.Enabled = login.Enabled = true; }
        };
        try
        {
            var state = await account.Status(); if (overlay.IsDisposed) return;
            login.Visible = state.AuthenticationRequired;
            login.Text = state.Username.Length > 0 ? "Выйти из аккаунта (" + state.Username + ")" : "Войти в аккаунт";
            login.Click += async (_, _) =>
            {
                if (busy) return;
                if (state.Username.Length == 0) { ShowLogin(() => { ShowServer(); return Task.CompletedTask; }); return; }
                busy = true;
                try { await account.Logout(); CloseModal(); ShowServer(); }
                catch { feedback.Text = "Не удалось выйти. Попробуйте снова."; }
                finally { busy = false; }
            };
        }
        catch { if (!feedback.IsDisposed) feedback.Text = "Текущий сервер недоступен. Можно указать другой адрес."; }
    }
    private void ShowLogin(Func<Task> completed)
    {
        var content = new TableLayoutPanel { ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
        var username = Theme.Input("Логин"); username.AccessibleName = "Логин";
        var password = Theme.Input("Пароль"); password.UseSystemPasswordChar = true; password.AccessibleName = "Пароль";
        var feedback = Theme.Label("", muted: true); feedback.MaximumSize = new Size(420, 0);
        var submit = Theme.Button("Войти", true); submit.Dock = DockStyle.Top;
        content.Controls.Add(Theme.Label(options.Server.Authority, muted: true)); content.Controls.Add(Theme.Label("Логин")); content.Controls.Add(username); content.Controls.Add(Theme.Label("Пароль")); content.Controls.Add(password); content.Controls.Add(submit); content.Controls.Add(feedback);
        bool busy = false;
        ShowModal("Вход на сервер", content, new Size(540, 440)); modal!.CanClose = () => !busy;
        async Task Submit()
        {
            if (busy) return; busy = true; submit.Enabled = false; submit.Text = "Входим…"; feedback.Text = "";
            try { await account.Login(username.Text.Trim(), password.Text); if (closing || IsDisposed) return; password.Clear(); CloseModal(); await completed(); }
            catch (Exception e) { if (!feedback.IsDisposed) feedback.Text = e is InvalidOperationException ? e.Message : "Не удалось войти. Проверьте подключение."; }
            finally { busy = false; if (!submit.IsDisposed) { submit.Enabled = true; submit.Text = "Войти"; } }
        }
        submit.Click += async (_, _) => await Submit(); password.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; await Submit(); } };
        username.Focus();
    }
    private async Task JoinCall(bool newRoom)
    {
        if (joining || inCall || authorizing || closing || IsDisposed) return;
        if (newRoom)
        {
            authorizing = true; homeError.Text = "Проверяем сервер…";
            try
            {
                var serverStatus = await account.Status();
                if (closing || IsDisposed) return;
                if (serverStatus.AuthenticationRequired && serverStatus.Username.Length == 0)
                {
                    homeError.Text = ""; ShowLogin(() => JoinCall(true)); return;
                }
                homeError.Text = "";
            }
            catch { homeError.Text = "Сервер недоступен. Проверьте адрес и подключение."; return; }
            finally { authorizing = false; }
        }
        if (!newRoom)
        {
            if (!Uri.TryCreate(invitation.Text.Trim(), UriKind.Absolute, out var uri) || !LaunchOptions.IsInvitation(options.Server, uri)) { homeError.Text = "Вставьте полную ссылку на комнату, включая часть после #."; invitation.Focus(); return; }
            key = uri.Fragment[1..];
        }
        else key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        creatingRoom = newRoom; joining = true; create!.Enabled = join!.Enabled = false; homeError.Text = "Подключение…";
        myName = name.Text.Trim(); if (myName.Length == 0) myName = "Гость"; SaveName();
        var current = ++generation;
        try
        {
            client = new(); audio = new(preferences); video = new();
            var connection = client;
            audio.Encoded += data => connection.Media("mic", data, 20000);
            audio.Error += message => Ui(() => { status.Text = message; if (audio is not null) { audio.SetMuted(true); microphone!.Text = "Микрофон выкл."; connection.Send(new { type = "mute", source = "mic", muted = true }); } });
            video.ScreenAudioChanged += enabled => connection.Send(new { type = "mute", source = "screen-audio", muted = !enabled });
            video.Encoded += (source, data, duration) =>
            {
                connection.Media(source, data, duration);
                if (source is "cam" or "screen") QueueVideo(selfId + "/" + source, data, current);
            }; video.Error += message => Ui(() => status.Text = message);
            client.Message += message => HandleMessage(message, current);
            client.Failed += message => Ui(async () => { if (current == generation) await LeaveCall(message); });
            client.Start(options.Server, key, myName, preferences.Relay, account.Token);
        }
        catch (Exception e) { await LeaveCall("Не удалось подключиться: " + e.Message); }
    }
    private void HandleMessage(JsonElement m, int current)
    {
        if (current != generation) return;
        var type = m.GetProperty("type").GetString();
        if (type == "frame")
        {
            var peer = m.GetProperty("peerId").GetString()!; var source = m.GetProperty("source").GetString()!;
            try
            {
                var bytes = m.GetProperty("data").GetBytesFromBase64();
                if (source is "mic" or "screen-audio") { audio?.Receive(peer, source, bytes, m.GetProperty("timestamp").GetUInt32()); return; }
                QueueVideo(peer + "/" + source, bytes, current);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or DllNotFoundException or BadImageFormatException) { Ui(() => status.Text = "Не удалось показать видео. Звук продолжает работать."); }
            return;
        }
        if (type == "keyframe") { video?.ForceKeyframe(m.GetProperty("source").GetString()!); return; }
        Ui(async () =>
        {
            if (current != generation) return;
            switch (type)
            {
                case "welcome":
                    history.Remember(options.Server,options.Server.AbsoluteUri+"#"+key,creatingRoom);
                    selfId = m.GetProperty("selfId").GetString()!; host = Bool(m, "host"); locked = Bool(m, "locked"); joining = false; inCall = true; ShowCall();
                    AddPeer(selfId, myName, true);
                    if (m.TryGetProperty("peers", out var list)) foreach (var peer in list.EnumerateArray()) AddPeer(peer);
                    try { audio!.StartPlayback(preferences.Speaker); } catch { status.Text = "Нет устройства вывода. Выберите наушники в настройках."; }

                    try { audio!.SetMuted(false); microphone!.Text = "Микрофон вкл."; client!.Send(new { type = "mute", source = "mic", muted = false }); }
                    catch { status.Text = "Микрофон недоступен. Можно слушать или выбрать устройство в настройках."; }
                    if (peers.TryGetValue(selfId, out var own)) { own.Muted = audio!.Muted; RefreshPeer(own); }
                    client!.Send(new { type = "mute", source = "mic", muted = audio!.Muted });
                    client!.Send(new { type = "mute", source = "cam", muted = true }); client.Send(new { type = "mute", source = "screen", muted = true }); client.Send(new { type = "mute", source = "screen-audio", muted = true }); break;
                case "peer-joined": AddPeer(m.GetProperty("peer")); break;
                case "peer-updated": var updated = m.GetProperty("peer"); var uid = updated.GetProperty("id").GetString()!; if (peers.TryGetValue(uid, out var existing)) { existing.Name = updated.GetProperty("name").GetString()!; RefreshPeer(existing); } break;
                case "peer-left": RemovePeer(m.GetProperty("peerId").GetString()!); break;
                case "mute": OnMute(m); break;
                case "mute-request": if (audio is not null) { audio.SetMuted(true); microphone!.Text = "Микрофон выкл."; client!.Send(new { type = "mute", source = "mic", muted = true }); } break;
                case "chat": var sender = m.GetProperty("peerId").GetString()!; AddChat(peers.TryGetValue(sender, out var p) ? p.Name : "Участник", m.GetProperty("text").GetString()!); break;
                case "connection": var state = m.GetProperty("state").GetString(); if (state == "connected") status.Text = ""; else if (state is "failed" or "disconnected") status.Text = "Связь прервалась. Проверьте интернет или смените режим в настройках."; break;
                case "error": await LeaveCall(m.GetProperty("error").GetString() ?? "Не удалось подключиться."); break;
                case "transport-complete":
                    transportBusy = false; preferences.Relay = Bool(m, "relay"); preferences.Save();
                    if (transportSelect is { IsDisposed: false }) { transportSelect.SelectedIndex = preferences.Relay ? 1 : 0; transportSelect.Enabled = true; }
                    status.Text = Bool(m, "rollback") ? "Переключение не удалось. Предыдущее подключение восстановлено." : ""; ReportSettings(status.Text); break;
                case "transport-rollback": status.Text = "Восстанавливаем предыдущее подключение…"; ReportSettings(status.Text); break;
                case "action-error":
                    chatSending = false;
                    if (m.TryGetProperty("action", out var action) && action.GetString() == "transport") { transportBusy = false; if (transportSelect is { IsDisposed: false }) { transportSelect.SelectedIndex = preferences.Relay ? 1 : 0; transportSelect.Enabled = true; } }
                    status.Text = m.GetProperty("error").GetString(); ReportSettings(status.Text ?? ""); break;
                case "chat-sent": chatSending = false; var sent = m.GetProperty("text").GetString()!; AddChat(myName, sent); if (chatInput.Text.Trim() == sent) chatInput.Clear(); break;
                case "removed": await LeaveCall("Организатор удалил вас из комнаты."); break;
                case "ended": await LeaveCall("Звонок завершён."); break;
                case "host": host = m.GetProperty("peerId").GetString() == selfId; RefreshPeople(); break;
                case "locked": locked = Bool(m, "locked"); if (lockRoom is { IsDisposed: false }) lockRoom.Text = locked ? "Открыть вход" : "Закрыть вход"; break;
            }
        });
    }
    private void QueueVideo(string id, byte[] bytes, int current)
    {
        lock (decoderGate)
        {
            if (current != generation || !tileMap.ContainsKey(id)) return;
            if (!decoders.TryGetValue(id, out var decoder)) decoders[id] = decoder = new(
                () => videoVisible && current == generation,
                bitmap => { if (current == generation && tileMap.TryGetValue(id, out var tile)) tile.SetVideo(bitmap); else bitmap.Dispose(); },
                () => Ui(() => status.Text = "Не удалось показать видео. Звук продолжает работать."));
            decoder.Receive(bytes);
        }
    }
    private void ScreenTile(string id, bool muted)
    {
        if (!peers.TryGetValue(id, out var peer)) return;
        var key = id + "/screen";
        if (!muted && !tileMap.ContainsKey(key))
        {
            var tile = new ParticipantTile(peer.Name + " · экран", volume => audio?.SetTrackVolume(id, "screen-audio", volume));
            tileMap[key] = tile; tiles.Controls.Add(tile); tiles.Controls.SetChildIndex(tile, 0);
        }
        else if (muted && tileMap.TryRemove(key, out var tile)) { tiles.Controls.Remove(tile); tile.Dispose(); lock (decoderGate) { if (decoders.Remove(key, out var decoder)) decoder.Dispose(); } }
        LayoutTiles();
    }
    private static bool Bool(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private void ShowCall()
    {
        ClearBody(); ClientSize = new Size(Math.Max(1160, ClientSize.Width), Math.Max(760, ClientSize.Height)); MinimumSize = new Size(780, 600); AcceptButton = null;
        var top = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(24) };
        top.Controls.Add(new Brand { Location = new Point(24, 24), Width = 178 });
        var secure = Theme.Label("Сквозное шифрование", 8.5f, true); secure.Location = new Point(232, 37); top.Controls.Add(secure);
        top.Paint += (_, e) => { Icons.Draw(e.Graphics, "lock", new Rectangle(208, 34, 18, 18), Theme.Muted); using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 87, top.Width, 87); };
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Height = 44 };
        var settings = Theme.Button(""); settings.Glyph = "settings"; settings.AccessibleName = "Настройки"; settings.Click += (_, _) => ShowSettings(); actions.Controls.Add(settings);
        var elapsed = Theme.Label("00:00", 9, true); elapsed.AutoSize = false; elapsed.Size = new Size(70, 44); elapsed.TextAlign = ContentAlignment.MiddleCenter; elapsed.Margin = new Padding(0, 0, 8, 0); actions.Controls.Add(elapsed);
        var people = Theme.Button("0"); people.Glyph = "people"; people.AccessibleName = "Участники"; people.AutoSize = false; people.Width = 72; people.Click += (_, _) => ShowSidebar("people"); actions.Controls.Add(people);
        peopleCount = people;
        var invite = Theme.Button("Пригласить", true); invite.Glyph = "plus"; invite.Click += (_, _) => ShowInvitation(); actions.Controls.Add(invite); top.Controls.Add(actions);
        top.Resize += (_, _) => { actions.Location = new Point(top.Width - actions.PreferredSize.Width - 16, 22); secure.Visible = top.Width > 950; };
        speakingTimer.Start(); callStarted = DateTime.Now; callTimer.Stop(); callTimer.Tick += Tick; callTimer.Start(); void Tick(object? s, EventArgs e) { if (elapsed.IsDisposed) { callTimer.Tick -= Tick; return; } var time = DateTime.Now - callStarted; elapsed.Text = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss"); }
        DisposeChildren(controls);
        ModernButton Tool(string text, string icon, EventHandler action) { var button = Theme.Button(text); button.Toolbar = true; button.Glyph = icon; button.AutoSize = false; button.Size = new Size(72, 76); button.Margin = new Padding(2, 0, 2, 0); button.Click += action; controls.Controls.Add(button); return button; }
        microphone = Tool("Микрофон выкл.", "mic", (_, _) => ToggleMicrophone());
        camera = Tool("Камера", "camera", async (_, _) => await ToggleCamera());
        screen = Tool("Показать экран", "screen", async (_, _) => await ToggleScreen());
        Tool("Чат", "chat", (_, _) => ShowSidebar("chat"));
        var leave = Tool("Выйти", "leave", async (_, _) => await LeaveCall()); leave.BackColor = Theme.Danger;
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 122 }; var dock = new RoundedPanel { Size = new Size(404, 96), Padding = new Padding(10), Fill = Theme.Surface }; controls.Location = new Point(10, 10); controls.Size = new Size(384, 78); dock.Controls.Add(controls); bottom.Controls.Add(dock); bottom.Resize += (_, _) => dock.Location = new Point((bottom.Width - dock.Width) / 2, 10);
        status.Dock = DockStyle.Bottom; status.AutoSize = false; status.Height = 28; status.Padding = new Padding(24, 2, 0, 0); status.Text = ""; status.Visible = false;
        tiles.Controls.Clear(); ClearSidebar(); sidebar.Visible = false;
        body.Controls.Add(tiles); body.Controls.Add(sidebar); body.Controls.Add(status); body.Controls.Add(bottom); body.Controls.Add(top);
        KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        if (WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }
    private void AddPeer(JsonElement p)
    {
        var id = p.GetProperty("id").GetString()!; bool hasMutes = p.TryGetProperty("mutes", out var m);
        AddPeer(id, p.GetProperty("name").GetString()!, hasMutes && Bool(m, "mic"));
        if (hasMutes && m.TryGetProperty("screen", out var shown) && shown.ValueKind == JsonValueKind.False) ScreenTile(id, false);
    }
    private void AddPeer(string id, string label, bool muted)
    {
        if (peers.ContainsKey(id)) return; var peer = new Peer { Id = id, Name = label, Muted = muted }; peers[id] = peer;
        var tile = new ParticipantTile(label); tileMap[id + "/cam"] = tile; tiles.Controls.Add(tile); RefreshPeer(peer); LayoutTiles();
    }
    private void RefreshPeer(Peer p) { if (tileMap.TryGetValue(p.Id + "/cam", out var tile)) tile.SetName(p.Name + (p.Id == selfId ? " (вы)" : ""), p.Muted); RefreshPeople(); }
    private void RemovePeer(string id) { peers.Remove(id); audio?.Remove(id); foreach (var source in new[] { "cam", "screen" }) { var k = id + "/" + source; if (tileMap.TryRemove(k, out var tile)) { tiles.Controls.Remove(tile); tile.Dispose(); } lock (decoderGate) { if (decoders.Remove(k, out var d)) d.Dispose(); } } LayoutTiles(); RefreshPeople(); }
    private void OnMute(JsonElement m)
    {
        var id = m.GetProperty("peerId").GetString()!; var src = m.GetProperty("source").GetString()!; var muted = Bool(m, "muted");
        if (!peers.TryGetValue(id, out var p)) return; if (src == "mic") { p.Muted = muted; RefreshPeer(p); }
        else if (src == "cam" && muted && tileMap.TryGetValue(id + "/cam", out var cam)) cam.ClearVideo();
        else if (src == "screen") ScreenTile(id, muted);
    }
    private void LayoutTiles()
    {
        if (!inCall) return;
        if (peopleCount is not null) { peopleCount.Text = peers.Count.ToString(); peopleCount.AccessibleName = "Участники: " + peers.Count; }
        var screens = tileMap.Where(p => p.Key.EndsWith("/screen")).Select(p => p.Value).ToArray();
        var cameras = tileMap.Where(p => p.Key.EndsWith("/cam")).Select(p => p.Value).ToArray();
        int gap = 12, w = Math.Max(240, tiles.ClientSize.Width - 48), h = Math.Max(200, tiles.ClientSize.Height - 32), y = 16;
        tiles.SuspendLayout();
        if (screens.Length > 0) { int sh = Math.Max(200, h - (cameras.Length > 0 ? 142 : 0)); foreach (var t in screens) { t.SetBounds(24, y, w, sh); y += sh + gap; } int cw = Math.Min(210, (w - Math.Max(0, cameras.Length - 1) * gap) / Math.Max(1, cameras.Length)); int x = 24 + (w - Math.Min(w, cameras.Length * (cw + gap) - gap)) / 2; foreach (var t in cameras) { t.SetBounds(x, y, Math.Max(120, cw), 118); x += cw + gap; } }
        else if (cameras.Length > 0) { int cols = cameras.Length <= 1 ? 1 : w >= 1000 && cameras.Length > 4 ? 3 : w >= 600 ? 2 : 1; int rows = (cameras.Length + cols - 1) / cols; int tw = (w - (cols - 1) * gap) / cols; int th = Math.Min(tw * 9 / 16, Math.Max(48, (h - (rows - 1) * gap) / rows)); int total = rows * (th + gap) - gap; y = Math.Max(16, (tiles.ClientSize.Height - total) / 2); for (int i = 0; i < cameras.Length; i++) { int count = Math.Min(cols, cameras.Length - i / cols * cols); int x = 24 + (w - (count * (tw + gap) - gap)) / 2 + (i % cols) * (tw + gap); cameras[i].SetBounds(x, y + i / cols * (th + gap), tw, th); } }
        tiles.ResumeLayout();
        tiles.Invalidate(true);
    }
    private void ToggleMicrophone() { if (audio is null || !inCall) return; try { audio.SetMuted(!audio.Muted); microphone!.Text = audio.Muted ? "Микрофон выкл." : "Микрофон вкл."; client?.Send(new { type = "mute", source = "mic", muted = audio.Muted }); if (peers.TryGetValue(selfId, out var p)) { p.Muted = audio.Muted; RefreshPeer(p); } } catch { status.Text = "Не удалось включить микрофон. Проверьте устройство и доступ в Windows."; } }
    private async Task ToggleCamera() { if (video is null || deviceBusy) return; deviceBusy = true; camera!.Enabled = false; try { await video.SetCamera(preferences.Camera, !video.CameraOn); camera.Text = video.CameraOn ? "Камера вкл." : "Камера"; client?.Send(new { type = "mute", source = "cam", muted = !video.CameraOn }); } catch (Exception) { status.Text = "Камера недоступна. Проверьте подключение и доступ в Windows."; } finally { deviceBusy = false; if (!camera.IsDisposed) camera.Enabled = true; } }
    private ShareOptions shareOptions = new();
    private Task ToggleScreen()
    {
        if (video is null || !inCall) return Task.CompletedTask;
        var current = generation; var devices = video;
        var dialog = new ShareDialog(devices, shareOptions, config =>
        {
            if (current != generation) return;
            shareOptions = config; ScreenTile(selfId, !devices.ScreenOn); screen!.Text = devices.ScreenOn ? "Демонстрация…" : "Показать экран";
            client?.Send(new { type = "mute", source = "screen", muted = !devices.ScreenOn });
            client?.Send(new { type = "mute", source = "screen-audio", muted = !devices.ScreenAudioOn });
        });
        ShowModal("Что хотите показать?", dialog, new Size(920, 700), () => dialog.CanClose);
        dialog.CloseRequested += () => { if(modal?.Content == dialog) CloseModal(); };
        return Task.CompletedTask;
    }
    private void ShowSidebar(string mode)
    {
        if(mode == "settings") { ShowSettings(); return; }
        if(mode == "people") { ShowPeople(); return; }
        if (sidebar.Visible && sideMode == mode) { sidebar.Visible = false; LayoutTiles(); return; }
        body.SuspendLayout(); sidebar.SuspendLayout();
        sideMode = mode; ClearSidebar();
        var title = new Panel { Dock = DockStyle.Top, Height = 58 };
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var heading = Theme.Label("Чат", 12, true); heading.Margin = new Padding(4, 12, 18, 0); tabs.Controls.Add(heading);
        var close = Theme.Button("Закрыть"); close.Click += (_, _) => ShowSidebar("chat"); tabs.Controls.Add(close);
        title.Controls.Add(tabs);
        BuildChat(); sidebar.Controls.Add(title);
        sidebar.ResumeLayout(true); sidebar.Visible = true; body.ResumeLayout(true); LayoutTiles(); sidebar.Invalidate(true); if(mode=="chat")chatInput.Focus();
    }
    private void BuildChat()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 108, Padding = new Padding(0, 8, 0, 0) }; chatInput.Dock = DockStyle.Top; var send = Theme.Button("Отправить"); send.Dock = DockStyle.Bottom; send.Click += (_, _) => SendChat(); chatInput.KeyDown -= ChatKey; chatInput.KeyDown += ChatKey; footer.Controls.Add(chatInput); footer.Controls.Add(send); sidebar.Controls.Add(chat); sidebar.Controls.Add(footer); chatInput.Focus();
    }
    private void ChatKey(object? sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { SendChat(); e.Handled = true; e.SuppressKeyPress = true; } }
    private void SendChat() { var text = chatInput.Text.Trim(); if (text.Length == 0 || chatSending) return; if (text.Length > 2000) { status.Text = "Сообщение должно быть короче 2000 символов."; return; } if (client?.Send(new { type = "chat", text }) == true) { chatSending = true; } else status.Text = "Сообщение не отправлено. Попробуйте ещё раз."; }
    private void AddChat(string who, string text) { chat.SelectionColor = Theme.Muted; chat.SelectionFont = Theme.Font(9, true); chat.AppendText(who + "\n"); chat.SelectionColor = Theme.Text; chat.SelectionFont = Theme.Font(10); chat.AppendText(text + "\n\n"); chat.SelectionStart = chat.TextLength; chat.ScrollToCaret(); }
    private void ShowPeople()
    {
        var content = new BufferedPanel(); BuildPeople(content);
        ShowModal("Участники", content, new Size(680, 620)); peopleContent = content;
    }
    private void RefreshPeople() { if (peopleContent is { IsDisposed: false }) { DisposeChildren(peopleContent); BuildPeople(peopleContent); } }
    private void BuildPeople(Control target)
    {
        var viewport = new ScrollViewport { Dock = DockStyle.Fill };
        var list = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        viewport.Content.Controls.Add(list); target.Controls.Add(viewport);
        foreach (var p in peers.Values)
        {
            var row = new Panel { Height = 58, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            row.Paint += (_, e) => { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using var b = new SolidBrush(Theme.Raised); e.Graphics.FillEllipse(b, 0, 4, 36, 36); using var f = Theme.Font(12); TextRenderer.DrawText(e.Graphics, p.Name.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(p.Name).ToUpperInvariant() : "?", f, new Rectangle(0, 4, 36, 36), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); Icons.Draw(e.Graphics, "mic", new Rectangle(row.Width - 22, 14, 16, 16), Theme.Muted); if (p.Muted) { using var pen = new Pen(Theme.Muted, 1.4f); e.Graphics.DrawLine(pen, row.Width - 22, 14, row.Width - 6, 30); } };
            var nameLabel = Theme.Label(p.Name + (p.Id == selfId ? " (вы)" : "")); nameLabel.AutoSize = false; nameLabel.Bounds = new Rectangle(48, 2, 200, 22); row.Resize += (_, _) => nameLabel.Width = Math.Max(1, row.Width - 82); nameLabel.Width = Math.Max(1,row.Width-82); row.Controls.Add(nameLabel);
            var role = Theme.Label(p.Id == selfId && host ? "Организатор" : "Участник", 8, true); role.Location = new Point(48, 27); row.Controls.Add(role); list.Controls.Add(row);
            if (p.Id != selfId) { Slider(list, "Громкость", p.Volume, 200, v => { p.Volume = v; audio?.SetTrackVolume(p.Id, "mic", v); }); if (host) { var remove = Theme.Button("Удалить из комнаты"); remove.Click += (_, _) => client?.Send(new { type = "remove", peerId = p.Id }); remove.Margin = new Padding(0, 0, 0, 20); list.Controls.Add(remove); } }
        }
        if (host) { lockRoom = Theme.Button(locked ? "Открыть вход" : "Закрыть вход"); lockRoom.Dock = DockStyle.Top; lockRoom.Margin = new Padding(0, 16, 0, 8); lockRoom.Click += (_, _) => client?.Send(new { type = "lock", locked = !locked }); list.Controls.Add(lockRoom); var mute = Theme.Button("Выключить всем микрофоны"); mute.Dock = DockStyle.Top; mute.Margin = new Padding(0, 0, 0, 8); mute.Click += (_, _) => client?.Send(new { type = "mute-all" }); list.Controls.Add(mute); var end = Theme.Button("Завершить для всех"); end.Dock = DockStyle.Top; end.Click += (_, _) => ConfirmEndCall(); list.Controls.Add(end); }
    }
    private void ConfirmEndCall()
    {
        var content = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Padding = new Padding(0,8,0,0) };
        var description = Theme.Label("Все участники будут отключены от звонка.");
        description.MaximumSize = new Size(450,0); description.Margin = new Padding(0,0,0,24);
        content.Controls.Add(description);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        var end = Theme.Button("Завершить для всех"); end.BackColor=Theme.Danger;
        end.Click += (_,_) => { client?.Send(new { type="end" }); CloseModal(); };
        var cancel=Theme.Button("Отмена"); cancel.Click+=(_,_)=>CloseModal();
        actions.Controls.Add(end);actions.Controls.Add(cancel);content.Controls.Add(actions);
        ShowModal("Завершить звонок?",content,new Size(560,280));cancel.Focus();
    }
    private void SaveName()
    {
        nameSaveTimer.Stop();
        preferences.Name = name.Text.Trim();
        preferences.Save();
    }
    private void ShowModal(string title, Control content, Size size, Func<bool>? canClose = null)
    {
        if(modal?.CanClose?.Invoke() == false) { content.Dispose(); return; }
        CloseModal();
        focusBeforeModal = ActiveControl;
        modal = new ModalOverlay(body, title, content, size) { CanClose = canClose };
        modal.CloseRequested += CloseModal;
        Controls.Add(modal);
        modal.BringToFront();
        modal.ActivateContent();
        body.Enabled = false;
    }
    private void CloseModal()
    {
        var old = modal;
        if(old is null)return;
        modal = null; peopleContent = null; audio?.StopTestSound();
        Controls.Remove(old);
        old.Dispose();
        body.Enabled = true;
        if(focusBeforeModal is { IsDisposed:false, CanFocus:true })focusBeforeModal.Focus();
        focusBeforeModal = null;
    }
    private void ShowInvitation()
    {
        var content = new Panel();
        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, Padding=new Padding(0,8,0,0) };
        var link = Theme.Input(); link.ReadOnly=true; link.Text=options.Server.AbsoluteUri+"#"+key; link.AccessibleName="Ссылка на комнату";
        var copy = Theme.Button("Скопировать ссылку",true);copy.Dock=DockStyle.Top;copy.Margin=Padding.Empty;
        var feedback = Theme.Label("",9,true);feedback.MaximumSize=new Size(500,0);
        layout.Controls.Add(Theme.Label("Ссылка на комнату",10,true));layout.Controls.Add(link);layout.Controls.Add(copy);layout.Controls.Add(feedback);content.Controls.Add(layout);
        var timer = new System.Windows.Forms.Timer { Interval=2200 };
        timer.Tick+=(_,_)=>{timer.Stop();if(!copy.IsDisposed)copy.Text="Скопировать ссылку";};
        content.Disposed+=(_,_)=>timer.Dispose();
        copy.Click+=(_,_)=>{try{Clipboard.SetText(link.Text);copy.Text="Скопировано";feedback.Text="";timer.Stop();timer.Start();}catch(System.Runtime.InteropServices.ExternalException){feedback.Text="Не удалось скопировать. Попробуйте ещё раз.";}};
        ShowModal("Пригласить в комнату",content,new Size(660,320));
    }
    private List<DeviceChoice> microphones = [new("-1", "Системный микрофон")], speakers = [new("", "Системное устройство")], cameras = [];
    private Task? deviceDiscovery;
    private Task DiscoverDevices()
    {
        if (deviceDiscovery is { IsCompleted: false }) return deviceDiscovery;
        return deviceDiscovery = Task.Run(async () =>
        {
            try { microphones = AudioDevices.Microphones(); } catch { }
            try { speakers = AudioDevices.Speakers(); } catch { }
            try { cameras = await VideoDevices.Cameras(); } catch { }
        });
    }
    private void ReportSettings(string text){if(settingsFeedback is {IsDisposed:false})settingsFeedback.Text=text;}
    private void ShowSettings()
    {
        var content = new BufferedPanel();
        BuildSettings(content);
        ShowModal("Настройки",content,new Size(860,630));
    }
    private void BuildSettings(Control target)
    {
        if(target.IsDisposed)return;
        DisposeChildren(target);
        var feedback=Theme.Label("",9,true);feedback.Dock=DockStyle.Bottom;feedback.AutoSize=false;feedback.Height=36;settingsFeedback=feedback;
        var scroll=new ScrollViewport{Dock=DockStyle.Fill};
        var columns=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=2,AutoSize=true};
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        TableLayoutPanel Column(int index){var c=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,AutoSize=true,Margin=Padding.Empty,Padding=new Padding(index==0?0:16,8,index==0?16:0,0)};columns.Controls.Add(c,index,0);return c;}
        var input=Column(0);var output=Column(1);
        scroll.Content.Controls.Add(columns);target.Controls.Add(scroll);target.Controls.Add(feedback);
        void Report(string message){if(!feedback.IsDisposed)feedback.Text=message;}
        input.Controls.Add(Theme.Label("Микрофон"));var micSelect=Theme.Select();micSelect.AccessibleName="Микрофон";input.Controls.Add(micSelect);
        try{Fill(micSelect,microphones,preferences.Microphone.ToString());}catch{Report("Не удалось прочитать список микрофонов.");}
        micSelect.SelectionChangeCommitted+=(_,_)=>{if(micSelect.SelectedItem is not DeviceChoice choice)return;try{audio?.SetMicrophone(int.Parse(choice.Id));preferences.Save();Report("");}catch{Report("Не удалось переключить микрофон. Предыдущее устройство сохранено.");Select(micSelect,preferences.Microphone.ToString());}};
        Slider(input,"Громкость микрофона",preferences.MicGain,200,v=>{preferences.MicGain=v;preferences.Save();});
        var noise=new ModernToggle{Text="Шумоподавление",Checked=preferences.NoiseSuppression,Dock=DockStyle.Top,Margin=new Padding(0,4,0,28)};noise.CheckedChanged+=(_,_)=>{preferences.NoiseSuppression=noise.Checked;preferences.Save();};input.Controls.Add(noise);
        input.Controls.Add(Theme.Label("Камера"));var cameraSelect=Theme.Select();cameraSelect.AccessibleName="Камера";input.Controls.Add(cameraSelect);
        output.Controls.Add(Theme.Label("Наушники / динамики"));var speakerSelect=Theme.Select();speakerSelect.AccessibleName="Наушники или динамики";output.Controls.Add(speakerSelect);
        try{Fill(speakerSelect,speakers,preferences.Speaker);}catch{Report("Не удалось прочитать список устройств вывода.");}
        speakerSelect.SelectionChangeCommitted+=(_,_)=>{if(speakerSelect.SelectedItem is not DeviceChoice choice)return;try{audio?.StopTestSound();audio?.StartPlayback(choice.Id);preferences.Speaker=choice.Id;preferences.Save();Report("");}catch{Report("Не удалось переключить звук. Предыдущее устройство сохранено.");Select(speakerSelect,preferences.Speaker);}};
        var testOutput = Theme.Button("Проверить звук"); testOutput.AccessibleName = "Проверить звук"; testOutput.Dock = DockStyle.Top; testOutput.Margin = new Padding(0, 0, 0, 20); output.Controls.Add(testOutput);
        bool testing = false; int testEpoch = 0;
        testOutput.Click += async (_, _) => {
            int epoch = ++testEpoch;
            if (testing) { audio?.StopTestSound(); testing = false; testOutput.Text = "Проверить звук"; return; }
            try {
                if (audio is null) throw new InvalidOperationException();
                audio.PlayTestSound(); testing = true; testOutput.Text = "Остановить проверку"; Report("");
                await Task.Delay(1400);
            } catch { Report("Не удалось воспроизвести звук. Выберите другое устройство вывода."); }
            finally { if (!testOutput.IsDisposed && epoch == testEpoch) { testing = false; testOutput.Text = "Проверить звук"; } }
        };
        testOutput.Disposed += (_, _) => audio?.StopTestSound();
        Slider(output,"Громкость звонка",preferences.Volume,100,v=>{audio?.SetVolume(v);preferences.Save();});
        output.Controls.Add(Theme.Label("Подключение"));var transport=Theme.Select();transportSelect=transport;transport.AccessibleName="Подключение";transport.Enabled=!transportBusy;transport.Items.AddRange(["Автоматически","Через TLS"]);transport.SelectedIndex=preferences.Relay?1:0;output.Controls.Add(transport);
        transport.SelectionChangeCommitted+=(_,_)=>{if(client?.Send(new{type="transport",relay=transport.SelectedIndex==1})==true){transportBusy=true;transport.Enabled=false;Report("Переключение подключения…");}else transport.SelectedIndex=preferences.Relay?1:0;};
        var refresh=Theme.Button("Обновить устройства");refresh.Dock=DockStyle.Top;refresh.Click+=async(_,_)=>{refresh.Enabled=false;await DiscoverDevices();if(!target.IsDisposed)BuildSettings(target);};output.Controls.Add(refresh);
        try
        {
            Fill(cameraSelect,cameras,preferences.Camera);
            if (deviceDiscovery is { IsCompleted: false } discovery) _ = CompleteDiscovery(discovery);
            async Task CompleteDiscovery(Task discovery)
            {
                await discovery;
                if (cameraSelect.IsDisposed || micSelect.IsDisposed || speakerSelect.IsDisposed) return;
                Fill(micSelect,microphones,preferences.Microphone.ToString());
                Fill(speakerSelect,speakers,preferences.Speaker);
                Fill(cameraSelect,cameras,preferences.Camera);
            }
            cameraSelect.SelectionChangeCommitted+=async(_,_)=>
            {
                if(deviceBusy||cameraSelect.SelectedItem is not DeviceChoice choice)return;
                deviceBusy=true;cameraSelect.Enabled=false;
                try{if(video?.CameraOn==true)await video.SetCamera(choice.Id,true);preferences.Camera=choice.Id;preferences.Save();Report("");}
                catch{Report("Не удалось переключить камеру. Предыдущее устройство сохранено.");if(!cameraSelect.IsDisposed)Select(cameraSelect,preferences.Camera);}
                finally{deviceBusy=false;if(!cameraSelect.IsDisposed)cameraSelect.Enabled=true;}
            };
        }
        catch{Report("Камеры не найдены.");}
    }
    private static void Fill(ModernSelect box, List<DeviceChoice> items, string selected) { box.Items.Clear(); box.Items.AddRange(items.Cast<object>().ToArray()); Select(box, selected); }
    private static void Select(ModernSelect box, string selected) { for (var i = 0; i < box.Items.Count; i++) if (((DeviceChoice)box.Items[i]!).Id == selected) { box.SelectedIndex = i; return; } if (box.Items.Count > 0) box.SelectedIndex = 0; }
    private static void Slider(TableLayoutPanel layout, string title, int value, int max, Action<int> changed) { var label = Theme.Label(title + " · " + value + "%"); layout.Controls.Add(label); var slider = new ModernSlider { Minimum = 0, Maximum = max, Value = Math.Clamp(value, 0, max), Dock = DockStyle.Top, Height = 36, AccessibleName = title, Margin = new Padding(0, 0, 0, 16) }; slider.ValueChanged += (_, _) => { label.Text = title + " · " + slider.Value + "%"; changed(slider.Value); }; layout.Controls.Add(slider); }
    internal void PreviewRoom(string mode = "")
    {
        selfId = "preview-self"; key = "AAAAAAAAAAAAAAAAAAAAAA"; myName = "Александр"; inCall = true; ShowCall();
        AddPeer(selfId, myName, false); AddPeer("preview-guest", "Мария", true);
        AddChat("Мария", "Привет! Сейчас покажу экран."); AddChat("Александр", "Да, всё видно. Давай посмотрим вместе.");
        microphone!.Text = "Микрофон вкл.";
        if(mode=="invite")ShowInvitation();
        else if(mode=="share"){var devices=new VideoDevices();var panel=new ShareDialog(devices,new ShareOptions(),_=>{});ShowModal("Что хотите показать?",panel,new Size(920,700));panel.CloseRequested+=CloseModal;panel.PreviewCards();}
        else if (mode.Length > 0) ShowSidebar(mode);
    }
    private async Task LeaveCall(string message = "")
    {
        if (leaving) return; CloseModal(); leaving = true; ++generation; joining = false; inCall = false; callTimer.Stop(); speakingTimer.Stop(); announcedSpeaking = false; lastSpeakingAnnouncement = 0;
        var oldAudio = audio; var oldVideo = video; var oldClient = client; audio = null; video = null; client = null;
        oldAudio?.Dispose(); if (oldVideo is not null) await oldVideo.DisposeAsync(); if (oldClient is not null) await oldClient.DisposeAsync();
        lock (decoderGate) { foreach (var d in decoders.Values) d.Dispose(); decoders.Clear(); }
        foreach (var tile in tileMap.Values) tile.Dispose(); tileMap.Clear(); peers.Clear(); chat.Clear(); selfId = ""; chatSending = false; transportBusy = false; leaving = false;
        if (!closing) { MinimumSize = new Size(540, 500); ClientSize = new Size(600, 520); ShowHome(); homeError.Text = message; }
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e) { if (closing) return; SaveName(); e.Cancel = true; closing = true; await LeaveCall(); Close(); }
    protected override void Dispose(bool disposing) { if (disposing) { nameSaveTimer.Dispose(); callTimer.Dispose(); speakingTimer.Dispose(); } base.Dispose(disposing); }
    private sealed class Peer { public string Id = "", Name = ""; public bool Muted; public int Volume = 100; }
}
