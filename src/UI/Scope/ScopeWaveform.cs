namespace SoncaAudioInspector;

/// <summary>Positions original samples on the time axis; no filtering or amplitude normalization.</summary>
public static class ScopeWaveform
{
    public sealed record Trace(double[] TimeMs, double[] Samples, bool Triggered, double DurationMs);

    public static Trace Create(float[] samples, int sampleRate, double durationMs, bool trigger)
    {
        if (samples.Length < 2 || sampleRate <= 0 || !double.IsFinite(durationMs) || durationMs <= 0)
            throw new ArgumentException("Chưa đủ dữ liệu hợp lệ để vẽ sóng thu.");
        int count = Math.Clamp((int)Math.Ceiling(durationMs * sampleRate / 1000) + 1, 2, samples.Length);
        int start = samples.Length - count;
        double fractionalOffset = 0;
        bool triggered = false;
        if (trigger && samples.All(float.IsFinite))
        {
            double center = samples.Average(s => (double)s);
            double peakToPeak = samples.Max() - samples.Min();
            double hysteresis = peakToPeak * .1;
            bool armed = false;
            if (peakToPeak > 1e-7)
            {
                // Use the most recent rising crossing that leaves a complete visible window.
                // Hysteresis prevents small fluctuations around the crossing from retriggering.
                for (int i = 1; i <= samples.Length - count; i++)
                {
                    if (samples[i - 1] <= center - hysteresis) armed = true;
                    if (armed && samples[i - 1] <= center && samples[i] > center)
                    {
                        start = i - 1;
                        fractionalOffset = (center - samples[i - 1]) / (samples[i] - samples[i - 1]);
                        triggered = true;
                        armed = false;
                    }
                }
            }
        }
        double[] x = new double[count], y = new double[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = (i - fractionalOffset) * 1000 / sampleRate;
            y[i] = samples[start + i];
        }
        return new Trace(x, y, triggered, Math.Min(durationMs, (count - 1) * 1000.0 / sampleRate));
    }
}
