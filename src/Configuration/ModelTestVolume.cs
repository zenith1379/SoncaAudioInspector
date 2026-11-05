using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

/// <summary>Shared acceptance setup for every route in a model. Speaker levels are read-only constraints.</summary>
public sealed class ModelTestVolume
{
    public static ModelTestVolume C360Defaults() => new() {BleMasterPercent=100,BleMusicPercent=50,BleMicPercent=50,WindowsPlaybackPercent=75,WindowsRecordingPercent=100};
    public double? BleMasterPercent { get; set; }
    public double? BleMusicPercent { get; set; }
    public double? BleMicPercent { get; set; }
    public double? WindowsPlaybackPercent { get; set; }
    public double? WindowsRecordingPercent { get; set; }
    [JsonIgnore] public bool RequiresBle => BleMasterPercent.HasValue || BleMusicPercent.HasValue || BleMicPercent.HasValue;
    [JsonIgnore] public bool IsEmpty => new[]{BleMasterPercent,BleMusicPercent,BleMicPercent,WindowsPlaybackPercent,WindowsRecordingPercent}.All(v=>!v.HasValue);
    public void Validate()
    {
        if (new[] { BleMasterPercent, BleMusicPercent, BleMicPercent, WindowsPlaybackPercent, WindowsRecordingPercent }
            .Any(v => v.HasValue && (!double.IsFinite(v.Value) || v < 1 || v > 100)))
            throw new ArgumentException("Âm lượng cố định của model phải từ 1 đến 100%; để trống để giữ cấu hình hiện tại.");
    }
    public string[] Mismatches(IReadOnlyDictionary<ushort, ushort> state, bool includeMic = true)
    {
        Validate(); var errors = new List<string>();
        foreach (var (name, register, target) in new[] {
            ("MASTER DSP", SoncaBleMixerProfile.MasterVolume, BleMasterPercent),
            ("MUSIC", SoncaBleMixerProfile.MusicVolume, BleMusicPercent),
            ("MIC", SoncaBleMixerProfile.MicVolume, includeMic ? BleMicPercent : null) })
        {
            if (target is not double required) continue;
            if (!state.TryGetValue(register, out ushort raw) || raw > 8192)
                errors.Add($"{name}: chưa đọc được mức thực tế; yêu cầu {required:0.##}%");
            // C360 physical keys/faders quantize to roughly half-percent steps.
            else if (Math.Abs(SoncaBleMixerProfile.VolumeFromRaw(raw) - required) > .55)
                errors.Add($"{name}: đang {SoncaBleMixerProfile.VolumeFromRaw(raw):0.##}% → cần {required:0.##}%");
        }
        return errors.ToArray();
    }
    public void RequireMatch(IReadOnlyDictionary<ushort, ushort> state, bool includeMic = true)
    {
        var errors = Mismatches(state, includeMic);
        if (errors.Length > 0) throw new InvalidOperationException("Chỉnh âm lượng loa đúng cấu hình model trước khi đo:\n" + string.Join("\n", errors));
    }
}

public partial class TestConfig
{
    [JsonIgnore] public ModelTestVolume? ModelVolume { get; set; }
}
