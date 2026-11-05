using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

/// <summary>Accepts JSON true/false and the legacy "True"/"False" spelling.</summary>
public sealed class ConfigBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.String when bool.TryParse(reader.GetString(), out bool value) => value,
        _ => throw new JsonException("Nhập true/false hoặc chuỗi \"True\"/\"False\".")
    };
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

public partial class TestConfig
{
    [JsonConverter(typeof(ConfigBooleanConverter))] public bool TestEffect { get; set; }
    [JsonConverter(typeof(ConfigBooleanConverter))] public bool TestAirLeak { get; set; }
    [JsonConverter(typeof(ConfigBooleanConverter))] public bool TestLooseParticle { get; set; }
    [JsonConverter(typeof(ConfigBooleanConverter))] public bool TestSoundLevel { get; set; }
    public double SoundLevelToleranceDb { get; set; } = 3;
    public double MechanicalFrequencyHz { get; set; } = 40;
    public double MechanicalLevelDbFs { get; set; } = -18;
    public double MechanicalDurationSeconds { get; set; } = 1.5;
    public double MechanicalPeakMarginDb { get; set; } = 12;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TestRubBuzz { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double[]? RubBuzzFrequenciesHz { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RubBuzzLevelDbFs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RubBuzzStepSeconds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RubBuzzPeakLimitDb { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RubBuzzRmsLimitPercent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RubBuzzNoiseMarginDb { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? RubBuzzHighPassMultiple { get; set; }
    [JsonIgnore] public static readonly double[] DefaultRubBuzzFrequenciesHz = [50, 70, 100, 150];
    [JsonIgnore] public bool IsRubBuzzEnabled => TestRubBuzz == true;
    [JsonIgnore] public IReadOnlyList<double> EffectiveRubBuzzFrequenciesHz => RubBuzzFrequenciesHz is { Length: > 0 } f ? f : DefaultRubBuzzFrequenciesHz;
    [JsonIgnore] public double EffectiveRubBuzzLevelDbFs => RubBuzzLevelDbFs ?? PlaybackLevelDbfs ?? -18.0;
    [JsonIgnore] public double EffectiveRubBuzzStepSeconds => RubBuzzStepSeconds ?? 1.0;
    [JsonIgnore] public double EffectiveRubBuzzNoiseMarginDb => RubBuzzNoiseMarginDb ?? 6.0;
    [JsonIgnore] public double EffectiveRubBuzzHighPassMultiple => RubBuzzHighPassMultiple ?? 20.0;

    [JsonIgnore] public bool HasAdditionalTests => TestEffect || TestAirLeak || TestLooseParticle || TestSoundLevel || AdditionalTestRegistry.HasAnyEnabled(this);
    public MechanicalTestRequest MechanicalRequest(MechanicalTestKind kind) =>
        new(kind, MechanicalFrequencyHz, MechanicalLevelDbFs, MechanicalDurationSeconds, MechanicalPeakMarginDb);

    public void ValidateAdditionalTests()
    {
        _ = MicrophoneCalibration?.Resolve();
        if (TestEffect)
        {
            var settings = ImpulseEffects ?? new();
            settings.Validate();
            if (RubBuzzTestFreq.HasValue) throw new ArgumentException($"{id}: Effect cần bài sweep riêng, không dùng chung RubBuzz Test Freq.");
            if (settings.CaptureTailSeconds * 1000 < Math.Max(settings.TailEndMs + 100,
                2 * (settings.ExpectedEchoDelayMs ?? settings.MaximumEchoDelayMs) + 100))
                throw new ArgumentException($"{id}: tăng CaptureTailSeconds để thu đủ đuôi effect.");
        }
        if (TestSoundLevel && (!double.IsFinite(SoundLevelToleranceDb) || SoundLevelToleranceDb <= 0 || SoundLevelToleranceDb > 30))
            throw new ArgumentException($"{id}: ngưỡng lệch mức âm phải >0 và ≤30 dB.");
        if (TestAirLeak || TestLooseParticle) MechanicalRequest(MechanicalTestKind.AirLeak).Validate();
        AdditionalTestRegistry.ValidateEnabled(this);
    }
}

public sealed record ConfiguredTestResult(string Name, string Status, string Reason)
{
    [JsonIgnore] public bool Passed => Status == "PASS";
    [JsonIgnore] public bool Invalid => Status == "INVALID";
    public string Display => $"{Name}: {Status} — {Reason}";
}

public static partial class ConfiguredAcousticTests
{
    public static ConfiguredTestResult Effect(ImpulseEffectResult? current, ImpulseEffectSettings settings, ImpulseEffectReference? reference)
    {
        if (current?.CanClassify(settings) != true)
            return new("Effect", "INVALID", "Phép đo effect chưa đủ chất lượng để kết luận. " + (current?.Describe(settings) ?? "Chưa có bản thu sweep."));
        if (current.EchoDetected && (!double.IsFinite(current.EchoDelayMs) || current.EchoTaps.Count == 0
            || current.EchoTaps.Any(t => !double.IsFinite(t.DelayMs) || !double.IsFinite(t.LevelDb))))
            return new("Effect", "INVALID", "Dữ liệu đỉnh echo không hợp lệ; đo lại.");
        if (reference != null && reference.SettingsKey != ImpulseEffectReference.Key(settings))
            return new("Effect", "INVALID", "Cấu hình effect khác line chuẩn; lấy lại line effect cùng cấu hình.");
        bool echo = settings.ExpectEcho ?? reference?.EchoDetected ?? true;
        bool? reverb = settings.ExpectReverb ?? reference?.ReverbDetected;
        var failures = new List<string>();
        if (current.EchoDetected != echo) failures.Add(echo ? "Thiếu echo yêu cầu" : "Có echo ngoài yêu cầu");
        if (reverb.HasValue && current.ReverbDetected != reverb) failures.Add(reverb.Value ? "Thiếu reverb yêu cầu" : "Có reverb ngoài yêu cầu");
        double? delay = settings.ExpectedEchoDelayMs ?? reference?.EchoDelayMs;
        if (echo && current.EchoDetected && delay.HasValue && (!double.IsFinite(current.EchoDelayMs)
            || Math.Abs(current.EchoDelayMs - delay.Value) > settings.EchoDelayToleranceMs))
            failures.Add($"Delay {current.EchoDelayMs:0.0} ms lệch chuẩn {delay:0.0} ms quá ±{settings.EchoDelayToleranceMs:0.0} ms");
        if (reference != null && settings.ExpectEcho == null && settings.ExpectReverb == null)
        {
            if (echo && current.EchoDetected && reference.EchoLevelDb is double level && current.EchoTaps.Count > 0
                && Math.Abs(current.EchoTaps.Max(t => t.LevelDb) - level) > 6) failures.Add("Mức echo/direct lệch line chuẩn quá 6 dB");
            if (reverb == true && current.ReverbDetected && Math.Abs(current.DiffuseTailEnergyDb - reference.DiffuseTailEnergyDb) > 6)
                failures.Add("Năng lượng đuôi reverb lệch line chuẩn quá 6 dB");
        }
        return new("Effect", failures.Count > 0 ? "FAIL" : "PASS", failures.Count > 0 ? string.Join("; ", failures)
            : $"Echo {(current.EchoDetected ? $"có ({current.EchoDelayMs:0.0} ms)" : "không")}, reverb {(current.ReverbDetected ? "có" : "không")}; đạt kỳ vọng cấu hình/line chuẩn.");
    }

    public static ConfiguredTestResult Mechanical(MechanicalTestResult result)
    {
        string name = result.Kind == MechanicalTestKind.AirLeak ? "Rò khí" : "Hạt rác";
        string status = !result.IsValid ? "INVALID" : result.Status switch
        {
            "CÓ XUNG PHI CHU KỲ" or "CÓ NHIỄU ĐIỀU BIÊN" or "CÓ XUNG KHÓA PHA (RÈ)" => "FAIL",
            "CHƯA THẤY XUNG" or "CHƯA THẤY ĐIỀU BIÊN RÕ" => "PASS",
            _ => "INVALID"
        };
        return new(name, status, $"{result.Status}: {result.Message} (tone {result.FundamentalHz:0.0} Hz; xung {result.Events.Count}; điều biên {result.ModulationPercent:0.0}%, nổi {result.ModulationProminenceDb:0.0} dB). Kết quả theo ngưỡng sàng lọc của lượt đo.");
    }

    public static ConfiguredTestResult SoundLevel(IReadOnlyList<DiagnosisResponsePoint> points, bool valid, bool comparable,
        bool normalized, double measured1k, double reference1k, double tolerance,
        FrequencyEvaluationRange? evaluationRange = null, ResponseLimitSettings? responseLimits = null)
    {
        if (!valid || !comparable || normalized && (!double.IsFinite(measured1k) || !double.IsFinite(reference1k)))
            return new("Mức âm", "INVALID", "Cần bản thu hợp lệ và line chuẩn cùng route/gain/mức phát; chuẩn hóa 1 kHz cần mức dBFS gốc.");
        var range = evaluationRange ?? new FrequencyEvaluationRange(20, 20000);
        range.Validate();
        bool InActiveDip(double hz) => responseLimits?.HasActiveWindows == true
            && responseLimits.DipWindows.Any(w => w.Enabled && w.Contains(hz));
        var inRangePoints = points.Where(p => double.IsFinite(p.FrequencyHz) && range.Contains(p.FrequencyHz) && !InActiveDip(p.FrequencyHz)).ToArray();
        if (inRangePoints.Any(p => !double.IsFinite(p.MeasuredDbFs) || !double.IsFinite(p.TargetDbFs)))
            return new("Mức âm", "INVALID", "Dữ liệu đáp tuyến hoặc line chuẩn có điểm không hữu hạn trong dải xét mức âm.");
        double[] offsets = inRangePoints.Select(p => p.MeasuredDbFs - p.TargetDbFs).Order().ToArray();
        if (offsets.Length < 3) return new("Mức âm", "INVALID", "Thiếu ít nhất 3 điểm đáp tuyến so được với line chuẩn.");
        double delta = (offsets[(offsets.Length - 1) / 2] + offsets[offsets.Length / 2]) / 2 + (normalized ? measured1k - reference1k : 0);
        string dipNote = responseLimits?.HasActiveWindows == true ? ", không tính khoảng xét hõm" : "";
        return new("Mức âm", Math.Abs(delta) <= tolerance ? "PASS" : "FAIL",
            $"Mức thu {(delta < 0 ? "thấp" : "cao")} hơn line chuẩn {Math.Abs(delta):0.00} dB; giới hạn ±{tolerance:0.00} dB (trung vị {offsets.Length} điểm {range.MinimumHz:0} Hz–{range.MaximumHz:0} Hz{dipNote}, dBFS; chưa phải SPL hiệu chuẩn).");
    }
}
