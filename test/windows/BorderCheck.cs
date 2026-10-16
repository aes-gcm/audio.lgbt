using AudioLgbt;
using System.Runtime.InteropServices;

internal static class BorderCheck
{
    [DllImport("audio-capture.dll", CallingConvention=CallingConvention.Cdecl)] static extern int capture_border_required(nint capture);
    internal static int Run()
    {
        Application.EnableVisualStyles();
        using var fixture = new Form { Text="audio.lgbt · Borderless capture test", ClientSize=new Size(960,540), BackColor=Color.FromArgb(25,40,50) };
        fixture.Controls.Add(new Label { Text="audio.lgbt\nЗахват без жёлтой рамки",Dock=DockStyle.Fill,Font=new Font("Segoe UI",28),ForeColor=Color.White });
        fixture.Shown+=async(_,_)=>
        {
            try
            {
                var handle=fixture.Handle;
                await Task.Run(async()=>
                {
                    var sources=CaptureSource.List().Where(s=>!s.Window).Take(1).Prepend(new CaptureSource(handle,true,"fixture",(uint)Environment.ProcessId));
                    foreach(var source in sources)
                    {
                        Marshal.ThrowExceptionForHR(NativeCapture.capture_start(source.Window?1:0,source.Handle,out var capture));
                        try
                        {
                            int border=capture_border_required(capture), frames=0, width=0,height=0;
                            if(border!=0)throw new Exception("Border still requested: "+border);
                            var pixels=new byte[4096*4096*4];var deadline=DateTime.UtcNow.AddSeconds(source.Window?20:5);
                            while(DateTime.UtcNow<deadline)
                            {
                                int hr=NativeCapture.capture_grab(capture,pixels,pixels.Length,out width,out height);Marshal.ThrowExceptionForHR(hr);
                                if(hr==0)frames++;await Task.Delay(33);
                            }
                            if(frames==0)throw new Exception("No captured frames");
                            Console.WriteLine($"{(source.Window?"window":"monitor")}: border={border}, frames={frames}, {width}x{height}");
                        }
                        finally { NativeCapture.capture_close(capture); }
                    }
                });
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally{fixture.Close();}
        };
        Application.Run(fixture);return Environment.ExitCode;
    }
}
