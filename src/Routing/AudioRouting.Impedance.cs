using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private static string ImpedanceContext(string output, string input, bool swap, ImpedanceSettings settings)
    {
        using var devices = new MMDeviceEnumerator(); using var p = devices.GetDevice(output); using var r = devices.GetDevice(input);
        using var capture = r.AudioClient;
        using var render = p.AudioClient;
        if (p.State != DeviceState.Active || r.State != DeviceState.Active || p.DataFlow != DataFlow.Render || r.DataFlow != DataFlow.Capture)
            throw new InvalidOperationException("Ngõ phát/thu đã chọn không còn hoạt động hoặc chọn nhầm chiều phát/thu.");
        if (capture.MixFormat.Channels < 2)
            throw new InvalidOperationException($"Ngõ thu “{r.FriendlyName}” hiện có {capture.MixFormat.Channels} kênh. Impedance cần thu đồng thời hai kênh: trước Rsense và tại tải. Kiểm tra định dạng stereo và cách nối hai điểm đo.");
        if (AudioEngine.IsWindowsMonoAudioEnabled()) throw new InvalidOperationException("Tắt Windows Mono audio trước khi đo điện hai kênh.");
        settings.Validate(capture.MixFormat.SampleRate);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Version = 2, output, input, swap, settings,
            outputRate = render.MixFormat.SampleRate, outputChannels = render.MixFormat.Channels,
            rate = capture.MixFormat.SampleRate, channels = capture.MixFormat.Channels, playbackRate = 44100,
            playbackLevel = p.AudioEndpointVolume.MasterVolumeLevelScalar, recordingLevel = r.AudioEndpointVolume.MasterVolumeLevelScalar,
            playbackMute = p.AudioEndpointVolume.Mute, recordingMute = r.AudioEndpointVolume.Mute }))));
    }
    private async Task<ImpedanceCapture> RunImpedanceCaptureAsync(string outputId, string inputId, bool swap, ImpedanceSettings settings, string? requiredContext, CancellationToken token)
    {
        settings.Validate(); CloseRoutingScope();
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null) throw new InvalidOperationException("Chờ bài Line/Auto Test hiện tại kết thúc trước khi đo impedance.");
        string context = ImpedanceContext(outputId, inputId, swap, settings);
        if (requiredContext != null && requiredContext != context) throw new InvalidOperationException("Ngõ/gain/rate/Windows Levels đã đổi so với calibration; chưa phát vào tải, cần chuẩn lại.");
        StopRoutingHeadroomMonitor(); _audioEngine.StopAllAudio();
        using var devices = new MMDeviceEnumerator(); using var output = devices.GetDevice(outputId); using var input = devices.GetDevice(inputId);
        if (output.AudioEndpointVolume.Mute || input.AudioEndpointVolume.Mute) throw new InvalidOperationException("Ngõ phát/thu đang mute; kiểm tra Windows Levels.");
        var previous = (_audioEngine.PlaybackSampleRate, _audioEngine.RecordingChannel, _audioEngine.PlaybackChannel, _audioEngine.UseExclusivePlayback, _audioEngine.RecordingGain);
        var left = new List<float>(); var right = new List<float>(); var gate = new object();
        try
        {
            _audioEngine.PlaybackSampleRate = 44100; _audioEngine.RecordingChannel = 0; _audioEngine.PlaybackChannel = 0;
            _audioEngine.UseExclusivePlayback = false; _audioEngine.RecordingGain = 1;
            float[] signal = ImpedanceMeasurement.Generate(settings, 44100);
            await _audioEngine.PlayAndRecordAsync(output, input, SignalType.Sweep, 0, settings.Seconds, cancellationToken: token,
                recordingChannel: 0, customSweepSamples: signal, stereoObserver: (l, r) => { lock (gate) { left.AddRange(l); right.AddRange(r); } });
            token.ThrowIfCancellationRequested();
            if (ImpedanceContext(outputId, inputId, swap, settings) != context) throw new InvalidOperationException("Ngõ/gain/Windows Levels đổi trong lúc đo; không sử dụng bản thu.");
            float[] a, b; lock (gate) { a = left.ToArray(); b = right.ToArray(); }
            int rate = _audioEngine.RecordingSampleRate;
            var ratios = await Task.Run(() => ImpedanceMeasurement.Measure(swap ? b : a, swap ? a : b, rate, settings), token);
            return new(context, ratios);
        }
        finally
        {
            _audioEngine.StopAllAudio();
            (_audioEngine.PlaybackSampleRate, _audioEngine.RecordingChannel, _audioEngine.PlaybackChannel, _audioEngine.UseExclusivePlayback, _audioEngine.RecordingGain) = previous;
        }
    }
}
