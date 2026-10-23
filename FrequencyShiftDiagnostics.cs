namespace SoncaAudioInspector;

public sealed record FrequencyShiftSettings
{
    public double DurationSeconds { get; init; } = 2.4;
    public double DetectionThresholdHz { get; init; } = .5;
    public bool? ExpectedDetected { get; init; }
    public bool? ExpectedFbxChange { get; init; }
    public void Validate()
    {
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < 2 || DurationSeconds > 5
            || !double.IsFinite(DetectionThresholdHz) || DetectionThresholdHz < .3 || DetectionThresholdHz > 15)
            throw new ArgumentException("Di tần: phát 2–5 giây/mức FBX; ngưỡng 0,3–15 Hz.");
    }
}

public sealed record FrequencyShiftCarrier(double NominalHz, double MeasuredHz, double StabilityHz, double LevelDbFs, double NarrowBandSnrDb)
{
    public double RawStabilityHz { get; init; }
    public double ClockJitterPpm { get; init; }
    public double[] SegmentFrequenciesHz { get; init; } = [];
}
public sealed record FrequencyShiftCaptureAnalysis(IReadOnlyList<FrequencyShiftCarrier> Carriers, string[] Problems,
    double SignalSeconds, double BackgroundSeconds, double PeakDbFs, bool Clipped)
{
    public bool IsValid => Problems.Length == 0;
}
public sealed record FrequencyShiftCaptureAttempt(bool FbxEnabled, int Attempt, FrequencyShiftCaptureAnalysis Analysis);
public sealed record FrequencyShiftLevelChange(double FrequencyHz, double OffDbFs, double OnDbFs)
{
    public double ChangeDb => OnDbFs - OffDbFs;
}
public sealed record FrequencyShiftResult(bool IsValid, bool? Detected, double ShiftHz, double UncertaintyHz,
    double RelativeClockPpm, double FitResidualHz, string Summary, IReadOnlyList<FrequencyShiftCarrier> Off, IReadOnlyList<FrequencyShiftCarrier> On)
{
    public int SampleRate { get; init; }
    public double OffShiftHz { get; init; } = double.NaN;
    public double OnShiftHz { get; init; } = double.NaN;
    public double OffUncertaintyHz { get; init; } = double.NaN;
    public double OnUncertaintyHz { get; init; } = double.NaN;
    public double OffClockPpm { get; init; } = double.NaN;
    public double OnClockPpm { get; init; } = double.NaN;
    public bool? FbxChangeDetected { get; init; }
    public IReadOnlyList<FrequencyShiftCaptureAttempt> CaptureAttempts { get; init; } = [];
    public IReadOnlyList<FrequencyShiftLevelChange> ToneLevelChanges => Off.Count == 3 && On.Count == 3
        ? Off.Select((p,i) => new FrequencyShiftLevelChange(p.NominalHz, p.LevelDbFs, On[i].LevelDbFs)).ToArray() : [];
    public static FrequencyShiftResult Invalid(string summary, IReadOnlyList<FrequencyShiftCarrier>? off = null, IReadOnlyList<FrequencyShiftCarrier>? on = null)
        => new(false, null, double.NaN, double.NaN, double.NaN, double.NaN, summary, off ?? [], on ?? []);
}

public static class FrequencyShiftDiagnostics
{
    public static readonly double[] CarriersHz = [700, 1700, 3700];
    public static float[] Generate(int rate, double levelDbFs, FrequencyShiftSettings settings)
    {
        settings.Validate();
        if (rate is not (44100 or 48000) || !double.IsFinite(levelDbFs) || levelDbFs < -60 || levelDbFs > -6) throw new ArgumentException("Kiểm tra sample rate và mức phát di tần.");
        int length = (int)Math.Round(rate * settings.DurationSeconds), fade = rate / 50;
        double amplitude = Math.Pow(10, levelDbFs / 20) / CarriersHz.Length;
        var signal = new float[length];
        for (int i = 0; i < length; i++)
        {
            double edge = Math.Min(1, Math.Min(i, length - 1 - i) / (double)fade);
            signal[i] = (float)(amplitude * CarriersHz.Sum(f => Math.Sin(2 * Math.PI * f * i / rate)) * (.5 - .5 * Math.Cos(Math.PI * edge)));
        }
        return signal;
    }

    public static IReadOnlyList<FrequencyShiftCarrier> Measure(float[] capture, float[] quiet, int rate, double? stimulusSeconds = null)
        => AnalyzeCapture(capture, quiet, rate, stimulusSeconds).Carriers;

    public static FrequencyShiftCaptureAnalysis AnalyzeCapture(float[] capture, float[] quiet, int rate, double? stimulusSeconds = null)
    {
        var problems = new List<string>();
        if (rate < 22050) problems.Add("Sample rate thu dưới 22050 Hz.");
        if (capture.Length < rate * 1.8) problems.Add("Bản thu tone ngắn hơn 1,8 giây.");
        if (quiet.Length < rate / 2) problems.Add("Bản thu nền ngắn hơn 0,5 giây.");
        if (capture.Concat(quiet).Any(v => !float.IsFinite(v))) problems.Add("Bản thu có NaN/Infinity.");
        bool clipped = capture.Concat(quiet).Any(v => Math.Abs(v) >= .995);
        if (clipped) problems.Add("Bản thu tone hoặc nền bị clipping.");
        double peak = capture.Length == 0 ? double.NegativeInfinity : 20 * Math.Log10(Math.Max(1e-12, capture.Max(v => Math.Abs(v))));
        var carriers = problems.Count == 0 ? MeasureCore(capture, quiet, rate, stimulusSeconds) : [];
        if (problems.Count == 0) problems.AddRange(CarrierProblems(carriers));
        return new(carriers, problems.ToArray(), capture.Length / (double)Math.Max(rate, 1), quiet.Length / (double)Math.Max(rate, 1), peak, clipped);
    }

    public static string[] CarrierProblems(IReadOnlyList<FrequencyShiftCarrier> carriers)
    {
        if (carriers.Count != 3 || carriers.Where((p,i) => p.NominalHz != CarriersHz[i]).Any())
            return ["Không tách được đủ ba carrier 700/1700/3700 Hz từ bản thu."];
        var problems = new List<string>();
        foreach (var p in carriers)
        {
            if (!double.IsFinite(p.MeasuredHz) || !double.IsFinite(p.StabilityHz))
                problems.Add($"{p.NominalHz:0} Hz: tần số chưa phù hợp clock chung / độ dịch Hz ổn định.");
            else if (p.StabilityHz > .15) problems.Add($"{p.NominalHz:0} Hz: dao động {p.StabilityHz:F3} Hz > 0,150 Hz.");
            if (!double.IsFinite(p.LevelDbFs) || p.LevelDbFs < -65) problems.Add($"{p.NominalHz:0} Hz: mức tone {p.LevelDbFs:F1} dBFS < -65 dBFS.");
            if (!double.IsFinite(p.NarrowBandSnrDb) || p.NarrowBandSnrDb < 15) problems.Add($"{p.NominalHz:0} Hz: SNR cục bộ {p.NarrowBandSnrDb:F1} dB < 15 dB.");
        }
        return problems.ToArray();
    }

    public static async Task<FrequencyShiftCaptureAnalysis> CaptureQualifiedAsync(
        Func<int, CancellationToken, Task<FrequencyShiftCaptureAnalysis>> acquire, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var captured = await acquire(attempt, token);
            if (captured.IsValid || captured.Clipped || attempt == 2) return captured;
        }
    }

    private static IReadOnlyList<FrequencyShiftCarrier> MeasureCore(float[] capture, float[] quiet, int rate, double? stimulusSeconds)
    {
        int size = (int)(rate * .6), length = size * 3;
        double power = 0, largest = -1; int start = 0;
        for (int i = 0; i < capture.Length; i++)
        {
            power += (double)capture[i] * capture[i];
            if (i >= length) power -= (double)capture[i - length] * capture[i - length];
            if (i >= length - 1 && power > largest) { largest = power; start = i - length + 1; }
        }
        if (stimulusSeconds is double duration && duration >= 2 && capture.Length >= rate * duration)
            start = Math.Clamp((int)Math.Round((duration - .05) * rate) - length, 0, capture.Length - length);
        var background = AdvancedAudioMeasurement.AnalyzeSpectrum(quiet, rate);
        int fftSize = 1; while (fftSize < capture.Length) fftSize <<= 1;
        var transform = new System.Numerics.Complex[fftSize];
        for (int i = 0; i < capture.Length; i++) transform[i] = new(capture[i], 0);
        MathNet.Numerics.IntegralTransforms.Fourier.Forward(transform, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
        var results = new List<FrequencyShiftCarrier>();
        foreach (double frequency in CarriersHz)
        {
            // Fit the unwrapped phase with equal time weights. An FFT-peak
            // estimate weights loud parts more strongly: FBX notch adaptation
            // then biases carriers differently during a shared clock transient.
            // Isolate each positive-frequency band to obtain its analytic signal.
            var analytic = new System.Numerics.Complex[fftSize];
            for (int bin = 1; bin < fftSize / 2; bin++)
                if (Math.Abs(bin * (double)rate / fftSize - frequency) <= 40) analytic[bin] = 2 * transform[bin];
            MathNet.Numerics.IntegralTransforms.Fourier.Inverse(analytic, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
            var readings = new List<(double FundamentalFrequencyHz, double FundamentalRms)>();
            for (int segment = 0; segment < 3; segment++)
            {
                var samples = capture.AsSpan(start + segment * size, size).ToArray();
                var spectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(samples, rate);
                int peak = Enumerable.Range(0, spectrum.FrequenciesHz.Length).Where(i => Math.Abs(spectrum.FrequenciesHz[i] - frequency) <= 30)
                    .MaxBy(i => spectrum.Magnitudes[i]);
                var tone = AdvancedAudioMeasurement.AnalyzeTone(samples, rate, spectrum.FrequenciesHz[peak], 1);
                // AnalyzeTone's single-tone 10%-of-total-power gate is unsuitable
                // for an unequal three-carrier response. Qualify each carrier
                // against its own measured background below instead.
                if (!double.IsFinite(tone.FundamentalRms) || tone.FundamentalRms <= 1e-9 || Math.Abs(tone.FundamentalFrequencyHz - frequency) > 30) return [];
                double phase = 0, previous = 0, sum = 0, weighted = 0;
                for (int i = 0; i < size; i++)
                {
                    int index = start + segment * size + i;
                    double angle = (analytic[index] * System.Numerics.Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency * index / rate)).Phase;
                    if (i > 0) phase += Math.IEEERemainder(angle - previous, 2 * Math.PI);
                    previous = angle; sum += phase; weighted += i * phase;
                }
                double meanIndex = (size - 1) / 2.0;
                double phaseSlope = (weighted - meanIndex * sum) / (size * (size * (double)size - 1) / 12);
                double phaseFrequency = frequency + phaseSlope * rate / (2 * Math.PI);
                if (!double.IsFinite(phaseFrequency) || Math.Abs(phaseFrequency - frequency) > 30) return [];
                readings.Add((phaseFrequency, tone.FundamentalRms));
            }
            double hz = readings.Average(r => r.FundamentalFrequencyHz);
            double stability = Math.Sqrt(readings.Average(r => Math.Pow(r.FundamentalFrequencyHz - hz, 2)));
            double rms = readings.Average(r => r.FundamentalRms);
            double noise = background.FrequenciesHz.Select((f,i) => Math.Abs(f - frequency) <= 35 ? background.Magnitudes[i] / Math.Sqrt(2) : 0).Max();
            results.Add(new(frequency, hz, stability, 20 * Math.Log10(Math.Max(1e-12, rms)), 20 * Math.Log10(Math.Max(1e-12, rms) / Math.Max(1e-12, noise)))
            { RawStabilityHz = stability, SegmentFrequenciesHz = readings.Select(r => r.FundamentalFrequencyHz).ToArray() });
        }
        // Clock/resampling drift is proportional to carrier frequency. Remove
        // only the common proportional change between simultaneous segments;
        // retain additive Hz variation and per-carrier residuals in StabilityHz.
        double xm = CarriersHz.Average(), variance = CarriersHz.Sum(f => Math.Pow(f - xm, 2));
        var slopes = new double[3];
        for (int segment = 0; segment < 3; segment++)
        {
            double ym = results.Average(p => p.SegmentFrequenciesHz[segment]);
            slopes[segment] = results.Sum(p => (p.NominalHz - xm) * (p.SegmentFrequenciesHz[segment] - ym)) / variance;
            double intercept = ym - slopes[segment] * xm;
            double error = Math.Sqrt(results.Average(p => Math.Pow(p.SegmentFrequenciesHz[segment] - slopes[segment] * p.NominalHz - intercept, 2)));
            if (Math.Abs((slopes[segment] - 1) * 1e6) > 2000 || error > .15)
                return results.Select(p => p with { StabilityHz = double.PositiveInfinity }).ToArray();
        }
        double meanSlope = slopes.Average(), clockJitter = Math.Sqrt(slopes.Average(a => Math.Pow(a - meanSlope, 2))) * 1e6;
        return results.Select(p => p with { StabilityHz = Math.Sqrt(p.SegmentFrequenciesHz.Select((f,s) =>
            Math.Pow(f - (slopes[s] - meanSlope) * p.NominalHz - p.MeasuredHz, 2)).Average()), ClockJitterPpm = clockJitter }).ToArray();
    }

    public static FrequencyShiftResult Compare(IReadOnlyList<FrequencyShiftCarrier> off, IReadOnlyList<FrequencyShiftCarrier> on, FrequencyShiftSettings settings)
    {
        settings.Validate();
        var problems = CarrierProblems(off).Select(p => "FBX TẮT: " + p).Concat(CarrierProblems(on).Select(p => "FBX BẬT: " + p)).ToArray();
        if (problems.Length > 0) return FrequencyShiftResult.Invalid("Di tần chưa xác định. " + string.Join(" ", problems), off, on);
        double meanX = off.Average(p => p.MeasuredHz), meanY = on.Average(p => p.MeasuredHz);
        double xx = off.Sum(p => Math.Pow(p.MeasuredHz - meanX, 2));
        double slope = off.Select((p,i) => (p.MeasuredHz - meanX) * (on[i].MeasuredHz - meanY)).Sum() / xx;
        double shift = meanY - slope * meanX;
        double residual = Math.Sqrt(off.Select((p,i) => Math.Pow(on[i].MeasuredHz - (slope * p.MeasuredHz + shift), 2)).Average());
        double scatter = Math.Sqrt(off.Select((p,i) => p.StabilityHz * p.StabilityHz + on[i].StabilityHz * on[i].StabilityHz).Average());
        double uncertainty = Math.Max(.15, 3 * Math.Max(scatter, residual) * Math.Sqrt(1.0 / 3 + meanX * meanX / xx));
        (double Shift, double Ppm, double Uncertainty, double Residual) Absolute(IReadOnlyList<FrequencyShiftCarrier> points)
        {
            double xm = points.Average(p => p.NominalHz), ym = points.Average(p => p.MeasuredHz);
            double variance = points.Sum(p => Math.Pow(p.NominalHz - xm, 2));
            double a = points.Sum(p => (p.NominalHz - xm) * (p.MeasuredHz - ym)) / variance;
            double b = ym - a * xm;
            double error = Math.Sqrt(points.Average(p => Math.Pow(p.MeasuredHz - a * p.NominalHz - b, 2)));
            double jitter = Math.Sqrt(points.Average(p => p.StabilityHz * p.StabilityHz));
            return (b, (a - 1) * 1e6, Math.Max(.15, 3 * Math.Max(jitter, error) * Math.Sqrt(1.0 / 3 + xm * xm / variance)), error);
        }
        var absoluteOff = Absolute(off); var absoluteOn = Absolute(on);
        if (!double.IsFinite(shift) || Math.Abs((slope - 1) * 1e6) > 2000 || residual > .15 || uncertainty > .6 || Math.Abs(shift) > 25
            || Math.Abs(absoluteOff.Ppm) > 2000 || Math.Abs(absoluteOn.Ppm) > 2000 || absoluteOff.Residual > .15 || absoluteOn.Residual > .15)
            return FrequencyShiftResult.Invalid("Chênh tần chưa phù hợp một độ dịch Hz ổn định; có thể do clock thay đổi, điều chế hoặc carrier yếu.", off, on);
        bool? Classify(double value, double error) => Math.Abs(value) - error >= settings.DetectionThresholdHz ? true
            : Math.Abs(value) + error < settings.DetectionThresholdHz ? false : null;
        bool? detected = Classify(absoluteOn.Shift, absoluteOn.Uncertainty);
        string message = detected == true ? $"Có di tần trong đường MIC: FBX BẬT {absoluteOn.Shift:+0.000;-0.000;0.000} ±{absoluteOn.Uncertainty:F2} Hz; TẮT {absoluteOff.Shift:+0.000;-0.000;0.000} ±{absoluteOff.Uncertainty:F2} Hz."
            : detected == false ? $"Chưa thấy di tần ≥{settings.DetectionThresholdHz:F2} Hz ở MIC khi FBX bật (đo {absoluteOn.Shift:+0.000;-0.000;0.000} ±{absoluteOn.Uncertainty:F2} Hz)."
            : "Kết quả sát ngưỡng di tần; cần đo lại.";
        return new(detected != null, detected, shift, uncertainty, (slope - 1) * 1e6, residual,
            message + $" Thay đổi BẬT − TẮT {shift:+0.000;-0.000;0.000} ±{uncertainty:F2} Hz; "
                + (Classify(shift, uncertainty) == false ? "chưa thấy di tần thay đổi theo nút FBX. " : Classify(shift, uncertainty) == true ? "có thay đổi di tần khi chuyển FBX. " : "chưa xác định thay đổi theo FBX. ")
                + LevelChangeSummary(off, on) + " Phép đo này chưa đánh giá khả năng dập tiếng hú thực tế.", off, on)
        { OffShiftHz = absoluteOff.Shift, OnShiftHz = absoluteOn.Shift, OffUncertaintyHz = absoluteOff.Uncertainty, OnUncertaintyHz = absoluteOn.Uncertainty,
            OffClockPpm = absoluteOff.Ppm, OnClockPpm = absoluteOn.Ppm, FbxChangeDetected = Classify(shift, uncertainty) };
    }

    public static string LevelChangeSummary(IReadOnlyList<FrequencyShiftCarrier> off, IReadOnlyList<FrequencyShiftCarrier> on)
        => off.Count == 3 && on.Count == 3 && off.Concat(on).All(p => double.IsFinite(p.LevelDbFs))
            ? "Mức tone khi BẬT so với TẮT: " + string.Join("; ", off.Select((p,i) => $"{p.NominalHz:0} Hz {on[i].LevelDbFs-p.LevelDbFs:+0.0;-0.0;0.0} dB")) + "." : "";

    public static string Status(FrequencyShiftResult result, FrequencyShiftSettings settings)
        => !result.IsValid || settings.ExpectedFbxChange.HasValue && result.FbxChangeDetected == null ? "INVALID"
            : settings.ExpectedDetected.HasValue && result.Detected != settings.ExpectedDetected
                || settings.ExpectedFbxChange.HasValue && result.FbxChangeDetected != settings.ExpectedFbxChange ? "FAIL" : "PASS";
}

public partial class TestConfig
{
    public FrequencyShiftSettings? FrequencyShift { get; set; }
}
