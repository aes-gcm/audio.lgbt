using System.Threading.Channels;
namespace AudioLgbt;

internal sealed class VideoReceiver : IDisposable
{
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private int needsKeyframe = 1;
    public VideoReceiver(Func<bool> visible, Action<Bitmap> display, Action failed)
    {
        worker = Task.Run(async () =>
        {
            try
            {
                using var decoder = new VideoDecoder();
                await foreach (var bytes in frames.Reader.ReadAllAsync())
                {
                    bool key = bytes.Length >= 10 && (bytes[0] & 1) == 0;
                    if (Volatile.Read(ref needsKeyframe) != 0 && !key) continue;
                    if (key) Interlocked.Exchange(ref needsKeyframe, 0);
                    var bitmap = decoder.Decode(bytes, visible()); if (bitmap is not null) display(bitmap);
                }
            }
            catch { failed(); }
        });
    }
    public void Receive(byte[] bytes) { if (!frames.Writer.TryWrite(bytes)) Interlocked.Exchange(ref needsKeyframe, 1); }
    public void Dispose() { frames.Writer.TryComplete(); worker.GetAwaiter().GetResult(); }
}
