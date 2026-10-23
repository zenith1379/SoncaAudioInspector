namespace SoncaAudioInspector;

public sealed record ReverbDecayBand(double LowHz, double HighHz, double ExcitationSnrDb,
    double TailSnrDb, double WetDryDifferenceDb, double DurationMs, double DecayDb, double RSquared, bool Detected)
{
    public double? PeakLevelDbFs { get; init; }
}
public sealed record ReverbProbeBand(double LowHz, double HighHz, double StartSeconds, double LevelDbFs)
{
    public double? PredictedPeakLevelDbFs { get; init; }
    public double SourceBandGainDb { get; init; }
}
public sealed record ReverbDecayResult(bool IsValid, bool Detected, string Summary, IReadOnlyList<ReverbDecayBand> Bands)
{
    public string FailureCode { get; init; } = "";
    public int Attempts { get; init; } = 1;
    public double? StimulusLevelDbFs { get; init; }
    public double PreRollSeconds { get; init; }
    public int StartupClippedSamplesIgnored { get; init; }
    public IReadOnlyList<ReverbDecayResult> PreviousAttempts { get; init; } = Array.Empty<ReverbDecayResult>();
    public IReadOnlyList<ReverbProbeBand> ProbeBands { get; init; } = Array.Empty<ReverbProbeBand>();
}

/// <summary>Electronic reverb verification with echo disabled and the same interrupted-noise stimulus, dry/wet.</summary>
public static class ReverbDecayDiagnostics
{
    public const double BurstSeconds = 1;
    // Separate bursts avoid spending MIC headroom on frequencies outside the band being verified.
    public static ReverbProbeBand[]? PlanBandRetry(ReverbDecayResult previous, double level, int rate, double preRollSeconds, double gapSeconds)
    {
        if (rate < 18000 || rate > 192000 || !double.IsFinite(level) || level < -60 || level > -6
            || !double.IsFinite(preRollSeconds) || preRollSeconds < 0 || preRollSeconds > 1
            || !double.IsFinite(gapSeconds) || gapSeconds < 2 || gapSeconds > 10 || previous.Bands.Count != 2
            || previous.Bands[0].LowHz != 500 || previous.Bands[0].HighHz != 2000
            || previous.Bands[1].LowHz != 2000 || previous.Bands[1].HighHz != Math.Min(8000, rate * .45))
            throw new ArgumentException("Không đủ thông số dải hoặc mức phát để lập mẫu đối chứng.");
        var initial = previous.Bands.Select((b, i) => new ReverbProbeBand(b.LowHz, b.HighHz,
            preRollSeconds + i * (BurstSeconds + gapSeconds), level)).ToArray();
        var broad = Generate(rate, level, preRollSeconds);
        var isolated = GenerateBandSequence(rate, initial);
        double ActivePower(double[] x, double start) => x.Skip((int)(rate * (start + .3))).Take(rate / 2).Select(v => v * v).Average();
        var planned = previous.Bands.Select((band, index) =>
        {
            bool quiet = previous.Bands.All(b => b.TailSnrDb < 6 && b.WetDryDifferenceDb < 6);
            double deficit = Math.Max((quiet ? 35 : 20) - band.ExcitationSnrDb, quiet ? 0 : 10 - band.TailSnrDb);
            double concentration = Db(ActivePower(Filter(isolated, rate, band.LowHz, band.HighHz), initial[index].StartSeconds)
                / Math.Max(1e-30, ActivePower(Filter(broad, rate, band.LowHz, band.HighHz), preRollSeconds)));
            // Account for spectral concentration before increasing a source peak. A filtered
            // capture peak is only a prediction; actual full-band clipping still rejects the pair.
            // Reserve 6 dB at capture, cap a source increase at 6 dB and source peak at -6 dBFS.
            double? peak = band.PeakLevelDbFs is double value && double.IsFinite(value) ? value : null;
            double headroom = peak.HasValue ? -6 - peak.Value : 0;
            double wanted = double.IsFinite(deficit) && deficit > 0 ? Math.Ceiling(deficit + 2) : 0;
            double adjusted = Math.Clamp(level + Math.Min(6, Math.Min(wanted, headroom) - concentration), -60, -6);
            double netGain = concentration + adjusted - level;
            return initial[index] with { LevelDbFs = adjusted, SourceBandGainDb = netGain,
                PredictedPeakLevelDbFs = peak + netGain };
        }).ToArray();
        // At the configured source floor it may be impossible to compensate for spectral
        // concentration. Keep the ordinary retry rather than emit an unsafe band sequence.
        return planned.Any(b => b.PredictedPeakLevelDbFs > -6 + 1e-8
            || !b.PredictedPeakLevelDbFs.HasValue && b.SourceBandGainDb > 1e-8) ? null : planned;
    }

    public static float[] GenerateBandSequence(int rate, IReadOnlyList<ReverbProbeBand> bands)
    {
        if (rate < 18000 || rate > 192000 || bands.Count != 2 || bands.Any(b => !double.IsFinite(b.StartSeconds) || b.StartSeconds < 0 || b.StartSeconds > 12
            || !double.IsFinite(b.LevelDbFs) || b.LevelDbFs < -60 || b.LevelDbFs > -6
            || !double.IsFinite(b.LowHz) || !double.IsFinite(b.HighHz) || b.LowHz <= 0 || b.HighHz <= b.LowHz || b.HighHz >= rate / 2.0)
            || bands[1].StartSeconds < bands[0].StartSeconds + BurstSeconds + 2)
            throw new ArgumentException("Kiểm tra hai dải và khoảng nghỉ của mẫu reverb.");
        var output = new float[(int)Math.Round(rate * (bands[^1].StartSeconds + BurstSeconds))];
        foreach (var band in bands)
        {
            var random = new Random(360);
            var noise = Enumerable.Range(0, rate).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            var filtered = Filter(noise, rate, band.LowHz, band.HighHz);
            // Mild crest reduction keeps the random burst useful at the same peak limit.
            // Filtering again confines the clipped waveform before the final peak normalization.
            for (int pass = 0; pass < 3; pass++)
            {
                double rms = Math.Sqrt(filtered.Select(v => v * v).Average());
                noise = filtered.Select(v => (float)Math.Clamp(v, -2.5 * rms, 2.5 * rms)).ToArray();
                filtered = Filter(noise, rate, band.LowHz, band.HighHz);
            }
            double peak = filtered.Max(Math.Abs), scale = Math.Pow(10, band.LevelDbFs / 20) / Math.Max(1e-30, peak);
            int start = (int)Math.Round(rate * band.StartSeconds);
            for (int i = 0; i < filtered.Length; i++)
            {
                double rise = .5 - .5 * Math.Cos(Math.PI * Math.Min(1, i / (.15 * rate)));
                double fade = Math.Min(rise, (filtered.Length - 1 - i) / (.005 * rate));
                output[start + i] = (float)(filtered[i] * scale * Math.Max(0, fade));
            }
        }
        return output;
    }
    public static async Task<ReverbDecayResult> VerifyAsync(double initialLevel,
        Func<int, double, CancellationToken, Task<ReverbDecayResult>> capturePair, CancellationToken token = default)
    {
        if (!double.IsFinite(initialLevel) || initialLevel < -60 || initialLevel > -6) throw new ArgumentOutOfRangeException(nameof(initialLevel));
        var previous = new List<ReverbDecayResult>(); double level = initialLevel;
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var result = (await capturePair(attempt, level, token))
                with { Attempts = attempt, StimulusLevelDbFs = level, PreviousAttempts = previous.ToArray() };
            token.ThrowIfCancellationRequested();
            if (result.IsValid || attempt >= 2 || result.FailureCode is "RATE_CHANGED" or "INVALID_SAMPLES" or "CAPTURE_TOO_SHORT") return result;
            previous.Add(result);
            if (result.FailureCode == "CLIPPING") level = Math.Max(-60, level - 3);
            await Task.Delay(250, token);
        }
    }
    public static float[] Generate(int rate, double levelDbFs, double preRollSeconds = 0)
    {
        if (rate < 8000 || rate > 192000 || !double.IsFinite(levelDbFs) || levelDbFs < -60 || levelDbFs > -6)
            throw new ArgumentOutOfRangeException(nameof(levelDbFs));
        if (!double.IsFinite(preRollSeconds) || preRollSeconds < 0 || preRollSeconds > 1) throw new ArgumentOutOfRangeException(nameof(preRollSeconds));
        var random = new Random(360);
        int offset = (int)Math.Round(rate * preRollSeconds), length = (int)Math.Round(rate * BurstSeconds);
        var signal = new float[offset + length];
        double peak = Math.Pow(10, levelDbFs / 20);
        for (int i = 0; i < length; i++)
        {
            // Let MIC/DSP gain settle gradually; retain the short shutoff needed for tail analysis.
            double rise = .5 - .5 * Math.Cos(Math.PI * Math.Min(1, i / (.15 * rate)));
            double fade = Math.Min(rise, (length - 1 - i) / (.005 * rate));
            signal[offset + i] = (float)((random.NextDouble() * 2 - 1) * peak * Math.Max(0, fade));
        }
        return signal;
    }

    public static ReverbDecayResult Analyze(float[] dry, float[] wet, int rate, double preRollSeconds = 0,
        IReadOnlyList<ReverbProbeBand>? probeBands = null)
    {
        int startupClips = 0;
        ReverbDecayResult Invalid(string why, string code) => new(false, false, why, Array.Empty<ReverbDecayBand>())
            { FailureCode = code, PreRollSeconds = preRollSeconds, StartupClippedSamplesIgnored = startupClips,
                ProbeBands = probeBands ?? Array.Empty<ReverbProbeBand>() };
        if (rate < 8000 || !double.IsFinite(preRollSeconds) || preRollSeconds < 0 || preRollSeconds > 1
            || new[] { dry, wet }.Any(a => a.Length < rate * (2.5 + preRollSeconds)))
            return Invalid("Thu khô/ướt chưa đủ 2,5 giây hoặc sample rate không hợp lệ.", "CAPTURE_TOO_SHORT");
        if (probeBands != null && (probeBands.Count != 2 || probeBands.Any(b => !double.IsFinite(b.StartSeconds) || b.StartSeconds < preRollSeconds
            || !double.IsFinite(b.LevelDbFs) || b.LevelDbFs < -60 || b.LevelDbFs > -6)
            || probeBands[0].LowHz != 500 || probeBands[0].HighHz != 2000
            || probeBands[1].LowHz != 2000 || probeBands[1].HighHz != Math.Min(8000, rate * .45)
            || probeBands[1].StartSeconds < probeBands[0].StartSeconds + BurstSeconds + 2
            || new[] { dry, wet }.Any(a => a.Length < rate * (probeBands[^1].StartSeconds + BurstSeconds + 1.5))))
            return Invalid("Mẫu theo dải chưa đủ thời gian hoặc sai cấu hình phát.", "CAPTURE_TOO_SHORT");
        if (new[] { dry, wet }.Any(a => a.Any(v => !float.IsFinite(v))))
            return Invalid("Bản thu khô/ướt chứa mẫu không hữu hạn; kiểm tra đường thu.", "INVALID_SAMPLES");
        // Only exclude startup samples guaranteed to precede this protocol's silent pre-roll.
        // Legacy waveforms (preRoll=0) keep the full-record clipping guard.
        int settle = (int)Math.Round(rate * Math.Min(.2, Math.Max(0, preRollSeconds - .1)));
        startupClips = new[] { dry, wet }.Sum(a => a.Take(settle).Count(v => Math.Abs(v) >= .999f));
        int dryClips = dry.Skip(settle).Count(v => Math.Abs(v) >= .999f), wetClips = wet.Skip(settle).Count(v => Math.Abs(v) >= .999f);
        if (dryClips + wetClips > 0)
            return Invalid($"Mẫu đối chứng chạm mức thu gần 0 dBFS (khô {dryClips}, ướt {wetClips} mẫu); chưa xác định reverb từ cặp này.", "CLIPPING");
        var bands = new List<ReverbDecayBand>();
        foreach (var limits in new[] { (500.0, 2000.0), (2000.0, Math.Min(8000, rate * .45)) })
        {
            var a = Filter(dry, rate, limits.Item1, limits.Item2);
            var b = Filter(wet, rate, limits.Item1, limits.Item2);
            // Locate the direct shutoff independently; WASAPI startup latency may differ.
            double startSeconds = probeBands?.Single(p => p.LowHz == limits.Item1).StartSeconds ?? preRollSeconds;
            int stopA = Stop(a, rate, startSeconds), stopB = Stop(b, rate, startSeconds);
            double Power(double[] x, int from, int count) => x.Skip(Math.Max(0, from)).Take(count).Select(v => v * v).DefaultIfEmpty(0).Average();
            double noiseA = Power(a, a.Length - rate / 2, rate / 2), noiseB = Power(b, b.Length - rate / 2, rate / 2);
            double activeA = Power(a, stopA - rate / 3, rate / 4), activeB = Power(b, stopB - rate / 3, rate / 4);
            double snr = Math.Min(Db(activeA / Math.Max(1e-30, noiseA)), Db(activeB / Math.Max(1e-30, noiseB)));
            var points = new List<(double Time, double Db)>(); int diffuse = 0;
            double tailSnr = double.NegativeInfinity, difference = double.NegativeInfinity;
            for (int ms = 40; ms < 1000; ms += 20)
            {
                double pa = Power(a, stopA + (int)(rate * ms / 1000.0), rate / 50);
                double pb = Power(b, stopB + (int)(rate * ms / 1000.0), rate / 50);
                double localSnr = Db(pb / Math.Max(1e-30, noiseB));
                double delta = Db((pb / Math.Max(1e-30, activeB)) / Math.Max(1e-30, pa / Math.Max(1e-30, activeA)));
                if (ms <= 140) { tailSnr = Math.Max(tailSnr, localSnr); difference = Math.Max(difference, delta); }
                if (localSnr >= 6 && delta >= 6)
                {
                    points.Add((ms / 1000.0, Db(Math.Max(1e-30, pb - noiseB))));
                    int first = stopB + (int)(rate * ms / 1000.0), length = rate / 50;
                    double fourth = b.Skip(first).Take(length).Select(v => v * v * v * v).Average();
                    if (fourth / Math.Max(1e-30, pb * pb) >= 2) diffuse++;
                }
                else if (points.Count > 0) break;
            }
            double span = points.Count > 0 ? points[^1].Time - points[0].Time : 0;
            double slope = 0, r2 = 0;
            if (points.Count >= 6)
            {
                double mx = points.Average(p => p.Time), my = points.Average(p => p.Db);
                slope = points.Sum(p => (p.Time - mx) * (p.Db - my)) / Math.Max(1e-30, points.Sum(p => Math.Pow(p.Time - mx, 2)));
                r2 = 1 - points.Sum(p => Math.Pow(p.Db - my - slope * (p.Time - mx), 2)) / Math.Max(1e-30, points.Sum(p => Math.Pow(p.Db - my, 2)));
            }
            double bandPeak = new[] { (a, stopA), (b, stopB) }.Max(p => p.Item1.Skip(Math.Max(0, p.Item2 - rate)).Take(rate).Select(Math.Abs).DefaultIfEmpty(0).Max());
            bands.Add(new(limits.Item1, limits.Item2, snr, tailSnr, difference, span * 1000, -slope * span, r2,
                snr >= 20 && tailSnr >= 10 && points.Count >= 6 && diffuse >= .6 * points.Count && span >= .1 && -slope * span >= 3 && r2 >= .7)
                { PeakLevelDbFs = 20 * Math.Log10(Math.Max(1e-30, bandPeak)) });
        }
        bool detected = bands.All(b => b.Detected);
        // A negative result requires substantially more dynamic range than presence detection.
        bool valid = detected || bands.All(b => b.ExcitationSnrDb >= 35 && b.TailSnrDb < 6 && b.WetDryDifferenceDb < 6);
        var reasons = new List<string>();
        bool quietComparison = bands.All(b => b.TailSnrDb < 6 && b.WetDryDifferenceDb < 6);
        if (!valid) foreach (var band in bands.Where(b => !b.Detected))
        {
            string name = $"{band.LowHz:0}–{band.HighHz:0} Hz";
            if (quietComparison && band.ExcitationSnrDb < 35) reasons.Add($"{name}: tín hiệu/nền {band.ExcitationSnrDb:0.0} dB < 35 dB cần để khẳng định không có reverb");
            else if (quietComparison) continue;
            else if (band.ExcitationSnrDb < 20) reasons.Add($"{name}: tín hiệu/nền {band.ExcitationSnrDb:0.0} dB < 20 dB");
            else if (band.TailSnrDb < 10) reasons.Add($"{name}: đuôi/nền {band.TailSnrDb:0.0} dB < 10 dB");
            else if (band.WetDryDifferenceDb < 6) reasons.Add($"{name}: đuôi ướt/khô {band.WetDryDifferenceDb:0.0} dB < 6 dB");
            else reasons.Add($"{name}: đuôi chưa liên tục/giảm đều đủ dài (dài {band.DurationMs:0} ms, giảm {band.DecayDb:0.0} dB, R² {band.RSquared:0.00})");
        }
        return new(valid, detected, detected ? "Có reverb trong bản thu (đuôi nhiễu ngắt, đối chứng khô; echo đã tắt)."
            : valid ? "Chưa phát hiện reverb trong bản thu đối chứng."
            : "Mẫu đối chứng chưa đủ chất lượng: " + string.Join("; ", reasons) + ".", bands)
            { FailureCode = valid ? "" : "LOW_TAIL_QUALITY", PreRollSeconds = preRollSeconds, StartupClippedSamplesIgnored = startupClips,
                ProbeBands = probeBands ?? Array.Empty<ReverbProbeBand>() };
    }
    private static double Db(double power) => 10 * Math.Log10(Math.Max(1e-30, power));
    private static int Stop(double[] x, int rate, double preRollSeconds = 0)
    {
        int width = rate / 50, best = (int)(rate * (BurstSeconds + preRollSeconds)); double score = double.NegativeInfinity;
        double[] sum = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) sum[i + 1] = sum[i] + x[i] * x[i];
        for (int i = (int)(rate * (.8 + preRollSeconds)); i < Math.Min(x.Length - rate, rate * (1.6 + preRollSeconds)); i += rate / 1000)
        {
            double before = (sum[i] - sum[i - width]) / width, after = (sum[i + width] - sum[i]) / width;
            double change = Db(before / Math.Max(1e-30, after));
            if (change > score) { score = change; best = i; }
        }
        return best;
    }
    private static double[] Filter(float[] signal, int rate, double low, double high)
    {
        var result = signal.Select(v => (double)v).ToArray();
        foreach (bool hp in new[] { true, false })
        foreach (double q in new[] { .541196100146197, 1.306562964876377 })
        {
            double w = 2 * Math.PI * (hp ? low : high) / rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            double b0 = (hp ? 1 + c : 1 - c) / (2 * a0), b1 = (hp ? -(1 + c) : 1 - c) / a0, a1 = -2 * c / a0, a2 = (1 - alpha) / a0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < result.Length; i++)
            { double x = result[i], y = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2; result[i] = y; x2 = x1; x1 = x; y2 = y1; y1 = y; }
        }
        return result;
    }
}
