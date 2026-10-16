namespace AudioLgbt;

internal sealed class ScrollViewport : BufferedPanel
{
    public Panel Content { get; } = new BufferedPanel { AutoSize = false };
    private int offset, dragStart = -1, dragOffset;
    private bool arranging;
    public ScrollViewport() { Controls.Add(Content); Content.ControlAdded += (_, e) => { if (e.Control is not null) { e.Control.SizeChanged += (_, _) => Arrange(); Watch(e.Control); } Arrange(); }; Content.Layout += (_, _) => Arrange(); Resize += (_, _) => Arrange(); }
    private void Watch(Control c) { c.MouseWheel += Wheel; c.Enter += (_, _) => Reveal(c); c.ControlAdded += (_, e) => { if (e.Control is not null) Watch(e.Control); }; foreach (Control child in c.Controls) Watch(child); }
    private void Reveal(Control c) { if (!c.IsHandleCreated || !IsHandleCreated) return; if (!c.ContainsFocus) return; int y = PointToClient(c.PointToScreen(Point.Empty)).Y; if (c.Top < 80) SetOffset(0); else if (y < 0) SetOffset(offset + y - 8); else if (y + c.Height > Height) SetOffset(offset + y + c.Height - Height + 8); }
    private void Wheel(object? s, MouseEventArgs e) { SetOffset(offset - e.Delta / 3); if (e is HandledMouseEventArgs h) h.Handled = true; }
    protected override void OnMouseWheel(MouseEventArgs e) { Wheel(this, e); base.OnMouseWheel(e); }
    private void Arrange()
    {
        if (arranging || IsDisposed) return;
        arranging = true;
        try
        {
            Content.Width = Math.Max(1, ClientSize.Width - 14);
            Content.PerformLayout();
            int height = 0;
            foreach (Control child in Content.Controls) height = Math.Max(height, child.Bottom + child.Margin.Bottom);
            Content.Height = Math.Max(ClientSize.Height, height);
            SetOffset(offset);
            Content.Invalidate(true);
        }
        finally { arranging = false; }
    }
    private void SetOffset(int y) { offset = Math.Clamp(y, 0, Math.Max(0, Content.Height - Height)); Content.Top = -offset; Invalidate(); }
    private Rectangle Thumb() { int h = Math.Max(32, Height * Height / Math.Max(1, Content.Height)); return new Rectangle(Width - 6, (Height - h) * offset / Math.Max(1, Content.Height - Height), 4, h); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if (Content.Height <= Height) return; var r = Thumb(); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; Theme.Box(e.Graphics, r, Theme.Line, Theme.Line, 2); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.X >= Width - 14) { dragStart = e.Y; dragOffset = offset; Capture = true; if (!Thumb().Contains(e.Location)) SetOffset((int)((e.Y / (double)Height) * Content.Height) - Height / 2); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragStart >= 0) SetOffset(dragOffset + (e.Y - dragStart) * Content.Height / Math.Max(1, Height)); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { dragStart = -1; Capture = false; base.OnMouseUp(e); }
}
