using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public readonly record struct FrequencyEvaluationRange(double MinimumHz, double MaximumHz)
{
    public bool Contains(double frequency) => double.IsFinite(frequency) && frequency >= MinimumHz && frequency <= MaximumHz;
    public void Validate()
    {
        if (!double.IsFinite(MinimumHz) || !double.IsFinite(MaximumHz) || MinimumHz < 20 || MaximumHz > 20000 || MinimumHz >= MaximumHz)
            throw new ArgumentException("freq_range cần hai số 20 ≤ từ Hz < đến Hz ≤ 20000.");
    }
}

public partial class TestConfig
{
    [JsonPropertyName("freq_range")]
    public double[] FrequencyRange { get; set; } = [50, 18000];

    [JsonPropertyName("THD_80"), JsonConverter(typeof(ConfigBooleanConverter))]
    public bool EvaluateThd80 { get; set; }
    [JsonPropertyName("THD_4000"), JsonConverter(typeof(ConfigBooleanConverter))]
    public bool EvaluateThd4000 { get; set; }

    public double ThdLimitAt(double frequency) => frequency < 250 ? BassThdLimit ?? Thd1000Hz
        : frequency >= 2500 ? TrebleThdLimit ?? Thd1000Hz : Thd1000Hz;
    public bool EvaluatesThdAt(double frequency) => Math.Abs(frequency - 1000) < .01
        || EvaluateThd80 && Math.Abs(frequency - 80) < .01 || EvaluateThd4000 && Math.Abs(frequency - 4000) < .01;

    private double? _thd1000Hz;
    [JsonPropertyName("THD_1000Hz")]
    public double Thd1000Hz
    {
        get => _thd1000Hz ?? MidThdLimit ?? ThdLimit ?? .5;
        set => _thd1000Hz = value;
    }

    [JsonIgnore]
    public FrequencyEvaluationRange EvaluationRange
    {
        get
        {
            if (FrequencyRange is not { Length: 2 }) throw new ArgumentException($"{id}: freq_range cần [từ Hz, đến Hz].");
            var range = new FrequencyEvaluationRange(FrequencyRange[0], FrequencyRange[1]); range.Validate(); return range;
        }
    }
    public void ValidateMeasurementCriteria()
    {
        _ = EvaluationRange;
        SweepLengthSettings.Validate(this);
        if (AllowLineOutliers && (!double.IsFinite(AllowedLineOutlierPercent) || AllowedLineOutlierPercent < 0 || AllowedLineOutlierPercent > 100))
            throw new ArgumentException($"{id}: tỷ lệ điểm được vượt biên LINE cần từ 0 đến 100 (%).");
        if (!double.IsFinite(Thd1000Hz) || Thd1000Hz <= 0 || Thd1000Hz > 100)
            throw new ArgumentException($"{id}: THD_1000Hz phải lớn hơn 0 và không quá 100 (%).");
        foreach (var frequency in new[] { 80d, 4000d }.Where(EvaluatesThdAt))
            if (!double.IsFinite(ThdLimitAt(frequency)) || ThdLimitAt(frequency) <= 0 || ThdLimitAt(frequency) > 100)
                throw new ArgumentException($"{id}: giới hạn THD {frequency:0} Hz phải lớn hơn 0 và không quá 100 (%).");
    }
}

public static class MeasurementCriteria
{
    public const double ThdFrequencyHz = 1000;
    public static FrequencyEvaluationRange Range(TestConfig? test) => test?.EvaluationRange ?? new(50, 18000);
    public static string ThdVerdict(IEnumerable<SweepDistortionSummary> points, double limit)
    {
        var oneKhz = points.SingleOrDefault(p => Math.Abs(p.ReferenceFrequencyHz - ThdFrequencyHz) < .01);
        return ThdVerdict(oneKhz?.ThdPercent ?? double.NaN, oneKhz?.IsValid == true, limit);
    }
    public static string ThdVerdict(IEnumerable<SweepDistortionSummary> points, TestConfig test)
    {
        var measured = points.ToArray();
        var statuses = new[] { 80d, 1000d, 4000d }.Where(test.EvaluatesThdAt).Select(f =>
        {
            var point = measured.SingleOrDefault(p => Math.Abs(p.ReferenceFrequencyHz - f) < .01);
            return ThdVerdict(point?.ThdPercent ?? double.NaN, point?.IsValid == true, test.ThdLimitAt(f));
        }).ToArray();
        return statuses.Contains("INVALID") ? "INVALID" : statuses.Contains("FAIL") ? "FAIL" : "PASS";
    }
    public static string ThdVerdict(double thd, bool valid, double limit)
        => !valid || !double.IsFinite(thd) || thd < 0 ? "INVALID" : thd > limit ? "FAIL" : "PASS";
}
