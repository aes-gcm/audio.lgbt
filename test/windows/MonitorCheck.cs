using AudioLgbt;
using System.Runtime.InteropServices;
internal static class MonitorCheck
{
    [DllImport("audio-capture.dll",CallingConvention=CallingConvention.Cdecl)] static extern int capture_backend(nint capture);
    internal static async Task Run()
    {
        foreach(var source in CaptureSource.List().Where(s=>!s.Window))
        {
            await Task.Run(async()=>
            {
                Marshal.ThrowExceptionForHR(NativeCapture.capture_start(0,source.Handle,out var capture));
                try
                {
                    if(capture_backend(capture)!=2)throw new Exception("Monitor must use DXGI");
                    int frames=0,width=0,height=0;bool content=false;var pixels=new byte[4096*4096*4];
                    var deadline=DateTime.UtcNow.AddSeconds(3);
                    while(DateTime.UtcNow<deadline)
                    {
                        int hr=NativeCapture.capture_grab(capture,pixels,pixels.Length,out width,out height);Marshal.ThrowExceptionForHR(hr);
                        if(hr==0){frames++;for(int i=0;i<width*height*4;i+=4096)content|=pixels[i]>8||pixels[i+1]>8||pixels[i+2]>8;}
                        await Task.Delay(16);
                    }
                    if(frames==0||!content)throw new Exception("Monitor has no real desktop image");
                    NativeCapture.capture_repeat(capture);
                    if(NativeCapture.capture_grab(capture,pixels,pixels.Length,out width,out height)!=0)throw new Exception("Cached keyframe unavailable");
                    Console.WriteLine($"DXGI {source.Name}: frames={frames}, {width}x{height}, image=yes, repeat=yes");
                }
                finally{NativeCapture.capture_close(capture);}
                int packets=0,decoded=0;using var decoder=new VideoDecoder();
                await using var video=new ScreenCapture(source,new ShareOptions(1080,30,false,false));
                video.Encoded+=(kind,data,duration)=>{if(kind!="screen")return;packets++;if(decoded<3){using var frame=decoder.Decode(data,true);if(frame is not null)decoded++;}};
                await video.Ready.WaitAsync(TimeSpan.FromSeconds(15)); await Task.Delay(1000);video.ForceKeyframe();await Task.Delay(1000);
                if(packets<1||decoded<1)throw new Exception("DXGI -> VP8 -> decoded image failed");
                Console.WriteLine($"DXGI -> VP8: packets={packets}, decoded={decoded}");
            });
        }
    }
}
