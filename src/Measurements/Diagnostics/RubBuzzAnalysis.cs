namespace SoncaAudioInspector;

public sealed record RubBuzzStepResult(string Status, string Reason, double PeakDb = double.NaN,
    double RmsPercent = double.NaN, double CrestDb = double.NaN, int EventCount = 0, double PhaseLock = 0,
    double NoisePeakDb = double.NaN, double NoiseRmsPercent = double.NaN);

public static class RubBuzzAnalysis
{
    public static float[] GenerateStepTone(double physicalFrequencyHz, double levelDbFs, double durationSeconds, int sampleRate)
    {
        if (sampleRate < 22050 || !double.IsFinite(physicalFrequencyHz) || physicalFrequencyHz <= 0 || physicalFrequencyHz >= sampleRate * .45
            || !double.IsFinite(levelDbFs) || levelDbFs < -60 || levelDbFs > -3 || !double.IsFinite(durationSeconds) || durationSeconds < .6 || durationSeconds > 3)
            throw new ArgumentException("Thông số tone bước Rub & Buzz không hợp lệ.");
        int count = (int)Math.Round(durationSeconds * sampleRate), fade = Math.Max(1, (int)(sampleRate * .02));
        var samples = new float[count];
        double amplitude = Math.Pow(10.0, levelDbFs / 20.0);
        for (int i = 0; i < count; i++)
        {
            double edge = Math.Min(1, Math.Min(i, count - 1 - i) / (double)fade);
            samples[i] = (float)(amplitude * (.5 - .5 * Math.Cos(Math.PI * edge)) * Math.Sin(2 * Math.PI * physicalFrequencyHz * i / sampleRate));
        }
        return samples;
    }

    public static RubBuzzStepResult AnalyzeStep(float[] captured, float[] background, int sampleRate,
        double nominalFrequencyHz, double physicalFrequencyHz, double stepSeconds, double? peakLimitDb, double? rmsLimitPercent,
        double noiseMarginDb = 6.0, double highPassMultiple = 20.0)
    {
        RubBuzzStepResult Invalid(string reason) => new("INVALID", $"{nominalFrequencyHz:0.#} Hz: {reason}");
        if (!double.IsFinite(stepSeconds) || stepSeconds < .6 || stepSeconds > 3
            || !double.IsFinite(physicalFrequencyHz) || physicalFrequencyHz <= 0
            || !double.IsFinite(highPassMultiple) || highPassMultiple < 10 || highPassMultiple > 50
            || !double.IsFinite(noiseMarginDb) || noiseMarginDb < 3 || noiseMarginDb > 30
            || peakLimitDb == null && rmsLimitPercent == null
            || peakLimitDb is double p && (!double.IsFinite(p) || p < -80 || p > 0)
            || rmsLimitPercent is double r && (!double.IsFinite(r) || r <= 0 || r > 100))
            return Invalid("CẤU HÌNH LỖI");
        if (sampleRate < 22050 || captured == null || background == null
            || captured.Length < (int)(Math.Min(stepSeconds, .5) * sampleRate) || background.Length < sampleRate / 4)
            return Invalid("THIẾU DỮ LIỆU");
        if (captured.Any(v => !float.IsFinite(v)) || background.Any(v => !float.IsFinite(v))) return Invalid("DỮ LIỆU LỖI");
        if (captured.Any(v => Math.Abs(v) >= .995f) || background.Any(v => Math.Abs(v) >= .995f)) return Invalid("CLIPPING");
        double noiseDbFs = Db(Math.Sqrt(background.Sum(v => (double)v * v) / background.Length));
        if (noiseDbFs > -55 || background.Any(v => Math.Abs(v) >= .05f)) return Invalid("NỀN CAO");
        int length = Math.Min(captured.Length, (int)Math.Round((stepSeconds - .25) * sampleRate));
        double sum = 0, best = -1; int start = 0;
        for (int i = 0; i < captured.Length; i++)
        {
            sum += (double)captured[i] * captured[i];
            if (i >= length) sum -= (double)captured[i - length] * captured[i - length];
            if (i >= length - 1 && sum > best) { best = sum; start = i - length + 1; }
        }
        float[] stable = captured.AsSpan(start, length).ToArray();
        var tone = AdvancedAudioMeasurement.AnalyzeTone(stable, sampleRate, physicalFrequencyHz, maxHarmonic: 5);
        double signalDbFs = Db(tone.FundamentalRms);
        if (!tone.IsValid || Math.Abs(tone.FundamentalFrequencyHz - physicalFrequencyHz) > Math.Max(.5, physicalFrequencyHz * .015)
            || signalDbFs < -60 || signalDbFs - noiseDbFs < 20) return Invalid("TÍN HIỆU CHƯA ĐẠT");
        double f0 = tone.FundamentalFrequencyHz;
        if (length * f0 / sampleRate < 24) return Invalid("THU QUÁ NGẮN");
        double fc = Math.Max(500, highPassMultiple * f0), fMax = Math.Min(.45 * sampleRate, 16000);
        if (fc >= fMax - 500) return Invalid("DẢI LỌC RỖNG");
        int harmonicCount = Math.Clamp((int)Math.Floor((fc - 1) / f0), 1, 40);
        double[] coeffs = AdvancedAudioMeasurement.FitHarmonicModel(stable, sampleRate, f0, harmonicCount);
        if (coeffs.Length == 0) return Invalid("KHÔNG KHỚP ĐƯỢC MÔ HÌNH TONE");
        double[] residual = AdvancedAudioMeasurement.BuildResidual(stable, sampleRate, f0, harmonicCount, coeffs);
        double[] bandResidualFull = AdvancedAudioMeasurement.BandLimit(residual, sampleRate, fc, fMax);
        double[] bandNoiseFull = AdvancedAudioMeasurement.BandLimit(background.Select(v => (double)v).ToArray(), sampleRate, fc, fMax);
        int trim = Math.Min(bandResidualFull.Length / 10, Math.Max(16, (int)(sampleRate * .02)));
        double[] bandResidual = bandResidualFull.AsSpan(trim, bandResidualFull.Length - 2 * trim).ToArray();
        int noiseTrim = Math.Min(trim, bandNoiseFull.Length / 10);
        double[] bandNoise = bandNoiseFull.AsSpan(noiseTrim, bandNoiseFull.Length - 2 * noiseTrim).ToArray();
        double tonePeak = Math.Sqrt(2) * tone.FundamentalRms;
        double residualPeak = bandResidual.Select(Math.Abs).DefaultIfEmpty(0).Max(), residualRms = Rms(bandResidual);
        double peakDb = Db(residualPeak / Math.Max(tonePeak, 1e-12)), crestDb = Db(residualPeak / Math.Max(residualRms, 1e-12));
        double rmsPercent = residualRms / Math.Max(tone.FundamentalRms, 1e-12) * 100;
        var (events, envelope, thresholdDbc) = AdvancedAudioMeasurement.DetectTransientEvents(bandResidual, sampleRate, f0, tone.FundamentalRms);
        double phaseLock1 = AdvancedAudioMeasurement.CalculatePhaseConsistency(events);
        double phaseLock2 = events.Count < 2 ? 0 : Math.Sqrt(
            Math.Pow(events.Sum(e => Math.Cos(2 * e.FundamentalPhaseDegrees * Math.PI / 180)), 2)
            + Math.Pow(events.Sum(e => Math.Sin(2 * e.FundamentalPhaseDegrees * Math.PI / 180)), 2)) / events.Count;
        double phaseLock = Math.Max(phaseLock1, phaseLock2);
        double noisePeakDb = Db(bandNoise.Select(Math.Abs).DefaultIfEmpty(0).Max() / Math.Max(tonePeak, 1e-12));
        double noiseRmsPercent = Rms(bandNoise) / Math.Max(tone.FundamentalRms, 1e-12) * 100;
        if (peakLimitDb is double pLim && noisePeakDb > pLim - noiseMarginDb
            || rmsLimitPercent is double rLim && noiseRmsPercent > rLim * Math.Pow(10, -noiseMarginDb / 20))
            return new("INVALID", $"{nominalFrequencyHz:0.#} Hz: nền ồn trong dải rè quá cao để kết luận", peakDb, rmsPercent, crestDb, events.Count, phaseLock, noisePeakDb, noiseRmsPercent);
        bool peakFailed = peakLimitDb is double pLimit && peakDb > pLimit;
        bool rmsFailed = rmsLimitPercent is double rLimit && rmsPercent > rLimit;
        string status = peakFailed || rmsFailed ? "FAIL" : "PASS";
        string pattern = events.Count >= 2 && phaseLock >= .75 ? "xung khóa pha (nghi rè/cọ coil)"
            : events.Count > 0 ? "xung ngẫu nhiên (nghi hạt rác hoặc nhiễu ngoài)" : "không có xung nổi bật";
        string reason = $"{nominalFrequencyHz:0.#} Hz (f0={f0:0.0} Hz): {status} — Peak {peakDb:0.0} dB, RMS {rmsPercent:0.00}%, Crest {crestDb:0.0} dB, nền [{noisePeakDb:0.0} dB / {noiseRmsPercent:0.00}%], xung={events.Count} (lock={phaseLock:0.00}, {pattern})";
        return new(status, reason, peakDb, rmsPercent, crestDb, events.Count, phaseLock, noisePeakDb, noiseRmsPercent);
    }
    private static double Db(double value) => 20 * Math.Log10(Math.Max(value, 1e-12));
    private static double Rms(double[] samples) => Math.Sqrt(samples.Sum(v => v * v) / Math.Max(1, samples.Length));
}
