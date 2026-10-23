namespace SoncaAudioInspector;

public sealed record ImpulseEffectSettings
{
    public bool? ExpectEcho { get; init; }
    public bool? ExpectReverb { get; init; }
    public double? ExpectedEchoDelayMs { get; init; }
    public double EchoDelayToleranceMs { get; init; } = 10;
    public double MinimumEchoDelayMs { get; init; } = 30;
    public double MaximumEchoDelayMs { get; init; } = 600;
    public double MinimumEchoLevelDb { get; init; } = -45;
    public double MinimumEchoProminenceDb { get; init; } = 10;
    public double MinimumDiffuseTailDb { get; init; } = -40;
    public double MinimumTailOccupancy { get; init; } = .60;
    public double TailStartMs { get; init; } = 50;
    public double TailEndMs { get; init; } = 1000;
    public double CaptureTailSeconds { get; init; } = 3;

    public void Validate()
    {
        double[] values = { EchoDelayToleranceMs, MinimumEchoDelayMs, MaximumEchoDelayMs, MinimumEchoLevelDb,
            MinimumEchoProminenceDb, MinimumDiffuseTailDb, MinimumTailOccupancy, TailStartMs, TailEndMs, CaptureTailSeconds };
        if (values.Any(v => !double.IsFinite(v)) || MinimumEchoDelayMs < 10 || MaximumEchoDelayMs <= MinimumEchoDelayMs
            || MaximumEchoDelayMs > 2000 || EchoDelayToleranceMs <= 0 || MinimumEchoLevelDb > 0 || MinimumEchoLevelDb < -100
            || MinimumEchoProminenceDb < 3 || MinimumTailOccupancy <= 0 || MinimumTailOccupancy > 1
            || TailStartMs < 10 || TailEndMs <= TailStartMs + 100 || TailEndMs > 5000
            || CaptureTailSeconds < 1 || CaptureTailSeconds > 10
            || ExpectedEchoDelayMs is double delay && (!double.IsFinite(delay) || delay < MinimumEchoDelayMs || delay > MaximumEchoDelayMs))
            throw new ArgumentException("Cấu hình ImpulseEffects không hợp lệ: kiểm tra delay, cửa sổ đuôi và ngưỡng dB.");
    }
}

public sealed record EchoTap(double DelayMs, double LevelDb);

public sealed record ImpulseEffectReference(bool EchoDetected, bool ReverbDetected, double? EchoDelayMs,
    double? EchoLevelDb, double DiffuseTailEnergyDb, double TailOccupancy, string SettingsKey)
{
    public static string Key(ImpulseEffectSettings settings) => "diffuse-bands-v6:" + System.Text.Json.JsonSerializer.Serialize(settings with { ExpectEcho = null, ExpectReverb = null });
    public static ImpulseEffectReference? From(ImpulseEffectResult? result, ImpulseEffectSettings settings) => result?.CanClassify(settings) == true
        ? new(result.EchoDetected, result.ReverbDetected, double.IsFinite(result.EchoDelayMs) ? result.EchoDelayMs : null,
            result.EchoTaps.Count > 0 ? result.EchoTaps.Max(t => t.LevelDb) : null,
            result.DiffuseTailEnergyDb, result.TailOccupancy, Key(settings)) : null;

    public static ImpulseEffectReference? Average(ImpulseEffectReference? first, ImpulseEffectReference? second, double delayToleranceMs)
    {
        if (first == null || second == null || first.SettingsKey != second.SettingsKey
            || first.EchoDetected != second.EchoDetected || first.ReverbDetected != second.ReverbDetected
            || first.EchoDelayMs is double a && second.EchoDelayMs is double b && Math.Abs(a - b) > delayToleranceMs
            || first.EchoLevelDb is double c && second.EchoLevelDb is double d && Math.Abs(c - d) > 6
            || first.ReverbDetected && Math.Abs(first.DiffuseTailEnergyDb - second.DiffuseTailEnergyDb) > 6) return null;
        return first with { EchoDelayMs = (first.EchoDelayMs + second.EchoDelayMs) / 2,
            EchoLevelDb = (first.EchoLevelDb + second.EchoLevelDb) / 2,
            DiffuseTailEnergyDb = (first.DiffuseTailEnergyDb + second.DiffuseTailEnergyDb) / 2,
            TailOccupancy = (first.TailOccupancy + second.TailOccupancy) / 2 };
    }

    public IReadOnlyList<string> Compare(ImpulseEffectResult current, ImpulseEffectSettings settings)
    {
        if (!current.CanClassify(settings))
            return new[] { "Chưa đủ dữ liệu để so echo/reverb với line chuẩn." };
        if (SettingsKey != Key(settings)) return new[] { "Cấu hình phân tích đuôi đã đổi; cần lấy lại mẫu chuẩn echo/reverb." };
        var notes = new List<string>();
        if (EchoDetected && !current.EchoDetected) notes.Add("ECHO_MISSING_REFERENCE: thiếu chuỗi echo so với line chuẩn cùng route.");
        if (!EchoDetected && current.EchoDetected) notes.Add("ECHO_ADDED_REFERENCE: xuất hiện chuỗi echo khác line chuẩn.");
        if (ReverbDetected && !current.ReverbDetected) notes.Add("REVERB_MISSING_REFERENCE: thiếu đuôi reverb so với line chuẩn cùng route.");
        if (!ReverbDetected && current.ReverbDetected) notes.Add("REVERB_ADDED_REFERENCE: xuất hiện đuôi liên tục khác line chuẩn.");
        if (EchoDetected && current.EchoDetected && EchoDelayMs is double delay && Math.Abs(current.EchoDelayMs - delay) > settings.EchoDelayToleranceMs)
            notes.Add($"Echo lệch delay {current.EchoDelayMs - delay:+0.0;-0.0} ms so với chuẩn {delay:0.0} ms.");
        if (EchoLevelDb is double level && current.EchoTaps.Count > 0 && Math.Abs(current.EchoTaps.Max(t => t.LevelDb) - level) > 6)
            notes.Add("Mức echo/direct lệch quá 6 dB so với line chuẩn.");
        if (ReverbDetected && current.ReverbDetected && Math.Abs(current.DiffuseTailEnergyDb - DiffuseTailEnergyDb) > 6)
            notes.Add("Năng lượng đuôi ngoài echo/direct lệch quá 6 dB so với line chuẩn.");
        if (notes.Count == 0) notes.Add("Echo/reverb chưa lệch các tiêu chí của line chuẩn (delay và mức tương đối). Giữ nguyên DSP/fixture khi đối chiếu.");
        return notes;
    }
}

public sealed record ImpulseEffectResult(
    bool IsValid, bool EchoDetected, bool ReverbDetected, double EchoDelayMs,
    IReadOnlyList<EchoTap> EchoTaps, double LateEnergyDb, double DiffuseTailEnergyDb,
    double TailOccupancy, double TailDecay60Seconds, double TailFitRSquared,
    double ImpulseNoiseDb, double CapturedTailSeconds, bool TailTruncated,
    string Verdict, IReadOnlyList<string> Findings)
{
    public double DiffuseTailDurationMs { get; init; }
    public double DiffuseTailSnrDb { get; init; } = double.NaN;
    public double ActiveTailOccupancy { get; init; }
    public string ReverbAnalysisBands { get; init; } = "";
    public ReverbDecayResult? ReverbVerification { get; init; }

    public bool CanClassify(ImpulseEffectSettings settings) => IsValid && !TailTruncated
        && CapturedTailSeconds * 1000 >= Math.Max(settings.TailEndMs + 100,
            2 * (settings.ExpectedEchoDelayMs ?? settings.MaximumEchoDelayMs) + 100)
        && (EchoDetected || HasQuietImpulse(settings))
        && (ReverbDetected || ReverbVerification?.IsValid == true || HasQuietImpulse(settings));

    private bool HasQuietImpulse(ImpulseEffectSettings settings) => double.IsFinite(ImpulseNoiseDb)
        && ImpulseNoiseDb <= Math.Min(-60, settings.MinimumEchoLevelDb - 6);

    public string Describe(ImpulseEffectSettings? settings = null)
    {
        settings ??= new();
        if (!IsValid) return "Echo/reverb chưa đo hợp lệ.";
        var notes = new List<string>();
        if (EchoDetected) notes.Add($"Có echo trong bản thu (lặp {EchoDelayMs:0.0} ms).");
        if (ReverbDetected) notes.Add("Có reverb trong bản thu" + (double.IsFinite(TailDecay60Seconds)
            ? $" (T60 đuôi ước tính {TailDecay60Seconds:0.00} s)."
            : DiffuseTailDurationMs > 0 ? $" (đuôi liên tục {DiffuseTailDurationMs:0} ms)." : "."));
        if (!EchoDetected || !ReverbDetected)
        {
            string missing = EchoDetected ? "Reverb" : ReverbDetected ? "Echo" : "Echo/reverb";
            if (CanClassify(settings)) notes.Add($"Chưa phát hiện {missing.ToLowerInvariant()} trong bản thu.");
            else if (!ReverbDetected && ReverbVerification is { IsValid: false } verification)
                notes.Add("Reverb chưa xác định: " + verification.Summary
                    + (verification.Attempts > 1 ? $" Đã thử {verification.Attempts} cặp khô/ướt." : "")
                    + (verification.ProbeBands.Count > 0 ? " Lượt cuối đo từng dải: "
                        + string.Join(", ", verification.ProbeBands.Select(b => $"{b.LowHz:0}–{b.HighHz:0} Hz {b.LevelDbFs:0.#} dBFS")) + "." : ""));
            else notes.Add($"{missing} chưa xác định: " + (TailTruncated ? "đuôi thu bị cắt."
                : CapturedTailSeconds * 1000 < Math.Max(settings.TailEndMs + 100, 2 * (settings.ExpectedEchoDelayMs ?? settings.MaximumEchoDelayMs) + 100)
                    ? "đuôi thu quá ngắn." : "nền IR cao."));
        }
        return string.Join(" ", notes);
    }
}

/// <summary>Analyses the full, ungated IR. Effect detection is distinct from model acceptance.</summary>
public static class ImpulseEffects
{
    public static ImpulseEffectResult Analyze(IReadOnlyList<double> impulse, int sampleRate, int directIndex,
        bool acquisitionValid, ImpulseEffectSettings? settings = null)
    {
        settings ??= new(); settings.Validate();
        ImpulseEffectResult Invalid(string reason) => new(false, false, false, double.NaN, Array.Empty<EchoTap>(),
            double.NaN, double.NaN, 0, double.NaN, double.NaN, double.NaN, 0, false, "INVALID", new[] { reason });
        if (!acquisitionValid || sampleRate < 8000 || directIndex < 0 || directIndex >= impulse.Count || impulse.Any(v => !double.IsFinite(v)))
            return Invalid("Bản thu/impulse không hợp lệ.");
        double duration = (impulse.Count - directIndex - 1) / (double)sampleRate;
        if (duration < .25) return Invalid("Đoạn thu sau direct quá ngắn.");
        int block = Math.Max(1, sampleRate / 1000); // 1 ms, preserves narrow echo peaks
        double binMs = block * 1000.0 / sampleRate;
        int AtMs(double milliseconds) => (int)Math.Ceiling(milliseconds / binMs);
        int count = (impulse.Count - directIndex) / block;
        var peaks = new double[count]; var powers = new double[count]; var peakOffsets = new int[count];
        for (int i = 0; i < count; i++)
        for (int j = 0; j < block; j++)
        {
            double sample = impulse[directIndex + i * block + j];
            if (Math.Abs(sample) > peaks[i]) { peaks[i] = Math.Abs(sample); peakOffsets[i] = j; }
            powers[i] += sample * sample / block;
        }
        double directPeak = peaks.Take(AtMs(6)).DefaultIfEmpty(0).Max();
        if (directPeak < 1e-12) return Invalid("Không có direct impulse đủ mạnh.");
        double[] ending = powers.Skip(Math.Max(10, count * 4 / 5)).Order().ToArray();
        double noisePower = ending.Length > 0 ? ending[ending.Length / 2] : 0;
        double noiseDb = 10 * Math.Log10(Math.Max(1e-30, noisePower / (directPeak * directPeak)));
        if (noiseDb > -25) return Invalid("Direct impulse không đủ nổi so với nền/đuôi; cần thu dài hơn hoặc cải thiện SNR.");
        double threshold = Math.Max(directPeak * Math.Pow(10, settings.MinimumEchoLevelDb / 20), Math.Sqrt(noisePower) * 4);
        var candidates = new List<EchoTap>();
        for (int i = Math.Max(AtMs(15), AtMs(settings.MinimumEchoDelayMs)); i + AtMs(15) < count; i++)
        {
            if (peaks[i] < threshold || peaks[i] < peaks[i - 1] || peaks[i] <= peaks[i + 1]) continue;
            double[] background = Enumerable.Range(i - 12, 25).Where(j => Math.Abs(j - i) > 3).Select(j => peaks[j]).Order().ToArray();
            double local = background[background.Length / 2];
            if (20 * Math.Log10(peaks[i] / Math.Max(1e-30, local)) < settings.MinimumEchoProminenceDb) continue;
            candidates.Add(new EchoTap((i * block + peakOffsets[i]) * 1000.0 / sampleRate, 20 * Math.Log10(peaks[i] / directPeak)));
        }
        var taps = new List<EchoTap>(); double delayMs = double.NaN;
        IEnumerable<double> delays = settings.ExpectedEchoDelayMs is double expected ? new[] { expected }
            : candidates.Where(t => t.DelayMs <= settings.MaximumEchoDelayMs).Select(t => t.DelayMs);
        foreach (double delay in delays)
        {
            if (delay < settings.MinimumEchoDelayMs || delay > settings.MaximumEchoDelayMs) continue;
            var sequence = new List<EchoTap>();
            for (int n = 1; n * delay < duration * 1000 - 15 && n <= 30; n++)
            {
                double tolerance = Math.Max(3, Math.Min(settings.EchoDelayToleranceMs, delay * .05));
                EchoTap? tap = candidates.Where(t => Math.Abs(t.DelayMs - n * delay) <= tolerance).OrderByDescending(t => t.LevelDb).FirstOrDefault();
                if (tap != null) sequence.Add(tap);
            }
            if (sequence.Count > taps.Count) { taps = sequence; delayMs = delay; }
        }
        bool echo = taps.Count >= 2;
        if (echo) delayMs = taps.Select((tap, i) => tap.DelayMs / Math.Max(1, Math.Round(tap.DelayMs / delayMs))).Average();
        else { taps.Clear(); delayMs = double.NaN; }
        double directEnergy = powers.Take(AtMs(10)).Sum();
        int tailStart = Math.Max(AtMs(10), AtMs(settings.TailStartMs)), tailEnd = Math.Min(count - 1, AtMs(settings.TailEndMs));
        double tailEnergy = 0, diffuseEnergy = 0; int diffuseCount = 0, above = 0;
        double occupiedThreshold = Math.Max(noisePower * 10, directPeak * directPeak * 1e-6);
        var fit = new List<(double Time, double Db)>();
        for (int i = tailStart; i < tailEnd; i++)
        {
            double energy = Math.Max(0, powers[i] - noisePower); tailEnergy += energy;
            if (echo && taps.Any(t => Math.Abs(t.DelayMs - i * binMs) <= 15)) continue;
            diffuseEnergy += energy; diffuseCount++;
            if (powers[i] > occupiedThreshold) above++;
        }
        // Fit the wet tail rather than a Schroeder fit dominated by the dry spike.
        for (int i = tailStart; i + AtMs(20) < tailEnd; i += AtMs(20))
        {
            if (echo && taps.Any(t => t.DelayMs >= i * binMs - 15 && t.DelayMs <= i * binMs + 35)) continue;
            double power = powers.Skip(i).Take(AtMs(20)).Average();
            if (power > noisePower * 10) fit.Add((i * binMs / 1000, 10 * Math.Log10(power / (directPeak * directPeak))));
        }
        double decay60 = double.NaN, r2 = double.NaN;
        if (fit.Count >= 8 && fit[^1].Time - fit[0].Time >= .15)
        {
            double mx = fit.Average(p => p.Time), my = fit.Average(p => p.Db);
            double slope = fit.Sum(p => (p.Time - mx) * (p.Db - my)) / Math.Max(1e-30, fit.Sum(p => Math.Pow(p.Time - mx, 2)));
            double total = fit.Sum(p => Math.Pow(p.Db - my, 2));
            r2 = 1 - fit.Sum(p => Math.Pow(p.Db - (my + slope * (p.Time - mx)), 2)) / Math.Max(1e-30, total);
            if (slope < 0 && r2 >= .85 && -slope * (fit[^1].Time - fit[0].Time) >= 10) decay60 = -60 / slope;
        }
        double lateDb = 10 * Math.Log10(Math.Max(1e-30, tailEnergy / Math.Max(1e-30, directEnergy)));
        double diffuseDb = 10 * Math.Log10(Math.Max(1e-30, diffuseEnergy / Math.Max(1e-30, directEnergy)));
        double occupancy = diffuseCount > 0 ? above / (double)diffuseCount : 0;
        // Deconvolution noise can be concentrated near the top of the sweep.
        // A broadband noise floor then masks a clearly decaying midband tail.
        // Require diffuse decay in two separate bands, each against its own
        // measured background; the echo detector still uses the untouched IR.
        double crossover = sampleRate >= 16000 ? 2000 : 1000;
        double lowCut = crossover / 4, highCut = Math.Min(crossover * 4, sampleRate * .45);
        var firstBand = BandEnvelope(impulse, sampleRate, directIndex, block, binMs, tailStart, tailEnd, taps,
            lowCut, crossover, settings.MinimumTailOccupancy);
        var secondBand = BandEnvelope(impulse, sampleRate, directIndex, block, binMs, tailStart, tailEnd, taps,
            crossover, highCut, settings.MinimumTailOccupancy);
        bool reverb = diffuseDb >= settings.MinimumDiffuseTailDb && firstBand.Detected && secondBand.Detected;
        var envelope = new DiffuseEnvelope(reverb, reverb ? Math.Min(firstBand.DurationMs, secondBand.DurationMs) : 0,
            Math.Min(firstBand.PeakSnrDb, secondBand.PeakSnrDb), Math.Min(firstBand.Occupancy, secondBand.Occupancy),
            double.NaN, Math.Min(firstBand.RSquared, secondBand.RSquared));
        if (!double.IsFinite(decay60) && envelope.Detected)
        {
            r2 = envelope.RSquared;
        }
        double endingPower = powers.Skip(Math.Max(AtMs(10), count - AtMs(100))).Average();
        bool noisyTail = noiseDb > Math.Min(-60, settings.MinimumEchoLevelDb - 6);
        bool truncated = endingPower > Math.Max(directPeak * directPeak * 1e-6, noisePower * 4);
        bool longEnough = duration * 1000 >= Math.Max(settings.TailEndMs + 100, 2 * (settings.ExpectedEchoDelayMs ?? settings.MaximumEchoDelayMs) + 100);
        var findings = new List<string>();
        if (settings.ExpectEcho == true && !echo && longEnough && !noisyTail && !truncated) findings.Add("ECHO_MISSING: không thấy chuỗi echo đạt ngưỡng cấu hình.");
        if (settings.ExpectEcho == false && echo) findings.Add("ECHO_UNEXPECTED: thấy chuỗi echo khi cấu hình yêu cầu tắt.");
        if (settings.ExpectReverb == true && !reverb && longEnough && !noisyTail && !truncated) findings.Add("REVERB_MISSING: đuôi liên tục không đạt ngưỡng cấu hình.");
        if (settings.ExpectReverb == false && reverb) findings.Add("REVERB_UNEXPECTED: thấy đuôi liên tục khi cấu hình yêu cầu tắt.");
        bool uncertainAbsence = noisyTail && (!echo || !reverb);
        string verdict = findings.Count > 0 ? "FAIL" : !longEnough || truncated || uncertainAbsence ? "INCONCLUSIVE"
            : settings.ExpectEcho.HasValue || settings.ExpectReverb.HasValue ? "PASS" : "OBSERVED";
        if (!longEnough || truncated) findings.Add("Đuôi thu thiếu hoặc bị cắt; tăng thời gian thu đuôi.");
        if (uncertainAbsence) findings.Add("Nền IR cao; các effect chưa phát hiện vẫn chưa xác định.");
        return new(true, echo, reverb, delayMs, taps, lateDb, diffuseDb, occupancy, decay60, r2, noiseDb, duration, truncated, verdict, findings)
        {
            DiffuseTailDurationMs = envelope.DurationMs,
            DiffuseTailSnrDb = envelope.PeakSnrDb,
            ActiveTailOccupancy = envelope.Occupancy,
            ReverbAnalysisBands = $"{lowCut:0}–{crossover:0} Hz + {crossover:0}–{highCut:0} Hz"
        };
    }

    private sealed record DiffuseEnvelope(bool Detected, double DurationMs, double PeakSnrDb,
        double Occupancy, double Decay60, double RSquared);

    private static DiffuseEnvelope BandEnvelope(IReadOnlyList<double> impulse, int sampleRate, int directIndex,
        int block, double binMs, int start, int end, IReadOnlyList<EchoTap> taps, double lowHz, double highHz, double occupancy)
    {
        double[] filtered = impulse.ToArray();
        // Fourth-order Butterworth high/low pass, applied to the full pre-roll
        // as well. Causal group delay is a few ms, within the excluded echo area.
        foreach (bool highPass in new[] { true, false })
        foreach (double q in new[] { .541196100146197, 1.306562964876377 })
        {
            double omega = 2 * Math.PI * (highPass ? lowHz : highHz) / sampleRate;
            double cos = Math.Cos(omega), alpha = Math.Sin(omega) / (2 * q), a0 = 1 + alpha;
            double b0 = (highPass ? 1 + cos : 1 - cos) / (2 * a0);
            double b1 = (highPass ? -(1 + cos) : 1 - cos) / a0;
            double a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < filtered.Length; i++)
            {
                double x = filtered[i], y = b0 * x + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
                filtered[i] = y; x2 = x1; x1 = x; y2 = y1; y1 = y;
            }
        }
        int count = (filtered.Length - directIndex) / block;
        var powers = new double[count];
        for (int i = 0; i < count; i++)
        for (int j = 0; j < block; j++)
        {
            double value = filtered[directIndex + i * block + j];
            powers[i] += value * value / block;
        }
        double[] background = powers.Skip(Math.Max(10, count * 4 / 5)).Order().ToArray();
        double noise = background.Length > 0 ? background[background.Length / 2] : 0;
        var envelope = AnalyzeDiffuseEnvelope(powers, noise, binMs, start, end, taps, occupancy);
        if (!envelope.Detected) return envelope;
        // A decaying single resonance leaks into both filters near their shared
        // edge. A diffuse tail has noise-like local sample statistics, whereas
        // a sinusoidal ring has kurtosis about 1.5. Assess only strong windows.
        int width = Math.Max(1, (int)Math.Ceiling(20 / binMs));
        int strong = 0, diffuse = 0;
        for (int i = start; i + width < end; i += width)
        {
            if (taps.Any(t => t.DelayMs >= i * binMs - 15 && t.DelayMs <= (i + width) * binMs + 15)) continue;
            int first = directIndex + i * block, length = width * block;
            double sum2 = 0, sum4 = 0;
            for (int j = first; j < first + length; j++)
            {
                double square = filtered[j] * filtered[j]; sum2 += square; sum4 += square * square;
            }
            double power = sum2 / length;
            if (power <= Math.Max(1e-30, noise * 10)) continue;
            strong++;
            if (sum4 / length / (power * power) >= 2) diffuse++;
        }
        bool isDiffuse = strong >= 4 && diffuse / (double)strong >= occupancy;
        return isDiffuse ? envelope : envelope with { Detected = false, DurationMs = 0 };
    }

    private static DiffuseEnvelope AnalyzeDiffuseEnvelope(double[] powers, double noisePower, double binMs,
        int start, int end, IReadOnlyList<EchoTap> taps, double minimumOccupancy)
    {
        var windows = new List<(double Time, double SnrDb)>();
        int width = Math.Max(1, (int)Math.Ceiling(20 / binMs));
        for (int i = start; i + width < end; i += width)
        {
            if (taps.Any(t => t.DelayMs >= i * binMs - 15 && t.DelayMs <= (i + width) * binMs + 15)) continue;
            // The median rejects isolated impulse/ringing peaks. Adjacent echo
            // windows are removed before assessing continuity and decay.
            double[] samples = powers.Skip(i).Take(width).Order().ToArray();
            double power = samples[samples.Length / 2];
            windows.Add(((i + width * .5) * binMs / 1000,
                10 * Math.Log10(Math.Max(1e-30, power / Math.Max(1e-30, noisePower)))));
        }
        var none = new DiffuseEnvelope(false, 0, double.NaN, 0, double.NaN, double.NaN);
        if (windows.Count < 6) return none;
        double[] smooth = windows.Select((_, i) =>
        {
            double[] local = windows.Skip(Math.Max(0, i - 1)).Take(i == 0 ? 2 : 3).Select(w => w.SnrDb).Order().ToArray();
            return (local[(local.Length - 1) / 2] + local[local.Length / 2]) * .5;
        }).ToArray();
        int peak = 0;
        for (int i = 1; i < smooth.Length; i++) if (smooth[i] > smooth[peak]) peak = i;
        // Require a broad tail >= 10 dB above the in-band background. A single
        // 20 ms dropout may occur; >= 60% (configurable) must stay >= 3 dB.
        if (smooth[peak] < 10) return none with { PeakSnrDb = smooth[peak] };
        int last = peak;
        while (last + 1 < windows.Count && (smooth[last + 1] >= 3 || last + 2 < windows.Count && smooth[last + 2] >= 3)) last++;
        var fit = Enumerable.Range(peak, last - peak + 1).Select(i =>
            (windows[i].Time, Db: 10 * Math.Log10(Math.Max(1e-30, Math.Pow(10, smooth[i] / 10) - 1)))).ToArray();
        double span = fit[^1].Time - fit[0].Time;
        double occupancy = windows.Skip(peak).Take(fit.Length).Count(w => w.SnrDb >= 3) / (double)fit.Length;
        if (fit.Length < 6 || span < .10 || occupancy < minimumOccupancy)
            return none with { PeakSnrDb = smooth[peak], Occupancy = occupancy };
        double mx = fit.Average(w => w.Time), my = fit.Average(w => w.Db);
        double slope = fit.Sum(w => (w.Time - mx) * (w.Db - my)) / Math.Max(1e-30, fit.Sum(w => Math.Pow(w.Time - mx, 2)));
        double total = fit.Sum(w => Math.Pow(w.Db - my, 2));
        double r2 = 1 - fit.Sum(w => Math.Pow(w.Db - my - slope * (w.Time - mx), 2)) / Math.Max(1e-30, total);
        double drop = -slope * span;
        bool detected = slope < 0 && drop >= 3 && r2 >= .70;
        // Presence needs less dynamic range than an extrapolated 60 dB decay.
        double t60 = detected && drop >= 10 && r2 >= .85 && span >= .15 ? -60 / slope : double.NaN;
        return new(detected, detected ? span * 1000 : 0, smooth[peak], occupancy, t60, r2);
    }
}
