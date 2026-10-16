using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AudioLgbt;
internal sealed class VideoDevices : IAsyncDisposable
{
    private MediaCapture? camera; private MediaFrameReader? reader; private VideoEncoder? cameraEncoder;
    private readonly object cameraGate = new(); private int frameBusy; private long lastFrame;
    private ScreenCapture? sharing;
    private readonly SemaphoreSlim operations = new(1, 1);
    private bool disposed;
    public bool CameraOn => reader is not null;
    public bool ScreenOn => sharing is not null;
    public bool ScreenAudioOn => sharing?.AudioOn == true;
    public CaptureSource? Source => sharing?.Source;
    public event Action<bool>? ScreenAudioChanged;
    public event Action<string, byte[], int>? Encoded;
    public event Action<string>? Error;
    public static async Task<List<DeviceChoice>> Cameras() { var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture); return devices.Select(d => new DeviceChoice(d.Id, d.Name)).ToList(); }
    public async Task SetCamera(string id, bool enabled)
    {
      await operations.WaitAsync();
      try {
        if (disposed) return;
        if (!enabled) { await StopCamera(); return; }
        var next = new MediaCapture(); MediaFrameReader? nextReader = null; VideoEncoder? nextEncoder = null;
        try
        {
            await next.InitializeAsync(new MediaCaptureInitializationSettings { VideoDeviceId = id, StreamingCaptureMode = StreamingCaptureMode.Video, MemoryPreference = MediaCaptureMemoryPreference.Cpu, SharingMode = MediaCaptureSharingMode.SharedReadOnly });
            var source = next.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) ?? throw new InvalidOperationException("Камера недоступна.");
            nextEncoder = new VideoEncoder(640, 360, 15, false);
            nextReader = await next.CreateFrameReaderAsync(source, "Bgra8"); nextReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime; nextReader.FrameArrived += CameraFrame;
            if (await nextReader.StartAsync() != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException("Не удалось включить камеру.");
            if (disposed) throw new ObjectDisposedException(nameof(VideoDevices));
            await StopCamera();
            lock (cameraGate) { camera = next; reader = nextReader; cameraEncoder = nextEncoder; }
        }
        catch { if (nextReader is not null) { await nextReader.StopAsync(); nextReader.Dispose(); } nextEncoder?.Dispose(); next.Dispose(); throw; }
      } finally { operations.Release(); }
    }
    private void CameraFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.Exchange(ref frameBusy, 1) != 0) return;
        try
        {
            var now = Environment.TickCount64; if (now - lastFrame < 65) return; lastFrame = now;
            using var reference = sender.TryAcquireLatestFrame(); var source = reference?.VideoMediaFrame?.SoftwareBitmap; if (source is null) return;
            using var bitmap = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4)); bitmap.CopyToBuffer(buffer);
            byte[]? packet; lock (cameraGate) { if (sender != reader || disposed) return; packet = cameraEncoder?.Encode(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight); }
            if (packet is not null) Encoded?.Invoke("cam", packet, 66667);
        }
        catch (Exception) { if (!disposed) Error?.Invoke("Камера перестала передавать изображение. Выберите её снова в настройках."); }
        finally { Volatile.Write(ref frameBusy, 0); }
    }
    private async Task StopCamera()
    {
        MediaFrameReader? oldReader; MediaCapture? old; VideoEncoder? encoder;
        lock (cameraGate) { oldReader = reader; old = camera; encoder = cameraEncoder; reader = null; camera = null; cameraEncoder = null; }
        if (oldReader is not null) { oldReader.FrameArrived -= CameraFrame; await oldReader.StopAsync(); oldReader.Dispose(); } encoder?.Dispose(); old?.Dispose();
    }
    public void ForceKeyframe(string source) { lock (cameraGate) { if (source == "cam" && cameraEncoder is not null) cameraEncoder.Keyframe = true; if (source == "screen") sharing?.ForceKeyframe(); } }
    public void UpdateScreen(ShareOptions options) => sharing?.Update(options);
    public async Task ShareScreen(CaptureSource source, ShareOptions options)
    {
        await operations.WaitAsync();
        try {
            if (disposed) return;
            var next = new ScreenCapture(source, options);
            try { await next.Ready.WaitAsync(TimeSpan.FromSeconds(15)); if (disposed) throw new ObjectDisposedException(nameof(VideoDevices)); }
            catch { await next.DisposeAsync(); throw; }
            var old = sharing; sharing = next;
            next.Encoded += (src, data, duration) => { if (sharing == next && !disposed) Encoded?.Invoke(src, data, duration); };
            next.Error += message => { if (sharing == next && !disposed) Error?.Invoke(message); };
            next.AudioChanged += enabled => { if (sharing == next && !disposed) ScreenAudioChanged?.Invoke(enabled); };
            next.ForceKeyframe(); ScreenAudioChanged?.Invoke(next.AudioOn);
            if (old is not null) await old.DisposeAsync();
        } finally { operations.Release(); }
    }
    public async Task StopScreen() { await operations.WaitAsync(); try { var old = sharing; sharing = null; if (old is not null) await old.DisposeAsync(); ScreenAudioChanged?.Invoke(false); } finally { operations.Release(); } }
    public async ValueTask DisposeAsync() { disposed = true; await operations.WaitAsync(); try { await StopCamera(); var old = sharing; sharing = null; if (old is not null) await old.DisposeAsync(); } finally { operations.Release(); } }
}
