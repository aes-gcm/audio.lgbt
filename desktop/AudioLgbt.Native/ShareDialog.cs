using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace AudioLgbt;

internal sealed class ShareDialog : UserControl
{
    internal event Action? CloseRequested;
    internal bool CanClose => !busy;
    private void Close() { if (!busy && !IsDisposed) CloseRequested?.Invoke(); }
    private readonly ModernSelect quality = Theme.Select(), fps = Theme.Select(), content = Theme.Select();
    private readonly ModernToggle audio = new() { Text = "Передавать звук", Width = 280 };
    private readonly Label error = Theme.Label("", muted: true);
    private readonly FlowLayoutPanel grid = new BufferedFlowPanel() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 8, 0, 8) };
    private readonly ScrollViewport sourceViewport = new() { Dock = DockStyle.Fill };
    private readonly VideoDevices video; private readonly Action<ShareOptions> changed;
    private readonly ModernButton start = Theme.Button("Начать показ", true), screensTab = Theme.Button("Экраны"), windowsTab = Theme.Button("Окна");
    private readonly CancellationTokenSource lifetime = new(); private CancellationTokenSource? previews;
    private CaptureSource? selected; private bool busy, showWindows;
    public ShareDialog(VideoDevices video, ShareOptions options, Action<ShareOptions> changed)
    {
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
        this.video = video; this.changed = changed; selected = video.Source;
        Text = "Демонстрация экрана · audio.lgbt"; Font = Theme.Font(); BackColor = Theme.Background; ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi; Size = new Size(852, 586);
        var header = new Panel { Dock = DockStyle.Top, Height = 56 };
        screensTab.Location = new Point(0, 4); windowsTab.Location = new Point(116, 4); screensTab.Width = 108; windowsTab.Width = 108; header.Controls.Add(screensTab); header.Controls.Add(windowsTab);
        screensTab.Click += (_, _) => LoadSources(false); windowsTab.Click += (_, _) => LoadSources(true);
        var refresh = Theme.Button("Обновить"); refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; refresh.Location = new Point(700, 4); header.Resize += (_, _) => refresh.Location = new Point(header.Width - refresh.Width, 4); refresh.Click += (_, _) => LoadSources(showWindows); header.Controls.Add(refresh);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 220, Padding = new Padding(0, 16, 0, 0) };
        footer.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, Height = 82, ColumnCount = 3, RowCount = 2 }; settings.ColumnStyles.Add(new(SizeType.Percent, 27)); settings.ColumnStyles.Add(new(SizeType.Percent, 27)); settings.ColumnStyles.Add(new(SizeType.Percent, 46));
        settings.Controls.Add(Theme.Label("Качество", 9, true), 0, 0); settings.Controls.Add(Theme.Label("Частота кадров", 9, true), 1, 0); settings.Controls.Add(Theme.Label("Приоритет", 9, true), 2, 0);
        quality.AccessibleName = "Качество демонстрации"; fps.AccessibleName = "Частота кадров";
        quality.Items.AddRange(["720p", "1080p", "1440p", "4K"]); quality.SelectedIndex = Array.IndexOf(new[] { 720, 1080, 1440, 2160 }, options.Height);
        fps.Items.AddRange(["15 FPS", "30 FPS", "60 FPS"]); fps.SelectedIndex = Array.IndexOf(new[] { 15, 30, 60 }, options.Fps);
        content.Items.AddRange(["Чёткость текста", "Плавность движения"]); content.SelectedIndex = options.Motion ? 1 : 0;
        quality.Margin = fps.Margin = new Padding(0, 0, 12, 0); content.Margin = Padding.Empty; settings.Controls.Add(quality, 0, 1); settings.Controls.Add(fps, 1, 1); settings.Controls.Add(content, 2, 1); footer.Controls.Add(settings);
        audio.Checked = options.Audio; audio.Location = new Point(0, 96); footer.Controls.Add(audio);
        var hint = Theme.Label("Звук источника передаётся отдельно от микрофона", 8.5f, true); hint.Location = new Point(0, 138); footer.Controls.Add(hint);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, WrapContents = false }; start.Margin = Padding.Empty; start.Enabled = selected is not null; start.Click += async (_, _) => await Apply(); actions.Controls.Add(start);
        var cancel = Theme.Button("Отмена"); cancel.Click += (_, _) => Close(); actions.Controls.Add(cancel);
        if (video.ScreenOn) { start.Text = "Применить"; var stop = Theme.Button("Остановить показ"); stop.ForeColor = Theme.Danger; stop.Click += async (_, _) => { if (busy) return; busy = true; SetBusy(true); try { await video.StopScreen(); changed(Selection()); busy = false; Close(); } finally { busy = false; if (!IsDisposed) SetBusy(false); } }; actions.Controls.Add(stop); }
        footer.Controls.Add(actions); error.AutoSize = false; error.Dock = DockStyle.Bottom; error.Height = 32; error.Visible=false; error.TextChanged+=(_,_)=>error.Visible=error.Text.Length>0;
        sourceViewport.Content.Controls.Add(grid); Controls.Add(sourceViewport); Controls.Add(error); Controls.Add(footer); Controls.Add(header);
        Load += (_, _) => LoadSources(selected?.Window ?? false); grid.Resize += (_, _) => LayoutCards(); sourceViewport.Resize+=(_,_)=>LayoutCards();

    }
    private void SetBusy(bool value)
    {
        void Visit(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ModernButton or ModernSelect or ModernToggle or SourceCard) c.Enabled = !value;
                else Visit(c);
            }
        }
        Visit(this);
        if (!value) start.Enabled = selected is not null;
    }
    private ShareOptions Selection() => new(new[] { 720, 1080, 1440, 2160 }[Math.Max(0, quality.SelectedIndex)], new[] { 15, 30, 60 }[Math.Max(0, fps.SelectedIndex)], content.SelectedIndex == 1, audio.Checked);
    private void LoadSources(bool windows)
    {
        previews?.Cancel(); previews?.Dispose(); previews = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); var token = previews.Token;
        showWindows = windows; screensTab.Primary = !windows; windowsTab.Primary = windows; screensTab.Invalidate(); windowsTab.Invalidate();
        foreach (Control control in grid.Controls.Cast<Control>().ToArray()) control.Dispose(); grid.Controls.Clear();
        var cards = CaptureSource.List().Where(s => s.Window == windows).Select(source => new SourceCard(source)).ToArray();
        foreach (var card in cards) { card.Selected = card.Source == selected; card.Click += (_, _) => { selected = card.Source; foreach (var c in grid.Controls.OfType<SourceCard>()) { c.Selected = c == card; c.Invalidate(); } start.Enabled = true; start.Text = video.ScreenOn ? (selected == video.Source ? "Применить" : "Сменить источник") : "Начать показ"; }; grid.Controls.Add(card); }
        if (cards.Length == 0) { var empty = Theme.Label("Нет доступных окон. Откройте приложение и нажмите «Обновить».", 10, true); empty.MaximumSize = new Size(600, 0); grid.Controls.Add(empty); }
        LayoutCards();
        _ = Task.Run(async () => { foreach (var card in cards) { if (token.IsCancellationRequested) break; var bitmap = await CapturePreview(card.Source, token); if (bitmap is null) { try { BeginInvoke(() => { if(!card.IsDisposed)card.PreviewUnavailable(); }); } catch(InvalidOperationException){} continue; } if (token.IsCancellationRequested) { bitmap.Dispose(); break; } try { BeginInvoke(() => { if (card.IsDisposed) bitmap.Dispose(); else card.SetPreview(bitmap); }); } catch (InvalidOperationException) { bitmap.Dispose(); } } }, token);
    }
    internal void PreviewCards()
    {
        previews?.Cancel(); foreach (Control c in grid.Controls.Cast<Control>().ToArray()) c.Dispose(); grid.Controls.Clear();
        screensTab.Primary = false; windowsTab.Primary = true;
        var names = new[] { "Заметки — План встречи", "Фотографии — Побережье", "Браузер — Документация" };
        foreach (var name in names) { var source = new CaptureSource(0, true, name, 0); var card = new SourceCard(source) { Selected = name == names[0] }; var bitmap = new Bitmap(480, 270); using (var g = Graphics.FromImage(bitmap)) { g.Clear(name == names[1] ? Color.FromArgb(50, 91, 107) : Theme.Raised); using var f = Theme.Font(16); g.DrawString(name, f, Brushes.White, 24, 24); for (int i = 0; i < 6; i++) { using var p = new Pen(Color.FromArgb(90, 90, 90), 3); g.DrawLine(p, 24, 90 + i * 22, 400 - i * 25, 90 + i * 22); } } card.SetPreview(bitmap); grid.Controls.Add(card); }
        start.Enabled = true; LayoutCards();
    }
    private void LayoutCards() { int columns = grid.ClientSize.Width >= 600 ? 3 : 2; int w = Math.Max(180, (grid.ClientSize.Width - columns * 12) / columns); foreach (var card in grid.Controls.OfType<SourceCard>()) card.Size = new Size(w, Math.Min(w * 9 / 16 + 50,Math.Max(114,sourceViewport.Height-24))); }
    private static async Task<Bitmap?> CapturePreview(CaptureSource source, CancellationToken token)
    {
        nint capture = 0;
        try { Marshal.ThrowExceptionForHR(NativeCapture.capture_start(source.Window ? 1 : 0, source.Handle, out capture)); byte[] pixels = new byte[1920 * 1080 * 4]; for (int attempt = 0; attempt < 20 && !token.IsCancellationRequested; attempt++) { int result = NativeCapture.capture_grab(capture, pixels, pixels.Length, out int w, out int h); if (result == unchecked((int)0x8007007A)) { if (w <= 0 || h <= 0 || (long)w * h > 4096L * 4096) return null; pixels = new byte[w * h * 4]; continue; } if (result == 0) { using var full = new Bitmap(w, h, PixelFormat.Format32bppRgb); var data = full.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb); try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length >= w * h * 4 ? w * h * 4 : 0); } finally { full.UnlockBits(data); } var thumb = new Bitmap(480, 270); using var g = Graphics.FromImage(thumb); g.Clear(Theme.Raised); g.InterpolationMode = InterpolationMode.HighQualityBicubic; float scale = Math.Min(480f / w, 270f / h); int tw = (int)(w * scale), th = (int)(h * scale); g.DrawImage(full, new Rectangle((480 - tw) / 2, (270 - th) / 2, tw, th)); return thumb; } if (result < 0) return null; await Task.Delay(40, token); } }
        catch (Exception e) when (e is OperationCanceledException or COMException or DllNotFoundException or ExternalException) { }
        finally { if (capture != 0) NativeCapture.capture_close(capture); }
        return null;
    }
    private async Task Apply()
    {
        if (busy || selected is null) return; busy = true; SetBusy(true); error.Text = "Запуск показа…";
        try { if (video.ScreenOn && selected == video.Source) video.UpdateScreen(Selection()); else await video.ShareScreen(selected, Selection()); changed(Selection()); busy = false; Close(); }
        catch (Exception) { error.Text = "Не удалось показать источник. Выберите другое окно или качество."; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }
    protected override void Dispose(bool disposing) { if (disposing && !IsDisposed) { lifetime.Cancel(); previews?.Cancel(); previews?.Dispose(); lifetime.Dispose(); } base.Dispose(disposing); }
}
internal sealed class SourceCard : Control
{
    public CaptureSource Source { get; }
    public bool Selected; private Bitmap? preview; private bool unavailable;
    public SourceCard(CaptureSource source) { Source = source; DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); TabStop = true; Cursor = Cursors.Hand; AccessibleName = source.Name; AccessibleRole = AccessibleRole.RadioButton; Margin = new Padding(0, 0, 12, 16); Font = Theme.Font(9); }
    public void PreviewUnavailable(){unavailable=true;Invalidate();}
    public void SetPreview(Bitmap image) { preview?.Dispose(); preview = image; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) { var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(g, ClientRectangle, Theme.Surface, Selected ? Theme.Text : Focused ? Theme.Muted : Theme.Line); var imageRect = new Rectangle(6, 6, Width - 12, Height - 50); var state = g.Save(); using (var clip = Theme.Round(imageRect, 8)) { g.SetClip(clip); if (preview is not null) { using var b=new SolidBrush(Theme.Raised);g.FillRectangle(b,imageRect);float scale=Math.Min(imageRect.Width/(float)preview.Width,imageRect.Height/(float)preview.Height);int w=(int)(preview.Width*scale),h=(int)(preview.Height*scale);g.DrawImage(preview,new Rectangle(imageRect.X+(imageRect.Width-w)/2,imageRect.Y+(imageRect.Height-h)/2,w,h)); } else { using var b = new SolidBrush(Theme.Raised); g.FillRectangle(b, imageRect); Icons.Draw(g, Source.Window ? "camera" : "screen", new Rectangle(Width / 2 - 16, imageRect.Height / 2 - 16, 32, 32), Theme.Muted); } } g.Restore(state); if(unavailable)TextRenderer.DrawText(g,"Предпросмотр недоступен",Font,new Rectangle(10,imageRect.Bottom-30,Width-20,24),Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.EndEllipsis); TextRenderer.DrawText(g, Source.Name, Font, new Rectangle(12, Height - 39, Width - 48, 30), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter); if (Selected) Icons.Draw(g, "check", new Rectangle(Width - 31, Height - 32, 19, 19), Theme.Text); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
    protected override void Dispose(bool disposing) { if (disposing) preview?.Dispose(); base.Dispose(disposing); }
}
