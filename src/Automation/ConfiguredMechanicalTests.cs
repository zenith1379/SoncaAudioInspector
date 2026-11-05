namespace SoncaAudioInspector;

public sealed record MechanicalCaptureData(float[]? Captured, float[]? Background, int SampleRate,
    string Route = "", MechanicalTestResult? PrebuiltUnavailable = null);

public static partial class ConfiguredAcousticTests
{
    public static bool CanShareMechanicalCapture(MechanicalTestRequest first, MechanicalTestRequest second) =>
        first.FrequencyHz == second.FrequencyHz && first.LevelDbFs == second.LevelDbFs && first.DurationSeconds == second.DurationSeconds;

    public static Task<IReadOnlyList<ConfiguredTestResult>> RunMechanicalTestsAsync(TestConfig config, double frequencyScale,
        Func<MechanicalTestRequest, CancellationToken, Task<MechanicalCaptureData>> captureAsync,
        Action<string, string>? log = null, CancellationToken cancellationToken = default)
    {
        var requests = new List<MechanicalTestRequest>();
        if (config.TestAirLeak) requests.Add(config.MechanicalRequest(MechanicalTestKind.AirLeak) with { FrequencyHz = config.MechanicalFrequencyHz * frequencyScale });
        if (config.TestLooseParticle) requests.Add(config.MechanicalRequest(MechanicalTestKind.LooseParticle) with { FrequencyHz = config.MechanicalFrequencyHz * frequencyScale });
        return RunMechanicalTestsAsync(requests, captureAsync, log, cancellationToken);
    }

    // The request overload also supports future per-test stimuli without changing capture sharing rules.
    public static async Task<IReadOnlyList<ConfiguredTestResult>> RunMechanicalTestsAsync(IReadOnlyList<MechanicalTestRequest> requests,
        Func<MechanicalTestRequest, CancellationToken, Task<MechanicalCaptureData>> captureAsync,
        Action<string, string>? log = null, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests) request.Validate();
        var results = new List<ConfiguredTestResult>();
        for (int i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[i];
            bool shared = i + 1 < requests.Count && CanShareMechanicalCapture(request, requests[i + 1]);
            var group = shared ? new[] { request, requests[++i] } : new[] { request };
            MechanicalCaptureData data;
            try
            {
                data = await captureAsync(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                data = new(null, null, 0, PrebuiltUnavailable: MechanicalTestResult.Unavailable(request.Kind, "THU LỖI", "Không hoàn tất bản thu: " + ex.Message));
            }
            if (shared) log?.Invoke("Kiểm tra bổ sung", data.PrebuiltUnavailable != null ? "Rò khí + Hạt rác: cùng trạng thái thu INVALID; không sử dụng bản thu dở dang." : "Rò khí + Hạt rác: dùng chung 1 lần phát tone và nền hợp lệ.");
            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = data.PrebuiltUnavailable != null ? data.PrebuiltUnavailable with { Kind = item.Kind }
                    : data.Captured == null || data.Background == null ? MechanicalTestResult.Unavailable(item.Kind, "THIẾU DỮ LIỆU", "Chưa có bản thu tone/nền.")
                    : await Task.Run(() => MechanicalDiagnostics.Analyze(data.Captured, data.Background, data.SampleRate, item), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(Mechanical(result with { Acquisition = data.Route }));
            }
        }
        return results;
    }
}
