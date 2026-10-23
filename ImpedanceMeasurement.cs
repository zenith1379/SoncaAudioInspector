using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record ImpedanceSettings(double SenseOhms = 100, double KnownOhms = 10, double StartHz = 2000,
    double EndHz = 18000, double LevelDbFs = -42, double Seconds = 3)
{
    public void Validate(int rate = 44100)
    {
        if (new[] { SenseOhms, KnownOhms, StartHz, EndHz, LevelDbFs, Seconds }.Any(v => !double.IsFinite(v))
            || SenseOhms < 47 || SenseOhms > 1000 || KnownOhms < 1 || KnownOhms > 200
            || StartHz < 500 || EndHz <= StartHz || EndHz > Math.Min(20000, rate * .45)
            || LevelDbFs < -60 || LevelDbFs > -30 || Seconds < 3 || Seconds > 10)
            throw new ArgumentException("Đo treble: Rsense không cảm 47–1000 Ω, R chuẩn 1–200 Ω, 500 Hz–20 kHz, mức −60…−30 dBFS peak, 3–10 s.");
    }
    public double[] Frequencies() => Enumerable.Range(0, 100).Select(i => Math.Round(StartHz * Math.Pow(2, i / 12.0) * 2) / 2)
        .Where(f => f <= EndHz).ToArray();
}
public sealed record ImpedanceRatio(double FrequencyHz, double Real, double Imaginary, double ReferenceSnrDb, double LoadSnrDb)
{ public Complex Ratio => new(Real, Imaginary); }
public sealed record CalibratedImpedancePoint(double FrequencyHz, double ResistanceOhms, double ReactanceOhms)
{
    public double MagnitudeOhms => Math.Sqrt(ResistanceOhms * ResistanceOhms + ReactanceOhms * ReactanceOhms);
    public double PhaseDegrees => Math.Atan2(ReactanceOhms, ResistanceOhms) * 180 / Math.PI;
}
public sealed record ImpedanceCalibration(string Context, ImpedanceSettings Settings, ImpedanceRatio[] Open,
    ImpedanceRatio[] Short, ImpedanceRatio[] Known, DateTimeOffset CreatedUtc);
public sealed record ImpedanceResult(bool IsValid, string Message, CalibratedImpedancePoint[] Points)
{
    public bool OpenOrHighImpedance { get; init; }
    public CalibratedImpedancePoint[] DiagnosticPoints { get; init; } = Array.Empty<CalibratedImpedancePoint>();
}

public static class ImpedanceMeasurement
{
    public static float[] Generate(ImpedanceSettings settings, int rate)
    {
        settings.Validate(rate); var frequencies = settings.Frequencies();
        var signal = new float[(int)(rate * settings.Seconds)]; double peak = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            double value = 0;
            for (int k = 0; k < frequencies.Length; k++) value += Math.Cos(2 * Math.PI * frequencies[k] * i / rate - Math.PI * k * (k - 1) / frequencies.Length);
            signal[i] = (float)value; peak = Math.Max(peak, Math.Abs(value));
        }
        double scale = Math.Pow(10, settings.LevelDbFs / 20) / Math.Max(1e-12, peak);
        for (int i = 0; i < signal.Length; i++) signal[i] = (float)(signal[i] * scale * Math.Min(1, Math.Min(i, signal.Length - i - 1) / (rate * .02)));
        return signal;
    }

    public static ImpedanceRatio[] Measure(float[] reference, float[] load, int rate, ImpedanceSettings settings)
    {
        settings.Validate(rate);
        if (reference.Length != load.Length || reference.Length < rate * 2.5
            || reference.Concat(load).Any(v => !float.IsFinite(v) || Math.Abs(v) >= .98))
            throw new InvalidOperationException("Bản thu hai kênh thiếu, không đồng thời hoặc clipping; chưa tính trở kháng.");
        int count = rate * 2, start = 0; double best = 0, power = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            power += reference[i] * reference[i]; if (i >= count) power -= reference[i - count] * reference[i - count];
            if (i >= count - 1 && power > best) { best = power; start = i - count + 1; }
        }
        if (best / count < 1e-12) throw new InvalidOperationException("Không có tín hiệu tham chiếu; kiểm tra ngõ phát và kênh thu 1.");
        int fft = 1; while (fft < count) fft <<= 1;
        var x = new Complex[fft]; var y = new Complex[fft];
        for (int i = 0; i < count; i++)
        {
            double window = .5 - .5 * Math.Cos(2 * Math.PI * i / (count - 1));
            x[i] = reference[start + i] * window; y[i] = load[start + i] * window;
        }
        Fourier.Forward(x, FourierOptions.Matlab); Fourier.Forward(y, FourierOptions.Matlab);
        var rows = new List<ImpedanceRatio>();
        foreach (double frequency in settings.Frequencies())
        {
            int center = (int)Math.Round(frequency * fft / rate), radius = Math.Max(3, (int)Math.Ceiling(frequency * .003 * fft / rate));
            int bin = Enumerable.Range(Math.Max(8, center - radius), radius * 2 + 1).MaxBy(k => x[k].Magnitude);
            double Noise(Complex[] values)
            {
                var powers = Enumerable.Range(bin - 15, 31).Where(k => Math.Abs(k - bin) >= 5).Select(k => values[k].Magnitude * values[k].Magnitude).Order().ToArray();
                return Math.Sqrt(powers[powers.Length / 2]);
            }
            double referenceSnr = 20 * Math.Log10(Math.Max(1e-20, x[bin].Magnitude) / Math.Max(1e-20, Noise(x)));
            double loadSnr = 20 * Math.Log10(Math.Max(1e-20, y[bin].Magnitude) / Math.Max(1e-20, Noise(y)));
            if (referenceSnr < 30) throw new InvalidOperationException($"Tín hiệu tham chiếu {frequency:0} Hz chưa nổi đủ 30 dB trên nền.");
            var h = y[bin] / x[bin]; rows.Add(new(frequency, h.Real, h.Imaginary, referenceSnr, loadSnr));
        }
        return rows.ToArray();
    }

    public static void ValidateCalibration(ImpedanceCalibration cal, string context)
    {
        cal.Settings.Validate();
        if (cal.Context != context || cal.Open.Length < 3 || cal.Short.Length != cal.Open.Length || cal.Known.Length != cal.Open.Length)
            throw new InvalidOperationException("Thiếu chuẩn hở/ngắn/R chuẩn cùng ngõ, gain, rate, mức phát và dải tần.");
        for (int i = 0; i < cal.Open.Length; i++)
        {
            var open = cal.Open[i]; var shorted = cal.Short[i]; var known = cal.Known[i];
            if (new[] { open, shorted, known }.Any(p => new[] { p.FrequencyHz, p.Real, p.Imaginary, p.ReferenceSnrDb, p.LoadSnrDb }.Any(v => !double.IsFinite(v)))
                || open.FrequencyHz != shorted.FrequencyHz || open.FrequencyHz != known.FrequencyHz
                || open.ReferenceSnrDb < 30 || open.LoadSnrDb < 30 || known.ReferenceSnrDb < 30 || known.LoadSnrDb < 30
                || open.Ratio.Magnitude < .79 || open.Ratio.Magnitude > 1.26 || shorted.Ratio.Magnitude > .15
                || (known.Ratio - shorted.Ratio).Magnitude < .003 || (open.Ratio - known.Ratio).Magnitude < .05)
                throw new InvalidOperationException($"Chuẩn {open.FrequencyHz:0} Hz không hợp lệ. Kiểm tra kênh 1 trước Rsense, kênh 2 tại tải và ba cách đấu chuẩn.");
            Complex scale = cal.Settings.KnownOhms * (open.Ratio - known.Ratio) / (known.Ratio - shorted.Ratio);
            if (scale.Real < cal.Settings.SenseOhms * .5 || scale.Real > cal.Settings.SenseOhms * 2 || Math.Abs(scale.Imaginary) > cal.Settings.SenseOhms * .5)
                throw new InvalidOperationException("R chuẩn/Rsense hoặc dây/kênh đo không nhất quán; kiểm tra fixture trước khi đo tải.");
        }
    }

    public static ImpedanceResult Calculate(ImpedanceRatio[] measured, ImpedanceCalibration cal, string context)
    {
        ValidateCalibration(cal, context);
        if (measured.Length != cal.Open.Length) return new(false, "Dải tần khác calibration; đo lại chuẩn.", Array.Empty<CalibratedImpedancePoint>());
        var points = new List<CalibratedImpedancePoint>();
        bool openOrHigh = false;
        for (int i = 0; i < measured.Length; i++)
        {
            var m = measured[i]; var o = cal.Open[i]; var s = cal.Short[i]; var r = cal.Known[i];
            if (new[] { m.FrequencyHz, m.Real, m.Imaginary, m.ReferenceSnrDb, m.LoadSnrDb }.Any(v => !double.IsFinite(v))
                || m.FrequencyHz != o.FrequencyHz || m.ReferenceSnrDb < 30 || m.LoadSnrDb < 25)
                return new(false, $"{m.FrequencyHz:0} Hz: tín hiệu chưa đủ; chưa kết luận điện trở.", Array.Empty<CalibratedImpedancePoint>());
            Complex scale = cal.Settings.KnownOhms * (o.Ratio - r.Ratio) / (r.Ratio - s.Ratio);
            if ((o.Ratio - m.Ratio).Magnitude < .002) openOrHigh = true;
            // An exact open circuit has no finite Z. Never replace it by an invented ceiling curve.
            if ((o.Ratio - m.Ratio).Magnitude < 1e-9) continue;
            Complex z = scale * (m.Ratio - s.Ratio) / (o.Ratio - m.Ratio);
            if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary))
                return new(false, "Trở kháng không hợp lệ; kiểm tra đấu dây, gain và chuẩn.", Array.Empty<CalibratedImpedancePoint>());
            if (z.Magnitude > 1000) openOrHigh = true;
            if (!openOrHigh && z.Real < -.5)
                return new(false, "Trở kháng không hợp lệ; kiểm tra đấu dây, gain và chuẩn.", Array.Empty<CalibratedImpedancePoint>());
            points.Add(new(m.FrequencyHz, z.Real, z.Imaginary));
        }
        if (openOrHigh)
            return new(false, "TẢI HỞ / TRỞ KHÁNG CAO · dòng qua tải quá nhỏ. Đường hữu hạn (nếu có) chỉ để chẩn đoán từ dữ liệu thu; sai số tăng mạnh gần chuẩn hở. Kiểm tra nối loa/tải và fixture, chưa kết luận điện trở bảo vệ.", Array.Empty<CalibratedImpedancePoint>())
            { OpenOrHighImpedance = true, DiagnosticPoints = points.ToArray() };
        return new(true, "Đã đo |Z|, R, X và pha của tải trên dải tần đã chọn.", points.ToArray());
    }

    public static string CompareSeriesResistance(CalibratedImpedancePoint[] current, CalibratedImpedancePoint[] bareDriver)
    {
        var reference = bareDriver.ToDictionary(p => p.FrequencyHz);
        var pairs = current.Where(p => reference.ContainsKey(p.FrequencyHz) && p.FrequencyHz >= 2000).ToArray();
        if (pairs.Length < 8) return "Chưa đủ ít nhất 8 điểm treble cùng tần số để so điện trở nối tiếp.";
        var delta = pairs.Select(p => p.ResistanceOhms - reference[p.FrequencyHz].ResistanceOhms).Order().ToArray();
        double median = delta[delta.Length / 2], spread = delta.Select(v => Math.Abs(v - median)).Order().ElementAt(delta.Length * 9 / 10);
        double reactive = pairs.Select(p => Math.Abs(p.ReactanceOhms - reference[p.FrequencyHz].ReactanceOhms)).Order().ElementAt(pairs.Length * 9 / 10);
        return median >= .5 && spread <= Math.Max(.3, median * .15) && reactive <= Math.Max(.3, median * .15)
            ? $"So với mẫu củ chuẩn: tăng phần thuần trở khoảng {median:0.00} Ω, phù hợp phần tử nối tiếp. Đường đo tổng chưa xác định vị trí/linh kiện cụ thể."
            : "Chưa thấy chênh lệch thuần trở nối tiếp ổn định so với mẫu. Không kết luận thiếu điện trở bảo vệ từ kết quả này.";
    }
}
