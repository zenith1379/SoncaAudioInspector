using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector;

public sealed record AudioBenchPreferences
{
    // Kept as an in-memory policy container for legacy callers, never stored as BLE settings.
    [System.Text.Json.Serialization.JsonIgnore] public int MaximumRuns { get; init; } = 3;
    [System.Text.Json.Serialization.JsonIgnore] public int RequiredPasses { get; init; } = 2;
    public string BleNameFilter { get; init; } = "";
    public string BleModelFilter { get; init; } = "";
    public bool BleFilterByModel { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<ushort,ulong>? KnownControllers { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<ulong, ushort>? KnownDeviceModels { get; init; }
    public void Validate()
    {
        if (MaximumRuns is < 1 or > 10 || RequiredPasses < 1 || RequiredPasses > MaximumRuns)
            throw new ArgumentException("Auto Test: số lần chạy 1–10; số lần đạt từ 1 đến số lần chạy.");
        if (BleNameFilter.Length > 200 || BleModelFilter.Length > 200) throw new ArgumentException("Bộ lọc BLE/model tối đa 200 ký tự.");
    }
    private static string FileName
    {
        get
        {
            string? data = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
            if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector");
            return Path.Combine(data, "audio-bench-preferences.json");
        }
    }
    public static AudioBenchPreferences Load()
    {
        var value = AtomicFile.ReadJson<AudioBenchPreferences>(FileName) ?? new();
        value.Validate(); return value;
    }
    public void Save() { Validate(); AtomicFile.WriteAllText(FileName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
    public bool MatchesBleName(params string[] names) => string.IsNullOrWhiteSpace(BleNameFilter)
        || BleNameFilter.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => names.Any(name => name.Contains(part, StringComparison.OrdinalIgnoreCase)));
    public bool MatchesBleDevice(BleDeviceInfo device, IEnumerable<BluetoothAudioPeer> peers, byte[]? liveInfo = null)
    {
        var identity = SoncaBleDeviceIdentity.Resolve(device, peers, this, liveInfo);
        return BleFilterByModel ? SoncaBleModelFilter.Matches(identity, BleModelFilter) : MatchesBleName(identity.BleName, identity.AudioName);
    }
}

public sealed record C360RunAttempt(int Run, string Status, string Summary);

/// <summary>Votes only after each individual capture has been qualified and compared to its reference.</summary>
public sealed class C360RepeatedBench(IC360ElectronicBench control, AudioBenchPreferences policy,
    Func<int, IC360ElectronicBench> captureBench, Action<C360ElectronicResult>? report = null) : IC360ElectronicBench
{
    public string DeviceIdentity => control.DeviceIdentity;
    public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => control.ReadAsync(token);
    public Task WriteAsync(ushort register, ushort value, CancellationToken token) => control.WriteAsync(register, value, token);
    public void StopAudio() => control.StopAudio();
    public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        var stagePolicy=setup.TestConfiguration?.RunPolicy ?? policy;
        stagePolicy.Validate(); var results = new List<C360ElectronicResult>();
        for (int run = 1; run <= stagePolicy.MaximumRuns; run++)
        {
            token.ThrowIfCancellationRequested(); StopAudio();
            report?.Invoke(new(stage, "RUNNING", $"Lượt {run}/{stagePolicy.MaximumRuns} · cần {stagePolicy.RequiredPasses} lượt đạt"));
            results.Add(await captureBench(run).CaptureAsync(stage, setup, token));
            token.ThrowIfCancellationRequested();
            var first = results[^1];
            if (first.Reference != null && SingleRunMarginPolicy.CanAccept(setup.TestConfiguration?.IsSingleRunMarginEnabled == true,
                setup.TestConfiguration?.EffectiveSingleRunMarginDb ?? 1, run, first.Status, false, first.ResponseAssessment, out double margin))
                return first with { Summary = "1/1 lượt đạt. " + SingleRunMarginPolicy.Describe(margin, setup.TestConfiguration!.EffectiveSingleRunMarginDb)
                    + ". " + first.Summary, Attempts = [new(1, first.Status, first.Summary)] };
            if (!TestRunVote.Count(results.Select(r=>r.Status),stagePolicy.MaximumRuns,stagePolicy.RequiredPasses).NeedsAnotherRun) break;
        }
        var vote=TestRunVote.Count(results.Select(r=>r.Status),stagePolicy.MaximumRuns,stagePolicy.RequiredPasses);
        int pass=vote.Passes; string status=vote.Status;
        var selected = status == "PASS" ? results.Last(r => r.Status == "PASS")
            : status == "FAIL" ? results.Last(r => r.Status == "FAIL") : results.LastOrDefault(r => r.Status is not ("PASS" or "FAIL")) ?? results.Last();
        return selected with { Status = status,
            Summary = $"{pass}/{results.Count} lượt đạt (cần {stagePolicy.RequiredPasses}/{stagePolicy.MaximumRuns}). " + selected.Summary,
            Attempts = results.Select((r,i) => new C360RunAttempt(i+1, r.Status, r.Summary)).ToArray() };
    }
}
