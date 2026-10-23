using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MathNet.Numerics.IntegralTransforms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScottPlot;
using ScottPlot.TickGenerators;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

public partial class StandardMeasurementWindow : UserControl
{
    private readonly AudioEngine _audioEngine;
    private bool _isInitialized;
    private StandardAcousticResult? _lastResult;
    private bool _isBusy;
    private string _currentModel = "";
    private string _currentSerial = "";

    // RTA & Scope capture buffer & timers
    private readonly DispatcherTimer _rtaTimer;
    private readonly DispatcherTimer _scopeTimer;
    private readonly DispatcherTimer _measureMonitorTimer;
    private readonly DispatcherTimer _generatorPlotTimer;
    private readonly DispatcherTimer _windowsVolumeTimer;
    private readonly object _captureLock = new();
    private readonly float[] _captureBuffer = new float[32768];
    private int _captureWriteIndex = 0;
    private int _captureSampleRate = 48000;
    private double[]? _rtaPeakHold;
    private bool _isRtaRunning;
    private bool _isScopeRunning;
    private bool _isMeasureMonitorRunning;

    // Generator state
    private SignalType _genSignalType = SignalType.Sine;
    private double _genFrequency = 1000.0;
    private double _genLevelDb = -12.0;
    private bool _isGenPlaying;

    // Channel state (null = Both, 0 = Left / Mic 1, 1 = Right / Mic 2)
    private int? _selectedPlaybackChannel = null;
    private int? _selectedRecordingChannel = null;
    private bool _isSyncingChannels = false;

    public event EventHandler? HideRequested;
    public bool IsBusy => _isBusy;

    public StandardMeasurementWindow(AudioEngine audioEngine)
    {
        _audioEngine = audioEngine;

        // RTA timer ~25 FPS
        _rtaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _rtaTimer.Tick += RtaTimer_Tick;

        // Scope timer ~25 FPS
        _scopeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _scopeTimer.Tick += ScopeTimer_Tick;

        _measureMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _measureMonitorTimer.Tick += MeasureMonitorTimer_Tick;

        _generatorPlotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _generatorPlotTimer.Tick += GeneratorPlotTimer_Tick;

        _windowsVolumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _windowsVolumeTimer.Tick += (_, _) => SyncWindowsPlaybackVolume();

        InitializeComponent();
        _isInitialized = true;

        ReloadDevices();
        InitializePlots();
        InitHarmonicInspectFrequencyCombo();

        SetPlaybackChannel(_audioEngine.PlaybackChannel);
        SetRecordingChannel(_audioEngine.RecordingChannel);

        MeasurementTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, MeasurementTabs))
            {
                UpdateNavButtonStyles();
                if (_isMeasureMonitorRunning) StopMeasureMonitor();
            }
        };

        base.PreviewKeyDown += HandlePreviewKeyDown;
        base.Loaded += StandardMeasurementWindow_Loaded;
        base.Unloaded += StandardMeasurementWindow_Unloaded;
        UpdateNavButtonStyles();
        UpdateGenPreview();
    }

    private void StandardMeasurementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _windowsVolumeTimer.Start();
        SyncWindowsPlaybackVolume();
    }

    private void StandardMeasurementWindow_Unloaded(object sender, RoutedEventArgs e)
    {
        _windowsVolumeTimer.Stop();
        _generatorPlotTimer.Stop();
        StopLiveInstruments();
    }

    private void UpdateNavButtonStyles()
    {
        int idx = MeasurementTabs.SelectedIndex;
        BtnNavMeasure.Style = (Style)FindResource(idx == 0 ? "GreenButtonStyle" : "CompactMeasurementButtonStyle");
        BtnNavGenerator.Style = (Style)FindResource(idx == 1 ? "GreenButtonStyle" : "CompactMeasurementButtonStyle");
        BtnNavRta.Style = (Style)FindResource(idx == 2 ? "GreenButtonStyle" : "CompactMeasurementButtonStyle");
        BtnNavScope.Style = (Style)FindResource(idx == 3 ? "GreenButtonStyle" : "CompactMeasurementButtonStyle");
    }

    private void BtnNavMeasure_Click(object sender, RoutedEventArgs e) => MeasurementTabs.SelectedItem = TabItemMeasure;
    private void BtnNavGenerator_Click(object sender, RoutedEventArgs e) => MeasurementTabs.SelectedItem = TabItemGenerator;
    private void BtnNavRta_Click(object sender, RoutedEventArgs e) => MeasurementTabs.SelectedItem = TabItemRta;
    private void BtnNavScope_Click(object sender, RoutedEventArgs e) => MeasurementTabs.SelectedItem = TabItemScope;

    public void SetCurrentContext(string model, string serial)
    {
        _currentModel = model;
        _currentSerial = serial;
    }

    private Window? HostWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    private void ShowMessage(string message, string caption, MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var owner = HostWindow;
        ModernMessageBox.MessageBoxType type = icon switch
        {
            MessageBoxImage.Error => ModernMessageBox.MessageBoxType.Error,
            MessageBoxImage.Warning => ModernMessageBox.MessageBoxType.Warning,
            MessageBoxImage.Question => ModernMessageBox.MessageBoxType.Confirmation,
            _ => ModernMessageBox.MessageBoxType.Info
        };
        ModernMessageBox.Show(owner!, message, caption, type);
    }

    private bool CheckAudioEngineAvailable()
    {
        var mainWin = Application.Current.MainWindow as MainWindow;
        if (mainWin?.IsAudioRoutingBusy == true)
        {
            ShowMessage(
                "Audio Routing đang thực hiện bài đo. Vui lòng đợi bài đo kết thúc hoặc dừng bài đo trước khi dùng bộ đo REW.",
                "Thiết bị âm thanh đang bận",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    public void ReloadDevices()
    {
        if (_isBusy || (Application.Current.MainWindow as MainWindow)?.IsAudioRoutingBusy == true) return;
        StopLiveInstruments();
        var previous = ComboPlayback.Items.OfType<MMDevice>().Concat(ComboRecording.Items.OfType<MMDevice>()).ToArray();
        var discovered = new List<MMDevice>();
        try
        {
            string? playbackId = (ComboPlayback.SelectedItem as MMDevice)?.ID;
            string? recordingId = (ComboRecording.SelectedItem as MMDevice)?.ID;
            var playbacks = _audioEngine.GetPlaybackDevices() ?? new List<MMDevice>();
            discovered.AddRange(playbacks);
            var recordings = _audioEngine.GetRecordingDevices() ?? new List<MMDevice>();
            discovered.AddRange(recordings);
            ComboPlayback.ItemsSource = playbacks;
            ComboRecording.ItemsSource = recordings;
            string? defaultPlaybackId = _audioEngine.TryGetWindowsDefaultPlaybackVolume()?.DeviceId;
            ComboPlayback.SelectedItem = playbackId == null
                ? playbacks.FirstOrDefault(d => string.Equals(d.ID, defaultPlaybackId, StringComparison.OrdinalIgnoreCase)) ?? playbacks.FirstOrDefault()
                : playbacks.FirstOrDefault(d => d.ID == playbackId);
            ComboRecording.SelectedItem = recordingId == null ? recordings.FirstOrDefault() : recordings.FirstOrDefault(d => d.ID == recordingId);
			SyncWindowsPlaybackVolume();

            ReloadCalibrations();
        }
        catch
        {
        }
        finally
        {
            var retained = ComboPlayback.Items.OfType<MMDevice>().Concat(ComboRecording.Items.OfType<MMDevice>()).ToHashSet();
            foreach (var device in previous.Concat(discovered).Distinct().Where(device => !retained.Contains(device)))
                try { device.Dispose(); } catch { }
        }
    }

    public void ReleaseDeviceItems()
    {
        var devices = ComboPlayback.Items.OfType<MMDevice>().Concat(ComboRecording.Items.OfType<MMDevice>()).Distinct().ToArray();
        ComboPlayback.ItemsSource = null;
        ComboRecording.ItemsSource = null;
        foreach (var device in devices) try { device.Dispose(); } catch { }
    }

    public void ReloadCalibrations()
    {
        var calibs = new List<MicrophoneCalibration>
        {
            new MicrophoneCalibration { Name = "(Không dùng calib)" }
        };
        var scanned = MicrophoneCalibration.ScanAvailableCalibrations();
        calibs.AddRange(scanned);
        ComboMicCalib.ItemsSource = calibs;

        var defaultCalib = calibs.FirstOrDefault(c =>
            c.Name.Contains("99-00192", StringComparison.OrdinalIgnoreCase) ||
            c.FilePath.Contains("99-00192", StringComparison.OrdinalIgnoreCase))
            ?? calibs.FirstOrDefault(c => c.IsLoaded)
            ?? calibs[0];

        ComboMicCalib.SelectedItem = defaultCalib;
    }

    public void SyncDevices(MMDevice? playback, MMDevice? recording, int? playbackChannel = null, int? recordingChannel = null)
    {
        if (playback != null && ComboPlayback.ItemsSource is List<MMDevice> playbacks)
        {
            var match = playbacks.FirstOrDefault(d => d.ID == playback.ID);
            ComboPlayback.SelectedItem = match;
        }
        if (recording != null && ComboRecording.ItemsSource is List<MMDevice> recordings)
        {
            var match = recordings.FirstOrDefault(d => d.ID == recording.ID);
            ComboRecording.SelectedItem = match;
        }
        if (playbackChannel.HasValue)
            SetPlaybackChannel(playbackChannel);
        if (recordingChannel.HasValue)
            SetRecordingChannel(recordingChannel);
		SyncWindowsPlaybackVolume();
    }

    private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        ReloadDevices();
        TxtStatus.Text = "Đã cập nhật danh sách thiết bị âm thanh và file hiệu chuẩn mic.";
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        StopLiveInstruments();
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StopLiveInstruments()
    {
        if (_isGenPlaying) StopGenerator();
        if (_isRtaRunning) StopRta();
        if (_isScopeRunning) StopScope();
        if (_isMeasureMonitorRunning) StopMeasureMonitor();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        BtnStartMeasure.IsEnabled = !busy;
        BtnRefreshDevices.IsEnabled = !busy;
        if (!string.IsNullOrEmpty(message)) TxtStatus.Text = message;
    }

    private bool TryGetDevices(out MMDevice? playback, out MMDevice? recording)
    {
        _audioEngine.RecordingChannel = _selectedRecordingChannel;
        _audioEngine.PlaybackChannel = _selectedPlaybackChannel;
        playback = ComboPlayback.SelectedItem as MMDevice;
        recording = ComboRecording.SelectedItem as MMDevice;
		SyncWindowsPlaybackVolume();
        if (playback != null && recording != null) return true;
        ShowMessage("Hãy chọn đủ ngõ phát và mic/ngõ thu.", "Thiếu thiết bị", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

	private void SyncWindowsPlaybackVolume()
	{
		if (TxtWindowsPlaybackVolume == null) return;
		MMDevice? selectedDevice = ComboPlayback.SelectedItem as MMDevice;
		double? selectedVolume = _audioEngine.TryGetWindowsPlaybackVolume(selectedDevice);
		if (!selectedVolume.HasValue || !double.IsFinite(selectedVolume.Value))
		{
			TxtWindowsPlaybackVolume.Text = "Master Volume: N/A (chỉ đọc)";
			TxtWindowsPlaybackVolume.ToolTip = "Không đọc được Master Volume của ngõ phát được chọn.";
			return;
		}
		double selectedPercent = Math.Round(selectedVolume.Value * 100.0);
		TxtWindowsPlaybackVolume.Text = $"Master Volume: {selectedPercent:F0}% (chỉ đọc)";
		TxtWindowsPlaybackVolume.ToolTip = $"Âm lượng endpoint đang phát: {selectedDevice?.FriendlyName}. Cập nhật realtime 0–100%; ứng dụng không tự thay đổi.";
	}

    private async Task<bool> EnsureSweepAudioReadyAsync(MMDevice playback, MMDevice recording)
    {
        if (!_audioEngine.TryValidateAudioDevices(playback, recording, out string validationError))
        {
            ShowMessage(
                "Không tìm thấy đầy đủ thiết bị âm thanh đang hoạt động. " + validationError,
                "Thiết bị âm thanh chưa kết nối",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        int activeSamples = 0;
        double maximumPeak = 0.0;
        for (int index = 0; index < 8; index++)
        {
            double peak = _audioEngine.TryGetWindowsPlaybackPeak(playback) ?? 0.0;
            maximumPeak = Math.Max(maximumPeak, peak);
            if (peak >= 0.001) activeSamples++;
            await Task.Delay(40);
        }
        if (activeSamples >= 2)
        {
            double peakDbFs = 20.0 * Math.Log10(Math.Max(1E-9, maximumPeak));
            ShowMessage(
                $"Đang có thiết bị hoặc ứng dụng khác phát âm thanh trên ngõ '{playback.FriendlyName}' " +
                $"(đỉnh khoảng {peakDbFs:F1} dBFS). Hãy dừng âm thanh đó rồi đo Sweep lại để không làm sai đáp tuyến và Distortion.",
                "Thiết bị đang phát âm thanh",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private static string FormatAudioDeviceError(Exception ex)
    {
        if (AudioEngine.IsAudioDeviceBusy(ex))
            return "Thiết bị âm thanh đang bị ứng dụng khác chiếm dụng hoặc đang mở ở Exclusive Mode. Hãy dừng ứng dụng đang phát/thu âm thanh rồi thử lại.";
        return AudioMeasurementErrors.Describe(ex);
    }

    private static bool TryReadDouble(string text, out double value)
    {
        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ==========================================
    // 1. MEASURE (SWEEP + RT60 & REW STATISTICS)
    // ==========================================

    private async void BtnCheckLevels_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (!CheckAudioEngineAvailable()) return;
        if (!TryGetDevices(out MMDevice? playback, out MMDevice? recording)) return;

        if (_isGenPlaying) StopGenerator();
        if (_isRtaRunning) StopRta();
        if (_isScopeRunning) StopScope();

        if (!TryReadDouble(TxtSweepLevel.Text, out double levelDb) || levelDb < -60 || levelDb > 0)
            levelDb = -12.0;

        var owner = Window.GetWindow(this);
        var (startMeasure, selectedSweepLevelDb, selectedOutCh, selectedInCh) = RewCheckLevelsDialog.Show(
            owner,
            _audioEngine,
            playback!,
            recording!,
            levelDb,
            _selectedPlaybackChannel,
            _selectedRecordingChannel);

        TxtSweepLevel.Text = $"{selectedSweepLevelDb:F1}";
        if (selectedOutCh != _selectedPlaybackChannel) SetPlaybackChannel(selectedOutCh);
        if (selectedInCh != _selectedRecordingChannel) SetRecordingChannel(selectedInCh);

        if (startMeasure)
        {
            MeasurementTabs.SelectedItem = TabItemMeasure;
            await RunSweepMeasurementAsync();
        }
    }

    private async void BtnSweep_Click(object sender, RoutedEventArgs e)
    {
        await RunSweepMeasurementAsync();
    }

    private void StartMeasureMonitorIfAvailable(bool showErrors, bool resetMeter = true)
    {
        if (_isBusy || _isMeasureMonitorRunning || !_isInitialized) return;
        if (ComboRecording.SelectedItem is not MMDevice recording) return;
        try
        {
            EnsureContinuousCaptureStarted(recording);
            if (resetMeter) RewMeterMeasure.Reset();
            _isMeasureMonitorRunning = true;
            _measureMonitorTimer.Start();
        }
        catch (Exception ex)
        {
            if (showErrors)
                ShowMessage(FormatAudioDeviceError(ex), "Lỗi theo dõi tín hiệu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MeasureMonitorTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isMeasureMonitorRunning) return;
        const int sampleCount = 4096;
        var samples = new float[sampleCount];
        lock (_captureLock)
        {
            int readIndex = (_captureWriteIndex - sampleCount + _captureBuffer.Length) % _captureBuffer.Length;
            for (int index = 0; index < sampleCount; index++)
                samples[index] = _captureBuffer[(readIndex + index) % _captureBuffer.Length];
        }
        RewMeterMeasure.PushSamples(samples);
    }

    private void StopMeasureMonitor()
    {
        _isMeasureMonitorRunning = false;
        _measureMonitorTimer.Stop();
        CheckStopContinuousCapture();
    }

    private async Task RunSweepMeasurementAsync()
    {
        if (!CheckAudioEngineAvailable()) return;
        if (!TryGetDevices(out MMDevice? playback, out MMDevice? recording)) return;

        // Stop playback instruments during sweep measurement. The automatic
        // headroom capture is released only after all settings are validated.
        if (_isGenPlaying) StopGenerator();
        if (_isRtaRunning) StopRta();
        if (_isScopeRunning) StopScope();
        bool resumeMonitor = _isMeasureMonitorRunning;

        if (!TryReadDouble(TxtSweepSeconds.Text, out double duration) || duration < 3 || duration > 20)
        {
            ShowMessage("Thời gian sweep phải từ 3 đến 20 giây.", "Cấu hình chưa hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryReadDouble(TxtSweepStartHz.Text, out double startHz) || startHz < 10 || startHz > 20000)
        {
            ShowMessage("Tần số bắt đầu phải từ 10 Hz đến 20,000 Hz.", "Cấu hình chưa hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryReadDouble(TxtSweepEndHz.Text, out double endHz) || endHz <= startHz || endHz > 24000)
        {
            ShowMessage("Tần số kết thúc phải lớn hơn tần số bắt đầu và không quá 24,000 Hz.", "Cấu hình chưa hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryReadDouble(TxtSweepLevel.Text, out double levelDb) || levelDb < -60 || levelDb > 0)
        {
            ShowMessage("Mức phát sweep phải từ -60 dBFS đến 0 dBFS.", "Cấu hình chưa hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        double fdwCycles = 15;
        if (!TryReadDouble(TxtIrLeftMs.Text, out double gateLeftMs) || gateLeftMs < 0.2 || gateLeftMs > 500
            || !TryReadDouble(TxtIrRightMs.Text, out double gateRightMs) || gateRightMs < 2 || gateRightMs > 500
            || (ChkFdw.IsChecked == true && (!TryReadDouble(TxtFdwCycles.Text, out fdwCycles) || fdwCycles < 2 || fdwCycles > 100)))
        {
            ShowMessage("Trước đỉnh: 0,2–500 ms; sau đỉnh tối đa: 2–500 ms; khi bật FDW: 2–100 chu kỳ.",
                "Cấu hình impulse chưa hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        double amplitude = Math.Pow(10.0, levelDb / 20.0);
        _lastResult = null;

        var measurementTimer = Stopwatch.StartNew();
        if (_isMeasureMonitorRunning) StopMeasureMonitor();
        await Task.Delay(120);
        if (!await EnsureSweepAudioReadyAsync(playback!, recording!))
        {
            if (resumeMonitor) StartMeasureMonitorIfAvailable(showErrors: false);
            return;
        }
        SetBusy(true, $"Đang đo Sweep: phát [{playback!.FriendlyName}] ({GetPlaybackChannelDisplayName()}) → thu [{recording!.FriendlyName}] ({GetRecordingChannelDisplayName()}), {startHz:0}-{endHz:0} Hz...");
		SweepProgressBar.Value = 0;
		TxtSweepProgress.Text = "Chuẩn bị đo · 0%";

        try
        {
            int targetSampleRate = _audioEngine.PlaybackSampleRate;
            var sourceSettings = new LogSweepSettings(
                SampleRate: targetSampleRate,
                StartFrequencyHz: startHz,
                EndFrequencyHz: endHz,
                DurationSeconds: duration,
                Amplitude: amplitude,
                DecayAnalysisSeconds: 3.0,
                ImpulseWindowLeftMs: gateLeftMs,
                ImpulseWindowRightMs: gateRightMs,
                FrequencyDependentWindowCycles: fdwCycles,
                FrequencyDependentWindowing: ChkFdw.IsChecked == true);

            float[] excitation = StandardAcousticMeasurement.GenerateLogSweep(sourceSettings);
            float[] playbackSweep = TestRunner.AddLogSweepPreroll(excitation, targetSampleRate);
            TxtStatus.Text = $"Đang thu noise floor {TestRunner.LogSweepPrerollSeconds:F0}s, sau đó phát sweep...";
			double totalMeasureSeconds = playbackSweep.Length / (double)targetSampleRate + sourceSettings.DecayAnalysisSeconds;
			var sweepStopwatch = Stopwatch.StartNew();

            // Dùng đúng đường phát/thu đã được Audio Routing kiểm chứng: chờ endpoint ổn định,
            // lấy lại endpoint đang hoạt động và phát mẫu sweep trực tiếp. Khoảng pre-roll 1s
            // vừa đánh thức chuỗi phát vừa là dữ liệu noise floor trước phép đo.
            float[] recorded = await _audioEngine.PlayAndRecordAsync(
                playback!,
                recording!,
                SignalType.Sweep,
                0.0,
                playbackSweep.Length / (double)targetSampleRate + sourceSettings.DecayAnalysisSeconds,
                realTimeRecordedCallback: samples =>
                {
					double progress = Math.Clamp(sweepStopwatch.Elapsed.TotalSeconds / totalMeasureSeconds * 100.0, 0.0, 99.0);
					Dispatcher.InvokeAsync(() =>
					{
						RewMeterMeasure?.PushSamples(samples);
						SweepProgressBar.Value = progress;
						TxtSweepProgress.Text = $"Đang đo sweep · {progress:F0}%";
					}, DispatcherPriority.Render);
                },
                customSweepSamples: playbackSweep);
			sweepStopwatch.Stop();
			double captureSeconds = sweepStopwatch.Elapsed.TotalSeconds;
			SweepProgressBar.Value = 100;
			TxtSweepProgress.Text = "Hoàn tất thu · 100%";

            int recordingSampleRate = _audioEngine.RecordingSampleRate;
            recorded = StandardAcousticMeasurement.ApplySubsonicConditioning(
                recorded, recordingSampleRate, 10.0);

            float[] analysisExcitation = recordingSampleRate == targetSampleRate
                ? excitation
                : StandardAcousticMeasurement.GenerateLogSweep(sourceSettings with { SampleRate = recordingSampleRate });

            var analysisSettings = sourceSettings with
            {
                SampleRate = recordingSampleRate,
                RecordingGain = 1.0
            };

            var selectedCalib = ComboMicCalib.SelectedItem as MicrophoneCalibration;
            MicrophoneCalibration? activeCalib = selectedCalib?.IsLoaded == true ? selectedCalib : null;

            int noiseCount = Math.Min(recorded.Length,
                (int)Math.Round(TestRunner.LogSweepPrerollSeconds * recordingSampleRate));
            int noiseStart = Math.Min(noiseCount / 4, recordingSampleRate / 10);
            if (noiseCount - noiseStart < 32) noiseStart = 0;
            double noisePower = 0.0;
            for (int index = noiseStart; index < noiseCount; index++)
                noisePower += (double)recorded[index] * recorded[index];
            double preSweepNoiseFloorDbFs = noiseCount > noiseStart
                ? 10.0 * Math.Log10(noisePower / (noiseCount - noiseStart) + 1E-18)
                : double.NaN;

            TxtStatus.Text = "Đã thu xong, đang phân tích sweep...";
            TxtSweepProgress.Text = "Đang phân tích...";
            var analysisTimer = Stopwatch.StartNew();
            var stageTimings = new List<string>();
            _lastResult = await Task.Run(() => StandardAcousticMeasurement.AnalyzeLogSweep(
                analysisExcitation, recorded, analysisSettings, activeCalib, preSweepNoiseFloorDbFs,
                (stage, ms) => stageTimings.Add($"{stage}={ms:F0}ms")));
            analysisTimer.Stop();

            RenderSweepResult(_lastResult);
            TxtSummary.Text += $"\nThời gian: phát/thu {captureSeconds:F2}s; phân tích {analysisTimer.Elapsed.TotalSeconds:F2}s; tổng đến hiển thị {measurementTimer.Elapsed.TotalSeconds:F2}s.";
            Debug.WriteLine("Sweep performance: " + string.Join(", ", stageTimings));
            TxtSweepProgress.Text = $"Hoàn tất · {measurementTimer.Elapsed.TotalSeconds:F1}s";
            TxtStatus.Text = _lastResult.Validity == "INVALID"
                ? "CHƯA ĐO ĐƯỢC: " + (_lastResult.Diagnostics.FirstOrDefault(item => item.Severity == "INVALID")?.Message ?? _lastResult.Warning)
                : "ĐO XONG HỢP LỆ (CHUẨN REW): Đã cập nhật đáp tuyến, méo hài THD, RT60 và xung.";

            // REW Level & Clipping evaluation
            double recPeak = recorded.Length > 0 ? recorded.Max(x => Math.Abs((double)x)) : 0.0;
            double recPeakDbFs = 20.0 * Math.Log10(Math.Max(1e-9, recPeak));
            int clippedSamples = recorded.Count(x => Math.Abs(x) >= 0.995f);
            double rmsDbFs = _lastResult.SignalLevelDbFs;
            double snrDb = _lastResult.EstimatedSnrDb;

            var distStats = _lastResult.RewDistortion 
                ?? (_lastResult.SweepHarmonics != null && _lastResult.SweepHarmonics.Count > 0 
                    ? StandardAcousticMeasurement.CalculateRewDistortionStats(_lastResult.SweepHarmonics) 
                    : null);
            double avgThd = distStats?.AverageThdPercent ?? double.NaN;
            double maxThd = distStats?.MaxThdPercent ?? double.NaN;

            bool isClipping = _lastResult.IsClipped 
                           || recPeak >= 0.995 
                           || recPeakDbFs >= -0.1
                           || clippedSamples > 0 
                           || _lastResult.Diagnostics.Any(d => d.Code == "INPUT_CLIPPING" || d.Code == "POST_GAIN_OVERLOAD");

            bool isTooWeak = (_lastResult.Validity == "INVALID" && recPeakDbFs < -35.0)
                          || recPeakDbFs < -42.0
                          || _lastResult.Diagnostics.Any(d => d.Code == "NO_AUDIO_CAPTURED" || d.Code == "INVALID_LOW_SIGNAL");

            bool isHighDistortion = !isClipping && (
                (double.IsFinite(avgThd) && avgThd > 10.0) ||
                (double.IsFinite(maxThd) && maxThd > 25.0)
            );

            bool isLowDistortion = !isClipping && !isTooWeak && (
                _lastResult.SweepHarmonics == null ||
                _lastResult.SweepHarmonics.Count == 0 ||
                (double.IsFinite(avgThd) && avgThd < 0.015) ||
                _lastResult.Diagnostics.Any(d => d.Code == "SWEEP_DISTORTION_UNRELIABLE")
            );

            bool isLowSnr = !isClipping && !isTooWeak && (
                (double.IsFinite(snrDb) && snrDb < 15.0) ||
                _lastResult.Diagnostics.Any(d => d.Code == "LOW_CAPTURE_SNR")
            );

            bool isHighSnrLoopback = !isClipping && !isTooWeak && double.IsFinite(snrDb) && snrDb > 72.0 && _lastResult.NoiseFloorDbFs < -90.0;

            RewMeterMeasure?.DisplayCapturedResult(recorded, recPeakDbFs, rmsDbFs, snrDb, isClipping);

            if (isClipping)
            {
                TxtStatus.Text = $"⛔ CẢNH BÁO REW: Tín hiệu bị Clipping! Đỉnh: {recPeakDbFs:F1} dBFS ({clippedSamples} mẫu chạm đỉnh).";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Crimson;
                var owner = Window.GetWindow(this);
                bool measureAgain = RewLevelWarningDialog.Show(
                    owner, 
                    RewLevelWarningType.Clipping, 
                    recPeakDbFs, 
                    recPeak, 
                    rmsDbFs, 
                    snrDb, 
                    clippedSamples,
                    avgThd);

                if (measureAgain)
                {
                    _ = Dispatcher.InvokeAsync(RunSweepMeasurementAsync);
                    return;
                }
            }
            else if (isTooWeak)
            {
                TxtStatus.Text = $"⚠ CẢNH BÁO REW: Mức tín hiệu quá yếu! Đỉnh: {recPeakDbFs:F1} dBFS (RMS: {rmsDbFs:F1} dBFS).";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
                var owner = Window.GetWindow(this);
                bool measureAgain = RewLevelWarningDialog.Show(
                    owner, 
                    RewLevelWarningType.TooWeak, 
                    recPeakDbFs, 
                    recPeak, 
                    rmsDbFs, 
                    snrDb, 
                    0,
                    avgThd);

                if (measureAgain)
                {
                    _ = Dispatcher.InvokeAsync(RunSweepMeasurementAsync);
                    return;
                }
            }
            else if (isHighDistortion)
            {
                TxtStatus.Text = $"⛔ CẢNH BÁO REW: Méo hài THD quá cao ({avgThd:F2}% trung bình, cực đại {maxThd:F2}%)! Nghi vấn rè / chạm côn / clip.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Crimson;
                var owner = Window.GetWindow(this);
                bool measureAgain = RewLevelWarningDialog.Show(
                    owner, 
                    RewLevelWarningType.HighDistortion, 
                    recPeakDbFs, 
                    recPeak, 
                    rmsDbFs, 
                    snrDb, 
                    clippedSamples,
                    avgThd);

                if (measureAgain)
                {
                    _ = Dispatcher.InvokeAsync(RunSweepMeasurementAsync);
                    return;
                }
            }
            else if (isLowSnr)
            {
                TxtStatus.Text = $"⚠ CẢNH BÁO REW: Tỷ số SNR quá thấp ({snrDb:F1} dB < 15 dB)! Nhiễu môi trường buồng đo làm sai lệch dải trầm & RT60.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
                var owner = Window.GetWindow(this);
                bool measureAgain = RewLevelWarningDialog.Show(
                    owner, 
                    RewLevelWarningType.LowSnr, 
                    recPeakDbFs, 
                    recPeak, 
                    rmsDbFs, 
                    snrDb, 
                    0,
                    avgThd);

                if (measureAgain)
                {
                    _ = Dispatcher.InvokeAsync(RunSweepMeasurementAsync);
                    return;
                }
            }
            else if (isLowDistortion)
            {
                TxtStatus.Text = $"⚠ CẢNH BÁO REW: Méo hài THD bất thường quá thấp ({(double.IsFinite(avgThd) ? $"{avgThd:F3}%" : "0%")})! Nghi vấn lỗi tách Farina hoặc loopback.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Goldenrod;
                var owner = Window.GetWindow(this);
                bool measureAgain = RewLevelWarningDialog.Show(
                    owner, 
                    RewLevelWarningType.LowDistortion, 
                    recPeakDbFs, 
                    recPeak, 
                    rmsDbFs, 
                    snrDb, 
                    0,
                    avgThd);

                if (measureAgain)
                {
                    _ = Dispatcher.InvokeAsync(RunSweepMeasurementAsync);
                    return;
                }
            }
            else if (isHighSnrLoopback)
            {
                TxtStatus.Text = $"ℹ CẢNH BÁO REW: SNR cao bất thường ({snrDb:F1} dB > 72 dB)! Nghi vấn loopback cáp trực tiếp hoặc Stereo Mix ảo.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.DeepSkyBlue;
                var owner = Window.GetWindow(this);
                bool measureAgain = RewLevelWarningDialog.Show(
                    owner, 
                    RewLevelWarningType.HighSnrLoopback, 
                    recPeakDbFs, 
                    recPeak, 
                    rmsDbFs, 
                    snrDb, 
                    0,
                    avgThd);

                if (measureAgain)
                {
                    _ = Dispatcher.InvokeAsync(RunSweepMeasurementAsync);
                    return;
                }
            }
            else if (_lastResult.Validity != "INVALID")
            {
                TxtStatus.Foreground = System.Windows.Media.Brushes.MediumSpringGreen;
            }
        }
        catch (Exception ex)
        {
			TxtSweepProgress.Text = "Đo lỗi · 0%";
			SweepProgressBar.Value = 0;
            TxtStatus.Text = "Không thể hoàn thành phép đo sweep.";
            ShowMessage(FormatAudioDeviceError(ex), "Lỗi đo Sweep REW", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            if (resumeMonitor)
                StartMeasureMonitorIfAvailable(showErrors: false, resetMeter: false);
        }
    }

    private void RenderSweepResult(StandardAcousticResult result)
    {
        // 1. SPL & Phase
        PlotResponse.Plot.Clear();
        double[] xFreq = result.FrequencyResponse.Select(p => Math.Log10(p.FrequencyHz)).ToArray();
        double[] ySpl = result.FrequencyResponse.Select(p => p.LevelDb + result.ExcitationRmsDbFs).ToArray();
        double[] yPhase = result.FrequencyResponse.Select(p => p.PhaseDegrees).ToArray();

        if (xFreq.Length > 0)
        {
            var splLine = PlotResponse.Plot.Add.Scatter(xFreq, ySpl);
            splLine.Color = ScottPlot.Color.FromHex("#10B981");
            splLine.LineWidth = 2.5f;
            splLine.MarkerSize = 0;
            splLine.LegendText = "Mức thu (dBFS)";

            double maxSpl = ySpl.Max();
            double minSpl = ySpl.Min();
            double avgSpl = ySpl.Average();
            double flatness = (maxSpl - minSpl) / 2.0;

            // Bandwidth -3dB
            double refLevel = avgSpl;
            double bwLow = result.FrequencyResponse.FirstOrDefault(p => p.LevelDb + result.ExcitationRmsDbFs >= refLevel - 3.0)?.FrequencyHz ?? 20;
            double bwHigh = result.FrequencyResponse.LastOrDefault(p => p.LevelDb + result.ExcitationRmsDbFs >= refLevel - 3.0)?.FrequencyHz ?? 20000;

            string snrTag = "";
            if (result.IsClipped)
            {
                snrTag = " | ⛔ CLIPPING (QUÁ TẢI ĐẦU VÀO)";
            }
            else if (double.IsFinite(result.EstimatedSnrDb) && result.EstimatedSnrDb < 15.0)
            {
                snrTag = $" | ⚠ SNR THẤP ({result.EstimatedSnrDb:F1} dB - NHIỄU PHÒNG LỚN)";
            }
            else if (double.IsFinite(result.EstimatedSnrDb) && result.EstimatedSnrDb > 72.0 && result.NoiseFloorDbFs < -90.0)
            {
                snrTag = $" | ℹ SNR BẤT THƯỜNG CAO ({result.EstimatedSnrDb:F1} dB - NGHI VẤN LOOPBACK)";
            }

            TxtSplHud.Text = $"Đỉnh: {maxSpl:F1} dB | Đáy: {minSpl:F1} dB | Trung bình: {avgSpl:F1} dB | Độ phẳng: ±{flatness:F1} dB | Dải thông (-3dB): {bwLow:0} Hz - {bwHigh:0} Hz{snrTag}";
        }
        PlotResponse.Plot.Title("Đáp tuyến mức thu — không chuẩn hóa");
        PlotResponse.Plot.Axes.Bottom.Label.Text = "Tần số Frequency (Hz)";
        PlotResponse.Plot.Axes.Left.Label.Text = "Mức thu RMS (dBFS)";
        ApplyLogTicks(PlotResponse.Plot);
        PlotResponse.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), ySpl.DefaultIfEmpty(-30).Min() - 5, ySpl.DefaultIfEmpty(10).Max() + 5);
        PlotResponse.Refresh();

        // 2. Distortion (Fundamental + THD + H2..H5)
        RenderHarmonicsPlotAndMetrics(result, _selectedInspectFrequencyHz);

        // 3. RT60 Decay
        PlotDecay.Plot.Clear();
        double[] decayX = result.EnergyDecayCurve.Select(p => p.TimeSeconds).ToArray();
        double[] decayY = result.EnergyDecayCurve.Select(p => p.LevelDb).ToArray();
        if (decayX.Length > 0)
        {
            var decayLine = PlotDecay.Plot.Add.Scatter(decayX, decayY);
            decayLine.Color = ScottPlot.Color.FromHex("#3B82F6");
            decayLine.LineWidth = 2.5f;
            decayLine.MarkerSize = 0;
            decayLine.LegendText = "Energy Decay Curve (EDC)";
        }
        PlotDecay.Plot.Title("Đường suy giảm năng lượng phòng Energy Decay Curve (EDC)");
        PlotDecay.Plot.Axes.Bottom.Label.Text = "Thời gian Time (s)";
        PlotDecay.Plot.Axes.Left.Label.Text = "Mức suy giảm Decay (dB)";
        PlotDecay.Plot.Axes.SetLimits(0, Math.Min(3.0, decayX.DefaultIfEmpty(1).Max()), -70, 5);
        PlotDecay.Refresh();

        // RT60 Table
        var edtEst = result.DecayEstimates.FirstOrDefault(e => e.Name == "EDT");
        var t20Est = result.DecayEstimates.FirstOrDefault(e => e.Name == "T20");
        var t30Est = result.DecayEstimates.FirstOrDefault(e => e.Name == "T30");
        string edtStr = edtEst?.IsValid == true ? $"{edtEst.Rt60Seconds:F3} s" : "N/A";
        string t20Str = t20Est?.IsValid == true ? $"{t20Est.Rt60Seconds:F3} s" : "N/A";
        string t30Str = t30Est?.IsValid == true ? $"{t30Est.Rt60Seconds:F3} s" : "N/A";
        TxtRt60Summary.Text = $"Tổng thể toàn dải: EDT = {edtStr} | T20 = {t20Str} | T30 = {t30Str}";

        var sbRt = new StringBuilder();
        sbRt.AppendLine(string.Format("{0,-14} | {1,-12} | {2,-12} | {3,-12} | {4,-12}",
            "Dải tần (Hz)", "EDT (s)", "T20 (s)", "T30 (s)", "Độ tin cậy R²"));
        sbRt.AppendLine(new string('-', 68));
        foreach (var band in result.OctaveBandRt60)
        {
            string bEdt = double.IsFinite(band.EdtSeconds) && band.EdtSeconds > 0 ? $"{band.EdtSeconds:F3}" : "--";
            string bT20 = double.IsFinite(band.T20Seconds) && band.T20Seconds > 0 ? $"{band.T20Seconds:F3}" : "--";
            string bT30 = double.IsFinite(band.T30Seconds) && band.T30Seconds > 0 ? $"{band.T30Seconds:F3}" : "--";
            string bR2 = double.IsFinite(band.RSquared) ? $"{band.RSquared:F3}" : "--";
            sbRt.AppendLine(string.Format("{0,-14:0} | {1,-12} | {2,-12} | {3,-12} | {4,-12}",
                band.FrequencyHz, bEdt, bT20, bT30, bR2));
        }
        TxtRt60Table.Text = sbRt.ToString();

        // 4. Impulse Response
        PlotImpulse.Plot.Clear();
        double[] irTimeMs = result.ImpulseResponse.Select(p => p.TimeSeconds * 1000.0).ToArray();
        double[] irVal = result.ImpulseResponse.Select(p => p.Value).ToArray();
        if (irTimeMs.Length > 0)
        {
            var irLine = PlotImpulse.Plot.Add.Scatter(irTimeMs, irVal);
            irLine.Color = ScottPlot.Color.FromHex("#F59E0B");
            irLine.LineWidth = 1.5f;
            irLine.MarkerSize = 0;
            PlotImpulse.Plot.Axes.SetLimits(irTimeMs.Min(), Math.Min(100.0, irTimeMs.Max()), -1.05, 1.05);
        }
        PlotImpulse.Plot.Title("Đáp ứng xung thời gian thực (Impulse Response)");
        PlotImpulse.Plot.Axes.Bottom.Label.Text = "Thời gian Time (ms)";
        PlotImpulse.Plot.Axes.Left.Label.Text = "Biên độ Amplitude (Linear)";
        PlotImpulse.Refresh();

        TxtImpulseHud.Text = $"Thời gian đỉnh: {result.DirectArrivalMs:F2} ms | Peak: {result.PeakSample:F3} | Cực tính: {result.Polarity} | SNR: {result.EstimatedSnrDb:F1} dB | C50: {result.ClarityC50Db:F1} dB | C80: {result.ClarityC80Db:F1} dB | D50: {result.DefinitionD50Percent:F1}%";

        // 5. Comprehensive Summary
        var sbSummary = new StringBuilder();
        sbSummary.AppendLine("================================================================================");
        sbSummary.AppendLine("                 BÁO CÁO THỐNG KÊ KẾT QUẢ ĐO SWEEP (CHUẨN REW)                  ");
        sbSummary.AppendLine("================================================================================");
        sbSummary.AppendLine($"Thời gian đo       : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sbSummary.AppendLine($"Loa / Model        : {_currentModel}");
        sbSummary.AppendLine($"Mã Serial          : {_currentSerial}");
        sbSummary.AppendLine($"Kênh phát (Out)    : {GetPlaybackChannelDisplayName()}");
        sbSummary.AppendLine($"Kênh thu (Mic)     : {GetRecordingChannelDisplayName()}");
        sbSummary.AppendLine($"Trạng thái đo      : {result.Validity}");
        sbSummary.AppendLine($"Sample Rate        : {result.SampleRate} Hz");
        sbSummary.AppendLine($"Mức tín hiệu       : {result.SignalLevelDbFs:F1} dBFS | Mức ồn nền: {result.NoiseFloorDbFs:F1} dBFS");
        sbSummary.AppendLine($"Tỷ số SNR ước tính : {result.EstimatedSnrDb:F1} dB | Peak: {result.PeakSample:F4} (Clipped: {result.IsClipped})");
        sbSummary.AppendLine($"Đường truyền âm    : Trễ {result.DirectArrivalMs:F2} ms ({result.DirectArrivalSample} mẫu) | Cực tính: {result.Polarity}");
        sbSummary.AppendLine();
        sbSummary.AppendLine("--- 1. ĐÁP TUYẾN TẦN SỐ (SPL) ---");
        if (ySpl.Length > 0)
        {
            sbSummary.AppendLine($"Mức cực đại (Peak) : {ySpl.Max():F2} dBr");
            sbSummary.AppendLine($"Mức cực tiểu (Min) : {ySpl.Min():F2} dBr");
            sbSummary.AppendLine($"Mức trung bình     : {ySpl.Average():F2} dBr");
            sbSummary.AppendLine($"Độ phẳng (Flatness): ±{(ySpl.Max() - ySpl.Min()) / 2.0:F2} dB");
        }
        sbSummary.AppendLine();
        sbSummary.AppendLine("--- 2. ĐỘ MÉO HÀI THD & CÁC BẬC HÀI (FARINA METHOD) ---");
        var ds = result.RewDistortion ?? StandardAcousticMeasurement.CalculateRewDistortionStats(result.SweepHarmonics ?? Array.Empty<SweepHarmonicPoint>());
        sbSummary.AppendLine($"THD Trung bình     : {ds.AverageThdPercent:F3}%");
        sbSummary.AppendLine($"THD Cực đại        : {ds.MaxThdPercent:F3}% tại {ds.MaxThdFrequencyHz:F0} Hz");
        sbSummary.AppendLine($"Hài chiếm ưu thế   : {ds.DominantHarmonic}");
        sbSummary.AppendLine($"Mức hài trung bình : H2 = {ds.AverageH2Dbc:F1} dBc | H3 = {ds.AverageH3Dbc:F1} dBc | H4 = {ds.AverageH4Dbc:F1} dBc | H5 = {ds.AverageH5Dbc:F1} dBc");
        sbSummary.AppendLine();
        sbSummary.AppendLine("Bảng giá trị méo hài tại các tần số chuẩn:");
        foreach (var pt in ds.TablePoints)
        {
            double thdDbr = pt.ThdPercent > 0 ? 20.0 * Math.Log10(pt.ThdPercent / 100.0) : -120.0;
            sbSummary.AppendLine($"  • {pt.FrequencyHz,5:0} Hz : THD = {pt.ThdPercent,6:F3}% ({thdDbr,6:F1} dBr) | H2 = {pt.H2Dbc,6:F1} dBc | H3 = {pt.H3Dbc,6:F1} dBc | H4 = {pt.H4Dbc,6:F1} dBc | H5 = {pt.H5Dbc,6:F1} dBc");
        }
        sbSummary.AppendLine();
        sbSummary.AppendLine("--- 3. THỜI GIAN SUY GIẢM RT60 & ÂM HỌC PHÒNG ---");
        foreach (var decay in result.DecayEstimates)
        {
            string val = decay.IsValid ? $"{decay.Rt60Seconds:F3} s (R²={decay.RSquared:F3})" : "Không hợp lệ / Dynamic range chưa đủ";
            sbSummary.AppendLine($"  • {decay.Name,-6} : {val}");
        }
        sbSummary.AppendLine($"Độ rõ âm C50 (50ms): {result.ClarityC50Db:F1} dB | C80 (80ms): {result.ClarityC80Db:F1} dB | D50: {result.DefinitionD50Percent:F1}%");
        sbSummary.AppendLine();
        sbSummary.AppendLine("--- 4. CHẨN ĐOÁN & CẢNH BÁO ---");
        if (result.Diagnostics.Count > 0)
        {
            foreach (var diag in result.Diagnostics)
            {
                sbSummary.AppendLine($"[{diag.Severity}] {diag.Code}: {diag.Message} -> {diag.Action}");
            }
        }
        else
        {
            sbSummary.AppendLine("Không có cảnh báo. Phép đo hoàn toàn hợp lệ.");
        }
        sbSummary.AppendLine("================================================================================");
        TxtSummary.Text = sbSummary.ToString();
    }

    private double _selectedInspectFrequencyHz = 1000.0;

    private void InitHarmonicInspectFrequencyCombo()
    {
        if (ComboHarmonicInspectFreq == null) return;
        ComboHarmonicInspectFreq.Items.Clear();
        string[] presets = new[]
        {
            "1000 Hz (Mặc định)",
            "80 Hz",
            "100 Hz",
            "200 Hz",
            "250 Hz",
            "500 Hz",
            "1000 Hz",
            "2000 Hz",
            "3000 Hz",
            "4000 Hz",
            "5000 Hz",
            "6300 Hz",
            "8000 Hz",
            "10000 Hz"
        };
        foreach (string p in presets)
        {
            ComboHarmonicInspectFreq.Items.Add(p);
        }
        ComboHarmonicInspectFreq.SelectedIndex = 0;

        ComboHarmonicInspectFreq.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                double f = ParseFrequencySafe(ComboHarmonicInspectFreq.Text);
                if (f > 0)
                {
                    _selectedInspectFrequencyHz = f;
                    if (_lastResult != null)
                    {
                        RenderHarmonicsPlotAndMetrics(_lastResult, _selectedInspectFrequencyHz);
                    }
                }
            }
        };
    }

    private void ComboHarmonicInspectFreq_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboHarmonicInspectFreq == null) return;
        string text = (ComboHarmonicInspectFreq.SelectedItem as ComboBoxItem)?.Content?.ToString()
                      ?? ComboHarmonicInspectFreq.SelectedItem?.ToString()
                      ?? ComboHarmonicInspectFreq.Text
                      ?? "";
        double freq = ParseFrequencySafe(text);
        if (freq > 0)
        {
            _selectedInspectFrequencyHz = freq;
            if (_lastResult != null)
            {
                RenderHarmonicsPlotAndMetrics(_lastResult, _selectedInspectFrequencyHz);
            }
        }
    }

    private static double ParseFrequencySafe(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1000.0;
        string cleaned = text.Replace("Hz", "").Replace("(Mặc định)", "").Replace("k", "000").Replace("K", "000").Trim();
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) && val > 0)
        {
            return val;
        }
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out val) && val > 0)
        {
            return val;
        }
        return 1000.0;
    }

    private void RenderHarmonicsPlotAndMetrics(StandardAcousticResult result, double inspectFreq)
    {
        PlotHarmonics.Plot.Clear();
        var harmonics = result.SweepHarmonics;
        if (harmonics != null && harmonics.Count > 0)
        {
            double[] hFreq = harmonics.Select(p => Math.Log10(p.FundamentalFrequencyHz)).ToArray();
            double[] thdPercent = harmonics.Select(p => p.ThdPercent).ToArray();
            double[] h2 = harmonics.Select(p => p.H2Dbc).ToArray();
            double[] h3 = harmonics.Select(p => p.H3Dbc).ToArray();
            double[] h4 = harmonics.Select(p => p.H4Dbc).ToArray();
            double[] h5 = harmonics.Select(p => p.H5Dbc).ToArray();

            AddScatterTrace(PlotHarmonics, hFreq, harmonics.Select(p => p.ThdPercent > 0 ? 20.0 * Math.Log10(p.ThdPercent / 100.0) : -120.0).ToArray(), "THD", "#10B981", 2.5f);
            AddScatterTrace(PlotHarmonics, hFreq, h2, "H2 (2nd)", "#F59E0B", 1.8f);
            AddScatterTrace(PlotHarmonics, hFreq, h3, "H3 (3rd)", "#EF4444", 1.8f);
            AddScatterTrace(PlotHarmonics, hFreq, h4, "H4 (4th)", "#60A5FA", 1.5f);
            AddScatterTrace(PlotHarmonics, hFreq, h5, "H5 (5th)", "#A78BFA", 1.5f);

            var distStats = result.RewDistortion ?? StandardAcousticMeasurement.CalculateRewDistortionStats(harmonics);
            string distWarningTag = "";
            if (distStats.AverageThdPercent > 10.0 || distStats.MaxThdPercent > 25.0)
            {
                distWarningTag = " ⛔ [CẢNH BÁO: THD QUÁ CAO - NGUY CƠ RÈ / CHẠM CÔN / CLIP]";
                TxtDistortionSummary.Foreground = System.Windows.Media.Brushes.Crimson;
            }
            else if (distStats.AverageThdPercent < 0.015)
            {
                distWarningTag = " ⚠ [CẢNH BÁO: THD BẤT THƯỜNG QUÁ THẤP - KIỂM TRA GATE / FARINA]";
                TxtDistortionSummary.Foreground = System.Windows.Media.Brushes.Goldenrod;
            }
            else
            {
                TxtDistortionSummary.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush") ?? System.Windows.Media.Brushes.White;
            }

            TxtDistortionSummary.Text = $"THD Trung bình: {distStats.AverageThdPercent:F2}% | THD Cực đại: {distStats.MaxThdPercent:F2}% tại {distStats.MaxThdFrequencyHz:0} Hz | Hài chủ đạo: {distStats.DominantHarmonic}{distWarningTag}";

            var sbDist = new StringBuilder();
            sbDist.AppendLine(string.Format("{0,-12} | {1,-10} | {2,-10} | {3,-10} | {4,-10} | {5,-10} | {6,-10}",
                "Tần số (Hz)", "THD (%)", "THD (dBr)", "H2 (dBc)", "H3 (dBc)", "H4 (dBc)", "H5 (dBc)"));
            sbDist.AppendLine(new string('-', 82));
            foreach (var pt in distStats.TablePoints)
            {
                double thdDbr = pt.ThdPercent > 0 ? 20.0 * Math.Log10(pt.ThdPercent / 100.0) : -120.0;
                sbDist.AppendLine(string.Format("{0,-12:0} | {1,-10:F3} | {2,-10:F2} | {3,-10:F1} | {4,-10:F1} | {5,-10:F1} | {6,-10:F1}",
                    pt.FrequencyHz, pt.ThdPercent, thdDbr, pt.H2Dbc, pt.H3Dbc, pt.H4Dbc, pt.H5Dbc));
            }
            TxtDistortionTable.Text = sbDist.ToString();

            // Selected frequency detailed inspection
            var closest = harmonics
                .Where(p => p.FundamentalFrequencyHz > 0)
                .OrderBy(p => Math.Abs(Math.Log(p.FundamentalFrequencyHz / inspectFreq)))
                .FirstOrDefault();

            if (closest != null)
            {
                double closestThdDbr = closest.ThdPercent > 0 ? 20.0 * Math.Log10(closest.ThdPercent / 100.0) : -120.0;
                var vLine = PlotHarmonics.Plot.Add.VerticalLine(Math.Log10(closest.FundamentalFrequencyHz));
                vLine.Color = ScottPlot.Color.FromHex("#EC4899");
                vLine.LineStyle.Width = 1.8f;
                vLine.LineStyle.Pattern = LinePattern.Dashed;
                vLine.LegendText = $"Kiểm tra ({closest.FundamentalFrequencyHz:0} Hz)";

                var ptMarker = PlotHarmonics.Plot.Add.Marker(Math.Log10(closest.FundamentalFrequencyHz), closestThdDbr);
                ptMarker.Color = ScottPlot.Color.FromHex("#EC4899");
                ptMarker.Size = 8f;

                var sbSel = new StringBuilder();
                sbSel.AppendLine($"TẦN SỐ KIỂM TRA: {closest.FundamentalFrequencyHz:F1} Hz (Yêu cầu: {inspectFreq:F0} Hz)");
                if (double.IsFinite(closest.FundamentalLevelDb))
                {
                    sbSel.AppendLine($"Biên độ Fundamental : {closest.FundamentalLevelDb:F1} dB");
                }
                sbSel.AppendLine($"Tổng méo THD        : {closest.ThdPercent:F3}% ({closestThdDbr:F1} dBr)");
                sbSel.AppendLine("----------------------------------------------");
                sbSel.AppendLine($"Hài bậc 2 (H2 - 2nd): {FormatHarmonicStr(closest.H2Dbc)}");
                sbSel.AppendLine($"Hài bậc 3 (H3 - 3rd): {FormatHarmonicStr(closest.H3Dbc)}");
                sbSel.AppendLine($"Hài bậc 4 (H4 - 4th): {FormatHarmonicStr(closest.H4Dbc)}");
                sbSel.AppendLine($"Hài bậc 5 (H5 - 5th): {FormatHarmonicStr(closest.H5Dbc)}");
                if (double.IsFinite(closest.H6Dbc)) sbSel.AppendLine($"Hài bậc 6 (H6 - 6th): {FormatHarmonicStr(closest.H6Dbc)}");
                if (double.IsFinite(closest.H7Dbc)) sbSel.AppendLine($"Hài bậc 7 (H7 - 7th): {FormatHarmonicStr(closest.H7Dbc)}");
                if (double.IsFinite(closest.H8Dbc)) sbSel.AppendLine($"Hài bậc 8 (H8 - 8th): {FormatHarmonicStr(closest.H8Dbc)}");
                if (double.IsFinite(closest.H9Dbc)) sbSel.AppendLine($"Hài bậc 9 (H9 - 9th): {FormatHarmonicStr(closest.H9Dbc)}");
                sbSel.AppendLine("----------------------------------------------");

                int domOrder = 2;
                double maxHarm = closest.H2Dbc;
                for (int o = 3; o <= 9; o++)
                {
                    double val = closest.GetHarmonicDbc(o);
                    if (double.IsFinite(val) && val > maxHarm)
                    {
                        maxHarm = val;
                        domOrder = o;
                    }
                }
                double maxHarmPct = double.IsFinite(maxHarm) ? 100.0 * Math.Pow(10.0, maxHarm / 20.0) : 0.0;
                sbSel.AppendLine($"Hài trội (Dominant) : H{domOrder} ({maxHarmPct:F3}% / {maxHarm:F1} dBc)");

                double limitPercent = 0.5;
                bool pass = closest.ThdPercent <= limitPercent;
                sbSel.AppendLine($"Đánh giá (ngưỡng {limitPercent:F1}%): {(pass ? "✔ ĐẠT (PASS)" : "⛔ VƯỢT (FAIL)")}");

                TxtSelectedFreqDistortion.Text = sbSel.ToString();
                TxtSelectedFreqDistortion.Foreground = pass ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Crimson;
            }
            else
            {
                TxtSelectedFreqDistortion.Text = $"Không tìm thấy điểm hài gần tần số {inspectFreq:F0} Hz.";
                TxtSelectedFreqDistortion.Foreground = System.Windows.Media.Brushes.Goldenrod;
            }

            PlotHarmonics.Plot.ShowLegend(Edge.Top);
        }
        else
        {
            TxtDistortionSummary.Text = "⚠ CẢNH BÁO: Không tách được hài Farina (xung bị ngắn / tín hiệu không hợp lệ).";
            TxtDistortionSummary.Foreground = System.Windows.Media.Brushes.Goldenrod;
            TxtDistortionTable.Text = "Không có số liệu méo hài.";
            TxtSelectedFreqDistortion.Text = "Chưa có dữ liệu méo hài từ sweep.";
            TxtSelectedFreqDistortion.Foreground = System.Windows.Media.Brushes.Silver;
        }

        PlotHarmonics.Plot.Title("Độ méo hài THD & H2-H5 tách từ Log-Sweep (Farina Method)");
        PlotHarmonics.Plot.Axes.Bottom.Label.Text = "Tần số cơ bản Fundamental (Hz)";
        PlotHarmonics.Plot.Axes.Left.Label.Text = "Mức hài Harmonic Level (dBc / dBr)";
        ApplyLogTicks(PlotHarmonics.Plot);
        PlotHarmonics.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(12000), -100, 5);
        PlotHarmonics.Refresh();
    }

    private static string FormatHarmonicStr(double dbc)
    {
        if (!double.IsFinite(dbc)) return "N/A";
        double pct = 100.0 * Math.Pow(10.0, dbc / 20.0);
        return $"{pct:F3}% ({dbc:F1} dBc)";
    }

    private static void AddScatterTrace(WpfPlot plot, double[] xs, double[] ys, string label, string hexColor, float lineWidth)
    {
        var validPairs = xs.Zip(ys, (x, y) => new { x, y })
            .Where(p => double.IsFinite(p.y) && p.y > -150)
            .ToList();
        if (validPairs.Count == 0) return;

        var scatter = plot.Plot.Add.Scatter(validPairs.Select(p => p.x).ToArray(), validPairs.Select(p => p.y).ToArray());
        scatter.Color = ScottPlot.Color.FromHex(hexColor);
        scatter.LineWidth = lineWidth;
        scatter.MarkerSize = 0;
        scatter.LegendText = label;
    }

    // ==========================================
    // CHANNEL SYNCHRONIZATION & EVENT HANDLERS
    // ==========================================

    public void SetPlaybackChannel(int? channel)
    {
        if (_isSyncingChannels) return;
        _isSyncingChannels = true;
        try
        {
            _selectedPlaybackChannel = channel;
            _audioEngine.PlaybackChannel = channel;

            int idx = channel == 0 ? 1 : channel == 1 ? 2 : 0;
            if (ComboPlaybackChannel != null && ComboPlaybackChannel.SelectedIndex != idx)
                ComboPlaybackChannel.SelectedIndex = idx;
            if (ComboGenChannel != null && ComboGenChannel.SelectedIndex != idx)
                ComboGenChannel.SelectedIndex = idx;
            if (ComboSweepOutChannel != null && ComboSweepOutChannel.SelectedIndex != idx)
                ComboSweepOutChannel.SelectedIndex = idx;

            UpdateChannelPresetButtons();

            if (_isGenPlaying)
            {
                _audioEngine.SetContinuousPlaybackChannel(_selectedPlaybackChannel);
                UpdateGenStatusText();
            }
            UpdateGenPreview();
        }
        finally
        {
            _isSyncingChannels = false;
        }
    }

    public void SetRecordingChannel(int? channel)
    {
        if (_isSyncingChannels) return;
        _isSyncingChannels = true;
        try
        {
            _selectedRecordingChannel = channel;
            _audioEngine.RecordingChannel = channel;

            int idx = channel == 0 ? 1 : channel == 1 ? 2 : 0;
            if (ComboRecordingChannel != null && ComboRecordingChannel.SelectedIndex != idx)
                ComboRecordingChannel.SelectedIndex = idx;
            if (ComboSweepInChannel != null && ComboSweepInChannel.SelectedIndex != idx)
                ComboSweepInChannel.SelectedIndex = idx;
            if (ComboRtaChannel != null && ComboRtaChannel.SelectedIndex != idx)
                ComboRtaChannel.SelectedIndex = idx;
            if (ComboScopeChannel != null && ComboScopeChannel.SelectedIndex != idx)
                ComboScopeChannel.SelectedIndex = idx;

            if ((_isRtaRunning || _isScopeRunning || _isMeasureMonitorRunning) && ComboRecording.SelectedItem is MMDevice recording)
            {
                _audioEngine.StopContinuousCapture();
                EnsureContinuousCaptureStarted(recording);
            }
        }
        finally
        {
            _isSyncingChannels = false;
        }
    }

    private void UpdateChannelPresetButtons()
    {
        if (!_isInitialized || BtnGenChLeft == null || BtnGenChRight == null || BtnGenChBoth == null) return;
        var inactiveStyle = (Style)FindResource("PresetButtonStyle");
        var activeStyle = (Style)FindResource("GreenButtonStyle");

        BtnGenChLeft.Style = _selectedPlaybackChannel == 0 ? activeStyle : inactiveStyle;
        BtnGenChRight.Style = _selectedPlaybackChannel == 1 ? activeStyle : inactiveStyle;
        BtnGenChBoth.Style = !_selectedPlaybackChannel.HasValue ? activeStyle : inactiveStyle;
    }

    public string GetPlaybackChannelDisplayName() => _selectedPlaybackChannel switch
    {
        0 => "Kênh Trái (Left / Mic 1)",
        1 => "Kênh Phải (Right / Mic 2)",
        _ => "Cả 2 kênh (Left + Right)"
    };

    public string GetRecordingChannelDisplayName() => _selectedRecordingChannel switch
    {
        0 => "Mic 1 (Kênh Trái / Left)",
        1 => "Mic 2 (Kênh Phải / Right)",
        _ => "Cả 2 Mic (Trộn Mono)"
    };

    private void ComboPlaybackChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboPlaybackChannel == null) return;
        int? ch = ComboPlaybackChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetPlaybackChannel(ch);
    }

    private void ComboRecordingChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboRecordingChannel == null) return;
        int? ch = ComboRecordingChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetRecordingChannel(ch);
    }

    private void ComboSweepOutChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboSweepOutChannel == null) return;
        int? ch = ComboSweepOutChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetPlaybackChannel(ch);
    }

    private void ComboSweepInChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboSweepInChannel == null) return;
        int? ch = ComboSweepInChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetRecordingChannel(ch);
    }

    private void ComboGenChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboGenChannel == null) return;
        int? ch = ComboGenChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetPlaybackChannel(ch);
    }

    private void BtnGenChLeft_Click(object sender, RoutedEventArgs e) => SetPlaybackChannel(0);
    private void BtnGenChRight_Click(object sender, RoutedEventArgs e) => SetPlaybackChannel(1);
    private void BtnGenChBoth_Click(object sender, RoutedEventArgs e) => SetPlaybackChannel(null);

    private void ComboRtaChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboRtaChannel == null) return;
        int? ch = ComboRtaChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetRecordingChannel(ch);
    }

    private void ComboScopeChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboScopeChannel == null) return;
        int? ch = ComboScopeChannel.SelectedIndex switch { 1 => 0, 2 => 1, _ => null };
        SetRecordingChannel(ch);
    }

    // ==========================================
    // 2. SIGNAL GENERATOR
    // ==========================================

    private void ComboGenType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboGenType == null) return;
        _genSignalType = ComboGenType.SelectedIndex switch
        {
            0 => SignalType.Sine,
            1 => SignalType.Square,
            2 => SignalType.Triangle,
            3 => SignalType.PinkNoise,
            4 => SignalType.WhiteNoise,
            _ => SignalType.Sine
        };
        if (_isGenPlaying)
        {
            _audioEngine.UpdateContinuousPlayback(_genSignalType, _genFrequency, Math.Pow(10.0, _genLevelDb / 20.0), _selectedPlaybackChannel);
        }
        UpdateGenPreview();
    }

    private void TxtGenFreq_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || TxtGenFreq == null || SliderGenFreq == null) return;
        if (TryReadDouble(TxtGenFreq.Text, out double freq) && freq >= 10 && freq <= 24000)
        {
            _genFrequency = freq;
            double logVal = Math.Log10(freq);
            if (Math.Abs(SliderGenFreq.Value - logVal) > 0.01)
                SliderGenFreq.Value = logVal;

            if (_isGenPlaying)
                _audioEngine.UpdateContinuousPlayback(_genSignalType, _genFrequency, Math.Pow(10.0, _genLevelDb / 20.0), _selectedPlaybackChannel);
            UpdateGenPreview();
        }
    }

    private void SliderGenFreq_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || TxtGenFreq == null) return;
        double freq = Math.Pow(10.0, e.NewValue);
        _genFrequency = Math.Round(freq, 1);
        if (TxtGenFreq != null && !TxtGenFreq.IsFocused)
            TxtGenFreq.Text = _genFrequency.ToString("0.#", CultureInfo.InvariantCulture);

        if (_isGenPlaying)
            _audioEngine.UpdateContinuousPlayback(_genSignalType, _genFrequency, Math.Pow(10.0, _genLevelDb / 20.0), _selectedPlaybackChannel);
        UpdateGenPreview();
    }

    private void BtnGenFreqPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && TryReadDouble(tagStr, out double freq))
        {
            if (TxtGenFreq != null) TxtGenFreq.Text = freq.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    private void TxtGenLevel_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitialized || TxtGenLevel == null || SliderGenLevel == null) return;
        if (TryReadDouble(TxtGenLevel.Text, out double level) && level >= -60 && level <= 0)
        {
            _genLevelDb = level;
            if (Math.Abs(SliderGenLevel.Value - level) > 0.1)
                SliderGenLevel.Value = level;

            if (_isGenPlaying)
                _audioEngine.UpdateContinuousPlayback(_genSignalType, _genFrequency, Math.Pow(10.0, _genLevelDb / 20.0), _selectedPlaybackChannel);
        }
    }

    private void SliderGenLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || TxtGenLevel == null) return;
        _genLevelDb = Math.Round(e.NewValue, 1);
        if (TxtGenLevel != null && !TxtGenLevel.IsFocused)
            TxtGenLevel.Text = _genLevelDb.ToString("0.#", CultureInfo.InvariantCulture);

        if (_isGenPlaying)
            _audioEngine.UpdateContinuousPlayback(_genSignalType, _genFrequency, Math.Pow(10.0, _genLevelDb / 20.0), _selectedPlaybackChannel);
    }

    private void BtnGenLevelPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && TryReadDouble(tagStr, out double level))
        {
            if (TxtGenLevel != null) TxtGenLevel.Text = level.ToString("0", CultureInfo.InvariantCulture);
        }
    }

    private void BtnGenPlayStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenPlaying)
        {
            StopGenerator();
        }
        else
        {
            StartGenerator();
        }
    }

    private void StartGenerator()
    {
        if (ComboPlayback.SelectedItem is not MMDevice playback)
        {
            ShowMessage("Vui lòng chọn ngõ phát âm thanh trước khi phát tín hiệu.", "Chưa chọn ngõ phát", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ComboRecording.SelectedItem is not MMDevice recording)
        {
            ShowMessage("Vui lòng chọn Mic/ngõ thu để hiển thị dạng sóng Generator realtime và phát hiện click.", "Chưa chọn ngõ thu", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            lock (_captureLock)
            {
                Array.Clear(_captureBuffer, 0, _captureBuffer.Length);
                _captureWriteIndex = 0;
            }
            EnsureContinuousCaptureStarted(recording);
            double vol = Math.Pow(10.0, _genLevelDb / 20.0);
            _audioEngine.StartContinuousPlayback(playback, _genSignalType, _genFrequency, vol, _selectedPlaybackChannel);
            _isGenPlaying = true;
            _generatorPlotTimer.Start();
            BtnGenPlayStop.Content = "⏹ DỪNG PHÁT";
            BtnGenPlayStop.Style = (Style)FindResource("AmberButtonStyle");
            UpdateGenStatusText();
            UpdateGenPreview();
        }
        catch (Exception ex)
        {
            _audioEngine.StopContinuousCapture();
            ShowMessage(FormatAudioDeviceError(ex), "Lỗi phát tín hiệu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateGenStatusText()
    {
        if (TxtGenStatus == null) return;
        string chName = GetPlaybackChannelDisplayName();
        TxtGenStatus.Text = $"Đang phát: {_genSignalType} | {chName} | {_genFrequency:0.#} Hz | {_genLevelDb:F1} dBFS";
        TxtGenStatus.Foreground = System.Windows.Media.Brushes.MediumSpringGreen;
    }

    private void StopGenerator()
    {
        _generatorPlotTimer.Stop();
        _audioEngine.StopContinuousPlayback();
        _audioEngine.StopContinuousCapture();
        _isGenPlaying = false;
        BtnGenPlayStop.Content = "▶ BẮT ĐẦU PHÁT";
        BtnGenPlayStop.Style = (Style)FindResource("GreenButtonStyle");
        TxtGenStatus.Text = "Trạng thái: Đang dừng phát";
        TxtGenStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
        UpdateGenPreview();
    }

    private void UpdateGenPreview()
    {
        if (!_isInitialized || PlotGenPreview == null) return;
        if (_isGenPlaying) return;
        PlotGenPreview.Plot.Clear();
        PlotGenPreview.Plot.Title("Chưa phát tín hiệu (waveform sẽ lấy trực tiếp từ Mic/ngõ thu)");
        PlotGenPreview.Plot.Axes.Bottom.Label.Text = "Thời gian Time (ms)";
        PlotGenPreview.Plot.Axes.Left.Label.Text = "Biên độ thu thực tế";
        PlotGenPreview.Plot.Axes.SetLimits(-100, 0, -1.05, 1.05);
        PlotGenPreview.Refresh();
    }

    private void GeneratorPlotTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isGenPlaying || PlotGenPreview == null) return;

        int sampleRate;
        const int maximumSamples = 4096;
        var samples = new float[maximumSamples];
        lock (_captureLock)
        {
            sampleRate = Math.Max(8000, _captureSampleRate);
            int readIndex = (_captureWriteIndex - maximumSamples + _captureBuffer.Length) % _captureBuffer.Length;
            for (int index = 0; index < maximumSamples; index++)
                samples[index] = _captureBuffer[(readIndex + index) % _captureBuffer.Length];
        }

        double[] xs = new double[maximumSamples];
        double[] ys = new double[maximumSamples];
        double peak = 0.0;
        double maxStep = 0.0;
        for (int index = 0; index < maximumSamples; index++)
        {
            xs[index] = (index - maximumSamples + 1) * 1000.0 / sampleRate;
            ys[index] = samples[index];
            peak = Math.Max(peak, Math.Abs(ys[index]));
            if (index > 0) maxStep = Math.Max(maxStep, Math.Abs(ys[index] - ys[index - 1]));
        }

        double expectedSineStep = 2.0 * peak * Math.Sin(Math.PI * Math.Min(_genFrequency, sampleRate * 0.49) / sampleRate);
        bool possibleClick = _genSignalType == SignalType.Sine
            && peak > 0.001
            && maxStep > Math.Max(0.03, expectedSineStep * 2.5);

        PlotGenPreview.Plot.Clear();
        var line = PlotGenPreview.Plot.Add.Scatter(xs, ys);
        line.Color = ScottPlot.Color.FromHex(possibleClick ? "#EF4444" : "#10B981");
        line.LineWidth = 1.5f;
        line.MarkerSize = 0;
        double displayPeak = Math.Max(0.02, Math.Min(1.05, peak * 1.15));
        PlotGenPreview.Plot.Title(
            $"THU REALTIME: {_genSignalType} {_genFrequency:0.#} Hz | Peak {20.0 * Math.Log10(Math.Max(1E-9, peak)):F1} dBFS | " +
            (possibleClick ? "NGHI CÓ CLICK / GIÁN ĐOẠN" : "liên tục"));
        PlotGenPreview.Plot.Axes.Bottom.Label.Text = "100 ms gần nhất (ms)";
        PlotGenPreview.Plot.Axes.Left.Label.Text = "Biên độ thu thực tế";
        PlotGenPreview.Plot.Axes.SetLimits(xs[0], 0, -displayPeak, displayPeak);
        PlotGenPreview.Refresh();
    }

    // ==========================================
    // 3. RTA (REAL-TIME ANALYZER)
    // ==========================================

    private void BtnRtaPlayStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isRtaRunning)
        {
            StopRta();
        }
        else
        {
            StartRta();
        }
    }

    private void StartRta()
    {
        if (ComboRecording.SelectedItem is not MMDevice recording)
        {
            ShowMessage("Vui lòng chọn Mic/ngõ thu trước khi bật RTA.", "Chưa chọn ngõ thu", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            EnsureContinuousCaptureStarted(recording);
            _isRtaRunning = true;
            _rtaTimer.Start();
            BtnRtaPlayStop.Content = "⏹ DỪNG RTA";
            BtnRtaPlayStop.Style = (Style)FindResource("AmberButtonStyle");
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Lỗi mở RTA", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopRta()
    {
        _isRtaRunning = false;
        _rtaTimer.Stop();
        BtnRtaPlayStop.Content = "▶ BẬT RTA THỜI GIAN THỰC";
        BtnRtaPlayStop.Style = (Style)FindResource("GreenButtonStyle");
        BadgeRtaClip.Visibility = Visibility.Collapsed;
        BadgeRtaLow.Visibility = Visibility.Collapsed;
        CheckStopContinuousCapture();
    }

    private void BtnRtaResetPeak_Click(object sender, RoutedEventArgs e)
    {
        _rtaPeakHold = null;
    }

    private void ComboRtaMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _rtaPeakHold = null;
    }

    private void EnsureContinuousCaptureStarted(MMDevice recording)
    {
        _audioEngine.RecordingChannel = _selectedRecordingChannel;
        if (!_audioEngine.IsContinuousCaptureActive)
        {
            _audioEngine.StartContinuousCapture(recording, (samples, sr) =>
            {
                lock (_captureLock)
                {
                    _captureSampleRate = sr;
                    int toCopy = Math.Min(samples.Length, _captureBuffer.Length);
                    for (int i = 0; i < toCopy; i++)
                    {
                        _captureBuffer[_captureWriteIndex] = samples[i];
                        _captureWriteIndex = (_captureWriteIndex + 1) % _captureBuffer.Length;
                    }
                }
            });
        }
    }

    private void CheckStopContinuousCapture()
    {
        if (!_isRtaRunning && !_isScopeRunning && !_isMeasureMonitorRunning)
        {
            _audioEngine.StopContinuousCapture();
        }
    }

    private void RtaTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isRtaRunning) return;

        const int fftSize = 4096;
        float[] samples = new float[fftSize];
        int sampleRate;

        lock (_captureLock)
        {
            sampleRate = _captureSampleRate;
            int readIdx = (_captureWriteIndex - fftSize + _captureBuffer.Length) % _captureBuffer.Length;
            for (int i = 0; i < fftSize; i++)
            {
                samples[i] = _captureBuffer[(readIdx + i) % _captureBuffer.Length];
            }
        }

        // Apply Hann window and Fourier FFT
        var complex = new Complex[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            double window = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / fftSize));
            complex[i] = samples[i] * window;
        }
        Fourier.Forward(complex, FourierOptions.Matlab);

        int numBins = fftSize / 2;
        double[] freqs = new double[numBins];
        double[] mags = new double[numBins];

        double peakMag = 1e-12;
        int peakBin = 1;
        double binWidth = (double)sampleRate / fftSize;

        for (int k = 1; k < numBins; k++)
        {
            double f = k * binWidth;
            freqs[k] = f;
            double mag = complex[k].Magnitude / (fftSize / 4.0);
            mags[k] = 20.0 * Math.Log10(Math.Max(1e-6, mag));
            if (f >= 30 && f <= 15000 && mag > peakMag)
            {
                peakMag = mag;
                peakBin = k;
            }
        }

        double peakFreq = peakBin * binWidth;
        double peakLevelDb = 20.0 * Math.Log10(Math.Max(1e-6, peakMag));

        // Live THD calculation
        double fundamentalPower = peakMag * peakMag;
        double harmonicPower = 0.0;
        for (int h = 2; h <= 5; h++)
        {
            int hBin = peakBin * h;
            if (hBin < numBins - 2)
            {
                double hMag = 0;
                for (int d = -2; d <= 2; d++)
                {
                    double m = complex[hBin + d].Magnitude / (fftSize / 4.0);
                    if (m > hMag) hMag = m;
                }
                harmonicPower += hMag * hMag;
            }
        }
        double liveThd = fundamentalPower > 1e-9 ? Math.Sqrt(harmonicPower / fundamentalPower) * 100.0 : 0.0;

        // Broadband RMS & SNR
        double totalPower = samples.Select(s => (double)s * s).Average();
        double totalRmsDb = 20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(totalPower)));
        double liveThdn = totalPower > fundamentalPower ? Math.Sqrt((totalPower - fundamentalPower) / totalPower) * 100.0 : 0.0;
        double liveSnr = Math.Max(0.0, peakLevelDb - (totalRmsDb - 20.0));

        TxtRtaPeakFreq.Text = $"{peakFreq:F1} Hz";
        TxtRtaPeakLevel.Text = $"{peakLevelDb:F1} dBFS";
        TxtRtaThd.Text = $"{liveThd:F2}%";
        TxtRtaThdn.Text = $"{liveThdn:F2}%";
        TxtRtaSnr.Text = $"{liveSnr:F1} dB";

        if (peakLevelDb >= -0.5)
        {
            BadgeRtaClip.Visibility = Visibility.Visible;
            BadgeRtaLow.Visibility = Visibility.Collapsed;
        }
        else if (peakLevelDb < -40.0)
        {
            BadgeRtaLow.Visibility = Visibility.Visible;
            BadgeRtaClip.Visibility = Visibility.Collapsed;
        }
        else
        {
            BadgeRtaClip.Visibility = Visibility.Collapsed;
            BadgeRtaLow.Visibility = Visibility.Collapsed;
        }

        // Octave banding or full spectrum
        int mode = ComboRtaMode.SelectedIndex;
        double[] plotX;
        double[] plotY;

        if (mode == 0) // Raw FFT Spectrum
        {
            plotX = freqs.Skip(1).Select(Math.Log10).ToArray();
            plotY = mags.Skip(1).ToArray();
        }
        else // Octave bands (1/1, 1/3, 1/6, 1/12, 1/24)
        {
            int ppo = mode switch { 1 => 1, 2 => 3, 3 => 6, 4 => 12, 5 => 24, _ => 12 };
            (plotX, plotY) = CalculateOctaveBands(freqs, mags, ppo);
        }

        // Peak hold
        bool peakHold = ChkRtaPeakHold.IsChecked == true;
        if (peakHold)
        {
            if (_rtaPeakHold == null || _rtaPeakHold.Length != plotY.Length)
            {
                _rtaPeakHold = (double[])plotY.Clone();
            }
            else
            {
                for (int i = 0; i < plotY.Length; i++)
                {
                    if (plotY[i] > _rtaPeakHold[i]) _rtaPeakHold[i] = plotY[i];
                }
            }
        }
        else
        {
            _rtaPeakHold = null;
        }

        // Render PlotRta
        PlotRta.Plot.Clear();
        if (plotX.Length > 0)
        {
            var rtaTrace = PlotRta.Plot.Add.Scatter(plotX, plotY);
            rtaTrace.Color = ScottPlot.Color.FromHex("#10B981");
            rtaTrace.LineWidth = 2f;
            rtaTrace.MarkerSize = 0;

            if (peakHold && _rtaPeakHold != null)
            {
                var peakTrace = PlotRta.Plot.Add.Scatter(plotX, _rtaPeakHold);
                peakTrace.Color = ScottPlot.Color.FromHex("#F59E0B");
                peakTrace.LineWidth = 1.2f;
                peakTrace.MarkerSize = 0;
            }
        }
        PlotRta.Plot.Title("Phổ âm thanh thời gian thực (Real-Time Analyzer)");
        PlotRta.Plot.Axes.Bottom.Label.Text = "Tần số Frequency (Hz)";
        PlotRta.Plot.Axes.Left.Label.Text = "Mức Level (dBFS)";
        ApplyLogTicks(PlotRta.Plot);
        PlotRta.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), -110, 0);
        PlotRta.Refresh();
    }

    private static (double[] xs, double[] ys) CalculateOctaveBands(double[] freqs, double[] magsDb, int pointsPerOctave)
    {
        double minFreq = 20.0;
        double maxFreq = 20000.0;
        int bandCount = (int)Math.Floor(Math.Log2(maxFreq / minFreq) * pointsPerOctave) + 1;
        var bXs = new List<double>(bandCount);
        var bYs = new List<double>(bandCount);

        double halfRatio = Math.Pow(2.0, 1.0 / (2.0 * pointsPerOctave));

        for (int b = 0; b < bandCount; b++)
        {
            double fc = minFreq * Math.Pow(2.0, (double)b / pointsPerOctave);
            if (fc > maxFreq) break;

            double fLow = fc / halfRatio;
            double fHigh = fc * halfRatio;

            double sumPow = 0.0;
            int count = 0;
            for (int i = 1; i < freqs.Length; i++)
            {
                if (freqs[i] >= fLow && freqs[i] < fHigh)
                {
                    sumPow += Math.Pow(10.0, magsDb[i] / 10.0);
                    count++;
                }
            }
            if (count > 0)
            {
                bXs.Add(Math.Log10(fc));
                bYs.Add(10.0 * Math.Log10(sumPow / count));
            }
        }

        return (bXs.ToArray(), bYs.ToArray());
    }

    // ==========================================
    // 4. OSCILLOSCOPE (SCOPE)
    // ==========================================

    private void BtnScopePlayStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isScopeRunning)
        {
            StopScope();
        }
        else
        {
            StartScope();
        }
    }

    private void StartScope()
    {
        if (ComboRecording.SelectedItem is not MMDevice recording)
        {
            ShowMessage("Vui lòng chọn Mic/ngõ thu trước khi bật Scope.", "Chưa chọn ngõ thu", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            EnsureContinuousCaptureStarted(recording);
            _isScopeRunning = true;
            _scopeTimer.Start();
            BtnScopePlayStop.Content = "⏹ DỪNG (STOP)";
            BtnScopePlayStop.Style = (Style)FindResource("AmberButtonStyle");
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, "Lỗi mở Scope", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopScope()
    {
        _isScopeRunning = false;
        _scopeTimer.Stop();
        BtnScopePlayStop.Content = "▶ BẬT SCOPE (RUN)";
        BtnScopePlayStop.Style = (Style)FindResource("GreenButtonStyle");
        BadgeScopeClip.Visibility = Visibility.Collapsed;
        BadgeScopeLow.Visibility = Visibility.Collapsed;
        CheckStopContinuousCapture();
    }

    private void ComboScopeTimebase_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void ComboScopeScale_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ScopeTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isScopeRunning) return;

        const int bufLen = 4096;
        float[] samples = new float[bufLen];
        int sampleRate;

        lock (_captureLock)
        {
            sampleRate = _captureSampleRate;
            int readIdx = (_captureWriteIndex - bufLen + _captureBuffer.Length) % _captureBuffer.Length;
            for (int i = 0; i < bufLen; i++)
            {
                samples[i] = _captureBuffer[(readIdx + i) % _captureBuffer.Length];
            }
        }

        // Parse Timebase & Scale
        double msPerDiv = ComboScopeTimebase.SelectedIndex switch
        {
            0 => 0.2,
            1 => 0.5,
            2 => 1.0,
            3 => 2.0,
            4 => 5.0,
            5 => 10.0,
            6 => 20.0,
            _ => 1.0
        };
        double scale = ComboScopeScale.SelectedIndex switch
        {
            0 => 0.1,
            1 => 0.2,
            2 => 0.5,
            3 => 1.0,
            _ => 1.0
        };

        double totalMs = msPerDiv * 10.0;
        int displaySamples = (int)(totalMs * sampleRate / 1000.0);
        displaySamples = Math.Clamp(displaySamples, 50, bufLen / 2);

        // Trigger detection
        bool isRising = ComboScopeEdge.SelectedIndex == 0;
        double triggerLevel = SliderScopeTriggerLevel?.Value ?? 0.0;
        int triggerIdx = 0;

        for (int i = 1; i < bufLen - displaySamples; i++)
        {
            bool triggered = isRising
                ? samples[i - 1] < triggerLevel && samples[i] >= triggerLevel
                : samples[i - 1] > triggerLevel && samples[i] <= triggerLevel;
            if (triggered)
            {
                triggerIdx = i;
                break;
            }
        }

        // Metrics: Vpp, Vrms, Frequency
        double minVal = 10.0;
        double maxVal = -10.0;
        double sumSq = 0.0;
        int zeroCrossings = 0;

        for (int i = triggerIdx; i < triggerIdx + displaySamples; i++)
        {
            float s = samples[i];
            if (s < minVal) minVal = s;
            if (s > maxVal) maxVal = s;
            sumSq += s * s;
            if (i > triggerIdx && ((samples[i - 1] < 0 && s >= 0) || (samples[i - 1] > 0 && s <= 0)))
                zeroCrossings++;
        }

        double vpp = Math.Max(0.0, maxVal - minVal);
        double vrms = Math.Sqrt(sumSq / displaySamples);
        double rmsDb = 20.0 * Math.Log10(Math.Max(1e-6, vrms));
        double estFreq = zeroCrossings > 1 ? (zeroCrossings / 2.0) / (totalMs / 1000.0) : 0.0;
        double crest = vrms > 1e-4 ? Math.Max(Math.Abs(minVal), Math.Abs(maxVal)) / vrms : 0.0;

        TxtScopeVpp.Text = $"{vpp:F3} V";
        TxtScopeVrms.Text = $"{vrms:F3} V ({rmsDb:F1} dBFS)";
        TxtScopeFreq.Text = estFreq > 10 ? $"{estFreq:F1} Hz" : "-- Hz";
        TxtScopeCrest.Text = crest > 0 ? $"{crest:F2}" : "--";

        double peakAbs = Math.Max(Math.Abs(minVal), Math.Abs(maxVal));
        if (peakAbs >= 0.995 || rmsDb > -0.5)
        {
            BadgeScopeClip.Visibility = Visibility.Visible;
            BadgeScopeLow.Visibility = Visibility.Collapsed;
        }
        else if (peakAbs < 0.01 || rmsDb < -45.0)
        {
            BadgeScopeLow.Visibility = Visibility.Visible;
            BadgeScopeClip.Visibility = Visibility.Collapsed;
        }
        else
        {
            BadgeScopeClip.Visibility = Visibility.Collapsed;
            BadgeScopeLow.Visibility = Visibility.Collapsed;
        }

        // PlotScope
        double[] tMs = new double[displaySamples];
        double[] sVals = new double[displaySamples];
        for (int i = 0; i < displaySamples; i++)
        {
            tMs[i] = (double)i / sampleRate * 1000.0;
            sVals[i] = samples[triggerIdx + i];
        }

        PlotScope.Plot.Clear();
        var scopeLine = PlotScope.Plot.Add.Scatter(tMs, sVals);
        scopeLine.Color = ScottPlot.Color.FromHex("#FACC15");
        scopeLine.LineWidth = 2.0f;
        scopeLine.MarkerSize = 0;

        PlotScope.Plot.Title($"Oscilloscope ({msPerDiv:0.#} ms/div, ±{scale:0.#} V)");
        PlotScope.Plot.Axes.Bottom.Label.Text = "Thời gian Time (ms)";
        PlotScope.Plot.Axes.Left.Label.Text = "Biên độ Amplitude (V / FS)";
        PlotScope.Plot.Axes.SetLimits(0, totalMs, -scale * 1.05, scale * 1.05);
        PlotScope.Refresh();
    }

    // ==========================================
    // PLOT HELPERS & DARK STYLING
    // ==========================================

    private void InitializePlots()
    {
        ApplyDark(PlotResponse.Plot, "Đáp tuyến mức thu — không chuẩn hóa", "Tần số Frequency (Hz)", "Mức thu RMS (dBFS)");
        ApplyLogTicks(PlotResponse.Plot);
        PlotResponse.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), -100, 0);
        AttachPlotInteractions(PlotResponse, true, Math.Log10(20), Math.Log10(20000), -100, 0);
        PlotResponse.Refresh();

        ApplyDark(PlotHarmonics.Plot, "Độ méo hài THD & H2-H5 tách từ Log-Sweep", "Tần số Frequency (Hz)", "Mức Level (dBc)");
        ApplyLogTicks(PlotHarmonics.Plot);
        AttachPlotInteractions(PlotHarmonics, true, Math.Log10(20), Math.Log10(12000), -100, 5);
        PlotHarmonics.Refresh();

        ApplyDark(PlotDecay.Plot, "Energy Decay Curve / RT60", "Thời gian Time (s)", "Độ suy giảm Decay (dB)");
        PlotDecay.Plot.Axes.SetLimits(0, 3, -70, 5);
        AttachPlotInteractions(PlotDecay, false, 0, 3, -70, 5);
        PlotDecay.Refresh();

        ApplyDark(PlotImpulse.Plot, "Đáp ứng xung (Impulse Response)", "Thời gian Time (ms)", "Biên độ Linear");
        PlotImpulse.Plot.Axes.SetLimits(0, 100, -1, 1);
        AttachPlotInteractions(PlotImpulse, false, 0, 100, -1, 1);
        PlotImpulse.Refresh();

        ApplyDark(PlotGenPreview.Plot, "Dạng sóng tín hiệu phát (Generator)", "Thời gian Time (ms)", "Biên độ Amplitude");
        PlotGenPreview.Refresh();

        ApplyDark(PlotRta.Plot, "Phổ âm thanh thời gian thực (RTA)", "Tần số Frequency (Hz)", "Mức Level (dBFS)");
        ApplyLogTicks(PlotRta.Plot);
        PlotRta.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), -110, 0);
        PlotRta.Refresh();

        ApplyDark(PlotScope.Plot, "Dao động ký Oscilloscope", "Thời gian Time (ms)", "Biên độ Amplitude (V)");
        PlotScope.Plot.Axes.SetLimits(0, 10, -1, 1);
        PlotScope.Refresh();
    }

    public static void ZoomPlot(WpfPlot wpfPlot, double factor, Point? mousePos = null)
    {
        AxisLimits limits = wpfPlot.Plot.Axes.GetLimits();
        double spanX = limits.Right - limits.Left;
        double spanY = limits.Top - limits.Bottom;
        if (spanX <= 0 || spanY <= 0) return;

        double centerX = limits.Left + spanX / 2.0;
        double centerY = limits.Bottom + spanY / 2.0;

        double newSpanX = spanX / factor;
        double newSpanY = spanY / factor;

        if (newSpanX < 0.001 || newSpanX > 100000 || newSpanY < 0.01 || newSpanY > 1000) return;

        wpfPlot.Plot.Axes.SetLimits(centerX - newSpanX / 2.0, centerX + newSpanX / 2.0, centerY - newSpanY / 2.0, centerY + newSpanY / 2.0);
        wpfPlot.Refresh();
    }

    public void ZoomActivePlot(double factor)
    {
        WpfPlot target = PlotDecay.IsMouseOver ? PlotDecay : (PlotHarmonics.IsMouseOver ? PlotHarmonics : PlotResponse);
        ZoomPlot(target, factor);
    }

    public void ResetActivePlotZoom()
    {
        WpfPlot target = PlotDecay.IsMouseOver ? PlotDecay : (PlotHarmonics.IsMouseOver ? PlotHarmonics : PlotResponse);
        if (ReferenceEquals(target, PlotDecay))
            target.Plot.Axes.SetLimits(0, 3, -70, 5);
        else
            target.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), -30, 20);
        target.Refresh();
    }

    private void HandlePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key is Key.OemPlus or Key.Add)
            {
                ZoomActivePlot(1.25);
                e.Handled = true;
            }
            else if (e.Key is Key.OemMinus or Key.Subtract)
            {
                ZoomActivePlot(0.80);
                e.Handled = true;
            }
            else if (e.Key is Key.D0 or Key.NumPad0)
            {
                ResetActivePlotZoom();
                e.Handled = true;
            }
        }
    }

    private void AttachPlotInteractions(WpfPlot wpfPlot, bool isLogX, double defaultMinX, double defaultMaxX, double defaultMinY, double defaultMaxY)
    {
        Point panStartPoint = default;
        AxisLimits panStartLimits = default;
        bool isPanning = false;

        wpfPlot.Focusable = true;
        wpfPlot.Cursor = Cursors.Arrow;

        wpfPlot.PreviewMouseDown += (s, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
            {
                panStartPoint = e.GetPosition(wpfPlot);
                panStartLimits = wpfPlot.Plot.Axes.GetLimits();
                isPanning = true;
                wpfPlot.CaptureMouse();
                wpfPlot.Focus();
                Mouse.OverrideCursor = Cursors.Hand;
                e.Handled = true;
            }
        };

        wpfPlot.PreviewMouseMove += (s, e) =>
        {
            if (isPanning && (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed))
            {
                Point pos = e.GetPosition(wpfPlot);
                double dx = pos.X - panStartPoint.X;
                double dy = pos.Y - panStartPoint.Y;
                double plotW = Math.Max(100.0, wpfPlot.ActualWidth - 80.0);
                double plotH = Math.Max(100.0, wpfPlot.ActualHeight - 60.0);
                double spanX = panStartLimits.Right - panStartLimits.Left;
                double spanY = panStartLimits.Top - panStartLimits.Bottom;
                double shiftX = -dx * (spanX / plotW);
                double shiftY = dy * (spanY / plotH);
                wpfPlot.Plot.Axes.SetLimits(panStartLimits.Left + shiftX, panStartLimits.Right + shiftX, panStartLimits.Bottom + shiftY, panStartLimits.Top + shiftY);
                wpfPlot.Refresh();
                Mouse.OverrideCursor = Cursors.Hand;
                e.Handled = true;
            }
            else if (isPanning)
            {
                isPanning = false;
                wpfPlot.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
            }
        };

        wpfPlot.PreviewMouseUp += (s, e) =>
        {
            if (isPanning)
            {
                isPanning = false;
                wpfPlot.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
                e.Handled = true;
            }
        };

        wpfPlot.LostMouseCapture += (s, e) =>
        {
            isPanning = false;
            Mouse.OverrideCursor = null;
        };

        wpfPlot.PreviewMouseWheel += (s, e) =>
        {
            Point mousePos = e.GetPosition(wpfPlot);
            double factor = e.Delta > 0 ? 1.25 : 0.80;
            ZoomPlot(wpfPlot, factor, mousePos);
            e.Handled = true;
        };

        wpfPlot.MouseDoubleClick += (s, e) =>
        {
            wpfPlot.Plot.Axes.SetLimits(defaultMinX, defaultMaxX, defaultMinY, defaultMaxY);
            wpfPlot.Refresh();
            e.Handled = true;
        };
    }

    private static void ApplyDark(Plot plot, string title, string xLabel, string yLabel)
    {
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#0E0E10");
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#27272A");
        plot.Axes.Color(ScottPlot.Color.FromHex("#A1A1AA"));
        plot.Title(title);
        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Axes.Left.Label.Text = yLabel;
    }

    private static void ApplyLogTicks(Plot plot)
    {
        double[] frequencies = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        string[] labels = { "20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "20k" };
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            frequencies.Select((frequency, index) => new Tick(Math.Log10(frequency), labels[index])).ToArray());
    }

    private static void WriteFloatWave(string path, float[] samples, int sampleRate)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
