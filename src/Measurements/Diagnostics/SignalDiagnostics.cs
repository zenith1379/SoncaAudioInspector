namespace SoncaAudioInspector;

public enum SignalDiagnosticKind { Imd, Compression }

public sealed record SignalDiagnosticRequest(SignalDiagnosticKind Kind, double FrequencyHz = 1000,
    double LowToneHz = 60, double HighToneHz = 7000, double LevelDbFs = -24,
    double IncreaseDb = 6, double DurationSeconds = 2, double Limit = 1)
{
    public void Validate(int sampleRate = 44100)
    {
        if (!Enum.IsDefined(Kind) || sampleRate < 22050 || sampleRate > 384000
            || !double.IsFinite(LevelDbFs) || LevelDbFs < -60 || LevelDbFs > -3
            || !double.IsFinite(DurationSeconds) || DurationSeconds < 1 || DurationSeconds > 5
            || !double.IsFinite(Limit) || Limit < .01 || Limit > (Kind == SignalDiagnosticKind.Imd ? 20 : 6))
            throw new ArgumentException("Dùng mức −60…−3 dBFS peak, 1–5 s và ngưỡng dương hợp lệ.");
        if (Kind == SignalDiagnosticKind.Imd)
        {
            if (!double.IsFinite(LowToneHz) || !double.IsFinite(HighToneHz) || LowToneHz < 20 || LowToneHz > 500
                || HighToneHz < 1000 || HighToneHz <= LowToneHz * 10 || LowToneHz <= HighToneHz * .004
                || HighToneHz + 2 * LowToneHz >= sampleRate * .45)
                throw new ArgumentException("IMD: bass 20–500 Hz, tone cao ≥1000 Hz và đủ băng thông hai sideband mỗi phía.");
        }
        else if (!double.IsFinite(FrequencyHz) || FrequencyHz < 40 || FrequencyHz >= Math.Min(10001, sampleRate * .4)
            || !double.IsFinite(IncreaseDb) || IncreaseDb < 3 || IncreaseDb > 12 || LevelDbFs + IncreaseDb > -3)
            throw new ArgumentException("Compression: 40–10000 Hz, tăng 3–12 dB; mức trên không quá −3 dBFS peak.");
    }
}

public sealed record DiagnosticPlotPoint(double X, double Y);
public sealed record SignalDiagnosticResult(SignalDiagnosticKind Kind, bool IsValid, string Status,
    string Message, double Value, string Unit, IReadOnlyList<DiagnosticPlotPoint> Points)
{
    public string Acquisition { get; init; } = "";
    public string Details { get; init; } = "";
    public static SignalDiagnosticResult Invalid(SignalDiagnosticKind kind, string message) =>
        new(kind, false, "CHƯA ĐO ĐƯỢC", message, double.NaN, kind == SignalDiagnosticKind.Imd ? "%" : "dB", []);
}

public sealed record DiagnosticTone(bool IsValid, string Reason, double LevelDbFs, double ThdPercent, double SnrDb);

public static class SignalDiagnostics
{
    private static double Db(double amplitude) => 20 * Math.Log10(Math.Max(1e-12, amplitude));
    public static bool QuietBackground(float[] samples, int rate) => rate >= 22050 && samples.Length >= rate / 4
        && samples.All(v => float.IsFinite(v) && Math.Abs(v) < .05)
        && Db(Math.Sqrt(samples.Sum(v => (double)v * v) / samples.Length)) <= -55;

    public static float[] Generate(SignalDiagnosticRequest request, int rate, bool upperLevel = false)
    {
        request.Validate(rate);
        int count = (int)Math.Round(request.DurationSeconds * rate), fade = Math.Max(1, rate / 50);
        var samples = new float[count];
        double amplitude = Math.Pow(10, (request.LevelDbFs + (upperLevel && request.Kind == SignalDiagnosticKind.Compression ? request.IncreaseDb : 0)) / 20);
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)rate;
            double value = request.Kind == SignalDiagnosticKind.Imd
                ? .8 * Math.Sin(2 * Math.PI * request.LowToneHz * t) + .2 * Math.Sin(2 * Math.PI * request.HighToneHz * t)
                : Math.Sin(2 * Math.PI * request.FrequencyHz * t);
            double edge = Math.Min(1, Math.Min(i, count - 1 - i) / (double)fade);
            samples[i] = (float)(amplitude * value * (.5 - .5 * Math.Cos(Math.PI * edge)));
        }
        return samples;
    }

    private static float[]? Stable(float[] captured, float[] background, int rate, SignalDiagnosticRequest request)
    {
        request.Validate(rate);
        int length = (int)((request.DurationSeconds - .3) * rate);
        if (!QuietBackground(background, rate) || captured.Length < length
            || captured.Any(v => !float.IsFinite(v) || Math.Abs(v) >= .995)) return null;
        // Driver lead/tail may precede/follow the stimulus. Choose a complete
        // steady interval by energy, then verify the requested carriers below.
        double energy = 0, best = -1; int start = 0;
        for (int i = 0; i < captured.Length; i++)
        {
            energy += (double)captured[i] * captured[i];
            if (i >= length) energy -= (double)captured[i - length] * captured[i - length];
            if (i >= length - 1 && energy > best) { best = energy; start = i - length + 1; }
        }
        return captured.AsSpan(start, length).ToArray();
    }

    public static SignalDiagnosticResult AnalyzeImd(float[] captured, float[] background, int rate, SignalDiagnosticRequest request)
    {
        if (request.Kind != SignalDiagnosticKind.Imd) throw new ArgumentException("Cần bài IMD.");
        float[]? stable = Stable(captured, background, rate, request);
        if (stable == null) return SignalDiagnosticResult.Invalid(request.Kind, "Nền cao, bản thu thiếu/lỗi hoặc clipping; giảm mức/gain và đo lại.");
        // Integrate the entire Hann main lobe instead of comparing individual
        // FFT peak bins: different scalloping at carrier/sidebands otherwise
        // biases the ratio (notably at 44.1 kHz).
        var spectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(stable, rate);
        double binWidth = spectrum.FrequenciesHz[1] - spectrum.FrequenciesHz[0];
        double halfBand = 5 * binWidth;
        double Power(double center) => spectrum.FrequenciesHz.Select((f, i) =>
            Math.Abs(f - center) <= halfBand ? spectrum.Magnitudes[i] * spectrum.Magnitudes[i] / 3 : 0).Sum();
        (double Hz, double Power) Carrier(double expected)
        {
            double search = Math.Max(4 * binWidth, expected * .0015);
            int best = -1;
            for (int i = 1; i < spectrum.FrequenciesHz.Length; i++)
                if (Math.Abs(spectrum.FrequenciesHz[i] - expected) <= search
                    && (best < 0 || spectrum.Magnitudes[i] > spectrum.Magnitudes[best])) best = i;
            double hz = best < 0 ? expected : spectrum.FrequenciesHz[best];
            return (hz, Power(hz));
        }
        var low = Carrier(request.LowToneHz); var high = Carrier(request.HighToneHz);
        double mean = stable.Average(v => (double)v);
        double acPower = stable.Sum(v => ((double)v - mean) * ((double)v - mean)) / stable.Length;
        if (request.LowToneHz <= 2 * halfBand || low.Power <= 1e-14 || high.Power <= 1e-14
            || Math.Min(low.Power, high.Power) < acPower * .005 || low.Power + high.Power < acPower * .7)
            return SignalDiagnosticResult.Invalid(request.Kind, "Thiếu carrier hoặc sideband chưa tách được với độ phân giải bản thu.");
        double lowDb = 10 * Math.Log10(low.Power), highDb = 10 * Math.Log10(high.Power);
        var products = new List<DiagnosticPlotPoint>(); double productPower = 0;
        foreach (int order in new[] { -2, -1, 1, 2 })
        {
            double frequency = high.Hz + order * low.Hz, power = Power(frequency);
            productPower += power;
            products.Add(new(frequency, 10 * Math.Log10(Math.Max(1e-24, power / high.Power))));
        }
        double percent = Math.Sqrt(productPower / high.Power) * 100;
        double noise = Db(Math.Sqrt(background.Sum(v => (double)v * v) / background.Length));
        double snr = Math.Min(lowDb, highDb) - noise;
        double requiredSnr = Math.Max(30, -Db(request.Limit / 100) + 6);
        if (!double.IsFinite(percent) || snr < requiredSnr
            || Math.Abs(low.Hz - request.LowToneHz) > Math.Max(1, request.LowToneHz * .01)
            || Math.Abs(high.Hz - request.HighToneHz) > Math.Max(2, request.HighToneHz * .002))
            return SignalDiagnosticResult.Invalid(request.Kind, $"Chưa nhận đủ hai tone sạch/SNR ≥{requiredSnr:0} dB cho ngưỡng đã chọn.");
        return new(request.Kind, true, percent > request.Limit ? "IMD VƯỢT NGƯỠNG" : "IMD TRONG NGƯỠNG",
            $"IMD hai tone 4:1 tại mic; giới hạn sàng lọc {request.Limit:0.###}%. Không xác định riêng lỗi củ loa/ampli/DSP.",
            percent, "%", products)
        { Details = $"Bass {low.Hz:0.##} Hz / {lowDb:0.0} dBFS · cao {high.Hz:0.##} Hz / {highDb:0.0} dBFS · SNR yếu hơn {snr:0.0} dB. Tổng RSS sideband ±F thấp và ±2F thấp, tham chiếu tone cao; tích phân mỗi dải ±{halfBand:0.##} Hz." };
    }

    public static DiagnosticTone MeasureTone(float[] captured, float[] background, int rate, SignalDiagnosticRequest request)
    {
        if (request.Kind != SignalDiagnosticKind.Compression) throw new ArgumentException("Cần bài Compression.");
        float[]? stable = Stable(captured, background, rate, request);
        if (stable == null) return new(false, "Nền cao, thiếu dữ liệu hoặc clipping.", double.NaN, double.NaN, double.NaN);
        var tone = AdvancedAudioMeasurement.AnalyzeTone(stable, rate, request.FrequencyHz, maxHarmonic: 5);
        double level = Db(tone.FundamentalRms), noise = Db(Math.Sqrt(background.Sum(v => (double)v * v) / background.Length));
        bool valid = tone.IsValid && double.IsFinite(level) && double.IsFinite(tone.ThdPercent) && level >= -65 && level - noise >= 30
            && Math.Abs(tone.FundamentalFrequencyHz - request.FrequencyHz) <= Math.Max(1, request.FrequencyHz * .01);
        return new(valid, valid ? "" : "Chưa có đúng tone với SNR ≥30 dB.", level, tone.ThdPercent, level - noise);
    }

    public static SignalDiagnosticResult Compression(DiagnosticTone lower, DiagnosticTone upper, SignalDiagnosticRequest request)
    {
        request.Validate();
        if (request.Kind != SignalDiagnosticKind.Compression) throw new ArgumentException("Cần bài Compression.");
        if (!lower.IsValid || !upper.IsValid || !double.IsFinite(lower.LevelDbFs) || !double.IsFinite(upper.LevelDbFs))
            return SignalDiagnosticResult.Invalid(request.Kind, "Một trong hai lượt chưa hợp lệ. " + lower.Reason + " " + upper.Reason);
        double compression = request.IncreaseDb - (upper.LevelDbFs - lower.LevelDbFs);
        string status = compression > request.Limit ? "CÓ NÉN / LIMITER" : compression < -request.Limit ? "GAIN TĂNG BẤT THƯỜNG" : "CHƯA VƯỢT NGƯỠNG";
        if (Math.Max(lower.ThdPercent, upper.ThdPercent) > 10) status = "MÉO TONE CAO";
        return new(request.Kind, true, status,
            $"Tăng đầu vào {request.IncreaseDb:0.#} dB; mức cơ bản tăng {upper.LevelDbFs - lower.LevelDbFs:0.00} dB. So ngưỡng ±{request.Limit:0.##} dB và line/model. Đây là phép ngắn, chưa đánh giá suy giảm do nóng hay SPL tối đa.",
            compression, "dB", [new(request.LevelDbFs, lower.LevelDbFs), new(request.LevelDbFs + request.IncreaseDb, upper.LevelDbFs)])
        { Details = $"{request.FrequencyHz:0.##} Hz · THD mức thấp/cao {lower.ThdPercent:0.###}/{upper.ThdPercent:0.###}% · SNR {lower.SnrDb:0.0}/{upper.SnrDb:0.0} dB. dBFS tại mic; không phải SPL hiệu chuẩn." };
    }
}

public sealed record SweepPolarityReference(string Polarity, string ContextKey, int SampleRate)
{
    public static bool Reliable(StandardAcousticResult? result) => result is { HasPlausibleDirectArrival: true, IsClipped: false }
        && result.Validity != "INVALID" && result.Polarity is "NORMAL" or "INVERTED"
        && double.IsFinite(result.EstimatedSnrDb) && result.EstimatedSnrDb >= 20;
    public static SweepPolarityReference? From(StandardAcousticResult? result, string contextKey) =>
        Reliable(result) && contextKey.Length > 0 ? new(result!.Polarity, contextKey, result.SampleRate) : null;
    public static SweepPolarityReference? Combine(SweepPolarityReference? a, SweepPolarityReference? b) =>
        a != null && a == b ? a : null;
}

public sealed record PolarityDiagnosticResult(bool IsValid, string Status, string Message, string Measured, string Reference)
{
    public static PolarityDiagnosticResult Compare(StandardAcousticResult? result, string contextKey, SweepPolarityReference? reference)
    {
        string measured = result?.Polarity ?? "Chưa đo";
        if (!SweepPolarityReference.Reliable(result)) return new(false, "CHƯA ĐỦ DỮ LIỆU", "Đo log sweep hợp lệ, đủ SNR/direct và không clipping.", measured, reference?.Polarity ?? "Chưa có");
        if (reference == null || reference.Polarity is not ("NORMAL" or "INVERTED") || reference.ContextKey != contextKey
            || reference.SampleRate != result!.SampleRate || contextKey.Length == 0)
            return new(false, "CHƯA CÓ CHUẨN CÙNG ROUTE", "Đã đọc cực tính IR. Cần lấy lại Line chuẩn cùng ngõ/kênh/gain/calibration để kết luận đấu dây.", measured, reference?.Polarity ?? "Chưa có");
        return new(true, measured == reference.Polarity ? "KHỚP CỰC TÍNH CHUẨN" : "KHÁC CỰC TÍNH CHUẨN",
            measured == reference.Polarity ? "Cực tính phù hợp line chuẩn của đường đo." : "Dấu direct khác line chuẩn: kiểm tra dây +/− và phân tần/đường tín hiệu. Không suy ra lỗi chỉ từ dấu âm tuyệt đối.", measured, reference.Polarity);
    }
}
