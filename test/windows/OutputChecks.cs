using AudioLgbt;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Reflection;
using System.Text.Json;

internal static class OutputChecks
{
    public static async Task Run()
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        var pcm = Enumerable.Range(0,960).Select(i=>(short)(1600*Math.Sin(2*Math.PI*440*i/48000d))).ToArray();
        var activity = new VoiceActivity();
        activity.Observe(pcm, 1000); if(activity.IsActive(1000)) throw new Exception("One short spike should not flash the avatar");
        activity.Observe(pcm, 1020); if(!activity.IsActive(1020)||!activity.IsActive(1500)||activity.IsActive(1620)) throw new Exception("Voice hold/expiry failed");
        activity.Reset(); activity.Observe(new short[960], 2000); if(activity.IsActive(2000)) throw new Exception("Silence marked as speaking");
        var encoded=new byte[4096];int length=encoder.Encode(pcm,960,encoded,encoded.Length);var packet=encoded[..length];
        double Energy(int? volume)
        {
            using var audio=new AudioDevices(new Preferences());
            var mixer=new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000,2)){ReadFully=true};
            typeof(AudioDevices).GetField("mixer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(audio,mixer);
            if(volume.HasValue)audio.SetTrackVolume("peer","mic",volume.Value);
            audio.Receive("peer","mic",packet,0);
            var samples=new float[1920];mixer.Read(samples,0,samples.Length);
            return Math.Sqrt(samples.Sum(s=>(double)s*s)/samples.Length);
        }
        using(var audio = new AudioDevices(new Preferences()))
        {
            typeof(AudioDevices).GetField("mixer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(audio,new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000,2)){ReadFully=true});
            audio.SetTrackVolume("voice","mic",0);
            audio.Receive("voice","mic",packet,0); audio.Receive("voice","mic",packet,960);
            if(!audio.IsSpeaking("voice"))throw new Exception("Decoded microphone speech must be visible even at listener volume 0");
            audio.Receive("screen","screen-audio",packet,0); audio.Receive("screen","screen-audio",packet,960);
            audio.PlayTestSound(); if(audio.IsSpeaking("screen")||audio.IsSpeaking())throw new Exception("Screen audio or output test incorrectly lights the microphone");
            await Task.Delay(650);if(audio.IsSpeaking("voice"))throw new Exception("Speaking is stuck after incoming packets stop");
            audio.Remove("voice");if(audio.IsSpeaking("voice"))throw new Exception("Removed peer still active");
        }
        Console.WriteLine("PASS speaking from decoded Opus, hold/timeout, silent/muted output, isolated screen audio and local test");
        double normal=Energy(null),boosted=Energy(200),muted=Energy(0);
        if(normal<=0||Math.Abs(boosted/normal-2)>.001||muted!=0)throw new Exception($"Gain regression {normal}/{boosted}/{muted}");
        Console.WriteLine(JsonSerializer.Serialize(new{gainRatio=boosted/normal,normal,muted}));
        using var enumerator=new MMDeviceEnumerator();
        var devices=enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active);
        foreach(var device in devices)
        {
            using(device)
            using(var audio=new AudioDevices(new Preferences{Volume=100}))
            using(var capture=new WasapiLoopbackCapture(device))
            {
                if(capture.WaveFormat.BitsPerSample!=32)throw new Exception("Unexpected loopback sample format");
                var samples=new List<float>();var gate=new object();int channels=capture.WaveFormat.Channels;
                capture.DataAvailable+=(_,e)=>{lock(gate)for(int i=0;i<e.BytesRecorded;i+=4*channels)samples.Add(BitConverter.ToSingle(e.Buffer,i));};
                audio.StartPlayback(device.ID);capture.StartRecording();await Task.Delay(150);
                audio.PlayTestSound();await Task.Delay(1450);audio.StopTestSound();capture.StopRecording();
                float[] data;lock(gate)data=samples.ToArray();
                // Correlate with the actual note frequency; unrelated room audio cannot satisfy this check.
                double amplitude=0;
                for(int start=0;start+4800<data.Length;start+=2400){double re=0,im=0;for(int i=0;i<4800;i++){double phase=2*Math.PI*660*i/capture.WaveFormat.SampleRate;re+=data[start+i]*Math.Cos(phase);im+=data[start+i]*Math.Sin(phase);}amplitude=Math.Max(amplitude,2*Math.Sqrt(re*re+im*im)/4800);}
                if(amplitude<.005)throw new Exception("No test tone in selected endpoint: "+device.FriendlyName+" amplitude="+amplitude);
                Console.WriteLine(JsonSerializer.Serialize(new{output=device.FriendlyName,samples=data.Length,toneAmplitude=amplitude}));
                audio.PlayTestSound();audio.StopTestSound();audio.StartPlayback(device.ID);
            }
        }
        Console.WriteLine("PASS actual selected WASAPI endpoints, finite local test tone, stop/switch/dispose, participant default100/200/mute");
    }
}
