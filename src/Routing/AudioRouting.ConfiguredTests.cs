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
                    _standardOneKilohertzLevelDbFs ?? double.NaN, config.SoundLevelToleranceDb,
                    config.EvaluationRange, ResponseLimitSettings.Capture(config, ParseDoubleSafe(TxtFreqTolerance?.Text ?? "", 3.0))));
            }
            if (config.TestAirLeak || config.TestLooseParticle)
            {
                var mechanical = await ConfiguredAcousticTests.RunMechanicalTestsAsync(config, _testRunner.PlaybackFrequencyScale,
                    async (request, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (!acquisitionValid) return new MechanicalCaptureData(null, null, 0, PrebuiltUnavailable:
                            MechanicalTestResult.Unavailable(request.Kind, "BẢN THU CHÍNH CHƯA ĐẠT", "Bản thu chính chưa hợp lệ; chưa phát tone bổ sung. Kiểm tra route và đo lại."));
                        var running = new List<ConfiguredTestResult>();
                        if (config.TestAirLeak) running.Add(new("Rò khí", "RUNNING", "Đang thu nền và phát tone…"));
                        if (config.TestLooseParticle) running.Add(new("Hạt rác", "RUNNING", "Đang thu nền và phát tone…"));
                        test.AdditionalResults = results.Concat(running).ToArray();
                        return await CaptureMechanicalSamplesAsync(request, playback, recording, token);
                    }, AppendLog, cancellation.Token);
                foreach (var result in mechanical) Add(result);
            }
            foreach (var additionalTest in AdditionalTestRegistry.GetEnabled(config))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!acquisitionValid)
                {
                    Add(new(additionalTest.Metadata.DisplayName, "INVALID", "Bản thu chính chưa hợp lệ; chưa phát tone bổ sung. Kiểm tra route và đo lại."));
                    continue;
                }
                test.AdditionalResults = results.Append(new ConfiguredTestResult(additionalTest.Metadata.DisplayName, "RUNNING", "Đang thu nền và phát tone bước…")).ToArray();
                try
                {
                    string route = $"Out: {playback.FriendlyName} · In: {recording.FriendlyName}";
                    var context = new AdditionalTestContext(_audioEngine.PlaybackSampleRate, _audioEngine.RecordingSampleRate, _testRunner.PlaybackFrequencyScale, route);
                    var result = await AdditionalTestRegistry.RunAsync(additionalTest, config, context, async (plan, token) =>
                    {
                        if (!_audioEngine.UseExclusivePlayback && AudioEngine.IsWindowsMonoAudioEnabled())
                            throw new InvalidOperationException("Tắt Windows Mono audio trước khi đo để giữ đúng kênh phát/thu.");
                        string contextKey = DiagnosticContextKey();
                        var background = await _audioEngine.CaptureSilenceAsync(recording, null, plan.BackgroundSeconds, token);
                        token.ThrowIfCancellationRequested();
                        if (!SignalDiagnostics.QuietBackground(background.DutSamples, background.DutSampleRate))
                            throw new InvalidOperationException("Nền cao/thiếu hoặc có xung ngoại lai; chưa phát tone. Chờ yên rồi đo lại.");
                        additionalTest.Validate(config, context with { RecordingSampleRate = background.DutSampleRate });
                        var captures = new List<float[]>();
                        foreach (var step in plan.Steps)
                        {
                            token.ThrowIfCancellationRequested();
                            if (_audioEngine.PlaybackSampleRate != context.PlaybackSampleRate || DiagnosticContextKey() != contextKey)
                                throw new InvalidOperationException("Route/gain/định dạng phát đã đổi giữa các bước; đo lại.");
                            var captured = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep,
                                step.PhysicalFrequencyHz, step.DurationSeconds, cancellationToken: token,
                                recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: step.Samples);
                            token.ThrowIfCancellationRequested();
                            if (_audioEngine.RecordingSampleRate != background.DutSampleRate || DiagnosticContextKey() != contextKey)
                                throw new InvalidOperationException("Route/gain/định dạng thu đã đổi giữa các bước; đo lại.");
                            captures.Add(captured);
                        }
                        return new AdditionalTestCaptureData(background.DutSampleRate, background.DutSamples, captures, route);
                    }, cancellation.Token);
                    Add(result);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Add(new(additionalTest.Metadata.DisplayName, "INVALID", "Không hoàn tất bản thu: " + GetMeasurementErrorMessage(ex))); }
                finally { _audioEngine.StopAllAudio(); }
            }
        }
        catch (OperationCanceledException) { Add(AdditionalTestRegistry.CancelledResult()); }
        finally { _autoDiagnosticsCancellation = null; }
    }
}
