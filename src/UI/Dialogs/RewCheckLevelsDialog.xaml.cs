using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class RewCheckLevelsDialog : Window
{
    private readonly AudioEngine? _audioEngine;
    private readonly MMDevice? _playbackDevice;
    private readonly MMDevice? _recordingDevice;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _windowsVolumeTimer;

    private readonly object _captureLock = new();
    private readonly float[] _captureBuffer = new float[16384];
    private int _captureWriteIndex = 0;
    private int _captureSampleRate = 48000;

    private bool _isInitialized = false;
    private bool _isPlaying = false;
    private double _outLevelDb = -12.0;
    private SignalType _signalType = SignalType.PinkNoise;
    private int? _outChannel = null;
    private int? _inChannel = null;
    private bool _startMeasureRequested = false;

    public double SelectedSweepLevelDb => _outLevelDb;
    public int? SelectedOutChannel => _outChannel;
    public int? SelectedInChannel => _inChannel;
    public bool StartMeasureRequested => _startMeasureRequested;

    public RewCheckLevelsDialog(AudioEngine? audioEngine, MMDevice? playback, MMDevice? recording, double currentSweepLevelDb, int? initialOutChannel = null, int? initialInChannel = null)
    {
        _audioEngine = audioEngine;
        _playbackDevice = playback;
        _recordingDevice = recording;
        _outLevelDb = Math.Clamp(currentSweepLevelDb, -60.0, 0.0);
        _outChannel = initialOutChannel ?? audioEngine?.PlaybackChannel;
        _inChannel = initialInChannel ?? audioEngine?.RecordingChannel;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _timer.Tick += Timer_Tick;
        _windowsVolumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _windowsVolumeTimer.Tick += (_, _) => RefreshWindowsVolumes();

        InitializeComponent();
        _isInitialized = true;

        TxtPlaybackDeviceName.Text = playback?.FriendlyName ?? "Chưa chọn thiết bị phát";
        TxtRecordingDeviceName.Text = recording?.FriendlyName ?? "Chưa chọn thiết bị thu";

        // Initialize sliders
        SliderGenLevel.Value = _outLevelDb;
        TxtGenLevelValue.Text = $"{_outLevelDb:F1} dBFS";
        BarOutMeter.Value = _outLevelDb;

        // Initialize channel combos
        ComboOutChannel.SelectedIndex = _outChannel == 0 ? 1 : _outChannel == 1 ? 2 : 0;
        ComboInChannel.SelectedIndex = _inChannel == 0 ? 1 : _inChannel == 1 ? 2 : 0;

        RefreshWindowsVolumes();

        Loaded += RewCheckLevelsDialog_Loaded;
        Closed += RewCheckLevelsDialog_Closed;
    }

    private void RewCheckLevelsDialog_Loaded(object sender, RoutedEventArgs e)
    {
        _windowsVolumeTimer.Start();
        StartContinuousTest();
    }

    private void RewCheckLevelsDialog_Closed(object? sender, EventArgs e)
    {
        _windowsVolumeTimer.Stop();
        StopContinuousTest();
    }

    private void RefreshWindowsVolumes()
    {
        double? selectedOut = _playbackDevice != null && _audioEngine != null
            ? _audioEngine.TryGetWindowsPlaybackVolume(_playbackDevice) : null;
        TxtWinOutVolValue.Text = selectedOut.HasValue ? $"{selectedOut.Value * 100.0:0}%" : "N/A";
        TxtWinOutVolValue.ToolTip = $"Master Volume realtime của ngõ phát đang chọn: {_playbackDevice?.FriendlyName}.";

        double? winIn = _recordingDevice != null && _audioEngine != null
            ? _audioEngine.TryGetWindowsRecordingVolume(_recordingDevice) : null;
        TxtWinInVolValue.Text = winIn.HasValue ? $"{winIn.Value * 100.0:0}%" : "N/A";
    }

    private void StartContinuousTest()
    {
        if (_audioEngine == null || _playbackDevice == null || _recordingDevice == null) return;

        try
        {
            _audioEngine.RecordingChannel = _inChannel;
            _audioEngine.PlaybackChannel = _outChannel;

            // Start continuous capture
            _audioEngine.StartContinuousCapture(_recordingDevice, (samples, sr) =>
            {
                lock (_captureLock)
                {
                    _captureSampleRate = sr;
                    int count = Math.Min(samples.Length, _captureBuffer.Length);
                    for (int i = 0; i < count; i++)
                    {
                        _captureBuffer[_captureWriteIndex] = samples[i];
                        _captureWriteIndex = (_captureWriteIndex + 1) % _captureBuffer.Length;
                    }
                }
            });

            // Start continuous playback
            double vol = Math.Pow(10.0, _outLevelDb / 20.0);
            _audioEngine.StartContinuousPlayback(_playbackDevice, _signalType, 1000.0, vol, _outChannel);
            _isPlaying = true;

            BtnPlayStop.Content = "⏹ TẠM DỪNG PHÁT";
            TxtOutActiveTag.Text = "ĐANG PHÁT";
            TxtOutActiveTag.Foreground = Brushes.MediumSpringGreen;

            _timer.Start();
        }
        catch (Exception ex)
        {
            StopContinuousTest();
            TxtEvalTitle.Text = "LỖI KHỞI ĐỘNG KIỂM TRA MỨC";
            TxtEvalTitle.Foreground = Brushes.Crimson;
            TxtEvalDesc.Text = ex.Message;
        }
    }

    private void StopContinuousTest()
    {
        _timer?.Stop();
        _isPlaying = false;
        try
        {
            _audioEngine?.StopContinuousPlayback();
            _audioEngine?.StopContinuousCapture();
        }
        catch { }

        BtnPlayStop.Content = "▶ BẬT PHÁT KIỂM TRA";
        TxtOutActiveTag.Text = "ĐÃ DỪNG";
        TxtOutActiveTag.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
        RewMeterDialog?.Reset();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        const int readSize = 2048;
        float[] samples = new float[readSize];

        lock (_captureLock)
        {
            int startIdx = (_captureWriteIndex - readSize + _captureBuffer.Length) % _captureBuffer.Length;
            for (int i = 0; i < readSize; i++)
            {
                samples[i] = _captureBuffer[(startIdx + i) % _captureBuffer.Length];
            }
        }

        RewMeterDialog?.PushSamples(samples);

        double peak = samples.Max(x => Math.Abs((double)x));
        double peakDb = 20.0 * Math.Log10(Math.Max(1e-6, peak));

        double sumSq = samples.Sum(x => (double)x * x);
        double rms = Math.Sqrt(sumSq / samples.Length);
        double rmsDb = 20.0 * Math.Log10(Math.Max(1e-6, rms));

        double headroom = Math.Max(0.0, -peakDb);
        double snr = Math.Max(0.0, rmsDb - (-75.0));

        // Update readouts
        TxtInMeterValue.Text = double.IsFinite(peakDb) ? $"{peakDb:F1} dBFS" : "-∞ dBFS";
        BarInMeter.Value = Math.Clamp(peakDb, -60.0, 0.0);

        TxtInPeak.Text = double.IsFinite(peakDb) ? $"{peakDb:F1} dBFS" : "-- dBFS";
        TxtInRms.Text = double.IsFinite(rmsDb) ? $"{rmsDb:F1} dBFS" : "-- dBFS";
        TxtInHeadroom.Text = $"{headroom:F1} dB";
        TxtInSnr.Text = $"{snr:F1} dB";

        // Level Evaluation & REW Diagnostics
        bool isClip = peak >= 0.995 || peakDb > -0.5;
        bool isNoSignal = peakDb < -60.0;
        bool isLow = !isNoSignal && (peakDb < -30.0 || rmsDb < -45.0);
        bool isHot = !isClip && peakDb > -6.0;
        bool isLowSnr = !isClip && !isNoSignal && !isLow && snr < 15.0;

        if (isClip)
        {
            TxtInStatusBadge.Text = "⛔ CLIPPING";
            TxtInStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BarInMeter.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            TxtInMeterValue.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));

            BorderEval.Background = new SolidColorBrush(Color.FromRgb(45, 14, 14));
            BorderEval.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            TxtEvalIcon.Text = "⛔";
            TxtEvalIcon.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            TxtEvalTitle.Text = "QUÁ TẢI ĐẦU VÀO (CLIPPING DETECTED)";
            TxtEvalTitle.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            TxtEvalDesc.Text = "Tín hiệu ngõ thu bị chạm trần 0 dBFS! Hãy giảm mức phát (Out) hoặc hạ độ nhạy micro (In) để tránh làm sai lệch méo hài THD.";
        }
        else if (isNoSignal)
        {
            TxtInStatusBadge.Text = "⚠ MẤT TÍN HIỆU";
            TxtInStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            BarInMeter.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            TxtInMeterValue.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));

            BorderEval.Background = new SolidColorBrush(Color.FromRgb(15, 25, 45));
            BorderEval.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            TxtEvalIcon.Text = "ℹ";
            TxtEvalIcon.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            TxtEvalTitle.Text = "KHÔNG NHẬN ĐƯỢC TÍN HIỆU (NO SIGNAL)";
            TxtEvalTitle.Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            TxtEvalDesc.Text = "Mức thu micro quá nhỏ (< -60 dBFS) hoặc không có tín hiệu âm thanh. Hãy kiểm tra cáp micro, đúng cổng thu và kiểm tra cấp nguồn / pin.";
        }
        else if (isLow)
        {
            TxtInStatusBadge.Text = "⚠ QUÁ YẾU (LOW)";
            TxtInStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            BarInMeter.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtInMeterValue.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));

            BorderEval.Background = new SolidColorBrush(Color.FromRgb(38, 26, 10));
            BorderEval.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtEvalIcon.Text = "⚠";
            TxtEvalIcon.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtEvalTitle.Text = "MỨC TÍN HIỆU QUÁ YẾU (LEVEL IS LOW)";
            TxtEvalTitle.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtEvalDesc.Text = "Mức thu micro quá nhỏ (< -30 dBFS). Hãy kéo tăng mức phát (Out) hoặc tăng độ nhạy micro (In) để kim rơi vào vùng xanh (-20 .. -6 dBFS).";
        }
        else if (isLowSnr)
        {
            TxtInStatusBadge.Text = "⚠ NHIỄU CAO (SNR THẤP)";
            TxtInStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            BarInMeter.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtInMeterValue.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));

            BorderEval.Background = new SolidColorBrush(Color.FromRgb(38, 26, 10));
            BorderEval.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtEvalIcon.Text = "⚠";
            TxtEvalIcon.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtEvalTitle.Text = "TỶ SỐ SNR QUÁ THẤP (< 15 dB - NHIỄU LỚN)";
            TxtEvalTitle.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TxtEvalDesc.Text = $"Tỷ số SNR ước tính chỉ đạt {snr:F1} dB. Tiếng ồn môi trường phòng quá lớn so với tín hiệu loa. Hãy tăng mức phát hoặc đóng cửa phòng để giảm ồn.";
        }
        else if (isHot)
        {
            TxtInStatusBadge.Text = "⚠ MỨC RẤT LỚN (HOT)";
            TxtInStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
            BarInMeter.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
            TxtInMeterValue.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));

            BorderEval.Background = new SolidColorBrush(Color.FromRgb(40, 20, 10));
            BorderEval.BorderBrush = new SolidColorBrush(Color.FromRgb(249, 115, 22));
            TxtEvalIcon.Text = "⚠";
            TxtEvalIcon.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
            TxtEvalTitle.Text = "MỨC TÍN HIỆU GẦN CHẠM ĐỈNH (HEADROOM < 6 dB)";
            TxtEvalTitle.Foreground = new SolidColorBrush(Color.FromRgb(249, 115, 22));
            TxtEvalDesc.Text = $"Mức thu ({peakDb:F1} dBFS) tiệm cận trần 0 dBFS. Nên hạ bớt mức phát một chút (khoảng 3 - 6 dB) để phòng ngừa xung phản hồi cộng hưởng gây clipping khi đo sweep.";
        }
        else
        {
            TxtInStatusBadge.Text = "✔ ĐẠT CHUẨN (OK)";
            TxtInStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            BarInMeter.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            TxtInMeterValue.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

            BorderEval.Background = new SolidColorBrush(Color.FromRgb(11, 39, 26));
            BorderEval.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            TxtEvalIcon.Text = "✔";
            TxtEvalIcon.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            TxtEvalTitle.Text = "MỨC TÍN HIỆU LÝ TƯỞNG (LEVEL OK) - SẴN SÀNG ĐO SWEEP";
            TxtEvalTitle.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            TxtEvalDesc.Text = $"Mức thu ({peakDb:F1} dBFS) nằm hoàn hảo trong vùng khuyến nghị của REW (-20 đến -6 dBFS). Tỷ số SNR cao, an toàn tuyệt đối không bị méo hài.";
        }
    }

    private void SliderGenLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || TxtGenLevelValue == null || BarOutMeter == null) return;
        _outLevelDb = SliderGenLevel.Value;
        TxtGenLevelValue.Text = $"{_outLevelDb:F1} dBFS";
        BarOutMeter.Value = _outLevelDb;

        if (_isPlaying && _audioEngine != null)
        {
            double vol = Math.Pow(10.0, _outLevelDb / 20.0);
            _audioEngine.UpdateContinuousPlayback(_signalType, 1000.0, vol);
        }
    }

    private void ComboSignalType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboSignalType == null) return;
        _signalType = ComboSignalType.SelectedIndex == 0 ? SignalType.PinkNoise : SignalType.Sine;
        if (_isPlaying && _audioEngine != null)
        {
            double vol = Math.Pow(10.0, _outLevelDb / 20.0);
            _audioEngine.UpdateContinuousPlayback(_signalType, 1000.0, vol);
        }
    }

    private void ComboOutChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboOutChannel == null) return;
        _outChannel = ComboOutChannel.SelectedIndex switch
        {
            1 => 0, // Left
            2 => 1, // Right
            _ => null // Both (Left + Right)
        };
        _audioEngine?.SetContinuousPlaybackChannel(_outChannel);
    }

    private void ComboInChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || ComboInChannel == null) return;
        _inChannel = ComboInChannel.SelectedIndex switch
        {
            1 => 0, // Mic 1 (Left)
            2 => 1, // Mic 2 (Right)
            _ => null // Both Mics (Mono Mix)
        };
        if (_audioEngine != null)
        {
            _audioEngine.RecordingChannel = _inChannel;
        }
        if (_isPlaying && _recordingDevice != null && _audioEngine != null)
        {
            _audioEngine.StopContinuousCapture();
            _audioEngine.StartContinuousCapture(_recordingDevice, (samples, sr) =>
            {
                lock (_captureLock)
                {
                    _captureSampleRate = sr;
                    int count = Math.Min(samples.Length, _captureBuffer.Length);
                    for (int i = 0; i < count; i++)
                    {
                        _captureBuffer[_captureWriteIndex] = samples[i];
                        _captureWriteIndex = (_captureWriteIndex + 1) % _captureBuffer.Length;
                    }
                }
            });
        }
    }

    private void BtnPlayStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopContinuousTest();
        }
        else
        {
            StartContinuousTest();
        }
    }

    private void BtnStartMeasureNow_Click(object sender, RoutedEventArgs e)
    {
        _startMeasureRequested = true;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _startMeasureRequested = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    public static (bool startMeasure, double selectedSweepLevelDb, int? selectedOutChannel, int? selectedInChannel) Show(
        Window? owner,
        AudioEngine engine,
        MMDevice playback,
        MMDevice recording,
        double currentSweepLevelDb,
        int? initialOutChannel = null,
        int? initialInChannel = null)
    {
        var dlg = new RewCheckLevelsDialog(engine, playback, recording, currentSweepLevelDb, initialOutChannel, initialInChannel);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
        }
        dlg.ShowDialog();
        return (dlg.StartMeasureRequested, dlg.SelectedSweepLevelDb, dlg.SelectedOutChannel, dlg.SelectedInChannel);
    }
}
