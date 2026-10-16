#pragma warning disable WFO1000
namespace AudioLgbt;

internal sealed class UpdateWindow : Form
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private readonly Label heading = Theme.Label("Проверяем обновления", 17), detail = Theme.Label("Подключаемся к audio.lgbt…", 10, true);
    private readonly UpdateBar bar = new() { Bounds = new Rectangle(52, 231, 416, 6) };
    private readonly ModernButton proceed = Theme.Button("Продолжить"), retry = Theme.Button("Повторить");
    private readonly CancellationTokenSource lifetime = new();
    private readonly string[] args;
    private readonly AppUpdate updater;
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 30 };
    private bool installing;
    internal UpdateWindow(string[] args, AppUpdate? updater = null)
    {
        this.args = args; this.updater = updater ?? new(); Text = "audio.lgbt"; ClientSize = new Size(520, 360); BackColor = Theme.Background; ForeColor = Theme.Text;
        Font = Theme.Font(); FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        HandleCreated += (_, _) => { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); };
        using var resource = typeof(UpdateWindow).Assembly.GetManifestResourceStream("AudioLgbt.app.ico")!; Icon = new Icon(resource);
        var logo = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(228, 40, 64, 64) };
        var brand = Theme.Label("audio.lgbt", 15); brand.AutoSize = false; brand.TextAlign = ContentAlignment.MiddleCenter; brand.SetBounds(20, 112, 480, 34);
        heading.AutoSize = false; heading.TextAlign = ContentAlignment.MiddleCenter; heading.SetBounds(20, 164, 480, 35);
        detail.AutoSize = false; detail.TextAlign = ContentAlignment.MiddleCenter; detail.SetBounds(20, 253, 480, 30);
        proceed.AutoSize = retry.AutoSize = false; proceed.SetBounds(274, 300, 194, 42); retry.SetBounds(52, 300, 194, 42); retry.Visible = false;
        proceed.Click += (_, _) => { if (!installing) { lifetime.Cancel(); DialogResult = DialogResult.OK; Close(); } };
        retry.Click += async (_, _) => await Run();
        Controls.AddRange([logo, brand, heading, detail, bar, proceed, retry]);
        animation.Tick += (_, _) => bar.Animate(); animation.Start();
        Shown += async (_, _) => await Run();
        FormClosing += (_, e) => { if (installing && DialogResult != DialogResult.Abort) e.Cancel = true; else lifetime.Cancel(); };
        FormClosed += (_, _) => { animation.Dispose(); this.updater.Dispose(); lifetime.Dispose(); logo.Image?.Dispose(); };
    }
    private async Task Run()
    {
        retry.Visible = false; heading.Text = "Проверяем обновления"; detail.Text = "Подключаемся к audio.lgbt…"; bar.Fraction = null;
        try
        {
            var release = await updater.Check(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (release is null) { DialogResult = DialogResult.OK; Close(); return; }
            heading.Text = "Скачиваем обновление"; detail.Text = "Версия " + release.Version; bar.Fraction = 0;
            var progress = new Progress<UpdateProgress>(p => { if (IsDisposed || installing || lifetime.IsCancellationRequested) return; heading.Text = p.Stage; bar.Fraction = (double)p.Received / p.Total; detail.Text = $"{p.Received * 100 / p.Total}%  ·  {p.Received / 1048576d:0.0} / {p.Total / 1048576d:0.0} МБ  ·  {p.BytesPerSecond / 1048576d:0.0} МБ/с"; });
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "audio.lgbt", "updates");
            Directory.CreateDirectory(cache);
            foreach (string old in Directory.GetDirectories(cache)) { try { if (Guid.TryParseExact(Path.GetFileName(old), "N", out _) && Directory.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-2)) Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            string folder = Path.Combine(cache, Guid.NewGuid().ToString("N"));
            var installer = await updater.Download(release, folder, progress, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            installing = true; proceed.Enabled = false; heading.Text = "Устанавливаем обновление"; detail.Text = "audio.lgbt откроется автоматически"; bar.Fraction = null;
            await AppUpdate.StartInstaller(installer, release, args, Location, lifetime.Token);
            DialogResult = DialogResult.Abort; Close();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            if (IsDisposed) return;
            installing = false; proceed.Enabled = true; retry.Visible = true; heading.Text = "Не удалось обновиться";
            detail.Text = "Проверьте интернет или продолжите с текущей версией."; bar.Fraction = 0;
        }
    }
}
internal sealed class UpdateBar : Control
{
    private double? fraction;
    private double phase;
    public double? Fraction { get => fraction; set { fraction = value; Invalidate(); } }
    public UpdateBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    public void Animate() { phase = (phase + .016) % 1; if (fraction is null) Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Raised); using var brush = new SolidBrush(Theme.Text);
        if (fraction is double p) e.Graphics.FillRectangle(brush, 0, 0, (int)(Width * Math.Clamp(p, 0, 1)), Height);
        else e.Graphics.FillRectangle(brush, (int)((Width + Width / 3) * phase) - Width / 3, 0, Width / 3, Height);
    }
}
