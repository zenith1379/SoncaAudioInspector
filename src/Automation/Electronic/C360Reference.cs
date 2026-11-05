using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public sealed record C360Reference(string Key, DateTimeOffset CreatedUtc, Dictionary<double, double> Curve,
    ImpulseEffectReference? Effects, FrequencyShiftResult? FrequencyShift, int Runs)
{
    [JsonIgnore] public bool IsUsable => Runs >= 2 && (FrequencyShift is { IsValid: true } shift
        ? double.IsFinite(shift.OnShiftHz) && double.IsFinite(shift.OffShiftHz)
        : Curve.Count >= 3 && Curve.All(p => double.IsFinite(p.Key) && p.Key > 0 && double.IsFinite(p.Value)));
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static string ConfigurationKey(string model, C360ModelPlan plan, C360ElectronicStage stage, string mixerKey = "", C360ElectronicSetup? resolvedSetup = null)
    {
        var test = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage);
        var setup = resolvedSetup ?? plan.SetupFor(test);
        var acquisitionTest=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(test))!.AsObject();
        // Voting changes how many accepted captures are required, not the acquisition/reference identity.
        acquisitionTest.Remove(nameof(TestConfig.MaximumRuns));acquisitionTest.Remove(nameof(TestConfig.RequiredPasses));
        acquisitionTest.Remove(nameof(TestConfig.ReferenceTestId));
        RemoveJudgementFields(acquisitionTest, test, setup.SampleRate);
        if (test.ReferenceTestId != null) acquisitionTest["id"] = test.ReferenceTestId;
        string payload = JsonSerializer.Serialize(new { Version = 1, Analysis = stage == C360ElectronicStage.MicFrequencyShift ? "multitone-phase-clock-v4-qualified-quiet"
            : stage == C360ElectronicStage.MicEffects ? "effects-v6-band-probe" : "effects-v4-decay", model, test=acquisitionTest,
            plan.Configuration.TestVolume, Playback = setup.PlaybackId(stage), setup.LineOutCaptureId, setup.SampleRate, setup.SweepSeconds,
            setup.AnalogPlaybackChannel, setup.CaptureChannel, Level = setup.PlaybackLevel(stage), mixerKey });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
    internal static void RemoveJudgementFields(System.Text.Json.Nodes.JsonObject acquisition, TestConfig test, int sampleRate)
    {
        acquisition.Remove(nameof(TestConfig.AllowLineOutliers)); acquisition.Remove(nameof(TestConfig.AllowedLineOutlierPercent));
        acquisition.Remove(nameof(TestConfig.DipWindowsEnabled)); acquisition.Remove(nameof(TestConfig.DipWindows));
        acquisition.Remove(nameof(TestConfig.BandAverageEnabled)); acquisition.Remove(nameof(TestConfig.BandAverageToleranceDb));
        acquisition.Remove(nameof(TestConfig.SingleRunPassWhenMarginDbEnabled)); acquisition.Remove(nameof(TestConfig.SingleRunPassMarginDb));
        foreach (string key in AdditionalTestRegistry.RegisteredJsonPropertyNames)
            acquisition.Remove(key);
        acquisition.Remove(nameof(TestConfig.SweepLengthSamples));
        if (test.SweepLengthSamples.HasValue) acquisition[nameof(TestConfig.LogSweepDurationSeconds)] = test.ResolveSweepSeconds(sampleRate);
    }
    public static Dictionary<double, double> Response(C360ElectronicResult result) => result.Measurement?.FrequencyResponse
        .Where(p => double.IsFinite(p.FrequencyHz) && double.IsFinite(p.LevelDb))
        .ToDictionary(p => p.FrequencyHz, p => p.LevelDb + result.Measurement.ExcitationRmsDbFs) ?? new();

    public static C360Reference Create(string key, IReadOnlyList<C360ElectronicResult> runs, TestConfig test)
    {
        if (runs.Count < 2 || runs.Any(r => r.Status != "PASS")) throw new InvalidOperationException("Cần hai lượt hợp lệ, đạt tiêu chí cấu hình trước khi lưu chuẩn.");
        foreach (var first in runs)
        foreach (var second in runs.SkipWhile(r => !ReferenceEquals(r, first)).Skip(1))
        {
            if (first.FrequencyShift is { IsValid: true } a && second.FrequencyShift is { IsValid: true } b)
            {
                if (Math.Abs(a.OnShiftHz - b.OnShiftHz) > .5 || Math.Abs(a.OffShiftHz - b.OffShiftHz) > .5)
                    throw new InvalidOperationException("Di tần chưa lặp lại trong ±0,5 Hz; chưa lưu chuẩn.");
            }
            else if (test.SpeakerStage != "MicEffects")
            {
                if (new[] { first, second }.Any(r => r.Measurement?.FrequencyResponse.Any(p => !double.IsFinite(p.FrequencyHz)
                    || test.EvaluationRange.Contains(p.FrequencyHz) && !double.IsFinite(p.LevelDb + r.Measurement.ExcitationRmsDbFs)) != false))
                    throw new InvalidOperationException("Thiếu dữ liệu đáp tuyến hữu hạn; khoảng xét hõm không bỏ qua bản thu lỗi. Chưa lưu chuẩn.");
                var aCurve = Response(first); var bCurve = Response(second);
                var expected = aCurve.Keys.Concat(bCurve.Keys).Where(test.EvaluationRange.Contains).Distinct().ToArray();
                var frequencies = expected.Where(f => aCurve.ContainsKey(f) && bCurve.ContainsKey(f)).ToArray();
                if (frequencies.Length < 3 || frequencies.Length != expected.Length)
                    throw new InvalidOperationException($"Thiếu điểm đáp tuyến để đối chiếu hai lượt ({frequencies.Length}/{expected.Length} điểm chung trong {test.EvaluationRange.MinimumHz:0}–{test.EvaluationRange.MaximumHz:0} Hz); chưa lưu chuẩn.");
                var repeatPolicy = new ResponseLimitPolicy(ResponseLimitSettings.Capture(test, 3), aCurve, null);
                frequencies = frequencies.Where(f => !repeatPolicy.SkipRepeatabilityAt(f)).ToArray();
                if (frequencies.Length < 3) throw new InvalidOperationException("Không đủ 3 điểm ngoài khoảng xét hõm để kiểm độ lặp lại; chưa lưu chuẩn.");
                double peakFrequency = frequencies.MaxBy(f => Math.Abs(aCurve[f] - bCurve[f]));
                double maximum = Math.Abs(aCurve[peakFrequency] - bCurve[peakFrequency]);
                int outside = frequencies.Count(f => Math.Abs(aCurve[f] - bCurve[f]) > 3);
                if (outside > 0)
                    throw new InvalidOperationException($"Đáp tuyến hai lượt chưa lặp lại trong 3 dB ({test.EvaluationRange.MinimumHz:0}–{test.EvaluationRange.MaximumHz:0} Hz): {outside}/{frequencies.Length} điểm vượt biên; lệch lớn nhất {maximum:F2} dB tại {peakFrequency:0.0} Hz. Chưa lưu chuẩn.");
            }
        }
        var curves = runs.Select(Response).ToArray();
        var curve = curves[0].Keys.Where(f => curves.All(c => c.ContainsKey(f))).ToDictionary(f => f, f => curves.Average(c => c[f]));
        ImpulseEffectReference? effects = null;
        if (test.SpeakerStage == "MicEffects")
        {
            var settings = test.ImpulseEffects ?? new();
            effects = ImpulseEffectReference.Average(ImpulseEffectReference.From(runs[0].Measurement?.ImpulseEffects, settings),
                ImpulseEffectReference.From(runs[1].Measurement?.ImpulseEffects, settings), settings.EchoDelayToleranceMs);
            if (effects == null) throw new InvalidOperationException("Echo/reverb hai lượt chưa ổn định hoặc chưa đủ dữ liệu; chưa lưu chuẩn.");
        }
        return new(key, DateTimeOffset.UtcNow, curve, effects, runs.Last().FrequencyShift, runs.Count);
    }
    public C360ElectronicResult Compare(C360ElectronicResult current, TestConfig test, double tolerance)
    {
        if (current.Status is not ("PASS" or "FAIL")) return current;
        var failures = new List<string>();
        if (test.SpeakerStage == "MicEffects" && Effects != null)
        {
            var comparison = ConfiguredAcousticTests.Effect(current.Measurement?.ImpulseEffects, test.ImpulseEffects ?? new(), Effects);
            if (comparison.Status != "PASS") return current with { Status = comparison.Status, Summary = current.Summary + " " + comparison.Reason };
        }
        else if (current.FrequencyShift is { IsValid: true } shift && FrequencyShift is { } reference)
        {
            if (Math.Abs(shift.OnShiftHz - reference.OnShiftHz) > .5 || Math.Abs(shift.OffShiftHz - reference.OffShiftHz) > .5)
                failures.Add("Di tần lệch line chuẩn quá 0,5 Hz.");
        }
        else
        {
            var measured = Response(current);
            var expected = Curve.Keys.Where(test.EvaluationRange.Contains).ToArray();
            var frequencies = expected.Where(f => measured.TryGetValue(f, out double value) && double.IsFinite(value)).ToArray();
            if (frequencies.Length < 3 || frequencies.Length != expected.Length)
                return current with { Status = "INVALID", Summary = current.Summary + " Thiếu điểm so với line chuẩn." };
            var settings = ResponseLimitSettings.Capture(test, tolerance);
            var assessment = new ResponseLimitPolicy(settings, Curve, null)
                .Assess(measured, test.EffectiveLineOutlierPercent, expected);
            var deltaFrequencies = settings.HasActiveWindows
                ? assessment.Points.Where(p => p.CountInQuota || p.Critical).Select(p => p.FrequencyHz).Where(f => measured.ContainsKey(f) && Curve.ContainsKey(f)).ToArray()
                : frequencies;
            double delta = (deltaFrequencies.Length > 0 ? deltaFrequencies : frequencies).Max(f => Math.Abs(measured[f] - Curve[f]));
            current = current with { ResponseAssessment = assessment, Summary = current.Summary + $" LINE CHUẨN ±{tolerance:0.##} dB; {assessment.Describe()}" };
            if (assessment.IsInvalid) return current with { Status = "INVALID" };
            if (!assessment.Passed) failures.Add($"Đáp tuyến lệch line chuẩn tối đa {delta:F2} dB{(settings.HasActiveWindows ? " (không tính khoảng xét hõm)" : "")}.");
        }
        return current with { Status = failures.Count > 0 ? "FAIL" : current.Status,
            Summary = current.Summary + " " + (failures.Count > 0 ? string.Join(" ", failures) : "Đã đối chiếu LINE CHUẨN cùng cấu hình.") };
    }
}
