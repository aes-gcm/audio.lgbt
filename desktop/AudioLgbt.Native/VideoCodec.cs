using FFmpeg.AutoGen;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace AudioLgbt;

internal sealed unsafe class VideoEncoder : IDisposable
{
    private AVCodecContext* codec;
    private AVFrame* frame;
    private AVPacket* packet;
    private SwsContext* converter;
    private long pts;
    public bool Keyframe { get; set; } = true;
    public VideoEncoder(int width, int height, int fps, bool screen, int bitrate = 0, bool motion = false)
    {
      try {
        NativeVideo.Initialize();
        var implementation = ffmpeg.avcodec_find_encoder_by_name("libvpx");
        if (implementation == null) throw new InvalidOperationException("Видеокодек недоступен. Переустановите приложение.");
        codec = ffmpeg.avcodec_alloc_context3(implementation);
        if (codec == null) throw new OutOfMemoryException();
        codec->width = width; codec->height = height; codec->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        codec->time_base = new AVRational { num = 1, den = fps }; codec->framerate = new AVRational { num = fps, den = 1 };
        if (codec == null) throw new OutOfMemoryException();
        codec->bit_rate = bitrate > 0 ? bitrate : screen ? 6000000 : 500000; codec->gop_size = fps * 2; codec->max_b_frames = 0; codec->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        ffmpeg.av_opt_set(codec->priv_data, "deadline", "realtime", 0);
        ffmpeg.av_opt_set(codec->priv_data, "cpu-used", "8", 0);
        ffmpeg.av_opt_set(codec->priv_data, "lag-in-frames", "0", 0);
        ffmpeg.av_opt_set(codec->priv_data, "error-resilient", "1", 0);
        if (screen) ffmpeg.av_opt_set(codec->priv_data, "tune-content", motion ? "default" : "screen", 0);
        NativeVideo.Check(ffmpeg.avcodec_open2(codec, implementation, null));
        frame = ffmpeg.av_frame_alloc(); frame->format = (int)codec->pix_fmt; frame->width = width; frame->height = height;
        NativeVideo.Check(ffmpeg.av_frame_get_buffer(frame, 32)); packet = ffmpeg.av_packet_alloc();
      } catch { Dispose(); throw; }
    }
    public byte[]? Encode(byte[] bgra, int width, int height)
    {
        NativeVideo.Check(ffmpeg.av_frame_make_writable(frame));
        converter = ffmpeg.sws_getCachedContext(converter, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, codec->width, codec->height, codec->pix_fmt, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        if (converter == null) throw new InvalidOperationException("Не удалось обработать видеокадр.");
        fixed (byte* raw = bgra)
        {
            byte_ptrArray4 source = default; int_array4 stride = default; source[0] = raw; stride[0] = width * 4;
            ffmpeg.sws_scale(converter, source, stride, 0, height, frame->data, frame->linesize);
        }
        frame->pts = pts++; frame->pict_type = Keyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE; Keyframe = false;
        NativeVideo.Check(ffmpeg.avcodec_send_frame(codec, frame));
        var result = ffmpeg.avcodec_receive_packet(codec, packet); if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN)) return null; NativeVideo.Check(result);
        var bytes = new byte[packet->size]; Marshal.Copy((IntPtr)packet->data, bytes, 0, bytes.Length); ffmpeg.av_packet_unref(packet); return bytes;
    }
    public void Dispose() { if (converter != null) { ffmpeg.sws_freeContext(converter); converter = null; } fixed (AVFrame** p = &frame) ffmpeg.av_frame_free(p); fixed (AVPacket** p = &packet) ffmpeg.av_packet_free(p); fixed (AVCodecContext** p = &codec) ffmpeg.avcodec_free_context(p); }
}
internal sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* codec; private AVFrame* frame; private AVPacket* packet; private SwsContext* converter;
    public VideoDecoder() { try { NativeVideo.Initialize(); var implementation = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_VP8); codec = ffmpeg.avcodec_alloc_context3(implementation); if (codec == null) throw new OutOfMemoryException(); codec->thread_count = 2; codec->thread_type = ffmpeg.FF_THREAD_SLICE; codec->max_pixels = 4096L * 4096; NativeVideo.Check(ffmpeg.avcodec_open2(codec, implementation, null)); frame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc(); } catch { Dispose(); throw; } }
    public Bitmap? Decode(byte[] bytes, bool visible)
    {
        NativeVideo.Check(ffmpeg.av_new_packet(packet, bytes.Length)); Marshal.Copy(bytes, 0, (IntPtr)packet->data, bytes.Length);
        var result = ffmpeg.avcodec_send_packet(codec, packet); ffmpeg.av_packet_unref(packet); if (result < 0) return null;
        if (ffmpeg.avcodec_receive_frame(codec, frame) < 0 || !visible) return null;
        if (frame->width <= 0 || frame->height <= 0 || frame->width > 4096 || frame->height > 4096) return null;
        var width = frame->width; var height = frame->height;
        converter = ffmpeg.sws_getCachedContext(converter, frame->width, frame->height, (AVPixelFormat)frame->format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        if (converter == null) return null;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { byte_ptrArray4 output = default; int_array4 strides = default; output[0] = (byte*)data.Scan0; strides[0] = data.Stride; ffmpeg.sws_scale(converter, frame->data, frame->linesize, 0, frame->height, output, strides); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    public void Dispose() { if (converter != null) { ffmpeg.sws_freeContext(converter); converter = null; } fixed (AVFrame** p = &frame) ffmpeg.av_frame_free(p); fixed (AVPacket** p = &packet) ffmpeg.av_packet_free(p); fixed (AVCodecContext** p = &codec) ffmpeg.avcodec_free_context(p); }
}
internal static class NativeVideo
{
    private static readonly object Gate = new(); private static bool initialized;
    public static void Initialize() { lock (Gate) { if (initialized) return; ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "media"); ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET); initialized = true; } }
    public static void Check(int result) { if (result < 0) throw new InvalidOperationException("Не удалось запустить видео. Код: " + result); }
}
