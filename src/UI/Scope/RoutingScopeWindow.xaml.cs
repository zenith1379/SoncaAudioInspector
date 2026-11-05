using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

/// <summary>
/// A non-calibrated input scope for Audio Routing. It intentionally uses the
/// chosen DUT recording endpoint rather than a second ambient microphone.
/// </summary>
public partial class RoutingScopeWindow : Window
{
    private readonly AudioEngine _audioEngine;
    private readonly ScopeWavPlayer _wavPlayer = new();
    private readonly MMDevice _playback;
    private readonly MMDevice _recording;
    private readonly int? _playbackChannel;
    private readonly int? _recordingChannel;
    private readonly DispatcherTimer _refreshTimer;
    private readonly FreshCaptureWindow _captureWindow = new();
    private static readonly double[] MillisecondsPerDivision = { 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0 };
    private static readonly double[] VoltsPerDivision = { 0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0 };
    private const int DefaultVoltsPerDivisionIndex = 11;
    private int _timebaseIndex = 4;
    private int _voltsPerDivisionIndex = DefaultVoltsPerDivisionIndex;
    private bool _resetScopeView = true;
    private const double SineFrequencyHz = 1000.0;
    private const double SineLevelRmsDbFs = -12.0;
    private DateTime _lastSineAnalysisUtc = DateTime.MinValue;
    private ToneQualityMetrics? _lastSineQuality;
    private bool _sineRunning;
    private bool _scopeCaptureStarted;
    private bool _scopeSettingsApplied;
    private int? _previousRecordingChannel;
    private bool _fitNextSineCapture = true;
    private string _lastNoiseClass = "";
    private DateTime _lastNoiseLogUtc = DateTime.MinValue;
    private bool _isRightPanning;
    private Point _rightPanStart;
    private double _rightPanStartBottom;
    private double _rightPanStartTop;
    private double? _recordingEndpointVolume;
    private bool? _recordingEndpointMuted;
    private DateTime _lastRecordingEndpointReadUtc = DateTime.MinValue;

    public RoutingScopeWindow(AudioEngine audioEngine, MMDevice playback, MMDevice recording, int? playbackChannel, int? recordingChannel)
    {
        _audioEngine = audioEngine;
        _playback = playback;
        _recording = recording;
        _playbackChannel = playbackChannel;
        _recordingChannel = recordingChannel;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _refreshTimer.Tick += (_, _) => RefreshScope();

        InitializeComponent();
        ApplyDarkPlotAppearance();
        UpdateTimebaseButtons();
        UpdateVoltsPerDivisionButtons();
        PlotScope.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PlotScope_PreviewMouseWheel), true);
        TxtDevices.Text = $"OUT: {_playback.FriendlyName} [{ChannelLabel(_playbackChannel)}]  →  IN: {_recording.FriendlyName} [{ChannelLabel(_recordingChannel)}]";
        Loaded += RoutingScopeWindow_Loaded;
        Closed += RoutingScopeWindow_Closed;
    }

    private void RoutingScopeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _voltsPerDivisionIndex = DefaultVoltsPerDivisionIndex;
            UpdateVoltsPerDivisionButtons();
            _previousRecordingChannel = _audioEngine.RecordingChannel;
            _scopeSettingsApplied = true;
            _audioEngine.RecordingChannel = _recordingChannel;
            ResetCaptureAnalysis();
            _audioEngine.StartContinuousCapture(_recording, _captureWindow.Append);
            _scopeCaptureStarted = true;
            _refreshTimer.Start();
            AppendNoiseLog("Scope mở; đang tự lấy mẫu nền từ ngõ thu DUT.");
        }
        catch (Exception ex)
        {
            AppendNoiseLog("Chi tiết kỹ thuật: " + ex);
            MessageBox.Show(this, "Không thể mở ngõ thu cho Scope: " + AudioMeasurementErrors.Describe(ex), "Lỗi Scope", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void RoutingScopeWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        try { _wavPlayer.Dispose(); } catch { }
        if (_scopeCaptureStarted) { try { _audioEngine.StopContinuousCapture(); } catch { } }
        _sineRunning = _scopeCaptureStarted = false;
        if (_scopeSettingsApplied)
        {
            _audioEngine.RecordingChannel = _previousRecordingChannel;
            _scopeSettingsApplied = false;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Button && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BtnSineCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_sineRunning)
            {
                _wavPlayer.Stop();
                _sineRunning = false;
                ResetCaptureAnalysis();
                UpdateSineButtonContent();
                BtnSineCheck.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
                AppendNoiseLog("Đã dừng WAV 1 kHz; Scope tiếp tục thu ngõ IN.");
                SetSineActionStatus("Đã dừng WAV. Scope tiếp tục thu; có thể theo dõi nguồn 1 kHz phát từ bên ngoài.");
                return;
            }

            _wavPlayer.Play(_playback.ID, _playbackChannel);
            _sineRunning = true;
            _fitNextSineCapture = true;
            FitSineTimebase();
            ResetCaptureAnalysis();
            UpdateSineButtonContent();
            BtnSineCheck.Background = new SolidColorBrush(Color.FromRgb(146, 64, 14));
            AppendNoiseLog($"Đang phát file WAV 1 kHz, -12 dBFS RMS qua {_playback.FriendlyName} [{ChannelLabel(_playbackChannel)}], WASAPI Shared.");
            SetSineActionStatus("Đang phát WAV 1 kHz qua OUT. Scope chỉ vẽ và phân tích tín hiệu thực thu ở IN.");
        }
        catch (Exception ex)
        {
            StopSineAfterError();
            AppendNoiseLog("Chi tiết kỹ thuật: " + ex);
            SetSineActionStatus("Không thể phát sine: " + AudioMeasurementErrors.Describe(ex));
            MessageBox.Show(this, TxtSineActionStatus.Text, "Lỗi phát sine", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetCaptureAnalysis()
    {
        _captureWindow.Reset();
        _lastSineQuality = null;
        _lastSineAnalysisUtc = DateTime.MinValue;
        _fitNextSineCapture = true;
    }

    private void SetSineActionStatus(string message)
    {
        TxtSineActionStatus.Text = message;
    }

    private static string ChannelLabel(int? channel) => channel.HasValue ? $"kênh {channel.Value + 1}" : "trộn/cả hai kênh";

    private void StopSineAfterError()
    {
        _wavPlayer.Stop();
        _sineRunning = false;
        ResetCaptureAnalysis();
        UpdateSineButtonContent();
    }

    private void UpdateSineButtonContent()
    {
        if (BtnSineCheck == null) return;
        BtnSineCheck.Content = _sineRunning
            ? "■ DỪNG WAV 1 kHz"
            : "▶ PHÁT WAV 1 kHz";
    }

    private void BtnTimebaseDown_Click(object sender, RoutedEventArgs e)
    {
        _timebaseIndex = Math.Max(0, _timebaseIndex - 1);
        UpdateTimebaseButtons();
    }

    private void BtnTimebaseUp_Click(object sender, RoutedEventArgs e)
    {
        _timebaseIndex = Math.Min(MillisecondsPerDivision.Length - 1, _timebaseIndex + 1);
        UpdateTimebaseButtons();
    }

    private void UpdateTimebaseButtons()
    {
        TxtTimebase.Text = $"{MillisecondsPerDivision[_timebaseIndex]:0.##} ms/div";
        BtnTimebaseDown.IsEnabled = _timebaseIndex > 0;
        BtnTimebaseUp.IsEnabled = _timebaseIndex < MillisecondsPerDivision.Length - 1;
        _resetScopeView = true;
    }

    private void FitSineTimebase()
    {
        // About five cycles across ten divisions; the operator can still zoom manually.
        double target = 500.0 / SineFrequencyHz;
        _timebaseIndex = Enumerable.Range(0, MillisecondsPerDivision.Length)
            .MinBy(index => Math.Abs(Math.Log(MillisecondsPerDivision[index] / target)));
        UpdateTimebaseButtons();
    }

    private void BtnVoltsPerDivisionDown_Click(object sender, RoutedEventArgs e)
    {
        _voltsPerDivisionIndex = Math.Max(0, _voltsPerDivisionIndex - 1);
        UpdateVoltsPerDivisionButtons();
    }

    private void BtnVoltsPerDivisionUp_Click(object sender, RoutedEventArgs e)
    {
        _voltsPerDivisionIndex = Math.Min(VoltsPerDivision.Length - 1, _voltsPerDivisionIndex + 1);
        UpdateVoltsPerDivisionButtons();
    }

    private void UpdateVoltsPerDivisionButtons()
    {
        double value = VoltsPerDivision[_voltsPerDivisionIndex];
        TxtVoltsPerDivision.Text = value < 0.001
            ? $"{value * 1_000_000.0:0} µV/div"
            : value < 1.0
            ? $"{value * 1000.0:0} mV/div"
            : $"{value:0} V/div";
        BtnVoltsPerDivisionDown.IsEnabled = _voltsPerDivisionIndex > 0;
        BtnVoltsPerDivisionUp.IsEnabled = _voltsPerDivisionIndex < VoltsPerDivision.Length - 1;
        _resetScopeView = true;
    }

    private void BtnFitScope_Click(object sender, RoutedEventArgs e)
    {
        if (!_captureWindow.TryRead(4096, out float[] samples, out _))
        {
            SetSineActionStatus("Chưa thể phóng sóng: chưa có đủ mẫu thu mới. Kiểm tra kết nối ngõ thu.");
            return;
        }
        FitScopeToSamples(samples);
        if (_sineRunning || _lastSineQuality is { IsValid: true }) FitSineTimebase();
    }

    private void FitScopeToSamples(float[] samples)
    {
        if (samples.Any(sample => !float.IsFinite(sample))) return;
        double peak = samples.Max(sample => Math.Abs((double)sample));
        // Only the plot scale changes. Never multiply the captured samples or change gain.
        int index = Array.FindIndex(VoltsPerDivision, scale => scale * 4 >= peak);
        _voltsPerDivisionIndex = index < 0 ? VoltsPerDivision.Length - 1 : index;
        UpdateVoltsPerDivisionButtons();
    }

    private void PlotScope_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // RefreshScope redraws every 40 ms, so retain the user-selected limits instead of
        // resetting them on the following tick. Timebase controls horizontal view; wheel zooms
        // vertically only, so a weak real input can be enlarged without losing the sine cycles.
        var limits = PlotScope.Plot.Axes.GetLimits();
        double zoomFactor = e.Delta > 0 ? 0.50 : 2.0;
        double centerY = (limits.Bottom + limits.Top) / 2.0;
        double halfHeight = Math.Max(0.000001, (limits.Top - limits.Bottom) * zoomFactor / 2.0);
        PlotScope.Plot.Axes.SetLimits(limits.Left, limits.Right, centerY - halfHeight, centerY + halfHeight);
        _resetScopeView = false;
        PlotScope.Refresh();
        e.Handled = true;
    }

    private void PlotScope_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var limits = PlotScope.Plot.Axes.GetLimits();
        _isRightPanning = true;
        _rightPanStart = e.GetPosition(PlotScope);
        _rightPanStartBottom = limits.Bottom;
        _rightPanStartTop = limits.Top;
        _resetScopeView = false;
        PlotScope.CaptureMouse();
        PlotScope.Cursor = Cursors.SizeNS;
        e.Handled = true;
    }

    private void PlotScope_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRightPanning || e.RightButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(PlotScope);
        double plotHeight = Math.Max(1.0, PlotScope.ActualHeight);
        double verticalRange = _rightPanStartTop - _rightPanStartBottom;
        double offset = (current.Y - _rightPanStart.Y) * verticalRange / plotHeight;
        var limits = PlotScope.Plot.Axes.GetLimits();
        PlotScope.Plot.Axes.SetLimits(limits.Left, limits.Right, _rightPanStartBottom + offset, _rightPanStartTop + offset);
        PlotScope.Refresh();
        e.Handled = true;
    }

    private void PlotScope_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndRightPan();
        e.Handled = true;
    }

    private void PlotScope_LostMouseCapture(object sender, MouseEventArgs e) => EndRightPan();

    private void EndRightPan()
    {
        if (!_isRightPanning) return;
        _isRightPanning = false;
        PlotScope.ReleaseMouseCapture();
        PlotScope.Cursor = Cursors.Arrow;
    }

    private void RefreshScope()
    {
        if (_sineRunning && !_wavPlayer.IsPlaying)
        {
            Exception? failure = _wavPlayer.Failure;
            StopSineAfterError();
            string message = failure != null ? AudioMeasurementErrors.Describe(failure)
                : "Luồng phát đã dừng. Chọn lại ngõ phát và kiểm tra kết nối Bluetooth rồi bật sine lại.";
            SetSineActionStatus("Đã dừng phát sine: " + message);
            AppendNoiseLog("Lỗi phát sine: " + (failure?.ToString() ?? message));
        }
        const int bufferLength = 4096;
        if (!_captureWindow.TryRead(bufferLength, out float[] samples, out int sampleRate))
        {
            _lastSineQuality = null;
            TxtRms.Text = TxtVpp.Text = TxtFrequency.Text = TxtHeadroom.Text = "--";
            TxtNoiseStatus.Text = "CHỜ DỮ LIỆU THU MỚI";
            TxtNoiseStatus.Foreground = Brushes.Gold;
            BorderNoiseStatus.Background = new SolidColorBrush(Color.FromRgb(69, 45, 9));
            BorderNoiseStatus.BorderBrush = Brushes.Gold;
            PlotScope.Plot.Clear();
            PlotScope.Plot.Title("Chưa đủ mẫu mới hoặc luồng thu đã ngừng — chưa tính THD");
            PlotScope.Refresh();
            return;
        }

        double msPerDivision = MillisecondsPerDivision[_timebaseIndex];
        double totalMs = msPerDivision * 10.0;
        // Measurements use the entire fresh analysis window, independently of plot zoom/trigger.
        int displaySamples = samples.Length;
        int start = 0;

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        double sumSquares = 0.0;
        double maximumStep = 0.0;
        for (int index = start; index < bufferLength; index++)
        {
            double value = samples[index];
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sumSquares += value * value;
            if (index > start) maximumStep = Math.Max(maximumStep, Math.Abs(value - samples[index - 1]));
        }

        double rms = Math.Sqrt(sumSquares / displaySamples);
        double rmsDbFs = 20.0 * Math.Log10(Math.Max(1E-9, rms));
        double peak = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        double headroomDb = Math.Max(0.0, -20.0 * Math.Log10(Math.Max(1E-9, peak)));
        double estimatedFrequency = 0;

		if (DateTime.UtcNow - _lastSineAnalysisUtc >= TimeSpan.FromMilliseconds(250))
		{
			_lastSineAnalysisUtc = DateTime.UtcNow;
			_lastSineQuality = peak >= 0.0005 ? AdvancedAudioMeasurement.AnalyzeTone(samples, sampleRate, SineFrequencyHz, 9) : null;
		}
		bool hasUsableSine = peak >= 0.0005 && _lastSineQuality is { IsValid: true };
		if (hasUsableSine && _fitNextSineCapture)
		{
			FitScopeToSamples(samples);
			_fitNextSineCapture = false;
			SetSineActionStatus(_sineRunning ? "Đang phát WAV; đã nhận tín hiệu ở IN. Đồ thị là mẫu thu thực tế."
				: "Đang thu tín hiệu 1 kHz từ nguồn bên ngoài. App không phát WAV.");
		}
		if (hasUsableSine && _lastSineQuality is { } sineQuality)
			estimatedFrequency = sineQuality.FundamentalFrequencyHz;

        TxtVpp.Text = $"{Math.Max(0.0, maximum - minimum):F3} FS";
        TxtRms.Text = $"{rmsDbFs:F1} dBFS";
        TxtFrequency.Text = estimatedFrequency >= 10 ? $"{estimatedFrequency:F1} Hz" : "-- Hz";
        TxtHeadroom.Text = $"{headroomDb:F1} dB";

        RefreshRecordingEndpointState();

        double expectedSineStep = 2.0 * peak * Math.Sin(Math.PI * Math.Min(SineFrequencyHz, sampleRate * 0.49) / sampleRate);
        bool possibleClick = _sineRunning
            && peak > 0.001
            && maximumStep > Math.Max(0.03, expectedSineStep * 2.5);
        bool traceHasNoise = IsNoisyCapture(rmsDbFs, peak, _lastSineQuality);
        UpdateNoiseAssessment(rmsDbFs, peak, _lastSineQuality, possibleClick);

        var waveform = ScopeWaveform.Create(samples, sampleRate, totalMs, hasUsableSine);
        PlotScope.Plot.Clear();
        var trace = PlotScope.Plot.Add.Scatter(waveform.TimeMs, waveform.Samples);
        bool showFaultTrace = possibleClick || traceHasNoise;
        trace.Color = ScottPlot.Color.FromHex(showFaultTrace ? "#EF4444" : _sineRunning && !hasUsableSine ? "#FBBF24" : "#38BDF8");
        trace.LineWidth = 1.75f;
        trace.MarkerSize = 0;
        PlotScope.Plot.Title(_sineRunning || hasUsableSine
            ? $"Sine Check {SineFrequencyHz:0.#} Hz · {(hasUsableSine ? waveform.Triggered ? "đã canh pha" : "chưa canh được pha" : "chưa thu được sine hợp lệ")} · {(showFaultTrace ? "CLICK / NHIỄU" : "tín hiệu thực thu")}" 
            : $"Theo dõi nền realtime · {(showFaultTrace ? "NHIỄU CAO" : "ổn định")}");
        PlotScope.Plot.Axes.Bottom.Label.Text = "Thời gian (ms)";
        PlotScope.Plot.Axes.Left.Label.Text = "Biên độ (V/div hiển thị)";
        if (_resetScopeView)
        {
			double voltsPerDivision = VoltsPerDivision[_voltsPerDivisionIndex];
            PlotScope.Plot.Axes.SetLimits(0, totalMs, -voltsPerDivision * 5.0, voltsPerDivision * 5.0);
            _resetScopeView = false;
        }
        PlotScope.Refresh();
    }

    private bool IsNoisyCapture(double rmsDbFs, double peak, ToneQualityMetrics? sineQuality)
    {
        if (RecordingEndpointShouldBeSilent() && peak >= 0.0005) return true;
        if (!_sineRunning && sineQuality is not { IsValid: true }) return rmsDbFs >= -50.0 || peak >= 0.995;
        if (peak >= 0.995) return true;
        if (peak < 0.0005 || sineQuality == null) return false;
        return !sineQuality.IsValid
            || Math.Abs(sineQuality.FundamentalFrequencyHz - SineFrequencyHz) > Math.Max(10.0, SineFrequencyHz * 0.01)
            || !double.IsFinite(sineQuality.ThdPercent)
            || sineQuality.ThdPercent > 5.0
            || sineQuality.SnrDb < 20.0;
    }

    private void RefreshRecordingEndpointState()
    {
        if (DateTime.UtcNow - _lastRecordingEndpointReadUtc < TimeSpan.FromMilliseconds(500)) return;
        _lastRecordingEndpointReadUtc = DateTime.UtcNow;
        _recordingEndpointVolume = _audioEngine.TryGetWindowsRecordingVolume(_recording);
        _recordingEndpointMuted = _audioEngine.TryGetWindowsRecordingMute(_recording);
    }

    private bool RecordingEndpointShouldBeSilent() =>
        _recordingEndpointMuted == true || (_recordingEndpointVolume.HasValue && _recordingEndpointVolume.Value <= 0.001);

    private void UpdateNoiseAssessment(double rmsDbFs, double peak, ToneQualityMetrics? sineQuality, bool possibleClick)
    {
        string status;
        Color background;
        Color border;
        Color foreground;
        string detail;
        if (RecordingEndpointShouldBeSilent() && peak >= 0.0005)
        {
            double peakDbFs = 20.0 * Math.Log10(Math.Max(1E-9, peak));
            status = peak >= 0.995
                ? "CLIPPING DÙ WINDOWS IN ĐANG 0% / MUTE"
                : "VẪN CÓ TÍN HIỆU DÙ WINDOWS IN ĐANG 0% / MUTE";
            detail = $"Windows IN đang 0%/mute nhưng luồng thu vẫn có peak {peakDbFs:F1} dBFS. Driver hoặc gain/preamp phần cứng đang nằm trước điều khiển Windows; không dùng software gain để che lỗi. Hãy giảm núm gain/input trên sound card, giảm mức nguồn phát hoặc chọn lại đúng endpoint IN.";
            background = Color.FromRgb(69, 10, 10); border = Color.FromRgb(239, 68, 68); foreground = Color.FromRgb(254, 202, 202);
        }
        else if (_sineRunning || sineQuality is { IsValid: true })
        {
            double peakDbFs = 20.0 * Math.Log10(Math.Max(1E-9, peak));
            if (peak < 0.0005)
            {
				status = "CHƯA THU ĐƯỢC TÍN HIỆU SINE";
				detail = $"Đã mở WAV {SineFrequencyHz:0.#} Hz ở {SineLevelRmsDbFs:F1} dBFS RMS nhưng ngõ thu [{ChannelLabel(_recordingChannel)}] chỉ có peak {peakDbFs:F1} dBFS. Kiểm tra tiếng phát thực, âm lượng/mute riêng của app trong Windows, kênh OUT/IN và đường nối tới mixer. Chưa đủ tín hiệu để tính tần số hoặc THD.";
                background = Color.FromRgb(69, 45, 9); border = Color.FromRgb(245, 158, 11); foreground = Color.FromRgb(254, 243, 199);
            }
			else if (peak >= 0.995 || (sineQuality?.ClippedSamplePercent ?? 0.0) > 0.01)
			{
				status = "WARNING · SINE BỊ CLIPPING";
				detail = $"WARNING: Sine thu bị clipping (peak {peakDbFs:F1} dBFS). Dừng Sine Check → giảm Windows Master Volume hoặc gain input → bật lại; chỉ đo khi peak không chạm 0 dBFS.";
				background = Color.FromRgb(69, 10, 10); border = Color.FromRgb(239, 68, 68); foreground = Color.FromRgb(254, 202, 202);
			}
			else if (possibleClick)
			{
				status = "WARNING · PHÁT HIỆN CLICK";
				detail = $"WARNING: Dạng sóng {SineFrequencyHz:0.#} Hz có bước nhảy bất thường. Đường Scope đã chuyển đỏ; kiểm tra buffer, cáp, clock và ứng dụng âm thanh khác trước khi đo sweep.";
				background = Color.FromRgb(69, 10, 10); border = Color.FromRgb(239, 68, 68); foreground = Color.FromRgb(254, 202, 202);
			}
			else if (sineQuality == null || !sineQuality.IsValid)
			{
				status = "WARNING · KHÔNG KHÓA ĐƯỢC SINE";
				detail = $"WARNING: Có mức input nhưng dạng sóng không khóa được sine {SineFrequencyHz:0.#} Hz. Dừng WAV → kiểm tra đúng ngõ/kênh thu, tắt nguồn âm khác và kiểm tra clock/sample rate → bật lại. Chưa nên đo sweep.";
				background = Color.FromRgb(69, 45, 9); border = Color.FromRgb(245, 158, 11); foreground = Color.FromRgb(254, 243, 199);
			}
			else if (Math.Abs(sineQuality.FundamentalFrequencyHz - SineFrequencyHz) > Math.Max(10.0, SineFrequencyHz * 0.01))
			{
				status = "WARNING · SAI TẦN SỐ SINE";
				detail = $"WARNING: WAV {SineFrequencyHz:0.#} Hz nhưng input khóa tại {sineQuality.FundamentalFrequencyHz:F1} Hz. Dừng WAV → chọn đúng input/kênh, kiểm tra route và clock → bật lại. Chưa nên đo sweep.";
				background = Color.FromRgb(69, 45, 9); border = Color.FromRgb(245, 158, 11); foreground = Color.FromRgb(254, 243, 199);
			}
			else if (!double.IsFinite(sineQuality.ThdPercent) || sineQuality.ThdPercent > 5.0 || sineQuality.SnrDb < 20.0)
			{
				status = "WARNING · SINE THU KHÔNG SẠCH";
				detail = $"WARNING: Sine {SineFrequencyHz:0.#} Hz đã khóa nhưng dạng sóng có nhiễu/méo (THD {sineQuality.ThdPercent:F2}%, SNR {sineQuality.SnrDb:F1} dB). Đường Scope đã chuyển đỏ; chỉnh mức phát, gain input, dây/ground và môi trường trước khi đo.";
				background = Color.FromRgb(69, 10, 10); border = Color.FromRgb(239, 68, 68); foreground = Color.FromRgb(254, 202, 202);
			}
            else
            {
				status = "SINE CHECK ĐẠT · CÓ THỂ ĐO";
				detail = $"PASS: Sine thực thu {sineQuality.FundamentalFrequencyHz:F1} Hz, peak {peakDbFs:F1} dBFS, THD {sineQuality.ThdPercent:F3}%, SNR {sineQuality.SnrDb:F1} dB. Có thể dừng Sine Check và bắt đầu sweep.";
                background = Color.FromRgb(6, 78, 59); border = Color.FromRgb(16, 185, 129); foreground = Color.FromRgb(167, 243, 208);
            }
        }
        else if (peak >= 0.995)
        {
            status = "CLIPPING / NHIỄU QUÁ LỚN";
            detail = $"Peak {20.0 * Math.Log10(Math.Max(1E-9, peak)):F1} dBFS chạm giới hạn input.";
            background = Color.FromRgb(69, 10, 10); border = Color.FromRgb(239, 68, 68); foreground = Color.FromRgb(254, 202, 202);
        }
        else if (rmsDbFs < -70.0)
        {
            status = "NỀN YÊN · QUIET";
            detail = $"RMS {rmsDbFs:F1} dBFS, chưa thấy nền đáng kể.";
            background = Color.FromRgb(6, 78, 59); border = Color.FromRgb(16, 185, 129); foreground = Color.FromRgb(167, 243, 208);
        }
        else if (rmsDbFs < -50.0)
        {
            status = "NHIỄU THẤP · THEO DÕI";
            detail = $"RMS {rmsDbFs:F1} dBFS; nên giữ môi trường yên trước sweep.";
            background = Color.FromRgb(69, 45, 9); border = Color.FromRgb(245, 158, 11); foreground = Color.FromRgb(254, 243, 199);
        }
        else
        {
            status = "NHIỄU CAO · KHÔNG NÊN ĐO";
            detail = $"RMS {rmsDbFs:F1} dBFS; dừng nguồn ồn/rò âm trước khi chạy sweep.";
            background = Color.FromRgb(69, 10, 10); border = Color.FromRgb(239, 68, 68); foreground = Color.FromRgb(254, 202, 202);
        }

        TxtNoiseStatus.Text = status;
        TxtNoiseStatus.Foreground = new SolidColorBrush(foreground);
        BorderNoiseStatus.Background = new SolidColorBrush(background);
        BorderNoiseStatus.BorderBrush = new SolidColorBrush(border);
        if (!string.Equals(status, _lastNoiseClass, StringComparison.Ordinal) || DateTime.UtcNow - _lastNoiseLogUtc >= TimeSpan.FromSeconds(5))
        {
            _lastNoiseClass = status;
            _lastNoiseLogUtc = DateTime.UtcNow;
            AppendNoiseLog(detail);
        }
    }

    private void AppendNoiseLog(string message)
    {
        TxtNoiseLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        TxtNoiseLog.ScrollToEnd();
    }

    private void ApplyDarkPlotAppearance()
    {
        PlotScope.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        PlotScope.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#0E0E10");
        PlotScope.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#27272A");
        PlotScope.Plot.Axes.Color(ScottPlot.Color.FromHex("#A1A1AA"));
        PlotScope.Plot.Axes.Title.Label.ForeColor = ScottPlot.Color.FromHex("#F4F4F5");
    }

}
