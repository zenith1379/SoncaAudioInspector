using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public partial class TestConfig
{
    [JsonConverter(typeof(ConfigBooleanConverter))]
    public bool AllowLineOutliers { get; set; }
    public double AllowedLineOutlierPercent { get; set; } = 10;
    [JsonIgnore] public double EffectiveLineOutlierPercent => AllowLineOutliers ? AllowedLineOutlierPercent : 0;
}

public readonly record struct LineOutlierAssessment(int Outside, int Total, double AllowedPercent, bool CriticalFailure = false)
{
    public double OutsidePercent => Total > 0 ? 100d * Outside / Total : double.NaN;
    public bool Passed => Total > 0 && Outside >= 0 && Outside <= Total && !CriticalFailure
        && double.IsFinite(AllowedPercent) && AllowedPercent >= 0 && AllowedPercent <= 100
        && Outside * 100d <= Total * AllowedPercent + 1e-9;
    public string Describe() => Total > 0
        ? $"Ngoài biên: {Outside}/{Total} điểm ({OutsidePercent:0.##}%), cho phép ≤{AllowedPercent:0.##}% trong freq_range."
            + (CriticalFailure ? " Có điểm CRITICAL vượt biên." : "")
        : "Chưa có điểm hợp lệ để so LINE CHUẨN.";
}
