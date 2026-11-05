using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record ImpedanceSettings(double SenseOhms = 100, double KnownOhms = 10, double StartHz = 20,
    double EndHz = 20000, double LevelDbFs = -42, double Seconds = 10,
    double InputOhms = 20000, double LeadOhms = 0, bool UseLogSweep = true)
{
    public void Validate(int rate = 44100)
    {
        if (new[] { SenseOhms, KnownOhms, StartHz, EndHz, LevelDbFs, Seconds, InputOhms, LeadOhms }.Any(v => !double.IsFinite(v))
            || SenseOhms < 47 || SenseOhms > 1000 || KnownOhms < 1 || KnownOhms > 200
            || StartHz < 20 || EndHz <= StartHz || EndHz > Math.Min(20000, rate * .49)
            || LevelDbFs < -60 || LevelDbFs > -30 || Seconds < 3 || Seconds > 30 || InputOhms < 1000 || InputOhms > 1e7 || LeadOhms < 0 || LeadOhms > 10)
            throw new ArgumentException("Rsense 47–1000 Ω, R chuẩn 1–200 Ω, 20 Hz–20 kHz, mức −60…−30 dBFS peak; nên sweep ≥10 s. R INPUT 1 kΩ–10 MΩ, R dây 0–10 Ω.");
    }
    public double[] Frequencies() => Enumerable.Range(0, 1 + (int)Math.Ceiling(24 * Math.Log2(EndHz / StartHz)))
        .Select(i => StartHz * Math.Pow(2, i / 24.0)).Where(f => f < EndHz).Append(EndHz).ToArray();
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
    public bool Uncalibrated { get; init; }
    public bool OpenOrHighImpedance { get; init; }
    public CalibratedImpedancePoint[] DiagnosticPoints { get; init; } = Array.Empty<CalibratedImpedancePoint>();
}

public static class ImpedanceMeasurement
{
    public static ImpedanceResult EstimateUncalibrated(ImpedanceRatio[] measured, ImpedanceSettings settings)
    {
        settings.Validate();
        ImpedanceResult Diagnostic(string message, CalibratedImpedancePoint[]? points = null, bool high = false)
            => new(false, "CHƯA HIỆU CHUẨN · " + message, Array.Empty<CalibratedImpedancePoint>())
                { Uncalibrated = true, DiagnosticPoints = points ?? Array.Empty<CalibratedImpedancePoint>(), OpenOrHighImpedance = high };
        if (!measured.Select(p => p.FrequencyHz).SequenceEqual(settings.Frequencies()))
            return Diagnostic("Thiếu điểm hoặc dải tần thu không khớp; kiểm tra ngõ thu/phát.");
        double effectiveSense = 1 / (1 / settings.SenseOhms + 1 / settings.InputOhms);
        var points = new List<CalibratedImpedancePoint>(); bool high = false;
        foreach (var m in measured)
        {
            if (new[] {m.FrequencyHz, m.Real, m.Imaginary, m.ReferenceSnrDb, m.LoadSnrDb}.Any(v => !double.IsFinite(v))
                || m.ReferenceSnrDb < 30 || m.LoadSnrDb < 25)
                return Diagnostic($"{m.FrequencyHz:0} Hz: tín hiệu hai kênh chưa đủ; kiểm tra dây, Rsense và gain.");
            Complex difference = Complex.One - m.Ratio;
            if (difference.Magnitude < .002) high = true;
            if (difference.Magnitude < 1e-9) continue;
            Complex z = effectiveSense * m.Ratio / difference - settings.LeadOhms;
            if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary))
                return Diagnostic("Không tính được tỉ số hai kênh; kiểm tra dây và Rsense.");
            if (z.Magnitude > 1000) high = true;
            points.Add(new(m.FrequencyHz, z.Real, z.Imaginary));
        }
        string reason = high ? "CH1/CH2 gần bằng nhau hoặc trở kháng rất cao: có thể tải hở, thiếu Rsense hoặc hai kênh thu cùng một tín hiệu. " : "";
        return Diagnostic(reason + "Đường ước tính dùng Rsense/R INPUT đã nhập, chưa bù lệch gain/pha hai kênh. Chuẩn hở trước khi dùng số ohm để kết luận; chuẩn ngắn/R chuẩn nằm trong Hiệu chuẩn nâng cao.", points.ToArray(), high);
    }
    public static float[] Generate(ImpedanceSettings settings, int rate)
    {
        settings.Validate(rate); var frequencies = settings.Frequencies();
        var signal = new float[(int)(rate * settings.Seconds)]; double peak = 0;
        if (settings.UseLogSweep)
        {
            int pad = (int)(rate * .15), count = signal.Length - 2 * pad;
            double seconds = count / (double)rate, logRatio = Math.Log(settings.EndHz / settings.StartHz);
            double k = 2 * Math.PI * settings.StartHz * seconds / logRatio, amplitude = Math.Pow(10, settings.LevelDbFs / 20);
            for (int i = 0; i < count; i++) signal[pad + i] = (float)(amplitude * Math.Sin(k * (Math.Exp(i / (double)rate * logRatio / seconds) - 1))
                * Math.Min(1, Math.Min(i, count - 1 - i) / (rate * .015)));
            return signal;
        }
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
        if (settings.UseLogSweep) return MeasureSweep(reference, load, rate, settings);
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

    private static ImpedanceRatio[] MeasureSweep(float[] reference, float[] load, int rate, ImpedanceSettings settings)
    {
        if (reference.Sum(v=>(double)v*v)/reference.Length < 1e-12)
            throw new InvalidOperationException("Không có mức tham chiếu đủ lớn; kiểm tra ngõ phát và kênh thu trước Rsense.");
        // Both simultaneous inputs share one FFT, so capture latency cancels in Y/X.
        // Quiet pads bracket the emitted sweep; do not estimate its noise from adjacent swept bins.
        int count = reference.Length, fft = 1; while (fft < count) fft <<= 1;
        int quietCount = Math.Min(rate / 10, count / 10);
        double Variance(float[] values, int offset)
        {
            double mean = 0, power = 0;
            for (int i = offset; i < offset + quietCount; i++) { mean += values[i]; power += values[i] * values[i]; }
            return Math.Max(1e-20, power / quietCount - Math.Pow(mean / quietCount, 2));
        }
        // The capture backend keeps 600 ms after playback. Lowest edge window also supports pre-roll captures.
        var quiet = new[] { 0, count - quietCount }.MinBy(i => Variance(reference, i));
        double nx = Variance(reference, quiet) * count, ny = Variance(load, quiet) * count;
        var x = new Complex[fft]; var y = new Complex[fft];
        for (int i = 0; i < count; i++) { x[i] = reference[i]; y[i] = load[i]; }
        Fourier.Forward(x, FourierOptions.Matlab); Fourier.Forward(y, FourierOptions.Matlab);
        var rows = new List<ImpedanceRatio>();
        foreach (double frequency in settings.Frequencies())
        {
            int bin = Math.Clamp((int)Math.Round(frequency * fft / rate), 2, fft / 2 - 3);
            double px = 0, py = 0; Complex cross = Complex.Zero;
            for (int k = bin - 2; k <= bin + 2; k++) { px += x[k].Magnitude * x[k].Magnitude; py += y[k].Magnitude * y[k].Magnitude; cross += y[k] * Complex.Conjugate(x[k]); }
            double sx = 10 * Math.Log10(Math.Max(1e-30, px / (5 * nx))), sy = 10 * Math.Log10(Math.Max(1e-30, py / (5 * ny)));
            if (sx < 30 || !double.IsFinite(sx)) throw new InvalidOperationException($"{frequency:0} Hz: sweep tham chiếu chưa đủ 30 dB trên nền; kiểm tra dây/gain.");
            var h = cross / px; rows.Add(new(frequency, h.Real, h.Imaginary, sx, sy));
        }
        return rows.ToArray();
    }

    public static void ValidateCalibration(ImpedanceCalibration cal, string context)
    {
        cal.Settings.Validate();
        if (cal.Context != context || cal.Open.Length < 3 || !cal.Open.Select(p=>p.FrequencyHz).SequenceEqual(cal.Settings.Frequencies())
            || cal.Short.Length != 0 && cal.Short.Length != cal.Open.Length || cal.Known.Length != 0 && (cal.Known.Length != cal.Open.Length || cal.Short.Length == 0))
            throw new InvalidOperationException("Cần ít nhất chuẩn hở cùng ngõ, gain, rate, mức phát và dải tần.");
        for (int i = 0; i < cal.Open.Length; i++)
        {
            var open = cal.Open[i];
            bool Finite(ImpedanceRatio p) => new[] {p.FrequencyHz,p.Real,p.Imaginary,p.ReferenceSnrDb,p.LoadSnrDb}.All(double.IsFinite);
            if (!Finite(open) || open.ReferenceSnrDb < 30 || open.LoadSnrDb < 30 || open.Ratio.Magnitude < .79 || open.Ratio.Magnitude > 1.26)
                throw new InvalidOperationException($"{open.FrequencyHz:0} Hz: chuẩn hở chưa hợp lệ, kiểm tra kênh/gain.");
            if (cal.Short.Length > 0 && (!Finite(cal.Short[i]) || cal.Short[i].FrequencyHz != open.FrequencyHz || cal.Short[i].Ratio.Magnitude > .15))
                throw new InvalidOperationException("Chuẩn ngắn chưa hợp lệ; kiểm tra dây/kênh độc lập.");
            if (cal.Known.Length == 0) continue;
            var shorted = cal.Short[i]; var known = cal.Known[i];
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
            var m = measured[i]; var o = cal.Open[i];
            if (new[] { m.FrequencyHz, m.Real, m.Imaginary, m.ReferenceSnrDb, m.LoadSnrDb }.Any(v => !double.IsFinite(v))
                || m.FrequencyHz != o.FrequencyHz || m.ReferenceSnrDb < 30 || m.LoadSnrDb < 25)
                return new(false, $"{m.FrequencyHz:0} Hz: tín hiệu chưa đủ; chưa kết luận điện trở.", Array.Empty<CalibratedImpedancePoint>());
            if ((o.Ratio - m.Ratio).Magnitude < .002) openOrHigh = true;
            // An exact open circuit has no finite Z. Never replace it by an invented ceiling curve.
            if ((o.Ratio - m.Ratio).Magnitude < 1e-9) continue;
            Complex z;
            if (cal.Known.Length > 0)
            {
                var s = cal.Short[i]; var r = cal.Known[i];
                Complex scale = cal.Settings.KnownOhms * (o.Ratio - r.Ratio) / (r.Ratio - s.Ratio);
                z = scale * (m.Ratio - s.Ratio) / (o.Ratio - m.Ratio);
            }
            else
            {
                double effectiveSense = 1 / (1 / cal.Settings.SenseOhms + 1 / cal.Settings.InputOhms);
                var h = m.Ratio / o.Ratio;
                Complex leads = new(cal.Settings.LeadOhms, 0);
                if (cal.Short.Length > 0) { var hs = cal.Short[i].Ratio / o.Ratio; leads = effectiveSense * hs / (1 - hs); }
                z = effectiveSense * h / (1 - h) - leads;
            }
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
        return new(true, "Đã đo |Z|, R, X và pha. " + (cal.Known.Length > 0 ? "Đã hiệu chuẩn hở/ngắn/R chuẩn." : "Đã chuẩn hở; độ chính xác phụ thuộc Rsense, R INPUT và R dây. Có thể thêm chuẩn ngắn/R chuẩn trong Hiệu chuẩn nâng cao."), points.ToArray());
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
