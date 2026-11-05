using System.Collections.Frozen;

namespace SoncaAudioInspector;

public sealed record AdditionalTestDescriptor(string Id, string DisplayName, int Version,
    IReadOnlyList<string> SupportedStages, IReadOnlyList<string> JsonPropertyNames);
public sealed record AdditionalTestContext(int PlaybackSampleRate = 44100, int RecordingSampleRate = 44100,
    double PlaybackFrequencyScale = 1.0, string RouteLabel = "");
public sealed record AdditionalTestStepStimulus(double NominalFrequencyHz, double PhysicalFrequencyHz,
    double LevelDbFs, double DurationSeconds, float[] Samples);
public sealed record AdditionalTestPlan(string TestId, double BackgroundSeconds, IReadOnlyList<AdditionalTestStepStimulus> Steps);
public sealed record AdditionalTestCaptureData(int SampleRate, float[] BackgroundSamples,
    IReadOnlyList<float[]> StepCaptures, string RouteLabel = "");

public interface IAdditionalAcousticTest
{
    AdditionalTestDescriptor Metadata { get; }
    bool IsEnabled(TestConfig config);
    void Validate(TestConfig config, AdditionalTestContext? context = null);
    AdditionalTestPlan Plan(TestConfig config, AdditionalTestContext context);
    ConfiguredTestResult Analyze(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context);
    string DescribeReference(TestConfig config, AdditionalTestContext context) => string.Empty;
}

public static class AdditionalTestRegistry
{
    private static readonly List<IAdditionalAcousticTest> Tests = new();
    static AdditionalTestRegistry() => Register(RubBuzzAdditionalTest.Instance);
    public static void Register(IAdditionalAcousticTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        lock (Tests)
        {
            if (Tests.Any(t => string.Equals(t.Metadata.Id, test.Metadata.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Trùng mã phép kiểm tra bổ sung: " + test.Metadata.Id);
            Tests.Add(test);
        }
    }
    public static IReadOnlyList<IAdditionalAcousticTest> All { get { lock (Tests) return Tests.AsReadOnly().ToArray(); } }
    public static IReadOnlySet<string> RegisteredJsonPropertyNames => All.SelectMany(t => t.Metadata.JsonPropertyNames).ToFrozenSet(StringComparer.Ordinal);
    public static bool HasAnyEnabled(TestConfig config) => All.Any(t => t.IsEnabled(config));
    public static IEnumerable<IAdditionalAcousticTest> GetEnabled(TestConfig config) => All.Where(t => t.IsEnabled(config));
    public static ConfiguredTestResult CancelledResult() => new("Bài bổ sung", "INVALID", "Đã hủy; không sử dụng kết quả dở dang.");
    public static async Task<ConfiguredTestResult> RunAsync(IAdditionalAcousticTest test, TestConfig config, AdditionalTestContext context,
        Func<AdditionalTestPlan, CancellationToken, Task<AdditionalTestCaptureData>> captureAsync, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = test.Plan(config, context);
        var capture = await captureAsync(plan, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Task.Run(() => test.Analyze(config, capture, context), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    public static void ValidateEnabled(TestConfig config, AdditionalTestContext? context = null)
    {
        foreach (var test in GetEnabled(config)) test.Validate(config, context);
    }
}

public sealed class RubBuzzAdditionalTest : IAdditionalAcousticTest
{
    public static RubBuzzAdditionalTest Instance { get; } = new();
    private RubBuzzAdditionalTest() { }
    public AdditionalTestDescriptor Metadata { get; } = new("RubBuzz", "Rè (Rub & Buzz)", 1, Array.Empty<string>(),
        Array.AsReadOnly(new[] { nameof(TestConfig.TestRubBuzz), nameof(TestConfig.RubBuzzFrequenciesHz), nameof(TestConfig.RubBuzzLevelDbFs),
            nameof(TestConfig.RubBuzzStepSeconds), nameof(TestConfig.RubBuzzPeakLimitDb), nameof(TestConfig.RubBuzzRmsLimitPercent),
            nameof(TestConfig.RubBuzzNoiseMarginDb), nameof(TestConfig.RubBuzzHighPassMultiple) }));
    public bool IsEnabled(TestConfig config) => config.IsRubBuzzEnabled;
    public void Validate(TestConfig test, AdditionalTestContext? context = null)
    {
        if (!test.IsRubBuzzEnabled) return;
        if (test.RubBuzzPeakLimitDb == null && test.RubBuzzRmsLimitPercent == null)
            throw new ArgumentException($"{test.id}: Rè (Rub & Buzz) cần nhập ít nhất một giới hạn (Giới hạn đỉnh dư dB hoặc Giới hạn RMS dư %).");
        if (test.RubBuzzPeakLimitDb is double peakLim && (!double.IsFinite(peakLim) || peakLim < -80 || peakLim > 0))
            throw new ArgumentException($"{test.id}: Giới hạn đỉnh dư Rub & Buzz phải trong khoảng -80 đến 0 dB.");
        if (test.RubBuzzRmsLimitPercent is double rmsLim && (!double.IsFinite(rmsLim) || rmsLim <= 0 || rmsLim > 100))
            throw new ArgumentException($"{test.id}: Giới hạn RMS dư Rub & Buzz phải > 0 và ≤ 100 %.");
        var freqs = test.EffectiveRubBuzzFrequenciesHz;
        if (freqs.Count is < 2 or > 8 || freqs.Any(f => !double.IsFinite(f) || f < 20 || f > 1000))
            throw new ArgumentException($"{test.id}: Rub & Buzz cần 2–8 tần số hữu hạn trong khoảng 20–1000 Hz.");
        double level = test.EffectiveRubBuzzLevelDbFs, seconds = test.EffectiveRubBuzzStepSeconds;
        if (!double.IsFinite(level) || level < -60 || level > -3)
            throw new ArgumentException($"{test.id}: mức phát Rub & Buzz phải từ -60 đến -3 dBFS peak.");
        if (!double.IsFinite(seconds) || seconds < .6 || seconds > 3)
            throw new ArgumentException($"{test.id}: thời lượng Rub & Buzz phải từ 0,6 đến 3 s.");
        double multiple = test.EffectiveRubBuzzHighPassMultiple, margin = test.EffectiveRubBuzzNoiseMarginDb;
        if (!double.IsFinite(multiple) || multiple < 10 || multiple > 50 || !double.IsFinite(margin) || margin < 3 || margin > 30)
            throw new ArgumentException($"{test.id}: bội số lọc Rub & Buzz cần 10–50; lề nền cần 3–30 dB.");
        double scale = context?.PlaybackFrequencyScale ?? 1.0;
        int rate = context?.RecordingSampleRate ?? 44100;
        if (!double.IsFinite(scale) || scale <= 0 || rate < 22050 || (context?.PlaybackSampleRate ?? 44100) < 22050)
            throw new ArgumentException($"{test.id}: sample rate/hệ số tần số Rub & Buzz không hợp lệ.");
        foreach (double f in freqs)
        {
            if ((seconds - .35) * f * Math.Min(1.0, scale) < 24)
                throw new ArgumentException($"{test.id}: tần số {f:0.#} Hz và thời lượng {seconds:0.##} s chưa đủ 24 chu kỳ ổn định sau khi trừ 0,35 s đầu/cuối (scale={scale:0.####}).");
            double physical = f * scale, fc = Math.Max(500, multiple * physical), fMax = Math.Min(.45 * rate, 16000);
            if (fc >= fMax - 500)
                throw new ArgumentException($"{test.id}: tần số {f:0.#} Hz (vật lý {physical:0.#} Hz) với bội số lọc {multiple:0.#} tạo fc = {fc:0.#} Hz vượt/sát giới hạn trên {fMax:0.#} Hz (dải lọc rỗng hoặc quá hẹp).");
        }
    }
    public AdditionalTestPlan Plan(TestConfig config, AdditionalTestContext context)
    {
        Validate(config, context);
        return new(Metadata.Id, .5, config.EffectiveRubBuzzFrequenciesHz.Select(f =>
            new AdditionalTestStepStimulus(f, f * context.PlaybackFrequencyScale, config.EffectiveRubBuzzLevelDbFs,
                config.EffectiveRubBuzzStepSeconds, RubBuzzAnalysis.GenerateStepTone(f * context.PlaybackFrequencyScale,
                    config.EffectiveRubBuzzLevelDbFs, config.EffectiveRubBuzzStepSeconds, context.PlaybackSampleRate))).ToArray());
    }
    public ConfiguredTestResult Analyze(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context)
    {
        var plan = Plan(config, context);
        if (capture.StepCaptures.Count != plan.Steps.Count)
            return new(Metadata.DisplayName, "INVALID", "Đã hủy hoặc thiếu bước thu; không dùng kết quả dở dang.");
        var results = plan.Steps.Select((step, i) => RubBuzzAnalysis.AnalyzeStep(capture.StepCaptures[i], capture.BackgroundSamples,
            capture.SampleRate, step.NominalFrequencyHz, step.PhysicalFrequencyHz, step.DurationSeconds,
            config.RubBuzzPeakLimitDb, config.RubBuzzRmsLimitPercent, config.EffectiveRubBuzzNoiseMarginDb, config.EffectiveRubBuzzHighPassMultiple)).ToArray();
        string status = results.Any(r => r.Status == "FAIL") ? "FAIL" : results.Any(r => r.Status == "INVALID") ? "INVALID" : "PASS";
        return new(Metadata.DisplayName, status, string.Join(Environment.NewLine, results.Select(r => r.Reason)));
    }
}
