using NAudio.Dsp;
namespace AudioLgbt;

internal sealed class NoiseReducer
{
    private const int N = 1024, Hop = N / 2;
    private readonly float[] input = new float[N], overlap = new float[Hop], noise = new float[N / 2 + 1], window = new float[N];
    private readonly Complex[] bins = new Complex[N];
    private readonly Queue<short> output = new();
    private int filled; private bool initialized;
    public NoiseReducer() { for (var i = 0; i < N; i++) window[i] = MathF.Sqrt(.5f - .5f * MathF.Cos(2 * MathF.PI * i / N)); }
    public void Process(Span<short> samples)
    {
        for (var j = 0; j < samples.Length; j++)
        {
            input[filled++] = samples[j] / 32768f;
            if (filled == N)
            {
                for (var i = 0; i < N; i++) bins[i] = new Complex { X = input[i] * window[i] };
                FastFourierTransform.FFT(true, 10, bins);
                for (var i = 0; i <= N / 2; i++)
                {
                    var power = bins[i].X * bins[i].X + bins[i].Y * bins[i].Y;
                    if (!initialized) noise[i] = Math.Min(power, .00000002f);
                    var rate = power < noise[i] * 1.7f ? .06f : .0004f;
                    noise[i] += rate * (power - noise[i]);
                    var gain = MathF.Sqrt(Math.Clamp(1 - noise[i] * 1.8f / (power + 1e-12f), .06f, 1));
                    bins[i].X *= gain; bins[i].Y *= gain;
                    if (i > 0 && i < N / 2) { bins[N-i].X *= gain; bins[N-i].Y *= gain; }
                }
                initialized = true; FastFourierTransform.FFT(false, 10, bins);
                for (var i = 0; i < Hop; i++) { var value = bins[i].X * window[i] + overlap[i]; output.Enqueue((short)Math.Clamp(value * 32768, short.MinValue, short.MaxValue)); overlap[i] = bins[i + Hop].X * window[i + Hop]; }
                Array.Copy(input, Hop, input, 0, Hop); filled = Hop;
            }
            samples[j] = output.Count > 0 ? output.Dequeue() : (short)0;
        }
    }
}
