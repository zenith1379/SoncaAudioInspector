using System.Threading;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private CancellationTokenSource? _autoDiagnosticsCancellation;

    private async Task RunTrackedConfiguredTestsAsync(AutoTestCaseItem test, MMDevice playback, MMDevice recording, bool acquisitionValid)
    {
        var task = RunConfiguredTestsAsync(test, playback, recording, acquisitionValid);
        _activeMeasurementTask = task;
        try { await task; }
        finally { if (_activeMeasurementTask == task) _activeMeasurementTask = null; }
    }

    private async Task RunConfiguredTestsAsync(AutoTestCaseItem test, MMDevice playback, MMDevice recording, bool acquisitionValid)
    {
        var config = test.Config;
        if (!config.HasAdditionalTests) return;
        using var cancellation = new CancellationTokenSource();
        _autoDiagnosticsCancellation = cancellation;
        var results = new List<ConfiguredTestResult>();
        void Add(ConfiguredTestResult result)
        {
            results.Add(result); test.AdditionalResults = results.ToArray(); AppendLog("Kiểm tra bổ sung", result.Display);
        }
        try
        {
            try { config.ValidateAdditionalTests(); }
            catch (Exception ex) { Add(new("Cấu hình bổ sung", "INVALID", ex.Message)); return; }
            cancellation.Token.ThrowIfCancellationRequested();
            if (config.TestEffect)
                Add(acquisitionValid ? ConfiguredAcousticTests.Effect(_testRunner.LastSweepResult?.ImpulseEffects,
                    _testRunner.ImpulseEffectsSettings, _standardImpulseEffects)
                    : new("Effect", "INVALID", "Bản thu chính chưa hợp lệ; kiểm tra route, mức thu, clipping và nhiễu nền."));
            if (config.TestSoundLevel)
            {
                var points = new List<DiagnosisResponsePoint>();
                for (int i = 0; i < Math.Min(_freqs.Count, _dbValues.Count); i++)
                    if (TryGetComparableStandardTarget(_freqs[i], out double target))
                        points.Add(new(_freqs[i], _dbValues[i], target, config.SoundLevelToleranceDb));
                Add(ConfiguredAcousticTests.SoundLevel(points, acquisitionValid, HasComparableStandardCurve(),
                    _testRunner.NormalizeFrequencyResponseToOneKilohertz, _testRunner.LastOneKilohertzLevelDbFs,
                    _standardOneKilohertzLevelDbFs ?? double.NaN, config.SoundLevelToleranceDb));
            }
            foreach (var (enabled, kind, name) in new[] { (config.TestAirLeak, MechanicalTestKind.AirLeak, "Rò khí"), (config.TestLooseParticle, MechanicalTestKind.LooseParticle, "Hạt rác") })
            {
                if (!enabled) continue;
                cancellation.Token.ThrowIfCancellationRequested();
                if (!acquisitionValid) { Add(new(name, "INVALID", "Bản thu chính chưa hợp lệ; chưa phát tone bổ sung. Kiểm tra route và đo lại.")); continue; }
                test.AdditionalResults = results.Append(new ConfiguredTestResult(name, "RUNNING", "Đang thu nền và phát tone riêng…")).ToArray();
                try
                {
                    var request = config.MechanicalRequest(kind) with { FrequencyHz = config.MechanicalFrequencyHz * _testRunner.PlaybackFrequencyScale };
                    var captured = await CaptureMechanicalDiagnosticAsync(request, playback, recording, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    Add(ConfiguredAcousticTests.Mechanical(captured));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Add(new(name, "INVALID", "Không hoàn tất bản thu: " + GetMeasurementErrorMessage(ex))); }
            }
        }
        catch (OperationCanceledException) { Add(new("Bài bổ sung", "INVALID", "Đã hủy; không sử dụng kết quả dở dang.")); }
        finally { _autoDiagnosticsCancellation = null; }
    }
}
