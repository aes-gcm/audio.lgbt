using System.Drawing.Drawing2D;
namespace AudioLgbt;

internal sealed class ParticipantTile : Panel
{
    private string label = "", initial = "?"; private bool muted, speaking; private Bitmap? frame, pending; private int scheduled;
    private ContextMenuStrip? audioMenu; private ModernSlider? volumeSlider; private ToolStripMenuItem? muteItem; private bool shareMuted; private int shareLevel = 100;
    private Form? fullscreen; private PictureBox? expanded; private readonly bool share;
    public ParticipantTile(string text, Action<int>? shareVolume = null)
    {
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = Theme.Background; Size = new Size(400, 225); SetName(text, false); share = shareVolume is not null;
        if (shareVolume is not null)
        {
            var expand = Theme.Button(""); expand.Glyph = "fullscreen"; expand.AccessibleName = "Полный экран";
            expand.AutoSize = false; expand.Size = new Size(40, 40); expand.MinimumSize = new Size(40, 40);
            expand.Click += (_, _) => Expand(); Controls.Add(expand);
            Resize += (_, _) => expand.Location = new Point(Width - expand.Width - 12, Height - expand.Height - 12);
            expand.Location = new Point(Width - expand.Width - 12, Height - expand.Height - 12);
            DoubleClick += (_, _) => Expand();
            audioMenu = new RoundedMenuStrip { ShowImageMargin = false, BackColor = Theme.Raised, ForeColor = Theme.Text, Renderer = new DarkMenuRenderer(), Padding = new Padding(6,8,6,8), Font = Theme.Font() };
            muteItem = new ToolStripMenuItem("Выключить звук") { AutoSize = false, Size = new Size(236, 40) };
            void ApplyVolume() { shareVolume(shareMuted ? 0 : shareLevel); muteItem.Text = shareMuted ? "Включить звук" : "Выключить звук"; }
            muteItem.Click += (_, _) => { shareMuted = !shareMuted; ApplyVolume(); };
            audioMenu.Items.Add(muteItem);
            var panel = new BufferedPanel { BackColor = Theme.Raised, Size = new Size(236, 76) };
            var title = Theme.Label("Громкость · 100%", 9); title.Location = new Point(12, 8); panel.Controls.Add(title);
            volumeSlider = new ModernSlider { Value = 100, Bounds = new Rectangle(12, 34, 208, 32), BackColor = Theme.Raised, AccessibleName = "Громкость демонстрации" };
            volumeSlider.ValueChanged += (_, _) => { shareLevel = volumeSlider.Value; shareMuted = shareLevel == 0; title.Text = "Громкость · " + shareLevel + "%"; ApplyVolume(); };
            panel.Controls.Add(volumeSlider); audioMenu.Items.Add(new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty });
            ContextMenuStrip = audioMenu;
        }
    }
    public void SetName(string text, bool isMuted) { label = text; muted = isMuted; if (muted) speaking = false; initial = text.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(text).ToUpperInvariant() : "?"; AccessibleName = text + (muted ? ", микрофон выключен" : ""); Invalidate(); }
    public void SetSpeaking(bool active)
    {
        active = active && !muted && !share;
        if (speaking == active) return;
        speaking = active; AccessibleDescription = speaking ? "Говорит" : ""; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(g, ClientRectangle, Theme.Surface, Theme.Line);
        if (frame is not null) { var state = g.Save(); using var clip = Theme.Round(new RectangleF(1, 1, Width - 2, Height - 2), 12); g.SetClip(clip); float scale = Math.Min(Width / (float)frame.Width, Height / (float)frame.Height); int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale); g.DrawImage(frame, new Rectangle((Width - w) / 2, (Height - h) / 2, w, h)); g.Restore(state); }
        else { int d = Math.Min(128, Math.Min(Width, Height) / 2); var circle = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d); using var b = new SolidBrush(Theme.Raised); using var p = new Pen(speaking ? Theme.Text : Theme.Line, speaking ? 2 : 1); g.FillEllipse(b, circle); g.DrawEllipse(p, circle); using var f = Theme.Font(Math.Max(16, d * .24f)); TextRenderer.DrawText(g, initial, f, circle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
        using var font = Theme.Font(9); int tw = Math.Min(Width - 24, TextRenderer.MeasureText(label, font).Width + 44); if (share) tw = Math.Min(tw, Math.Max(1, Width - 84)); var pill = new Rectangle(12, Height - 43, tw, 31); Theme.Box(g, pill, Theme.Raised, Theme.Line, 8); Icons.Draw(g, share ? "screen" : "mic", new Rectangle(21, Height - 35, 15, 15), Theme.Muted); if (muted && !share) { using var pen = new Pen(Theme.Muted, 1.5f); g.DrawLine(pen, 21, Height - 35, 36, Height - 20); }
        TextRenderer.DrawText(g, label, font, new Rectangle(42, Height - 43, Math.Max(1, tw - 35), 31), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (speaking) { using var outline = Theme.Round(new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), 12); using var pen = new Pen(Theme.Text, 2); g.DrawPath(pen, outline); }
    }
    private void Expand() { if (fullscreen is not null) { fullscreen.Activate(); return; } fullscreen = new Form { Text = label, FormBorderStyle = FormBorderStyle.None, Bounds = Screen.FromControl(this).Bounds, StartPosition = FormStartPosition.Manual, BackColor = Color.Black, KeyPreview = true }; expanded = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = frame }; expanded.ContextMenuStrip = audioMenu; fullscreen.Controls.Add(expanded); var close = Theme.Button("Вернуться · Esc"); close.Location = new Point(20, 20); close.Click += (_, _) => fullscreen?.Close(); fullscreen.Controls.Add(close); close.BringToFront(); fullscreen.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) fullscreen?.Close(); }; fullscreen.FormClosed += (_, _) => { if (expanded is not null) expanded.Image = null; expanded = null; fullscreen = null; }; fullscreen.Show(FindForm()); }
    public void SetVideo(Bitmap bitmap) { Interlocked.Exchange(ref pending, bitmap)?.Dispose(); if (Interlocked.Exchange(ref scheduled, 1) != 0) return; if (IsDisposed || !IsHandleCreated) { Interlocked.Exchange(ref pending, null)?.Dispose(); Volatile.Write(ref scheduled, 0); return; } try { BeginInvoke(() => { Volatile.Write(ref scheduled, 0); var next = Interlocked.Exchange(ref pending, null); if (IsDisposed) { next?.Dispose(); return; } if (next is null) return; var old = frame; frame = next; if (expanded is not null) expanded.Image = next; old?.Dispose(); Invalidate(); }); } catch (InvalidOperationException) { Interlocked.Exchange(ref pending, null)?.Dispose(); Volatile.Write(ref scheduled, 0); } }
    public void ClearVideo() { var old = frame; frame = null; if (expanded is not null) expanded.Image = null; old?.Dispose(); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) { fullscreen?.Close(); audioMenu?.Dispose(); Interlocked.Exchange(ref pending, null)?.Dispose(); frame?.Dispose(); } base.Dispose(disposing); }
}
