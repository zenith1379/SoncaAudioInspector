using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private const int StandardSampleRate = 44100;
    private DateTime _lastSampleRateWarningPollUtc;
    private int? _lastObservedCaptureRate;
    private string _sampleRateWarningText = "";
    private string _sampleRateSyncFailure = "";
    private (string? OutputId, int? OutputRate, string? InputId, int? InputRate)? _observedEndpointRates;
    private bool _captureFormatRefreshPending;
    private int? _observedSharedInputRate;

    private void EnsureSelectedStandardSampleRates(bool beforeMeasurement = false)
    {
        if (GetSelectedPlaybackSampleRate() != StandardSampleRate) { _sampleRateSyncFailure = ""; return; }
        if (_audioEngine == null
            || _activeMeasurementTask != null || _routingScopeWindow != null
            || (!beforeMeasurement && IsModelTransitionBusy)
            || ((Application.Current?.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false)) return;
        _sampleRateSyncFailure = "";
        var devices = new[] { SelectedPlaybackDevice, SelectedRecordingDevice }.Where(d => d != null).Cast<MMDevice>().ToArray();
        var pending = devices.Where(d => ReadEndpointSampleRate(d) != StandardSampleRate).ToArray();
        if (pending.Length == 0) return;
        StopRoutingHeadroomMonitor();
        _audioEngine.StopAllAudio();
        _audioEngine.CaptureSession.Reset();
        foreach (var device in pending)
        {
            bool success = AudioSampleRateSetup.EnsureStandard(device, out string message);
            if (message.Length > 0) AppendLog(success ? "Đồng bộ 44.1 kHz" : "Cảnh báo 44.1 kHz", message);
            if (!success) _sampleRateSyncFailure += message + " ";
        }
        _lastObservedCaptureRate = null;
        UpdateSampleRateWarning();
    }

    private static int? ReadEndpointSampleRate(MMDevice? device)
    {
        try { return device == null ? null : AudioSampleRateSetup.ReadDefaultFormat(device.ID).SampleRate; }
        catch { return null; }
    }

    private static WaveFormat? ReadEndpointFormat(MMDevice? device)
    {
        if (device == null) return null;
        using var enumerator = new MMDeviceEnumerator();
        using var current = enumerator.GetDevice(device.ID);
        using var client = current.AudioClient;
        return client.MixFormat;
    }

    private static string DescribeAudioFormats(WaveFormat device, WaveFormat shared, bool playback)
    {
        static string Format(WaveFormat f)
        {
            bool floating = f.Encoding == WaveFormatEncoding.IeeeFloat
                || f is WaveFormatExtensible e && e.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            string rate = (f.SampleRate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return $"{rate} kHz · {f.BitsPerSample}-bit {(floating ? "Float" : "PCM")} · {f.Channels} kênh";
        }
        return $"Default Format: {Format(device)}\nShared {(playback ? "phát" : "thu")}: {Format(shared)}";
    }

    private static string BuildSharedFormatWarning(int? defaultRate, int? sharedRate)
    {
        if (defaultRate is not int configured || sharedRate is not int shared || configured == shared) return "";
        static string Rate(int rate) => (rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz";
        return $"Luồng Shared ngõ thu {Rate(shared)} khác Default Format {Rate(configured)}. "
            + "Windows đang dùng đường thu có chuyển đổi/xử lý; kiểm tra Audio enhancements. Rate phân tích lấy từ luồng thu thực tế.";
    }

    private void ObserveEndpointSampleRates(string? outputId, int? outputRate, string? inputId, int? inputRate)
    {
        if (_observedEndpointRates is { } previous)
        {
            bool outputChanged = previous.OutputId != outputId
                || (previous.OutputRate.HasValue && outputRate.HasValue && previous.OutputRate != outputRate);
            bool inputChanged = previous.InputId != inputId
                || (previous.InputRate.HasValue && inputRate.HasValue && previous.InputRate != inputRate);
            if (outputChanged || inputChanged)
            {
                // A previous sweep describes its own capture, not the current Windows setup.
                // Its rate remains in LastSweepResult and the measurement details.
                _lastObservedCaptureRate = null;
                _sampleRateSyncFailure = "";
                _captureFormatRefreshPending = true;
            }
            // A transient read failure must not erase the last known format transition.
            if (previous.OutputId == outputId) outputRate ??= previous.OutputRate;
            if (previous.InputId == inputId) inputRate ??= previous.InputRate;
        }
        _observedEndpointRates = (outputId, outputRate, inputId, inputRate);
    }

    private void RefreshCaptureFormatIfIdle()
    {
        if (!_captureFormatRefreshPending || _audioEngine == null || IsTestingBusy
            || _activeMeasurementTask != null || _routingScopeWindow != null || IsModelTransitionBusy
            || ((Application.Current?.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false)) return;
        StopRoutingHeadroomMonitor();
        _audioEngine.CaptureSession.Reset();
        _captureFormatRefreshPending = false;
        _headroomRetryAtUtc = DateTime.MinValue;
        if (IsLoaded) MaintainRoutingHeadroomMonitor();
    }

    private static string BuildSampleRateWarning(int appRate, int? outputRate, int? inputRate, int? actualCaptureRate)
    {
        var mismatches = new List<string>();
        static string Rate(int rate) => (rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz";
        if (appRate != StandardSampleRate) mismatches.Add("App " + Rate(appRate));
        if (outputRate is int output && output != StandardSampleRate) mismatches.Add("Default Format ngõ phát " + Rate(output));
        if (inputRate is int input && input != StandardSampleRate) mismatches.Add("Default Format ngõ thu " + Rate(input));
        if (actualCaptureRate is int actual && actual != StandardSampleRate) mismatches.Add("lượt vừa thu " + Rate(actual));
        return mismatches.Count == 0 ? "" : "CẢNH BÁO SAMPLE RATE — chuẩn đo 44.1 kHz. "
            + string.Join("; ", mismatches) + ". Default Format và rate luồng thu là hai thông tin riêng. Đo lại sau khi thay đổi thiết lập.";
    }

    private void UpdateSampleRateWarning(int? actualCaptureRate = null)
    {
        if (_reviewedAutoTestCase is { Config.SpeakerStage: not null, Acquisition: { } saved })
        {
            _sampleRateWarningText = saved.PlaybackSampleRate == StandardSampleRate && saved.RecordingSampleRate == StandardSampleRate ? ""
                : $"Bản thu bài đang xem: phát yêu cầu {saved.PlaybackSampleRate} Hz / thu thực {saved.RecordingSampleRate} Hz; chuẩn đối chiếu 44100 Hz.";
            TxtSampleRateWarning.Text = _sampleRateWarningText;
            BorderSampleRateWarning.Visibility = _sampleRateWarningText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        int? outputRate = ReadEndpointSampleRate(SelectedPlaybackDevice);
        int? inputRate = ReadEndpointSampleRate(SelectedRecordingDevice);
        int? sharedInputRate;
        try { sharedInputRate = ReadEndpointFormat(SelectedRecordingDevice)?.SampleRate; }
        catch { sharedInputRate = null; }
        ObserveEndpointSampleRates(SelectedPlaybackDevice?.ID, outputRate, SelectedRecordingDevice?.ID, inputRate);
        if (_observedSharedInputRate.HasValue && sharedInputRate.HasValue && _observedSharedInputRate != sharedInputRate)
        {
            _lastObservedCaptureRate = null;
            _captureFormatRefreshPending = true;
        }
        _observedSharedInputRate = sharedInputRate ?? _observedSharedInputRate;
        if (actualCaptureRate.HasValue) _lastObservedCaptureRate = actualCaptureRate;
        if (outputRate == StandardSampleRate && inputRate == StandardSampleRate)
            _sampleRateSyncFailure = "";
        RefreshCaptureFormatIfIdle();
        _sampleRateWarningText = BuildSampleRateWarning(GetSelectedPlaybackSampleRate(),
            outputRate, inputRate, _lastObservedCaptureRate);
        string sharedWarning = _audioEngine?.UseExclusivePlayback == true ? "" : BuildSharedFormatWarning(inputRate, sharedInputRate);
        if (sharedWarning.Length > 0) _sampleRateWarningText = (_sampleRateWarningText + " " + sharedWarning).Trim();
        if (_sampleRateSyncFailure.Length > 0) _sampleRateWarningText += " " + _sampleRateSyncFailure;
        if (TxtSampleRateWarning == null || BorderSampleRateWarning == null) return;
        TxtSampleRateWarning.Text = _sampleRateWarningText;
        BorderSampleRateWarning.Visibility = _sampleRateWarningText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
