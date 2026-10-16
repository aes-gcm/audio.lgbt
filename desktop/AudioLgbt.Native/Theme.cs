#pragma warning disable WFO1000 // These internal controls are constructed in code, never serialized by the designer.
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
namespace AudioLgbt;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(16, 16, 16), Surface = Color.FromArgb(23, 23, 23), Raised = Color.FromArgb(32, 32, 32), Hover = Color.FromArgb(41, 41, 41), Text = Color.FromArgb(244, 244, 244), Muted = Color.FromArgb(162, 162, 162), Line = Color.FromArgb(48, 48, 48), Danger = Color.FromArgb(196, 55, 50);
    [DllImport("gdi32.dll")] private static extern nint AddFontMemResourceEx(nint font, uint length, nint reserved, ref uint count);
    private static readonly PrivateFontCollection fonts = new();
    private static readonly List<nint> fontMemory = [];
    static Theme() { foreach (var resource in typeof(Theme).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf"))) { using var stream = typeof(Theme).Assembly.GetManifestResourceStream(resource)!; var bytes = new byte[stream.Length]; stream.ReadExactly(bytes); var ptr = Marshal.AllocCoTaskMem(bytes.Length); Marshal.Copy(bytes, 0, ptr, bytes.Length); fonts.AddMemoryFont(ptr, bytes.Length); uint count = 0; AddFontMemResourceEx(ptr, (uint)bytes.Length, 0, ref count); fontMemory.Add(ptr); } }
    public static Font Font(float size = 10.5f, bool bold = false) => new(fonts.Families.FirstOrDefault() ?? new FontFamily("Segoe UI"), size, bold ? FontStyle.Bold : FontStyle.Regular);
    public static GraphicsPath Round(RectangleF r, float radius) { var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height)); if (d <= 0) return p; p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
    public static void Box(Graphics g, Rectangle r, Color fill, Color border, int radius = 12) { if (r.Width < 2 || r.Height < 2) return; using var path = Round(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), radius); using var b = new SolidBrush(fill); using var p = new Pen(border); g.FillPath(b, path); g.DrawPath(p, path); }
    public static Label Label(string text, float size = 10.5f, bool muted = false) => new() { Text = text, ForeColor = muted ? Muted : Text, Font = Font(size), AutoSize = true, Margin = new Padding(0, 0, 0, 9) };
    public static ModernButton Button(string text, bool primary = false) => new() { Text = text, Primary = primary };
    public static ModernInput Input(string placeholder = "") => new() { PlaceholderText = placeholder, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 16) };
    public static ModernSelect Select() => new() { Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 18) };
}
internal class BufferedPanel : Panel
{
    public BufferedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
}
internal class BufferedFlowPanel : FlowLayoutPanel
{
    public BufferedFlowPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
}
internal class RoundedPanel : BufferedPanel
{
    public int Radius { get; set; } = 16;
    public Color Fill { get => BackColor; set => BackColor = value; }
    public RoundedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = Theme.Surface; Padding = new Padding(16); }
    protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Parent?.BackColor ?? Theme.Background); }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(e.Graphics, ClientRectangle, Fill, Theme.Line, Radius); base.OnPaint(e); }
}
internal sealed class ModernButton : Button
{
    public bool Primary { get; set; }
    public string Glyph { get; set; } = ""; public bool Toolbar { get; set; }
    public bool Active { get; set; }
    private bool hover, down;
    public ModernButton() { SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = Theme.Surface; ForeColor = Theme.Text; Font = Theme.Font(); AutoSize = true; MinimumSize = new Size(44, 44); Padding = new Padding(18, 8, 18, 8); Cursor = Cursors.Hand; Margin = new Padding(0, 0, 8, 0); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
    public override Size GetPreferredSize(Size proposedSize) => Toolbar ? new Size(72, 76) : new Size(Math.Max(MinimumSize.Width, TextRenderer.MeasureText(Text, Font).Width + Padding.Horizontal + (Glyph.Length > 0 ? 24 : 0)), 44);
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using var path = Theme.Round(ClientRectangle, 12);
        var old = Region; Region = new Region(path); old?.Dispose();
    }
    internal (Rectangle Icon, Rectangle Label) ContentBounds()
    {
        if (Toolbar) return (new Rectangle((Width - 22) / 2, 13, 22, 22), new Rectangle(0, 51, Width, 23));
        if (Glyph.Length == 0) return (Rectangle.Empty, new Rectangle(8, 0, Width - 16, Height));
        if (Text.Length == 0) return (new Rectangle((Width - 22) / 2, (Height - 22) / 2, 22, 22), Rectangle.Empty);
        int textWidth = Math.Min(Math.Max(1, Width - 46), TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        int left = (Width - 22 - 8 - textWidth) / 2;
        return (new Rectangle(left, (Height - 22) / 2, 22, 22), new Rectangle(left + 30, 0, textWidth, Height));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
        bool white = Primary || Active || (Toolbar && (Glyph == "mic" && Text.Contains("выкл") || Glyph == "camera" && Text == "Камера" || Glyph == "screen" && Text == "Демонстрация…"));
        var fill = BackColor == Theme.Danger ? Theme.Danger : white ? Theme.Text : hover ? Theme.Hover : Theme.Raised;
        if (down) fill = white ? Color.FromArgb(210, 210, 210) : Theme.Line;
        var ink = !Enabled ? Theme.Muted : white ? Theme.Background : Theme.Text;
        var box = Toolbar ? new Rectangle((Width - 46) / 2, 2, 46, 44) : ClientRectangle;
        Theme.Box(g, box, fill, Focused ? Theme.Muted : fill, Toolbar ? 10 : 12);
        var content = ContentBounds();
        if (Glyph.Length > 0) Icons.Draw(g, Glyph, content.Icon, ink);
        string label = Toolbar ? Glyph switch { "mic" => "Микрофон", "camera" => "Камера", "screen" => "Экран", "hand" => "Рука", "chat" => "Чат", "leave" => "Выйти", _ => Text } : Text;
        using var font = Toolbar ? Theme.Font(9) : null;
        if (label.Length > 0) TextRenderer.DrawText(g, label, font ?? Font, content.Label, Toolbar ? Theme.Muted : ink, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Toolbar && (Text.Contains("выкл") || Text == "Камера")) { using var p = new Pen(ink, 1.6f); g.DrawLine(p, box.Left + 12, box.Top + 12, box.Right - 12, box.Bottom - 12); }
    }
}
internal sealed class ModernInput : UserControl
{
    private readonly HintTextBox editor = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Raised, ForeColor = Theme.Text };
    public string PlaceholderText { get => editor.Hint; set => editor.Hint = value; }
    public bool ReadOnly { get => editor.ReadOnly; set => editor.ReadOnly = value; }
    public bool UseSystemPasswordChar { get => editor.UseSystemPasswordChar; set => editor.UseSystemPasswordChar = value; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => editor.Text; set => editor.Text = value; }
    public ModernInput() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); BackColor = Theme.Background; Height = 46; MinimumSize = new Size(60, 46); Font = Theme.Font(10.5f); editor.Font = Font; editor.AutoSize = false; Controls.Add(editor); editor.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty); editor.GotFocus += (_, _) => Invalidate(); editor.LostFocus += (_, _) => Invalidate(); editor.KeyDown += (_, e) => OnKeyDown(e); Enter += (_, _) => editor.Focus(); AccessibleRole = AccessibleRole.Text; }
    public void Clear() => editor.Clear();
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using var path = Theme.Round(ClientRectangle, 12);
        var old = Region; Region = new Region(path); old?.Dispose();
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if (editor is not null) editor.SetBounds(14, (Height - editor.Font.Height) / 2 + 1, Math.Max(1, Width - 28), editor.Font.Height + 2); }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(e.Graphics, ClientRectangle, Theme.Raised, ContainsFocus ? Theme.Muted : Theme.Line); editor.AccessibleName = AccessibleName; }
    protected override void OnClick(EventArgs e) { editor.Focus(); base.OnClick(e); }
}
internal sealed class HintTextBox : TextBox
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, string text);
    private string hint = "";
    public string Hint { get => hint; set { hint = value; ApplyHint(); } }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyHint(); }
    private void ApplyHint() { if (IsHandleCreated) SendMessage(Handle, 0x1501, 1, hint); }
}
internal sealed class ModernSelect : Control
{
    public List<object> Items { get; } = []; private int selected = -1;
    private ContextMenuStrip? dropdown;
    public int SelectedIndex { get => selected; set { selected = value >= 0 && value < Items.Count ? value : -1; Invalidate(); } }
    public object? SelectedItem => selected >= 0 && selected < Items.Count ? Items[selected] : null; public event EventHandler? SelectionChangeCommitted;
    public ModernSelect() { SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Height = 44; MinimumSize = new Size(80, 44); Font = Theme.Font(); BackColor = Theme.Background; ForeColor = Theme.Text; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.ComboBox; }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(e.Graphics, ClientRectangle, Theme.Raised, Focused ? Theme.Muted : Theme.Line); TextRenderer.DrawText(e.Graphics, SelectedItem?.ToString() ?? "Нет устройств", Font, new Rectangle(12, 0, Width - 44, Height), Enabled ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); Icons.Draw(e.Graphics, "chevron", new Rectangle(Width - 28, (Height - 16) / 2, 16, 16), Theme.Muted); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Open(); }
    internal void Open()
    {
        if (Items.Count == 0 || IsDisposed) return;
        if (dropdown?.Visible == true) { dropdown.Close(); return; }
        dropdown ??= new RoundedMenuStrip { ShowImageMargin = false, BackColor = Theme.Raised, ForeColor = Theme.Text, Renderer = new DarkMenuRenderer(), Padding = new Padding(4, 8, 4, 8) };

        foreach (ToolStripItem old in dropdown.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        dropdown.Items.Clear();
        dropdown.Font = Font;
        dropdown.AutoSize = false;
        dropdown.Width = Math.Max(Width, 180);
        dropdown.Height = Math.Min(440, Items.Count * 40 + 16);
        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(Items[i].ToString()) { AutoSize = false, Width = dropdown.Width - 8, Height = 40, Checked = i == selected };
            item.Click += (_, _) => Commit(index);
            dropdown.Items.Add(item);
        }
        dropdown.Show(this, new Point(0, Height + 4));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && dropdown is { } menu)
        {
            dropdown = null;
            menu.Close();

            if (SynchronizationContext.Current is { } context) context.Post(_ => menu.Dispose(), null);
            else menu.Dispose();
        }
        base.Dispose(disposing);
    }
    private void Commit(int value) { if (Items.Count == 0) return; SelectedIndex = value; SelectionChangeCommitted?.Invoke(this, EventArgs.Empty); }
    protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) { Open(); e.Handled = true; } else if (e.KeyCode is Keys.Down or Keys.Up) { Commit(Math.Clamp(selected + (e.KeyCode == Keys.Down ? 1 : -1), 0, Math.Max(0, Items.Count - 1))); e.Handled = true; } base.OnKeyDown(e); }
}
internal sealed class RoundedMenuStrip : ContextMenuStrip
{
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using var outline = Theme.Round(ClientRectangle, 10);
        var old = Region; Region = new Region(outline); old?.Dispose();
    }
}
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) { using var b = new SolidBrush(e.Item.Selected ? Theme.Hover : Theme.Raised); e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size)); }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool chosen = e.Item is ToolStripMenuItem { Checked: true };
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, new Rectangle(12, 0, Math.Max(1, e.Item.Width - (chosen ? 48 : 24)), e.Item.Height), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (chosen) Icons.Draw(e.Graphics, "check", new Rectangle(e.Item.Width - 30, (e.Item.Height - 18) / 2, 18, 18), Theme.Text);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using var p = new Pen(Theme.Line); using var outline = Theme.Round(new RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),10); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.DrawPath(p, outline); }
}
internal sealed class ModernSlider : Control
{
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100; private int value;
    public int Value { get => value; set { int next = Math.Clamp(value, Minimum, Maximum); if (this.value == next) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? ValueChanged;
    public ModernSlider() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); Height = 32; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.Slider; }
    protected override void OnPaint(PaintEventArgs e) { var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; float x = 9 + (Width - 18) * (Value - Minimum) / (float)Math.Max(1, Maximum - Minimum); using var track = new Pen(Theme.Line, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }; using var fill = new Pen(Theme.Text, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(track, 9, Height / 2, Width - 9, Height / 2); g.DrawLine(fill, 9, Height / 2, x, Height / 2); g.FillEllipse(Brushes.White, x - 6, Height / 2 - 6, 12, 12); if (Focused) { using var p = new Pen(Theme.Muted); g.DrawEllipse(p, x - 9, Height / 2 - 9, 18, 18); } }
    private void Set(int x) => Value = Minimum + (int)Math.Round(Math.Clamp((x - 9d) / Math.Max(1, Width - 18), 0, 1) * (Maximum - Minimum));
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Capture = true; Set(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture) Set(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k); protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left) Value -= 5; if (e.KeyCode == Keys.Right) Value += 5; base.OnKeyDown(e); }
}
internal sealed class ModernToggle : CheckBox
{
    public ModernToggle() { SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); AutoSize = false; Height = 40; Width = 260; Font = Theme.Font(); ForeColor = Theme.Text; Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e) { var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Background); g.SmoothingMode = SmoothingMode.AntiAlias; Theme.Box(g, new Rectangle(Width - 42, (Height - 24) / 2, 42, 24), Checked ? Theme.Text : Theme.Line, Focused ? Theme.Muted : Theme.Line, 12); using var b = new SolidBrush(Checked ? Theme.Background : Theme.Muted); g.FillEllipse(b, Width - 42 + (Checked ? 21 : 3), (Height - 18) / 2, 18, 18); TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - 54, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); }
}
internal static class Icons
{
    public static void Draw(Graphics g, string name, Rectangle r, Color color)
    {
        var state = g.Save(); g.TranslateTransform(r.X, r.Y); g.ScaleTransform(r.Width / 24f, r.Height / 24f); using var p = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; switch (name)
        {
            case "mic": using (var path = Theme.Round(new RectangleF(9, 3, 6, 12), 3)) g.DrawPath(p, path); g.DrawArc(p, 6, 6, 12, 12, 0, 180); g.DrawLine(p, 12, 18, 12, 21); g.DrawLine(p, 9, 21, 15, 21); break;
            case "camera": using (var path = Theme.Round(new RectangleF(3, 6, 12, 12), 2)) g.DrawPath(p, path); g.DrawLines(p, [new(15, 10), new(21, 7), new(21, 17), new(15, 14)]); break;
            case "fullscreen": g.DrawLines(p, [new(9,3),new(3,3),new(3,9)]); g.DrawLines(p,[new(15,3),new(21,3),new(21,9)]); g.DrawLines(p,[new(3,15),new(3,21),new(9,21)]);g.DrawLines(p,[new(15,21),new(21,21),new(21,15)]); break;
            case "screen": g.DrawRectangle(p, 3, 4, 18, 13); g.DrawLine(p, 12, 17, 12, 21); g.DrawLine(p, 8, 21, 16, 21); if (name == "screen") { g.DrawLine(p, 12, 14, 12, 8); g.DrawLines(p, [new(9, 11), new(12, 8), new(15, 11)]); } break;
            case "chat": using (var path = Theme.Round(new RectangleF(3, 4, 18, 14), 3)) g.DrawPath(p, path); g.DrawLines(p, [new(7, 18), new(7, 21), new(11, 18)]); g.DrawLine(p, 7, 9, 17, 9); g.DrawLine(p, 7, 13, 14, 13); break;
            case "leave": g.DrawArc(p, 3, 7, 18, 15, 205, 130); g.DrawLines(p, [new(3, 14), new(6, 15), new(8, 11)]); g.DrawLines(p, [new(21, 14), new(18, 15), new(16, 11)]); break;
            case "hand": g.DrawLines(p, [new(5, 13), new(5, 5), new(8, 5), new(8, 12), new(8, 3), new(11, 3), new(11, 12), new(11, 4), new(14, 4), new(14, 13), new(14, 7), new(17, 7), new(17, 14), new(19, 12), new(21, 14), new(17, 20), new(9, 21), new(5, 17), new(3, 12), new(5, 11)]); break;
            case "people": g.DrawEllipse(p, 5, 3, 7, 7); g.DrawArc(p, 2, 12, 13, 12, 180, 180); g.DrawArc(p, 12, 3, 7, 7, 260, 200); g.DrawArc(p, 12, 12, 10, 12, 260, 100); break;
            case "settings":
                using (var gear = new GraphicsPath())
                {
                    var points = new PointF[32];
                    for (int i = 0; i < points.Length; i++)
                    {
                        double angle = i * Math.PI / 16 - Math.PI / 2;
                        float radius = i % 4 is 0 or 3 ? 8 : 10.5f;
                        points[i] = new PointF(12 + radius * (float)Math.Cos(angle), 12 + radius * (float)Math.Sin(angle));
                    }
                    gear.AddPolygon(points); g.DrawPath(p, gear);
                }
                g.DrawEllipse(p, 8.5f, 8.5f, 7, 7); break;
            case "lock": g.DrawArc(p, 8, 2, 8, 12, 180, 180); using (var path = Theme.Round(new RectangleF(5, 10, 14, 11), 2)) g.DrawPath(p, path); break;
            case "edit": g.DrawLines(p,[new(5,16),new(16,5),new(20,9),new(9,20),new(4,21),new(5,16)]);g.DrawLine(p,14,7,18,11);break;
            case "close": g.DrawLine(p, 6, 6, 18, 18); g.DrawLine(p, 18, 6, 6, 18); break;
            case "chevron": g.DrawLines(p, [new(6, 9), new(12, 15), new(18, 9)]); break;
            case "check": g.DrawLines(p, [new(5, 12), new(10, 17), new(20, 6)]); break;
            case "plus": g.DrawLine(p, 12, 5, 12, 19); g.DrawLine(p, 5, 12, 19, 12); break;
        }
        g.Restore(state);
    }
}
internal sealed class Brand : Control
{
    public Brand() { DoubleBuffered = true; Size = new Size(190, 40); AccessibleName = "audio.lgbt"; ForeColor = Theme.Text; }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var pen = new Pen(ForeColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round }; int[] x = [3,9,15,21], top = [10,5,2,8], bottom = [14,19,22,16]; for (int i = 0; i < 4; i++) e.Graphics.DrawLine(pen,x[i],(Height-24)/2+top[i],x[i],(Height-24)/2+bottom[i]); using var font = Theme.Font(14, true); TextRenderer.DrawText(e.Graphics, "audio.lgbt", font, new Rectangle(36, 0, Width - 36, Height), ForeColor, TextFormatFlags.VerticalCenter); }
}
