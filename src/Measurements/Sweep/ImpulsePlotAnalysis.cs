using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

// Zero is deliberately the legacy window for results saved before window metadata existed.
public enum ImpulseWindowKind { HalfHann = 0, Tukey25 = 1 }

public sealed record ImpulsePlotData(
    IReadOnlyList<TimeDomainPoint> Impulse,
    IReadOnlyList<TimeDomainPoint> Envelope,
    double PeakTransferGainDb,
    double ClarityC20Db,
    double CentreTimeSeconds)
{
    // Keep the actual samples for zoom and windowing. Display extrema alone cannot
    // reconstruct a gated impulse or recover narrow valleys after reloading.
    public double[] Samples { get; init; } = Array.Empty<double>();
    public int SampleRate { get; init; }
    public int DirectIndex { get; init; }
    private double[]? _fullEnvelopeDb;
    internal void CacheEnvelopeDb(double[] values) => _fullEnvelopeDb = values;
    public double[] GetFullEnvelopeDb() => LazyInitializer.EnsureInitialized(ref _fullEnvelopeDb,
        () => ImpulsePlotAnalysis.EnvelopeDb(Samples));
}

public sealed record ImpulseTraceData(double[] Times, double[] Values);

/// <summary>Display data from the full ungated IR, independent of FEQ normalisation and FDW.</summary>
public static class ImpulsePlotAnalysis
{
    // A display-only opening range. Keep the measurement window and all peaks
    // within 60 dB of the strongest sample; quieter tails remain in the full IR.
    public static (double Start, double End) InitialTimeRange(ImpulseTraceData raw,
        double leftWindow, double rightWindow)
    {
        if (raw.Times.Length == 0) return (-.005, .1);
        double first = raw.Times[0], last = raw.Times[^1];
        if (last <= first) return (first - .001, first + .001);
        double peak = 0;
        foreach (double value in raw.Values)
            if (double.IsFinite(value)) peak = Math.Max(peak, Math.Abs(value));
        double start = Math.Clamp(-leftWindow, first, last);
        double end = Math.Clamp(rightWindow, first, last);
        start = Math.Min(start, Math.Clamp(0, first, last));
        end = Math.Max(end, Math.Clamp(0, first, last));
        if (peak > 0)
        {
            double threshold = peak * .001;
            for (int i = 0; i < raw.Values.Length; i++)
                if (Math.Abs(raw.Values[i]) >= threshold)
                {
                    start = Math.Min(start, raw.Times[i]);
                    end = Math.Max(end, raw.Times[i]);
                }
        }
        double padding = Math.Max(.002, (end - start) * .02);
        return (Math.Max(first, start - padding), Math.Min(last, end + padding));
    }

    public static int SampleCount(double milliseconds, int sampleRate) =>
        (int)Math.Round(milliseconds * sampleRate / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>Shared by the frequency-response calculation and the IR view.</summary>
    public static double WindowWeight(double timeSeconds, double leftSeconds, double rightSeconds,
        ImpulseWindowKind kind = ImpulseWindowKind.Tukey25)
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(leftSeconds) || !double.IsFinite(rightSeconds)
            || leftSeconds < 0 || rightSeconds <= 0) throw new ArgumentException("Giới hạn cửa sổ IR không hợp lệ.");
        if (timeSeconds < -leftSeconds || timeSeconds > rightSeconds) return 0;
        if (timeSeconds == 0) return 1;
        double position = Math.Abs(timeSeconds) / (timeSeconds < 0 ? leftSeconds : rightSeconds);
        return kind switch
        {
            ImpulseWindowKind.HalfHann => .5 + .5 * Math.Cos(Math.PI * position),
            ImpulseWindowKind.Tukey25 => position <= .75 ? 1 : .5 + .5 * Math.Cos(Math.PI * (position - .75) / .25),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public static ImpulsePlotData Build(IReadOnlyList<double> impulse, int sampleRate, int directIndex)
    {
        if (sampleRate <= 0 || directIndex < 0 || directIndex >= impulse.Count || impulse.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("IR hiển thị không hợp lệ.");
        double peak = impulse.Select(Math.Abs).DefaultIfEmpty(0).Max();
        double[] envelope = EnvelopeDb(impulse);
        double[] normalized = impulse.Select(value => peak > 0 ? value / peak : 0).ToArray();
        var irPoints = Reduce(normalized, sampleRate, directIndex, absolute: true);
        var etcPoints = Reduce(envelope, sampleRate, directIndex, absolute: false);
        double early = 0, late = 0, weighted = 0;
        for (int index = directIndex; index < impulse.Count; index++)
        {
            double time = (index - directIndex) / (double)sampleRate, energy = impulse[index] * impulse[index];
            if (time < .020) early += energy; else late += energy;
            weighted += time * energy;
        }
        double total = early + late;
        var result = new ImpulsePlotData(irPoints, etcPoints, peak > 0 ? 20 * Math.Log10(peak) : double.NaN,
            early > 0 && late > 0 ? 10 * Math.Log10(early / late) : double.NaN,
            total > 0 ? weighted / total : double.NaN)
            { Samples = normalized, SampleRate = sampleRate, DirectIndex = directIndex };
        result.CacheEnvelopeDb(envelope);
        return result;
    }

    // Retain both tops and valleys in time order, including the endpoints.
    // Full samples remain available above for detailed plotting and processing.
    public static IReadOnlyList<TimeDomainPoint> Reduce(IReadOnlyList<double> values, int sampleRate, int directIndex, bool absolute)
    {
        int stride = Math.Max(1, (int)Math.Ceiling(values.Count / 16000.0));
        var points = new List<TimeDomainPoint>();
        double Magnitude(int i) => absolute ? Math.Abs(values[i]) : values[i];
        for (int start = 0; start < values.Count; start += stride)
        {
            int end = Math.Min(values.Count, start + stride), min = start, max = start;
            for (int i = start + 1; i < end; i++)
            {
                if (Magnitude(i) < Magnitude(min)) min = i;
                if (Magnitude(i) > Magnitude(max)) max = i;
            }
            foreach (int i in new[] { start, min, max, end - 1 }.Distinct().OrderBy(i => i))
                points.Add(new((i - directIndex) / (double)sampleRate, values[i]));
        }
        return points;
    }

    public static double[] ApplyWindow(IReadOnlyList<double> samples, int sampleRate, int directIndex,
        double leftMs, double rightMs, ImpulseWindowKind kind) => samples.Select((value, i) =>
            value * WindowWeight((i - directIndex) / (double)sampleRate, leftMs / 1000, rightMs / 1000, kind)).ToArray();

    /// <summary>
    /// Display a signed, linearly interpolated IR on a log-magnitude axis. Taking
    /// abs/log before joining samples loses every zero between opposite signs.
    /// Insert that crossing at the display floor without changing stored samples.
    /// This is a rendering operation, not an ETC envelope or a measurement filter.
    /// </summary>
    public static IReadOnlyList<TimeDomainPoint> LogMagnitudeTrace(IReadOnlyList<TimeDomainPoint> samples,
        bool includeZeroCrossings = true)
    {
        var output = new List<TimeDomainPoint>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            var current = samples[i];
            if (includeZeroCrossings && i > 0)
            {
                var previous = samples[i - 1];
                if ((previous.Value < 0 && current.Value > 0) || (previous.Value > 0 && current.Value < 0))
                {
                    double fraction = Math.Abs(previous.Value) / (Math.Abs(previous.Value) + Math.Abs(current.Value));
                    output.Add(new(previous.TimeSeconds + fraction * (current.TimeSeconds - previous.TimeSeconds), -180));
                }
            }
            output.Add(new(current.TimeSeconds, 20 * Math.Log10(Math.Max(1e-9, Math.Abs(current.Value)))));
        }
        return output;
    }

    // Arrays go directly to the dense-signal renderer. Avoid allocating a record
    // for every source sample and then copying all records back into arrays.
    public static ImpulseTraceData PrepareLogMagnitude(IReadOnlyList<double> samples, int sampleRate, int directIndex)
    {
        int crossings = 0;
        bool Opposite(int i) => (samples[i - 1] < 0 && samples[i] > 0) || (samples[i - 1] > 0 && samples[i] < 0);
        for (int i = 1; i < samples.Count; i++) if (Opposite(i)) crossings++;
        var times = new double[samples.Count + crossings]; var values = new double[times.Length];
        int output = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (i > 0 && Opposite(i))
            {
                double fraction = Math.Abs(samples[i - 1]) / (Math.Abs(samples[i - 1]) + Math.Abs(samples[i]));
                // Use the same arithmetic as the full-resolution reference trace.
                double before = (i - 1 - directIndex) / (double)sampleRate;
                double after = (i - directIndex) / (double)sampleRate;
                times[output] = before + fraction * (after - before); values[output++] = -180;
            }
            times[output] = (i - directIndex) / (double)sampleRate;
            values[output++] = 20 * Math.Log10(Math.Max(1e-9, Math.Abs(samples[i])));
        }
        return new(times, values);
    }

    // Magnitude of the analytic signal, not abs(IR). Zero padding avoids joining
    // the end of the captured tail directly to the pre-arrival samples.
    public static double[] EnvelopeDb(IReadOnlyList<double> impulse)
    {
        if (impulse.Count == 0) return Array.Empty<double>();
        int fftSize = 1;
        while (fftSize < impulse.Count * 2) fftSize <<= 1;
        var spectrum = new Complex[fftSize];
        double peak = 0;
        for (int index = 0; index < impulse.Count; index++)
        {
            spectrum[index] = impulse[index];
            peak = Math.Max(peak, Math.Abs(impulse[index]));
        }
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        for (int bin = 1; bin < fftSize / 2; bin++) spectrum[bin] *= 2;
        Array.Clear(spectrum, fftSize / 2 + 1, fftSize / 2 - 1);
        Fourier.Inverse(spectrum, FourierOptions.Matlab);
        return Enumerable.Range(0, impulse.Count).Select(index =>
            20 * Math.Log10(Math.Max(1e-12, peak > 0 ? spectrum[index].Magnitude / peak : 0))).ToArray();
    }
}
