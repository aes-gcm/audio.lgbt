using NAudio.Wave;

namespace AudioLgbt;

internal sealed class OutputTestTone : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private int position;
    public int Read(float[] buffer, int offset, int count)
    {
        int read = Math.Min(count, 62400 * 2 - position);
        for (int i = 0; i < read; i++, position++)
        {
            double time = (position / 2) / 48000d;
            int note = (int)(time / .4);
            double local = time - note * .4;
            double envelope = note < 3 && local < .3 ? Math.Min(1, local / .015) * Math.Min(1, (.3 - local) / .06) : 0;
            double frequency = note == 0 ? 660 : note == 1 ? 880 : 990;
            buffer[offset + i] = (float)(.12 * envelope * Math.Sin(2 * Math.PI * frequency * local));
        }
        return read;
    }
}
