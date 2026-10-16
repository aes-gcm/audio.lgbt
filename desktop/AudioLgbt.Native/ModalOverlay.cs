using System.Drawing.Drawing2D;

namespace AudioLgbt;

internal sealed class ModalOverlay : Panel
{
    private readonly Control background;
    private readonly RoundedPanel card;
    private readonly Size preferred;
    private Bitmap? backdrop;
    private bool prepared;
    internal Control Content { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? CanClose { get; set; }
    internal event Action? CloseRequested;

    internal ModalOverlay(Control background, string title, Control content, Size preferred)
    {
        this.background = background;
        this.preferred = preferred;
        Content = content;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Visible = false;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        TabStop = true;
        card = new RoundedPanel { Padding = new Padding(24), Fill = Theme.Background, Radius = 16 };
        card.SizeChanged += (_, _) =>
        {
            using var outline = Theme.Round(card.ClientRectangle, 16);
            var old = card.Region; card.Region = new Region(outline); old?.Dispose();
        };
        var header = new Panel { Dock = DockStyle.Top, Height = 56 };
        var label = Theme.Label(title, 17);
        label.Location = new Point(0, 7);
        header.Controls.Add(label);
        var close = Theme.Button("");
        close.Glyph = "close";
        close.AccessibleName = "Закрыть";
        close.AutoSize = false;
        close.Size = new Size(40, 40);
        close.MinimumSize = new Size(40, 40);
        close.Click += (_, _) => RequestClose();
        header.Controls.Add(close);
        header.Resize += (_, _) => close.Location = new Point(header.Width - close.Width, 0);
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        card.Controls.Add(header);
        Controls.Add(card);
        Resize += (_, _) => { Arrange(); if (prepared) CaptureBackdrop(); };
        MouseClick += (_, _) => RequestClose();
    }

    internal void RequestClose()
    {
        if (CanClose?.Invoke() == false) return;
        CloseRequested?.Invoke();
    }

    internal void ActivateContent()
    {
        Arrange();
        CaptureBackdrop();
        prepared = true;
        Visible = true;
        BringToFront();
        card.SelectNextControl(null, true, true, true, true);
    }

    private void Arrange()
    {
        int margin = Width < 850 ? 16 : 32;
        int w = Math.Max(1, Math.Min(preferred.Width, Width - margin * 2));
        int h = Math.Max(1, Math.Min(preferred.Height, Height - margin * 2));
        card.SetBounds((Width - w) / 2, (Height - h) / 2, w, h);
    }

    private void CaptureBackdrop()
    {
        if (Width <= 0 || Height <= 0 || background.IsDisposed) return;
        var bitmap = new Bitmap(Width, Height);
        try
        {
            background.DrawToBitmap(bitmap, new Rectangle(Point.Empty, background.Size));
            using var g = Graphics.FromImage(bitmap);
            using var dim = new SolidBrush(Color.FromArgb(178, 0, 0, 0));
            g.FillRectangle(dim, 0, 0, Width, Height);
            var old = backdrop;
            backdrop = bitmap;
            old?.Dispose();
            Invalidate();
        }
        catch { bitmap.Dispose(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Background);
        if (backdrop is not null) e.Graphics.DrawImageUnscaled(backdrop, 0, 0);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { RequestClose(); return true; }
        if (keyData is Keys.Tab or (Keys.Tab | Keys.Shift))
        {
            static Control? FocusedChild(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    if (child.Focused) return child;
                    if (child.ContainsFocus) return FocusedChild(child);
                }
                return null;
            }
            var active = FocusedChild(card);
            card.SelectNextControl(active, keyData == Keys.Tab, true, true, true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { backdrop?.Dispose(); backdrop = null; }
        base.Dispose(disposing);
    }
}
