using System.Runtime.InteropServices;
using System.Text;
using Concentus;
using Concentus.Enums;

namespace AudioLgbt;

internal sealed record ShareOptions(int Height = 1080, int Fps = 30, bool Motion = false, bool Audio = true)
{
    public int Bitrate => (int)((Height switch { 720 => 3000000, 1440 => 10000000, 2160 => 18000000, _ => 6000000 }) * (Fps == 60 ? 1.6 : 1));
}
internal sealed record CaptureSource(nint Handle, bool Window, string Name, uint ProcessId)
{
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle,int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle,int attribute,out int value,int size);
    public override string ToString() => Name;
    public static List<CaptureSource> List()
    {
        var result = new List<CaptureSource>();
        foreach (var screen in Screen.AllScreens)
        {
            var rect = new NativeCapture.Rect { Left = screen.Bounds.Left, Top = screen.Bounds.Top, Right = screen.Bounds.Right, Bottom = screen.Bounds.Bottom };
            result.Add(new(NativeCapture.MonitorFromRect(ref rect, 2), false, $"Экран {result.Count + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}", 0));
        }
        NativeCapture.EnumWindows((handle, _) =>
        {
            if (!NativeCapture.IsWindowVisible(handle) || NativeCapture.IsIconic(handle)) return true;
            NativeCapture.GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId) return true;
            if ((GetWindowLongPtr(handle,-20).ToInt64() & 0x80) != 0) return true;
            DwmGetWindowAttribute(handle,14,out int cloaked,4);if(cloaked!=0)return true;
            var title = new StringBuilder(512); NativeCapture.GetWindowText(handle, title, title.Capacity);
            if (title.Length > 0 && NativeCapture.GetWindow(handle, 4) == 0) result.Add(new(handle, true, title.ToString(), pid));
            return true;
        }, 0);
        return result;
    }
}
internal sealed class ScreenCapture : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private ShareOptions options;
    private int keyframe = 1;
    public CaptureSource Source { get; }
    public bool AudioOn { get; private set; }
    public event Action<string, byte[], int>? Encoded;
    public event Action<string>? Error;
    public event Action<bool>? AudioChanged;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public ScreenCapture(CaptureSource source, ShareOptions options)
    {
        Source = source; this.options = options;
        worker = Task.Run(Run);
    }
    public void Update(ShareOptions next) => Volatile.Write(ref options, next);
    public void ForceKeyframe() => Interlocked.Exchange(ref keyframe, 1);
    private async Task Run()
    {
        nint capture = 0; VideoEncoder? encoder = null; LoopbackAudio? audio = null;
        try
        {
            Marshal.ThrowExceptionForHR(NativeCapture.capture_start(Source.Window ? 1 : 0, Source.Handle, out capture));
            byte[] pixels = new byte[1920 * 1080 * 4];
            ShareOptions? applied = null; int encodedWidth = 0, encodedHeight = 0;
            var last = System.Diagnostics.Stopwatch.GetTimestamp();
            bool audioAttempted = false;
            while (!lifetime.IsCancellationRequested)
            {
                var config = Volatile.Read(ref options);
                if (config.Audio && !audioAttempted)
                {
                    audioAttempted = true;
                    try
                    {
                        audio = new(Source.Window ? Source.ProcessId : (uint)Environment.ProcessId, !Source.Window);
                        audio.Encoded += data => Encoded?.Invoke("screen-audio", data, 20000);
                        audio.Error += message => { AudioOn = false; AudioChanged?.Invoke(false); Error?.Invoke(message); };
                        await audio.Ready; AudioOn = true; AudioChanged?.Invoke(true);
                    }
                    catch { if (audio is not null) await audio.DisposeAsync(); audio = null; AudioChanged?.Invoke(false); Error?.Invoke("Показ идёт без звука: не удалось захватить звук источника."); }
                }
                else if (!config.Audio && audioAttempted)
                {
                    if (audio is not null) await audio.DisposeAsync(); audio = null; audioAttempted = false; AudioOn = false; AudioChanged?.Invoke(false);
                }
                var tick = System.Diagnostics.Stopwatch.GetTimestamp();

                if (Volatile.Read(ref keyframe) != 0 || applied != config) NativeCapture.capture_repeat(capture);
                var result = NativeCapture.capture_grab(capture, pixels, pixels.Length, out int width, out int height);
                if (result == unchecked((int)0x8007007A))
                {
                    if (width <= 0 || height <= 0 || (long)width * height > 4096L * 4096) throw new InvalidOperationException("Источник слишком большой.");
                    pixels = new byte[checked(width * height * 4)]; continue;
                }
                Marshal.ThrowExceptionForHR(result);
                if (result == 0)
                {
                    var scale = Math.Min(1d, Math.Min(config.Height / (double)height, Math.Round(config.Height * 16d / 9) / width));
                    int w = Math.Max(2, (int)(width * scale) / 2 * 2), h = Math.Max(2, (int)(height * scale) / 2 * 2);
                    if (encoder is null || applied != config || encodedWidth != w || encodedHeight != h)
                    {
                        var next = new VideoEncoder(w, h, config.Fps, true, config.Bitrate, config.Motion);
                        encoder?.Dispose(); encoder = next; encodedWidth = w; encodedHeight = h; applied = config;
                    }
                    if (Interlocked.Exchange(ref keyframe, 0) != 0) encoder.Keyframe = true;
                    var packet = encoder.Encode(pixels, width, height);
                    if (packet is not null)
                    {
                        var duration = (int)Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(last, tick).TotalMicroseconds, 1000, 1000000);
                        last = tick; Encoded?.Invoke("screen", packet, duration); ready.TrySetResult();
                    }
                }
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                await Task.Delay(Math.Max(1, (int)(1000d / config.Fps - elapsed)), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception e) { ready.TrySetException(e); Error?.Invoke("Показ остановлен. Проверьте источник или уменьшите качество."); }
        finally { encoder?.Dispose(); if (audio is not null) await audio.DisposeAsync(); if (capture != 0) NativeCapture.capture_close(capture); AudioChanged?.Invoke(false); }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await worker; lifetime.Dispose(); }
}
internal sealed class LoopbackAudio : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public event Action<byte[]>? Encoded;
    public event Action<string>? Error;
    public LoopbackAudio(uint pid, bool exclude) { worker = Task.Run(() => Run(pid, exclude)); }
    private async Task Run(uint pid, bool exclude)
    {
        nint handle = 0;
        try
        {
            Marshal.ThrowExceptionForHR(NativeCapture.loopback_start(pid, exclude ? 1 : 0, out handle));
            var encoder = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = 192000; encoder.Complexity = 4; encoder.UseVBR = true;
            var buffer = new byte[48000 * 4]; var pcm = new byte[960 * 4]; int filled = 0; var encoded = new byte[4096];
            ready.TrySetResult();
            while (!lifetime.IsCancellationRequested)
            {
                int result = NativeCapture.loopback_read(handle, buffer, buffer.Length, out int bytes); Marshal.ThrowExceptionForHR(result);
                for (int offset = 0; offset < bytes;)
                {
                    int count = Math.Min(pcm.Length - filled, bytes - offset); Buffer.BlockCopy(buffer, offset, pcm, filled, count); filled += count; offset += count;
                    if (filled == pcm.Length) { int length = encoder.Encode(MemoryMarshal.Cast<byte, short>(pcm.AsSpan()), 960, encoded, encoded.Length); Encoded?.Invoke(encoded.AsSpan(0, length).ToArray()); filled = 0; }
                }
                if (result == 1) await Task.Delay(5, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception e) { ready.TrySetException(e); Error?.Invoke("Передача звука демонстрации остановилась."); }
        finally { if (handle != 0) NativeCapture.loopback_close(handle); }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await worker; lifetime.Dispose(); }
}
internal static class NativeCapture
{
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int capture_start(int window, nint handle, out nint capture);
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int capture_grab(nint capture, [Out] byte[] pixels, int capacity, out int width, out int height);
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void capture_close(nint capture);
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void capture_repeat(nint capture);
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int loopback_start(uint pid, int exclude, out nint capture);
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int loopback_read(nint capture, [Out] byte[] data, int capacity, out int bytes);
    [DllImport("audio-capture.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void loopback_close(nint capture);
    internal delegate bool WindowCallback(nint handle, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint handle, StringBuilder title, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint handle, uint command);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern nint MonitorFromRect(ref Rect rect, uint flags);
}
