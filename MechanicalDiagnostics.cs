using System.Numerics;

namespace SoncaAudioInspector;

public enum MechanicalTestKind { LooseParticle, AirLeak }
public sealed record MechanicalTestRequest(MechanicalTestKind Kind, double FrequencyHz = 40,
    double LevelDbFs = -18, double DurationSeconds = 1.5, double PeakMarginDb = 12)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !double.IsFinite(FrequencyHz) || FrequencyHz < 20 || FrequencyHz > 200
            || !double.IsFinite(LevelDbFs) || LevelDbFs < -60 || LevelDbFs > -3
            || !double.IsFinite(DurationSeconds) || DurationSeconds < 1 || DurationSeconds > 5
            || !double.IsFinite(PeakMarginDb) || PeakMarginDb < 6 || PeakMarginDb > 30)
            throw new ArgumentException("Dùng 20–200 Hz, −60…−3 dBFS, 1–5 s và ngưỡng xung 6–30 dB trên nền.");
        if ((DurationSeconds - .35) * FrequencyHz < 24)
            throw new ArgumentException("Tăng thời lượng để có ít nhất 24 chu kỳ ổn định (20 Hz cần từ 2 s).");
    }
}
public sealed record MechanicalEvent(double TimeMs, double PeakDbFs, double PhaseDegrees);
public sealed record MechanicalTestResult(MechanicalTestKind Kind, bool IsValid, string Status, string Message,
    double FundamentalHz, double SignalDbFs, double NoiseDbFs, double HighBandDbFs, double ThresholdDbFs,
    double ModulationPercent, double ModulationProminenceDb, IReadOnlyList<MechanicalEvent> Events,
    IReadOnlyList<TimeDomainPoint> Envelope)
{
    public string Acquisition { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public double AnalysisSeconds { get; init; }
    public static MechanicalTestResult Unavailable(MechanicalTestKind kind, string status, string message) =>
        new(kind, false, status, message, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, Array.Empty<MechanicalEvent>(), Array.Empty<TimeDomainPoint>());
}

public static class MechanicalDiagnostics
{
    private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-12, value));
    private static double Rms(IReadOnlyList<double> samples) => Math.Sqrt(samples.Sum(v => v * v) / Math.Max(1, samples.Count));

    public static float[] GenerateTone(MechanicalTestRequest request, int sampleRate)
    {
        request.Validate();
        if (sampleRate < 22050 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int count = (int)Math.Round(request.DurationSeconds * sampleRate), fade = (int)(sampleRate * .02);
        var samples = new float[count]; double amplitude = Math.Pow(10, request.LevelDbFs / 20);
        for (int i = 0; i < count; i++)
        {
            double edge = Math.Min(1, Math.Min(i, count - 1 - i) / (double)fade);
            samples[i] = (float)(amplitude * (.5 - .5 * Math.Cos(Math.PI * edge)) * Math.Sin(2 * Math.PI * request.FrequencyHz * i / sampleRate));
        }
        return samples;
    }

    public static MechanicalTestResult Analyze(float[] captured, float[] background, int rate, MechanicalTestRequest request)
    {
        request.Validate();
        MechanicalTestResult Invalid(string code, string message) => MechanicalTestResult.Unavailable(request.Kind, code, message);
        if (rate < 22050 || captured.Length < rate || background.Length < rate / 4)
            return Invalid("THIẾU DỮ LIỆU", "Cần ngõ thu ≥22,05 kHz, ít nhất 1 s tín hiệu và 0,25 s nền.");
        if (captured.Any(v => !float.IsFinite(v)) || background.Any(v => !float.IsFinite(v)))
            return Invalid("DỮ LIỆU LỖI", "Bản thu có NaN/Infinity; đo lại.");
        if (captured.Any(v => Math.Abs(v) >= .995) || background.Any(v => Math.Abs(v) >= .995))
            return Invalid("CLIPPING", "Ngõ thu quá tải; giảm mức phát/gain rồi đo lại.");
        double noiseDb = Db(Math.Sqrt(background.Sum(v => (double)v * v) / background.Length));
        if (noiseDb > -55 || background.Any(v => Math.Abs(v) >= .05))
            return Invalid("NỀN CAO", "Nền trước phát cao hoặc có xung ngoại lai; chờ yên rồi đo lại.");
        // Find the strongest complete stable interval, excluding onset/offset.
        // Capture includes driver lead/tail and is longer than the actual tone.
        int length = Math.Min(captured.Length, (int)((request.DurationSeconds - .25) * rate));
        double sum = 0, best = -1; int start = 0;
        for (int i = 0; i < captured.Length; i++)
        {
            sum += (double)captured[i] * captured[i];
            if (i >= length) sum -= (double)captured[i - length] * captured[i - length];
            if (i >= length - 1 && sum > best) { best = sum; start = i - length + 1; }
        }
        float[] stable = captured.AsSpan(start, length).ToArray();
        var tone = AdvancedAudioMeasurement.AnalyzeTone(stable, rate, request.FrequencyHz, maxHarmonic: 5);
        double signalDb = Db(tone.FundamentalRms);
        if (!tone.IsValid || Math.Abs(tone.FundamentalFrequencyHz - request.FrequencyHz) > Math.Max(.5, request.FrequencyHz * .015)
            || signalDb < -60 || signalDb - noiseDb < 20)
            return Invalid("TÍN HIỆU CHƯA ĐẠT", "Chưa nhận đúng tone đủ mạnh/SNR ≥20 dB. Kiểm tra route, kênh, mức phát và micro.");
        double f = tone.FundamentalFrequencyHz;
        if (length * f / rate < 24) return Invalid("THU QUÁ NGẮN", "Cần ít nhất 24 chu kỳ ổn định để phân biệt xung và điều biên.");
        double low = request.Kind == MechanicalTestKind.LooseParticle ? 2000 : 3000;
        double high = request.Kind == MechanicalTestKind.LooseParticle ? rate * .45 : 8000;
        bool rejectHarmonics = request.Kind == MechanicalTestKind.AirLeak;
        // LP uses phase tracking below. Narrow spectral notches would give a
        // single impact a long oscillating tail and inflate its event count.
        double[] envelope = AnalyticEnvelope(stable, rate, low, high, f, rejectHarmonics);
        double[] noiseEnvelope = AnalyticEnvelope(background, rate, low, high, f, rejectHarmonics);
        int trim = (int)(rate * .05);
        envelope = envelope.AsSpan(trim, envelope.Length - trim * 2).ToArray();
        double[] noiseSorted = noiseEnvelope.AsSpan(trim, Math.Max(1, noiseEnvelope.Length - trim * 2)).ToArray();
        Array.Sort(noiseSorted);
        double[] sorted = envelope.Order().ToArray();
        double threshold = Math.Max(1e-6, Math.Max(noiseSorted[(int)((noiseSorted.Length - 1) * .995)] * Math.Pow(10, request.PeakMarginDb / 20),
            sorted[sorted.Length / 2] * Math.Pow(10, request.PeakMarginDb / 20)));
        var events = new List<MechanicalEvent>();
        for (int i = 0; i < envelope.Length; i++)
        {
            if (envelope[i] <= threshold) continue;
            int peak = i, quiet = 0;
            while (++i < envelope.Length)
            {
                if (envelope[i] > envelope[peak]) peak = i;
                quiet = envelope[i] <= threshold ? quiet + 1 : 0;
                if (quiet > rate * .005 && i - peak > rate * .008) break;
            }
            double time = (peak + trim) / (double)rate;
            events.Add(new(time * 1000, Db(envelope[peak]), time * f % 1 * 360));
        }
        double Coherence(int order) => events.Count == 0 ? 0 : Complex.Abs(events.Aggregate(Complex.Zero,
            (a, e) => a + Complex.FromPolarCoordinates(1, order * e.PhaseDegrees * Math.PI / 180))) / events.Count;
        double phaseLock = Math.Max(Coherence(1), Coherence(2));
        double mean = envelope.Average();
        double Component(double frequency, int first = 0, int count = -1)
        {
            if (count < 0) count = envelope.Length;
            Complex value = Complex.Zero;
            for (int i = first; i < first + count; i++)
                value += (envelope[i] - mean) * Complex.FromPolarCoordinates(1, 2 * Math.PI * frequency * i / rate);
            return 2 * value.Magnitude / count;
        }
        double modulation = Math.Sqrt(Math.Pow(Component(f), 2) + Math.Pow(Component(f * 2), 2));
        double[] surrounding = new[] { -13.0, -9, -6, 6, 9, 13 }.Select(offset => Component(f + offset)).Order().ToArray();
        double prominence = Db(modulation / Math.Max(1e-12, surrounding[surrounding.Length / 2]));
        double depth = modulation / Math.Max(1e-12, mean) * 100;
        double highDb = Db(Rms(envelope) / Math.Sqrt(2));
        double noiseHighDb = Db(Rms(noiseEnvelope) / Math.Sqrt(2));
        string status, message;
        if (request.Kind == MechanicalTestKind.LooseParticle)
        {
            bool asynchronous = events.Count >= 3 && phaseLock < .85;
            status = asynchronous ? "CÓ XUNG PHI CHU KỲ" : events.Count > 0 ? "CẦN ĐO LẠI" : "CHƯA THẤY XUNG";
            message = asynchronous ? "Có dấu hiệu va chạm phi chu kỳ. Có thể do hạt rác hoặc nhiễu ngoại lai; lặp lại trong điều kiện yên và đối chiếu mẫu chuẩn."
                : events.Count > 0 ? "Xung ít hoặc còn khóa pha theo tone; chưa đủ bằng chứng hạt rác. Đo lại để kiểm tra tính lặp."
                : "Chưa thấy xung vượt ngưỡng trong lượt này; không loại trừ hạt rác chưa bị kích thích.";
        }
        else
        {
            int half = envelope.Length / 2;
            double firstDepth = Math.Max(Component(f, 0, half), Component(2 * f, 0, half)) / Math.Max(1e-12, mean);
            double secondDepth = Math.Max(Component(f, half, half), Component(2 * f, half, half)) / Math.Max(1e-12, mean);
            bool modulated = depth >= 15 && prominence >= 10 && highDb >= noiseHighDb + 6 && Math.Min(firstDepth, secondDepth) >= .08;
            status = modulated ? "CÓ NHIỄU ĐIỀU BIÊN" : "CHƯA THẤY ĐIỀU BIÊN RÕ";
            message = modulated ? "Nhiễu 3–8 kHz điều biên theo bass: nghi rò khí/tiếng dòng khí. Định vị mic gần đường keo, phân biệt tiếng cổng hơi và đối chiếu mẫu chuẩn."
                : "Chưa thấy điều biên đủ nổi trên nền; không kết luận kín khí hoặc loại trừ lỗ rò nhỏ.";
        }
        int stride = Math.Max(1, envelope.Length / 2500);
        var curve = new List<TimeDomainPoint>();
        for (int i = 0; i < envelope.Length; i += stride)
        {
            int end = Math.Min(envelope.Length, i + stride), peak = i;
            for (int j = i + 1; j < end; j++) if (envelope[j] > envelope[peak]) peak = j;
            curve.Add(new((peak + trim) / (double)rate, Db(envelope[peak])));
        }
        return new(request.Kind, true, status, message, f, signalDb, noiseDb, highDb, Db(threshold), depth, prominence, events, curve)
            { AnalysisSeconds = envelope.Length / (double)rate };
    }

    // Hilbert analytic band, with narrow synchronous-harmonic rejection. This is
    // a diagnostic implementation, not Klippel's proprietary ALD or MODrel scale.
    private static double[] AnalyticEnvelope(float[] samples, int rate, double low, double high, double fundamental, bool rejectHarmonics)
    {
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)samples.Length);
        var spectrum = new Complex[size];
        int fade = Math.Max(1, (int)(rate * .025));
        for (int i = 0; i < samples.Length; i++)
        {
            double edge = Math.Min(1, Math.Min(i, samples.Length - 1 - i) / (double)fade);
            spectrum[i] = samples[i] * (.5 - .5 * Math.Cos(Math.PI * edge));
        }
        var fft = new SweepFourier(); fft.Forward(spectrum, samples.Length);
        double binWidth = rate / (double)size;
        for (int bin = 0; bin < size; bin++)
        {
            double hz = bin * binWidth;
            double distance = Math.Abs(hz - Math.Round(hz / fundamental) * fundamental);
            if (bin == 0 || bin >= size / 2 || hz < low || hz > high
                || rejectHarmonics && distance < Math.Min(fundamental * .12, Math.Max(2, binWidth * 2.5)))
                spectrum[bin] = Complex.Zero;
            else
            {
                double edge = Math.Min(1, Math.Min(hz - low, high - hz) / 200);
                spectrum[bin] *= 1 - Math.Cos(Math.PI * edge);
            }
        }
        fft.Inverse(spectrum);
        return spectrum.Take(samples.Length).Select(v => v.Magnitude).ToArray();
    }
}
