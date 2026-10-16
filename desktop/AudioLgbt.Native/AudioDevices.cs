using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace AudioLgbt;
internal sealed record DeviceChoice(string Id, string Name) { public override string ToString() => Name; }
internal sealed class AudioDevices : IDisposable
{
    private readonly object gate = new();
    private WaveInEvent? input;
    private IOpusEncoder? encoder;
    private NoiseReducer noise = new();
    private readonly VoiceActivity localActivity = new();
    private WasapiOut? output;
    private MMDevice? speaker;
    private MixingSampleProvider? mixer;
    private OutputTestTone? testTone;
    private readonly Dictionary<string, RemoteAudio> peers = new();
    private readonly Preferences preferences;
    private bool disposed;
    public bool Muted { get; private set; } = true;
    public event Action<byte[]>? Encoded;
    public event Action<float>? Level;
    public event Action<string>? Error;
    public AudioDevices(Preferences preferences) { this.preferences = preferences; }
    public static List<DeviceChoice> Microphones()
    {
        var result = new List<DeviceChoice> { new("-1", "Системный микрофон") };
        for (var i = 0; i < WaveIn.DeviceCount; i++) result.Add(new(i.ToString(), WaveIn.GetCapabilities(i).ProductName));
        return result;
    }
    public static List<DeviceChoice> Speakers()
    {
        var result = new List<DeviceChoice> { new("", "Системное устройство") };
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) { result.Add(new(device.ID, device.FriendlyName)); device.Dispose(); }
        return result;
    }
    public void StartPlayback(string id)
    {
        StopTestSound();
        using var devices = new MMDeviceEnumerator();
        var nextDevice = id.Length == 0 ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications) : devices.GetDevice(id);
        var nextMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
        var next = new WasapiOut(nextDevice, AudioClientShareMode.Shared, true, 60);
        try { next.Init(nextMixer); next.Volume = Math.Clamp(preferences.Volume / 100f, 0, 1); next.Play(); }
        catch { next.Dispose(); nextDevice.Dispose(); throw; }
        WasapiOut? old; MMDevice? oldDevice;
        lock (gate) { if (disposed) { next.Dispose(); nextDevice.Dispose(); return; } foreach (var peer in peers.Values) nextMixer.AddMixerInput(peer.Samples); old = output; oldDevice = speaker; output = next; speaker = nextDevice; mixer = nextMixer; }
        old?.Stop(); old?.Dispose(); oldDevice?.Dispose();
    }
    public void PlayTestSound()
    {
        lock (gate)
        {
            if (disposed || mixer is null) throw new InvalidOperationException("Выберите доступное устройство вывода.");
            if (testTone is not null) mixer.RemoveMixerInput(testTone);
            testTone = new OutputTestTone(); mixer.AddMixerInput(testTone);
        }
    }
    public void StopTestSound()
    {
        lock (gate) { if (testTone is not null) mixer?.RemoveMixerInput(testTone); testTone = null; }
    }
    public void SetVolume(int volume) { preferences.Volume = volume; lock (gate) { if (output is not null) output.Volume = Math.Clamp(volume / 100f, 0, 1); } }
    public void SetMicrophone(int id)
    {
        if (Muted) { preferences.Microphone = id; return; }
        StartCapture(id); preferences.Microphone = id;
    }
    public void SetMuted(bool muted)
    {
        if (muted) { WaveInEvent? old; lock (gate) { Muted = true; localActivity.Reset(); old = input; input = null; encoder = null; } old?.StopRecording(); old?.Dispose(); Level?.Invoke(0); }
        else { StartCapture(preferences.Microphone); Muted = false; }
    }
    private void StartCapture(int id)
    {
        var next = new WaveInEvent { DeviceNumber = id, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 20, NumberOfBuffers = 3 };
        var nextEncoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        nextEncoder.Bitrate = 32000; nextEncoder.Complexity = 4; nextEncoder.UseVBR = true; nextEncoder.UseInbandFEC = true; nextEncoder.PacketLossPercent = 5;
        next.DataAvailable += Capture;
        next.RecordingStopped += (_, e) => { if (e.Exception is not null && !disposed) Error?.Invoke("Микрофон отключён. Выберите другое устройство в настройках."); };
        try { next.StartRecording(); } catch { next.Dispose(); throw; }
        WaveInEvent? old;
        lock (gate) { if (disposed) { next.Dispose(); return; } old = input; input = next; encoder = nextEncoder; noise = new(); localActivity.Reset(); Muted = false; }
        old?.StopRecording(); old?.Dispose();
    }
    private void Capture(object? sender, WaveInEventArgs args)
    {
        byte[]? packet = null; float peak = 0;
        lock (gate)
        {
            if (disposed || Muted || sender != input || encoder is null) return;
            var samples = MemoryMarshal.Cast<byte, short>(args.Buffer.AsSpan(0, args.BytesRecorded));
            if (samples.Length != 960) return;
            if (preferences.NoiseSuppression) noise.Process(samples);
            var gain = Math.Clamp(preferences.MicGain / 100f, 0, 2);
            for (var i = 0; i < samples.Length; i++) { samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue); peak = Math.Max(peak, Math.Abs(samples[i] / 32768f)); }
            localActivity.Observe(samples, Environment.TickCount64);
            Span<byte> encoded = stackalloc byte[4096]; var length = encoder.Encode(samples, 960, encoded, encoded.Length); packet = encoded[..length].ToArray();
        }
        Encoded?.Invoke(packet); Level?.Invoke(peak);
    }
    public void Receive(string id, string source, byte[] packet, uint timestamp)
    {
        lock (gate)
        {
            if (disposed || mixer is null) return;
            var track = id + "/" + source;
            if (!peers.TryGetValue(track, out var peer)) { peer = new(); peers[track] = peer; mixer.AddMixerInput(peer.Samples); }
            try
            {
                if (peer.HasTimestamp)
                {
                    int gap = unchecked((int)(timestamp - peer.NextTimestamp));
                    if (gap < 0) return;

                    if (gap > 0 && gap <= 2880 && gap % 960 == 0)
                        for (int missing = 0; missing < gap; missing += 960) { var concealed = peer.Decoder.Decode(ReadOnlySpan<byte>.Empty, peer.Pcm, 960, false); peer.Append(concealed); }
                }
                var length = peer.Decoder.Decode(packet, peer.Pcm, 5760, false);
                if (source == "mic") peer.Activity.Observe(peer.Pcm.AsSpan(0, length * 2), Environment.TickCount64);
                peer.NextTimestamp = timestamp + (uint)length; peer.HasTimestamp = true;
                if (peer.Buffer.BufferedDuration.TotalMilliseconds > 160) peer.Buffer.ClearBuffer();
                peer.Append(length);
            }
            catch (Exception e) when (e is ArgumentException or Concentus.OpusException) { }
        }
    }
    public bool IsSpeaking(string? id = null)
    {
        lock (gate)
        {
            if (disposed) return false;
            return id is null ? !Muted && localActivity.IsActive(Environment.TickCount64)
                : peers.TryGetValue(id + "/mic", out var peer) && peer.Activity.IsActive(Environment.TickCount64);
        }
    }
    public void SetTrackVolume(string id, string source, int volume) { lock (gate) { var track = id + "/" + source; if (!peers.TryGetValue(track, out var peer)) { peer = new(); peers[track] = peer; mixer?.AddMixerInput(peer.Samples); } peer.Samples.Volume = Math.Clamp(volume / 100f, 0, 2); } }
    public void Remove(string id) { lock (gate) { foreach (var key in peers.Keys.Where(k => k.StartsWith(id + "/", StringComparison.Ordinal)).ToArray()) if (peers.Remove(key, out var peer)) mixer?.RemoveMixerInput(peer.Samples); } }
    public void Dispose()
    {
        WaveInEvent? oldInput; WasapiOut? oldOutput; MMDevice? oldSpeaker;
        lock (gate) { if (disposed) return; disposed = true; Muted = true; oldInput = input; input = null; oldOutput = output; output = null; oldSpeaker = speaker; speaker = null; mixer = null; peers.Clear(); encoder = null; }
        oldInput?.StopRecording(); oldInput?.Dispose(); oldOutput?.Stop(); oldOutput?.Dispose(); oldSpeaker?.Dispose();
    }
    private sealed class RemoteAudio
    {
        public VoiceActivity Activity { get; } = new();
        public IOpusDecoder Decoder { get; } = OpusCodecFactory.CreateDecoder(48000, 2);
        public short[] Pcm { get; } = new short[5760 * 2];
        public BufferedWaveProvider Buffer { get; } = new(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromMilliseconds(200), DiscardOnBufferOverflow = true };
        public VolumeSampleProvider Samples { get; }
        public uint NextTimestamp; public bool HasTimestamp;
        public void Append(int frames) { var bytes = MemoryMarshal.AsBytes(Pcm.AsSpan(0, frames * 2)).ToArray(); Buffer.AddSamples(bytes, 0, bytes.Length); }
        public RemoteAudio() { Samples = new(Buffer.ToSampleProvider()); }
    }
}
