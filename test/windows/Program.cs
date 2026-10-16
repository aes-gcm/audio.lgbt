using AudioLgbt;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("server-checks")) { Task.Run(ServerChecks.Run).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("server-ui-checks")) return ServerChecks.RunUi();
            if (args.Contains("update-restarted")) { File.WriteAllText("dist/update-chain-restarted.txt", "OK"); return 0; }
            if (args.Contains("update-install-chain"))
            {
                string file=Path.GetFullPath("dist/audio.lgbt-Native-Preview-Setup.exe");
                string folder=Path.GetFullPath("dist/update-chain"); Directory.CreateDirectory(folder);
                string installer=Path.Combine(folder,"Setup.exe");File.Copy(file,installer,true);
                var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(installer)));
                var release=new UpdateRelease(AppUpdate.CurrentVersion,"x64",$"https://audio.lgbt/downloads/audio.lgbt-{AppUpdate.CurrentVersion}-x64.exe",new FileInfo(installer).Length,hash);
                AppUpdate.StartInstaller(installer,release,["update-restarted"],new Point(450,300),default).GetAwaiter().GetResult();return 0;
            }
            if (args.Contains("output-checks")) { Task.Run(OutputChecks.Run).GetAwaiter().GetResult(); return 0; }
            if (args.Contains("update-checks")) { UpdateChecks.Run().GetAwaiter().GetResult(); return 0; }
            if (args.Contains("update-ui")) return UpdateChecks.Preview();
            if (args.Contains("border-check")) return BorderCheck.Run();
            if (args.Contains("monitor-check")) { MonitorCheck.Run().GetAwaiter().GetResult(); return 0; }
            if (args.Contains("render-ui") || args.Contains("ui-checks")) return UiChecks.Run(args.Contains("render-ui"));
            if (args.Contains("fixture"))
            {
                using var output = new WasapiOut(); if (!args.Contains("quiet")) { output.Init(new SignalGenerator(48000,2) { Frequency=440,Gain=.03,Type=SignalGeneratorType.Sin }); output.Play(); }
                using var testWindow = new Form { Text="audio.lgbt · Test source", ClientSize=args.Contains("quiet") ? new Size(960,540) : new Size(1920,1080),BackColor=Color.FromArgb(25,40,50) };
                int frame=0; testWindow.Paint+=(_,e)=> { e.Graphics.DrawString("audio.lgbt · 1080p\nТекст и движение · "+frame, SystemFonts.CaptionFont!, Brushes.White,40,40);e.Graphics.FillRectangle(Brushes.Turquoise,40+frame%1000,200,160,160); };
                using var timer=new System.Windows.Forms.Timer { Interval=16 };timer.Tick+=(_,_)=>{frame+=5;testWindow.Invalidate();};timer.Start();Application.Run(testWindow);return 0;
            }
            if (args.Contains("tone"))
            {
                using var output = new WasapiOut();
                output.Init(new SignalGenerator(48000,2) { Frequency = 440, Gain = .05, Type = SignalGeneratorType.Sin });
                output.Play(); Thread.Sleep(20000); return 0;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { microphones = AudioDevices.Microphones(), speakers = AudioDevices.Speakers(), cameras = VideoDevices.Cameras().GetAwaiter().GetResult() }));
            foreach (var size in new[] { (1920,1080), (3840,2160) })
            {
                var pixels = new byte[size.Item1 * size.Item2 * 4];
                for(int i=0;i<pixels.Length;i+=4) { pixels[i]=(byte)(i/4 % 256); pixels[i+1]=180; pixels[i+2]=70; pixels[i+3]=255; }
                var watch = Stopwatch.StartNew();
                using var encoder = new VideoEncoder(size.Item1,size.Item2,30,true);
                using var decoder = new VideoDecoder();
                var packet=encoder.Encode(pixels,size.Item1,size.Item2) ?? throw new Exception("Encoder produced no frame");
                using var image=decoder.Decode(packet,true) ?? throw new Exception("Decoder produced no image");
                if(image.Width!=size.Item1||image.Height!=size.Item2)throw new Exception("Decoded size differs");
                Console.WriteLine(JsonSerializer.Serialize(new {codec="VP8",width=image.Width,height=image.Height,bytes=packet.Length,milliseconds=watch.ElapsedMilliseconds}));
            }
            using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "tone") { UseShellExecute=false, CreateNoWindow=true })!;
            Task.Run(async()=>
            {
                int packets=0; double peak=0;
                await using var capture = new LoopbackAudio((uint)child.Id,false);
                var decoder=Concentus.OpusCodecFactory.CreateDecoder(48000,2);var pcm=new short[5760*2];
                capture.Encoded+=packet=>{packets++;var count=decoder.Decode(packet,pcm,5760,false);for(int i=0;i<count*2;i++)peak=Math.Max(peak,Math.Abs(pcm[i]/32768d));};
                await capture.Ready;await Task.Delay(2500);
                if(packets<10||peak<.001)throw new Exception($"No real loopback sound: {packets}/{peak}");
                Console.WriteLine(JsonSerializer.Serialize(new {loopback="process include",packets,peak}));
            }).GetAwaiter().GetResult();
            child.Kill();child.WaitForExit();
            using var fixture = new Form { Text="audio.lgbt · Capture fixture",ClientSize=new Size(1280,720),BackColor=Color.DarkSlateBlue };
            fixture.Controls.Add(new Label { Text="audio.lgbt\nWindows Graphics Capture",Dock=DockStyle.Fill,Font=new Font("Segoe UI",32),ForeColor=Color.White });
            fixture.Shown+=async(_,_)=>
            {
                try
                {
                    int frames=0;
                    await using var capture=new ScreenCapture(new CaptureSource(fixture.Handle,true,fixture.Text,(uint)Environment.ProcessId),new ShareOptions(1080,30,false,false));
                    capture.Encoded+=(source,data,duration)=>{if(source=="screen")Interlocked.Increment(ref frames);};
                    await capture.Ready.WaitAsync(TimeSpan.FromSeconds(10));await Task.Delay(3000);
                    if(frames<15)throw new Exception("Insufficient captured frames: "+frames);
                    Console.WriteLine(JsonSerializer.Serialize(new {capture="WGC window",frames}));
                }
                catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
                finally{fixture.Close();}
            };
            Application.Run(fixture);return Environment.ExitCode;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
