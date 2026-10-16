namespace AudioLgbt;

internal sealed class VoiceActivity
{
    private long activeUntil;
    private int attack;
    public void Observe(ReadOnlySpan<short> samples, long now)
    {
        double energy = 0;
        foreach (var sample in samples) { double value = sample / 32768d; energy += value * value; }
        if (samples.Length > 0 && energy / samples.Length >= .012 * .012)
        {
            if (++attack >= 2) activeUntil = now + 600;
        }
        else attack = 0;
    }
    public bool IsActive(long now) => now < activeUntil;
    public void Reset() { attack = 0; activeUntil = 0; }
}
