using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public partial class TestConfig
{
    public int MaximumRuns { get; set; } = 3;
    public int RequiredPasses { get; set; } = 2;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigBooleanConverter))]
    public bool? SingleRunPassWhenMarginDbEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SingleRunPassMarginDb { get; set; }
    [JsonIgnore] public bool IsSingleRunMarginEnabled => SingleRunPassWhenMarginDbEnabled == true;
    [JsonIgnore] public double EffectiveSingleRunMarginDb => SingleRunPassMarginDb ?? 1.0;
    public void ValidateRunPolicy()
    {
        if ((SingleRunPassMarginDb.HasValue || IsSingleRunMarginEnabled)
            && (!double.IsFinite(EffectiveSingleRunMarginDb) || EffectiveSingleRunMarginDb <= 0 || EffectiveSingleRunMarginDb > 12))
            throw new ArgumentException($"{id}: khoảng cách biên M cần hữu hạn, > 0 và ≤ 12.0 dB.");
        if (MaximumRuns is < 1 or > 10 || RequiredPasses < 1 || RequiredPasses > MaximumRuns)
            throw new ArgumentException($"{id}: số lần chạy 1–10, số lần cần đạt từ 1 đến số lần chạy.");
    }
    [JsonIgnore] public AudioBenchPreferences RunPolicy => new() {MaximumRuns=MaximumRuns,RequiredPasses=RequiredPasses};
}

public sealed record TestRunVote(string Status,int Passes,int Attempts,bool NeedsAnotherRun)
{
    public static TestRunVote Count(IEnumerable<string> results,int maximum,int required)
    {
        new TestConfig {MaximumRuns=maximum,RequiredPasses=required}.ValidateRunPolicy();
        var rows=results.ToArray();
        if(rows.Length>maximum) throw new ArgumentException("Số lượt đo vượt cấu hình.");
        int pass=rows.Count(s=>s=="PASS"),fail=rows.Count(s=>s=="FAIL");
        bool more=pass<required && rows.Length<maximum && pass+maximum-rows.Length>=required;
        return new(pass>=required?"PASS":more?"RUNNING":fail>maximum-required?"FAIL":"INVALID",pass,rows.Length,more);
    }
}

/// <summary>Shared by sweep confirmation and full-test voting; reference acquisition never shortcuts.</summary>
public static class SingleRunMarginPolicy
{
    public static bool CanAccept(bool enabled, double requiredMarginDb, int attempt, string status,
        bool referenceAcquisition, ResponseLimitAssessment? assessment, out double margin)
    {
        margin = double.NaN;
        return enabled && double.IsFinite(requiredMarginDb) && requiredMarginDb > 0 && requiredMarginDb <= 12
            && attempt == 1 && status == "PASS" && !referenceAcquisition && assessment != null
            && assessment.TryGetMinimumEffectiveMarginDb(out margin) && margin >= requiredMarginDb;
    }
    public static string Describe(double margin, double required) => $"1 lượt: cách biên tối thiểu {margin:F2} dB ≥ {required:F2} dB";
}
