using System.Numerics;

namespace SoncaAudioInspector;

public sealed record HighOrderHarmonicPoint(double FundamentalHz, int Order, double OutputHz,
    double LevelDbc, double NoiseLimitDbc, double WindowMs, double SeparationMs, bool IsUsable, string Status);

public sealed record HighOrderHarmonicResult(double SweepSeconds, double OutputBandwidthHz,
    double FullOrderMaximumFundamentalHz, IReadOnlyList<HighOrderHarmonicPoint> Points, string Note)
{
    public static HighOrderHarmonicResult Unavailable(string note) => new(0, 0, 0, Array.Empty<HighOrderHarmonicPoint>(), note);
    public bool ReferenceAtHarmonicFrequency { get; init; }
}

public static partial class StandardAcousticMeasurement
{
    private static HighOrderHarmonicResult BuildHighOrderHarmonics(IReadOnlyList<Complex> impulse, int direct,
        LogSweepSettings settings, MicrophoneCalibration? calibration, IReadOnlyList<Complex[]> lowOrders, SweepFourier fft)
    {
        if (lowOrders.Count < 10 || lowOrders[1].Length == 0)
            return HighOrderHarmonicResult.Unavailable("Không có fundamental Farina tin cậy để tham chiếu H10–H35.");
        double bandwidth = Math.Min(settings.EndFrequencyHz, settings.SampleRate * .475);
        double timeScale = settings.DurationSeconds / Math.Log(settings.EndFrequencyHz / settings.StartFrequencyHz);
        // Skip the fade-in octave, but retain bass below the 50 Hz FEQ display
        // limit: 40 Hz is important for mechanical-defect investigation.
        double min = Math.Max(20, settings.StartFrequencyHz * 2), max = Math.Min(2000, bandwidth / 10);
        if (max <= min) return HighOrderHarmonicResult.Unavailable("Băng thông sweep không đủ để đọc hài bậc cao.");
        var frequencies = Enumerable.Range(0, 1 + (int)Math.Ceiling(Math.Log2(max / min) * 12))
            .Select(i => min * Math.Pow(2, i / 12.0)).Concat(new[] { 40.0, 50, 80, 100, 200, 500, 1000, max })
            .Where(f => f >= min && f <= max).Distinct().Order().ToArray();
        var points = new List<HighOrderHarmonicPoint>();
        for (int order = 10; order <= 35; order++)
        {
            // Include both neighbours, including H36 beyond the requested range.
            // Never extend a short gate into the neighbouring order to pad it.
            double leftGap = timeScale * Math.Log((order + 1.0) / order);
            double rightGap = timeScale * Math.Log(order / (order - 1.0));
            int left = (int)Math.Floor(Math.Min(.05, leftGap * .45) * settings.SampleRate);
            int right = (int)Math.Floor(Math.Min(.5, rightGap * .45) * settings.SampleRate);
            int length = left + right + 1;
            double windowSeconds = length / (double)settings.SampleRate;
            int size = NextPowerOfTwo(Math.Max(64, length * 4));
            var spectrum = new Complex[size];
            int center = Mod((int)Math.Round(direct - timeScale * Math.Log(order) * settings.SampleRate), impulse.Count);
            double edgePower = 0; int edgeCount = 0;
            for (int rel = -left; rel <= right; rel++)
            {
                double value = impulse[Mod(center + rel, impulse.Count)].Real;
                double position = rel <= 0 ? -rel / (double)Math.Max(1, left) : rel / (double)Math.Max(1, right);
                if (position >= .8) { edgePower += value * value; edgeCount++; }
                double weight = position <= .9 ? 1 : .5 + .5 * Math.Cos(Math.PI * (position - .9) / .1);
                spectrum[rel + left] = value * weight;
            }
            fft.Forward(spectrum, length);
            double floorMagnitude = Math.Sqrt(edgePower / Math.Max(1, edgeCount) * length);
            foreach (double f in frequencies)
            {
                double output = order * f;
                string status = "Đo được"; bool usable = true;
                double referenceFrequency = settings.UseHarmonicFrequencyAsDistortionReference ? output : f;
                double fundamental = DistortionBandMagnitude(lowOrders[1], referenceFrequency, settings.SampleRate);
                double magnitude = DistortionBandMagnitude(spectrum, Math.Min(output, bandwidth), settings.SampleRate);
                double correction = settings.ApplyDistortionCalibration && calibration != null
                    ? -(calibration.GetGainCorrectionDb(output) - calibration.GetGainCorrectionDb(referenceFrequency)) : 0;
                double floorDb = 20 * Math.Log10(Math.Max(Tiny, floorMagnitude) / Math.Max(Tiny, fundamental)) + correction;
                double level = 20 * Math.Log10(Math.Max(Tiny, magnitude) / Math.Max(Tiny, fundamental)) + correction;
                if (output > bandwidth) { status = "Ngoài băng thông"; usable = false; }
                else if (left < 8 || right < 8 || output * windowSeconds < 3) { status = "Cửa sổ quá ngắn"; usable = false; }
                else if (fundamental <= 1e-5) { status = "Fundamental quá yếu"; usable = false; }
                else if (magnitude < floorMagnitude * 3) { status = "Dưới nền / đuôi chồng lấn"; usable = false; }
                points.Add(new(f, order, output, usable ? level : double.NaN, output <= bandwidth && fundamental > 1e-5 ? floorDb : double.NaN,
                    windowSeconds * 1000, Math.Min(leftGap, rightGap) * 1000, usable, status));
            }
        }
        return new(settings.DurationSeconds, bandwidth, bandwidth / 35, points,
            "Từ cùng lượt ESS (lượt cuối nếu đo nhiều lượt). H10–H35 là dấu hiệu méo bậc cao; chưa tự xác định lỗi rub/buzz. "
            + "Cửa sổ giới hạn bởi hài lân cận; đuôi dài có thể chồng lấn. Cần mẫu chuẩn cùng mức và fixture để kết luận.")
            { ReferenceAtHarmonicFrequency = settings.UseHarmonicFrequencyAsDistortionReference };
    }
}
