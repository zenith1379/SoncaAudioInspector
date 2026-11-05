using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record FeedbackTone(double InputHz, double MeasuredHz, double LevelDbFs,
    double Purity, double SpreadHz, string Problem)
{
    public bool IsValid => string.IsNullOrEmpty(Problem)
        && double.IsFinite(InputHz) && double.IsFinite(MeasuredHz) && Math.Abs(MeasuredHz - InputHz) < 30
        && double.IsFinite(LevelDbFs) && LevelDbFs >= -65 && LevelDbFs < 0
        && double.IsFinite(Purity) && Purity >= 0.98 && Purity <= 1
        && double.IsFinite(SpreadHz) && SpreadHz >= 0 && SpreadHz <= 0.3;
}

public sealed record FeedbackVerdict(string Code, string Message, double ShiftHz, double ClockPpm);

/// <summary>Open-loop test for a constant-Hz frequency shifter, not a gain-before-feedback test.</summary>
public static class FeedbackShiftMeasurement
{
    public static readonly IReadOnlyList<double> Frequencies = Array.AsReadOnly(new[] { 500.0, 1000.0, 2000.0 });
    public const double DetectionFloorHz = 0.7;

    public static FeedbackTone Analyze(float[] samples, int sampleRate, double inputHz)
    {
        FeedbackTone Invalid(string reason) => new(inputHz, 0, -180, 0, 0, reason);
        if (sampleRate < 8000 || sampleRate > 384000 || samples.Length < sampleRate * 4 || !Frequencies.Contains(inputHz))
            return Invalid("Thu chưa đủ thời gian. Kiểm tra dây/sound card rồi đo lại.");
        if (samples.Any(x => !float.IsFinite(x))) return Invalid("Dữ liệu thu không hợp lệ.");
        if (samples.Any(x => Math.Abs(x) >= 0.95f)) return Invalid("Ngõ thu gần clipping; giảm gain và đo lại.");
        int start = (samples.Length - sampleRate * 3) / 2;
        var frequencies = new List<double>();
        var purities = new List<double>();
        var levels = new List<double>();
        for (int frame = 0; frame < 3; frame++)
        {
            int offset = start + frame * sampleRate;
            double mean = 0;
            for (int i = 0; i < sampleRate; i++) mean += samples[offset + i] / sampleRate;
            int fftSize = 1;
            while (fftSize < sampleRate * 4) fftSize <<= 1;
            var fft = new Complex[fftSize];
            double power = 0;
            for (int i = 0; i < sampleRate; i++)
            {
                double value = samples[offset + i] - mean;
                power += value * value;
                fft[i] = value * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (sampleRate - 1)));
            }
            double level = 10 * Math.Log10(Math.Max(1e-18, power / sampleRate));
            if (level < -65) return Invalid("Tín hiệu quá nhỏ; kiểm tra MIC IN, mute và mức phát/thu.");
            Fourier.Forward(fft, FourierOptions.Matlab);
            double binHz = (double)sampleRate / fftSize;
            int low = (int)Math.Ceiling((inputHz - 30) / binHz);
            int high = (int)Math.Floor((inputHz + 30) / binHz);
            int peak = low;
            for (int i = low + 1; i <= high; i++)
                if (fft[i].Magnitude > fft[peak].Magnitude) peak = i;
            if (peak <= low + 1 || peak >= high - 1) return Invalid("Không khóa được tone trong phạm vi ±30 Hz.");
            double a = Math.Log(Math.Max(1e-30, fft[peak - 1].Magnitude));
            double b = Math.Log(Math.Max(1e-30, fft[peak].Magnitude));
            double c = Math.Log(Math.Max(1e-30, fft[peak + 1].Magnitude));
            double denominator = a - 2 * b + c;
            double fraction = Math.Abs(denominator) < 1e-15 ? 0 : Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
            double frequency = (peak + fraction) * binHz;
            double sine = 0, cosine = 0;
            for (int i = 0; i < sampleRate; i++)
            {
                double phase = 2 * Math.PI * frequency * i / sampleRate;
                sine += (samples[offset + i] - mean) * Math.Sin(phase);
                cosine += (samples[offset + i] - mean) * Math.Cos(phase);
            }
            double purity = Math.Clamp(2 * (sine * sine + cosine * cosine) / (sampleRate * power), 0, 1);
            frequencies.Add(frequency);
            purities.Add(purity);
            levels.Add(level);
        }
        double spread = frequencies.Max() - frequencies.Min();
        string problem = purities.Min() < 0.98
            ? "Âm thu bị lẫn nhiễu hoặc méo. Giữ yên lặng, tắt echo; nhờ kỹ thuật kiểm tra mức âm rồi đo lại."
            : spread > 0.3 ? "Âm thu không ổn định. Giữ nguyên loa/micro rồi đo lại." : "";
        return new(inputHz, frequencies.Average(), levels.Average(), purities.Min(), spread, problem);
    }

    public static FeedbackVerdict Evaluate(IReadOnlyList<FeedbackTone> measured, double? expectedHz, double toleranceHz)
    {
        FeedbackVerdict Result(string code, string message, double shift = 0, double ppm = 0) => new(code, message, shift, ppm);
        if (!double.IsFinite(toleranceHz) || toleranceHz < 0.1 || toleranceHz > 5
            || (expectedHz.HasValue && (!double.IsFinite(expectedHz.Value) || Math.Abs(expectedHz.Value) < 1
                || Math.Abs(expectedHz.Value) > 20 || toleranceHz >= Math.Abs(expectedHz.Value) - DetectionFloorHz)))
            return Result("INVALID", "Giới hạn chưa hợp lệ: độ dịch 1–20 Hz có dấu; dung sai phải tách được trạng thái không di tần.");
        if (measured.Count != 3 || measured.Any(x => !x.IsValid))
            return Result("INVALID", "CHƯA ĐỦ ĐIỀU KIỆN — Kiểm tra âm thu và đo lại. Không kết luận mất chống hú khi chưa thu hợp lệ.");
        for (int i = 0; i < 3; i++)
            if (measured[i].InputHz != Frequencies[i])
                return Result("INVALID", "Bộ tần số thử không hợp lệ; đo lại.");
        // Reference is the commanded stimulus, not a fabricated OFF capture.
        // delta = constant shift + proportional clock error * input frequency.
        var delta = measured.Select(x => x.MeasuredHz - x.InputHz).ToArray();
        var fit = Fit(delta);
        // Conservative software repeatability margin, not calibrated measurement uncertainty.
        double margin = 0.05 + measured.Max(x => x.SpreadHz) + 2 * fit.Residual;
        if (fit.Residual > 0.4 || Math.Abs(fit.Slope) > 0.0005)
            return Result("INCONCLUSIVE", "Độ lệch không giống di tần cố định theo Hz; kiểm tra clock, xử lý pitch hoặc di tần biến thiên.", fit.Intercept, fit.Slope * 1e6);
        if (Math.Abs(fit.Intercept) + margin < DetectionFloorHz)
            return Result("FAIL_NO_SHIFT", "LỖI MẤT CHỐNG HÚ DI TẦN — Đã thu hợp lệ qua đường đo đã xác nhận nhưng không có di tần. Chuyển kỹ thuật kiểm tra.", fit.Intercept, fit.Slope * 1e6);
        if (Math.Abs(fit.Intercept) - margin <= DetectionFloorHz)
            return Result("INCONCLUSIVE", "CẦN ĐO LẠI — Độ dịch quá gần ngưỡng phát hiện. Chưa kết luận loa đạt.", fit.Intercept, fit.Slope * 1e6);
        if (!expectedHz.HasValue)
            return Result("ACTIVE", "Có dấu hiệu mạch di tần hoạt động. Chưa có giới hạn model để kết luận ĐẠT/KHÔNG ĐẠT.", fit.Intercept, fit.Slope * 1e6);
        double error = Math.Max(delta.Select((x, i) => Math.Abs(x - fit.Slope * Frequencies[i] - expectedHz.Value)).Max(), Math.Abs(fit.Intercept - expectedHz.Value));
        if (error + margin >= toleranceHz && error - margin <= toleranceHz)
            return Result("INCONCLUSIVE", "CẦN ĐO LẠI — Kết quả sát giới hạn. Giữ nguyên thiết lập và đo lại; chưa xếp loa vào nhóm đạt.", fit.Intercept, fit.Slope * 1e6);
        bool pass = error + margin < toleranceHz;
        return Result(pass ? "PASS" : "FAIL", pass ? "ĐẠT giới hạn di tần đã nhập cho phép thử MIC IN."
            : "KHÔNG ĐẠT giới hạn di tần đã nhập; kiểm tra lại thiết lập và đối chiếu loa mẫu.", fit.Intercept, fit.Slope * 1e6);
    }

    private static (double Intercept, double Slope, double Residual) Fit(double[] y)
    {
        double mx = Frequencies.Average(), my = y.Average();
        double slope = Frequencies.Select((x, i) => (x - mx) * (y[i] - my)).Sum()
            / Frequencies.Sum(x => (x - mx) * (x - mx));
        double intercept = my - slope * mx;
        return (intercept, slope, Frequencies.Select((x, i) => Math.Abs(y[i] - intercept - slope * x)).Max());
    }
}
