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
        string payload = JsonSerializer.Serialize(new { Version = 1, Analysis = stage == C360ElectronicStage.MicFrequencyShift ? "multitone-phase-clock-v4-qualified-quiet"
            : stage == C360ElectronicStage.MicEffects ? "effects-v6-band-probe" : "effects-v4-decay", model, test,
            Playback = setup.PlaybackId(stage), setup.LineOutCaptureId, setup.SampleRate, setup.SweepSeconds,
            setup.AnalogPlaybackChannel, setup.CaptureChannel, Level = setup.PlaybackLevel(stage), mixerKey });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
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
                var aCurve = Response(first); var bCurve = Response(second);
                var frequencies = aCurve.Keys.Where(f => f >= 50 && f <= 18000 && bCurve.ContainsKey(f)).ToArray();
                if (frequencies.Length < 3 || frequencies.Max(f => Math.Abs(aCurve[f] - bCurve[f])) > 3)
                    throw new InvalidOperationException("Đáp tuyến hai lượt chưa lặp lại trong 3 dB (50 Hz–18 kHz); chưa lưu chuẩn.");
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
            var frequencies = Curve.Keys.Where(f => f >= 50 && f <= 18000 && measured.ContainsKey(f)).ToArray();
            if (frequencies.Length < 3 || frequencies.Length < .95 * Curve.Keys.Count(f => f >= 50 && f <= 18000))
                return current with { Status = "INVALID", Summary = current.Summary + " Thiếu điểm so với line chuẩn." };
            double delta = frequencies.Max(f => Math.Abs(measured[f] - Curve[f]));
            if (delta > tolerance) failures.Add($"Đáp tuyến lệch line chuẩn {delta:F2} dB, giới hạn ±{tolerance:F2} dB.");
        }
        return current with { Status = failures.Count > 0 ? "FAIL" : current.Status,
            Summary = current.Summary + " " + (failures.Count > 0 ? string.Join(" ", failures) : "Đã đối chiếu LINE CHUẨN cùng cấu hình.") };
    }
}
