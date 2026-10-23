using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private SweepPolarityReference? _standardPolarityReference, _pendingPolarityReference, _capturePolarityReference;
    private string _captureDiagnosticContext = "";
    private readonly ConditionalWeakTable<StandardAcousticResult, PolaritySnapshot> _polaritySnapshots = new();
    private sealed record PolaritySnapshot(string Context, PolarityDiagnosticResult Result);

    private string DiagnosticContextKey()
    {
        var mic = ResolveFeqMicrophoneCalibration();
        string calibrationHash = mic != null && System.IO.File.Exists(mic.FilePath)
            ? Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(mic.FilePath))) : "NONE";
        var context = new { route = _referenceRouteKey, output = SelectedPlaybackDevice?.ID, input = SelectedRecordingDevice?.ID,
            _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel, _audioEngine.RecordingGain,
            _audioEngine.UseExclusivePlayback, _audioEngine.PlaybackSampleRate, level = PlaybackLevelDbfs, calibrationHash,
            outputVolume = SelectedPlaybackDevice?.AudioEndpointVolume.MasterVolumeLevelScalar,
            inputVolume = SelectedRecordingDevice?.AudioEndpointVolume.MasterVolumeLevelScalar };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(context))));
    }

    private void RememberSweepPolarity(StandardAcousticResult result)
    {
        string context = _captureDiagnosticContext;
        try { if (DiagnosticContextKey() != context) context = ""; }
        catch { context = ""; } // A changed/unavailable context invalidates polarity, not the saved sweep.
        _polaritySnapshots.Remove(result);
        _polaritySnapshots.Add(result, new(context,
            PolarityDiagnosticResult.Compare(result, context, _capturePolarityReference)));
    }

    private SweepPolarityReference? CurrentPolarityReference() => _testRunner.LastSweepResult is { } result
        && _polaritySnapshots.TryGetValue(result, out var snapshot) ? SweepPolarityReference.From(result, snapshot.Context) : null;

    private async Task<SignalDiagnosticResult> RunSignalDiagnosticAsync(SignalDiagnosticRequest request, CancellationToken cancellationToken)
    {
        request.Validate(_audioEngine.PlaybackSampleRate);
        CloseRoutingScope();
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null) throw new InvalidOperationException("Thiết bị đang đo Line/Auto Test hoặc bài khác; chờ hoàn tất.");
        StopRoutingHeadroomMonitor();
        var playback = SelectedPlaybackDevice ?? throw new InvalidOperationException("Chưa chọn ngõ phát.");
        var recording = SelectedRecordingDevice ?? throw new InvalidOperationException("Chưa chọn ngõ thu.");
        if (!_audioEngine.UseExclusivePlayback && AudioEngine.IsWindowsMonoAudioEnabled())
            throw new InvalidOperationException("Tắt Windows Mono audio để giữ đúng kênh phát/thu.");
        int playbackRate = _audioEngine.PlaybackSampleRate;
        string contextKey = DiagnosticContextKey();
        string route = $"Out {playback.FriendlyName} / {(_audioEngine.PlaybackChannel is int o ? (o + 1).ToString() : "L+R")} → In {recording.FriendlyName} / {(_audioEngine.RecordingChannel is int i ? (i + 1).ToString() : "Trộn")}";
        var background = await _audioEngine.CaptureSilenceAsync(recording, null, .5, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SignalDiagnostics.QuietBackground(background.DutSamples, background.DutSampleRate))
            return SignalDiagnosticResult.Invalid(request.Kind, "Nền cao/thiếu hoặc có xung ngoại lai; chưa phát tín hiệu. Chờ yên rồi đo lại.") with { Acquisition = route };
        request.Validate(background.DutSampleRate);
        async Task<float[]> Capture(bool upper)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_audioEngine.PlaybackSampleRate != playbackRate) throw new InvalidOperationException("Định dạng phát đã thay đổi; đo lại.");
            float[] stimulus = SignalDiagnostics.Generate(request, playbackRate, upper);
            float[] captured = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, request.FrequencyHz,
                request.DurationSeconds, cancellationToken: cancellationToken, recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: stimulus);
            cancellationToken.ThrowIfCancellationRequested();
            if (DiagnosticContextKey() != contextKey) throw new InvalidOperationException("Route/gain/calibration hoặc mức Windows đã đổi giữa lượt đo; đo lại.");
            if (_audioEngine.RecordingSampleRate != background.DutSampleRate) throw new InvalidOperationException("Định dạng thu đã thay đổi giữa các lượt; đo lại.");
            return captured;
        }
        float[] lower = await Capture(false);
        SignalDiagnosticResult result;
        if (request.Kind == SignalDiagnosticKind.Imd)
            result = await Task.Run(() => SignalDiagnostics.AnalyzeImd(lower, background.DutSamples, background.DutSampleRate, request), cancellationToken);
        else
        {
            var first = await Task.Run(() => SignalDiagnostics.MeasureTone(lower, background.DutSamples, background.DutSampleRate, request), cancellationToken);
            if (!first.IsValid) return SignalDiagnosticResult.Invalid(request.Kind, "Mức thấp chưa đạt; chưa phát mức cao. " + first.Reason) with { Acquisition = route };
            float[] upper = await Capture(true);
            result = await Task.Run(() => SignalDiagnostics.Compression(first,
                SignalDiagnostics.MeasureTone(upper, background.DutSamples, background.DutSampleRate, request), request), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Acquisition = $"{DateTime.Now:HH:mm:ss} · {route} · phát {playbackRate} Hz / thu {background.DutSampleRate} Hz · {request.LevelDbFs:0} dBFS peak"
            + (request.Kind == SignalDiagnosticKind.Compression ? $" → {request.LevelDbFs + request.IncreaseDb:0} dBFS peak" : " · hai tone 4:1") + $" · {request.DurationSeconds:0.#} s/mức" };
    }
}
