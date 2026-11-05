using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoncaAudioInspector;

/// <summary>
/// Bàn điều khiển phần cứng loa Sonca Mixer Studio Console (tập trung 1 cửa sổ theo phong cách ứng dụng Sonca Mixer di động).
/// Mở bằng tổ hợp phím Ctrl + Shift + H.
/// </summary>
public partial class SoncaBleMixerWindow : Window
{
    private readonly SoncaBleManager _ble = SoncaBleManager.Instance;
    private readonly ISoncaMixerWriter _writer;
    private readonly List<BleDeviceInfo> _deviceList = new();
    private readonly DispatcherTimer _sliderDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(75)
    };
    private readonly Dictionary<string, (byte channel, ushort reg, ushort val, string name)> _pendingCommands = new();
    private bool _suppressSliderEvents = true; // Chặn các sự kiện slider nạp ban đầu từ XAML
    private bool _sendingSliderCommand;
    private string _targetModelKeyword = "";

    // Channel mute tracking
    private readonly Dictionary<string, bool> _channelMuted = new()
    {
        ["Mic"] = false,
        ["Echo"] = false,
        ["Reverb"] = false,
        ["Music"] = false,
        ["Master"] = false,
        ["Live"] = false
    };

    public SoncaBleMixerWindow(string? modelKeyword = null) : this(modelKeyword, SoncaBleManager.Instance) { }

    internal SoncaBleMixerWindow(string? modelKeyword, ISoncaMixerWriter writer)
    {
        _writer = writer;
        _suppressSliderEvents = true;
        _targetModelKeyword = modelKeyword?.Trim() ?? "";
        InitializeBluetoothAudioConnection();

        _sliderDebounceTimer.Tick += OnSliderDebounceTick;
        _audioRefreshTimer.Tick += async (_, _) => await RefreshAudioPeersAsync();

        InitializeComponent();
        InitializeBenchPreferences();
        UpdateConnectionUi(false, "BLE chưa kết nối — mở Cài đặt để chọn ngõ điều khiển.");

        Loaded += SoncaBleMixerWindow_Loaded;
        Closing += SoncaBleMixerWindow_Closing;
        PreviewKeyDown += SoncaBleMixerWindow_PreviewKeyDown;
    }

    public void SetTargetModel(string? modelKeyword)
    {
        _targetModelKeyword = modelKeyword?.Trim() ?? "";
        TxtTargetModelIndicator.Text = string.IsNullOrEmpty(_targetModelKeyword) ? "Model mục tiêu: —" : $"Model mục tiêu: {_targetModelKeyword}";
        UpdateDeviceCombo();
    }

    private async void SoncaBleMixerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.ConnectionStatusChanged += OnConnectionStatusChanged;
        _ble.LogMessage += AppendLog;
        _ble.MixerStateReceived += OnMixerStateReceived;
        _ble.MixerParametersReceived += OnMixerParametersReceived;

        UpdateConnectionUi(_ble.IsConnected, _ble.IsConnected ? $"Đã kết nối: {_ble.ConnectedDeviceName}" : "Chưa kết nối");
        if (_writer.IsConnected && _writer.MixerState.Count > 0) ApplyMixerSnapshot(_writer.MixerState, liveUpdate: false);

        // Cho phép nhận sự kiện slider sau khi UI đã nạp đầy đủ
        _suppressSliderEvents = false;
        _windowClosing = false;
        _audioRefreshTimer.Start(); _discoveryUiTimer.Start();
        SetTargetModel(_targetModelKeyword);
        if (!_writer.IsConnected) { PanelSettings.Visibility = Visibility.Visible; _settingsOpenedForConnection = true; }
        foreach (var device in _ble.DiscoveredDevices) OnDeviceDiscovered(device);
        // Advertisement discovery and Windows association can run together.
        try
        {
            _ble.StartScanning();
            UpdateFilterModeUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Lỗi khởi động quét BLE: {ex.Message}");
        }
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (!_windowClosing) await RefreshAudioPeersAsync();
        if (!_windowClosing && _writer.IsConnected) _ = EnsureBluetoothAudioAsync();
    }

    private void SoncaBleMixerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _windowClosing = true;
        CancelBluetoothAudioConnection();
        _audioRefreshTimer.Stop(); _discoveryUiTimer.Stop();
        _ble.DeviceDiscovered -= OnDeviceDiscovered;
        _ble.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _ble.LogMessage -= AppendLog;
        _ble.MixerStateReceived -= OnMixerStateReceived;
        _ble.MixerParametersReceived -= OnMixerParametersReceived;
        _ble.StopScanning();
        _sliderDebounceTimer.Stop();
        ClearPendingCommands();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #region Fader Sliders & Parameter Debouncing
    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;

        foreach (var control in VolumeControls())
        {
            if (!ReferenceEquals(sender, control.Slider) || !control.Slider.IsEnabled
                || IsAudioMeasurementBusy?.Invoke() == true || !_knownVolumes.ContainsKey(control.Register)) continue;
            control.Text.Text = control.Slider.Value.ToString("F0");
            QueueCommand(control.Key, 0, control.Register, SoncaBleMixerProfile.VolumeToRaw(control.Slider.Value), control.Label);
            break;
        }
    }

    private void QueueCommand(string key, byte channel, ushort reg, ushort val, string name)
    {
        if (!_writer.IsConnected || !_writer.SupportsMixerControls) return;
        lock (_pendingCommands)
        {
            _pendingCommands[key] = (channel, reg, val, name);
        }
        _sliderDebounceTimer.Stop();
        _sliderDebounceTimer.Start();
    }

    private async void OnSliderDebounceTick(object? sender, EventArgs e)
    {
        _sliderDebounceTimer.Stop();
        if (_sendingSliderCommand) { _sliderDebounceTimer.Start(); return; }
        if (!_writer.IsConnected || IsAudioMeasurementBusy?.Invoke() == true) { ClearPendingCommands(); return; }

        List<(byte channel, ushort reg, ushort val, string name)> toSend;
        lock (_pendingCommands)
        {
            toSend = _pendingCommands.Values.ToList();
            _pendingCommands.Clear();
        }

        _sendingSliderCommand = true;
        try
        {
            int epoch = _commandEpoch;
            foreach (var cmd in toSend)
            {
                if (epoch != _commandEpoch || !_writer.IsConnected || IsAudioMeasurementBusy?.Invoke() == true) return;
                byte[] packet = SoncaBleProtocol.BuildParamPacket(cmd.channel, cmd.reg, cmd.val);
                if (!await _writer.SendPacketAsync(packet, $"{cmd.name}: {cmd.val}"))
                {
                    AppendLog($"Loa chưa xác nhận {cmd.name}.");
                    if (epoch == _commandEpoch) await ReadMixerStateAsync();
                }
            }
        }
        finally
        {
            _sendingSliderCommand = false;
            bool pending; lock (_pendingCommands) pending = _pendingCommands.Count > 0;
            if (pending && _writer.IsConnected) _sliderDebounceTimer.Start();
            else RestoreKnownLevels();
        }
    }
    #endregion

    #region Channel Mute Buttons
    private async void BtnMuteChannel_Click(object sender, RoutedEventArgs e)
    {
        if (IsAudioMeasurementBusy?.Invoke() == true || sender is not Button btn || btn.Tag is not string channelTag) return;

        if (!_knownMuteChannels.Contains(channelTag) || !_writer.SupportsMixerControls || !_busyMuteChannels.Add(channelTag)) return;
        bool currentMute = _channelMuted.TryGetValue(channelTag, out bool isMuted) && isMuted;
        bool newMute = !currentMute;
        if (!_writer.IsConnected || !_writer.TryMuteRegister(channelTag, out ushort register, out bool inverted))
        { _busyMuteChannels.Remove(channelTag); return; }
        byte[] packet = SoncaBleProtocol.BuildParamPacket(0, register, (ushort)((inverted ? !newMute : newMute) ? 1 : 0));
        btn.IsEnabled = false;
        int commandEpoch = _commandEpoch;
        try
        {
            if (await _writer.SendPacketAsync(packet, $"{channelTag} Mute: {newMute}") && _writer.IsConnected && commandEpoch == _commandEpoch)
            {
                _channelMuted[channelTag] = newMute;
                btn.Content = newMute ? "🔇" : "🔊";
                btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(newMute ? "#EF4444" : "#10B981"));
                AppendLog($"Loa đã xác nhận {channelTag} {(newMute ? "mute" : "unmute")} bằng cấu hình đọc lại.");
            }
            else AppendLog($"Gửi {channelTag} mute thất bại; giữ nguyên trạng thái nút.");
        }
        finally { _busyMuteChannels.Remove(channelTag); SetControlAvailability(_writer.IsConnected); }
    }
    #endregion

    #region Input Sources & Toggles
    private async void RadioInput_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tagStr || !byte.TryParse(tagStr, out byte sourceIdx))
            return;

        await SwitchInputSourceAsync(sourceIdx);
    }

    private bool _settingOption;
    private async void ChkFbx_Click(object sender, RoutedEventArgs e) => await ChangeOptionAsync(SoncaMixerOption.MicFbx, ChkFbx.IsChecked == true);
    private async void ChkBassBoost_Click(object sender, RoutedEventArgs e) => await ChangeOptionAsync(SoncaMixerOption.BassBoost, ChkBassBoost.IsChecked == true);
    private async void ChkEnhancer_Click(object sender, RoutedEventArgs e) => await ChangeOptionAsync(SoncaMixerOption.Enhancer, ChkEnhancer.IsChecked == true);
    private async Task ChangeOptionAsync(SoncaMixerOption option, bool enabled)
    {
        SetControlAvailability(_writer.IsConnected);
        if (_settingOption || IsAudioMeasurementBusy?.Invoke() == true || !_writer.IsConnected || !_writer.SupportsMixerControls
            || !_inputCapabilities.HasValue || !SoncaBleMixerProfile.SupportsOption(_inputCapabilities.Value, option)) return;
        int epoch = _commandEpoch; _settingOption = true; SetControlAvailability(true);
        try
        {
            bool confirmed = await _writer.SetOptionAsync(option, enabled);
            if (epoch != _commandEpoch || _windowClosing || !_writer.IsConnected) return;
            if (_writer.MixerState.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort flags)) _inputCapabilities = flags;
            AppendLog(confirmed ? $"{option}: loa đã xác nhận trạng thái." : $"{option}: chưa xác nhận, giữ trạng thái đọc lại từ loa.");
        }
        catch (Exception ex) { AppendLog("Tùy chọn: " + ex.Message); }
        finally { _settingOption = false; if (epoch == _commandEpoch && !_windowClosing) SetControlAvailability(_writer.IsConnected); }
    }
    #endregion

    #region Presets
    private void BtnPreset_Click(object sender, RoutedEventArgs e)
    {
        if (BtnPreset.ContextMenu != null)
        {
            BtnPreset.ContextMenu.PlacementTarget = BtnPreset;
            BtnPreset.ContextMenu.IsOpen = true;
        }
    }

    private void MenuPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string presetTag) return;

        BtnPreset.Content = $"{mi.Header.ToString()?.ToUpperInvariant()} ▾";

        _suppressSliderEvents = true;
        switch (presetTag)
        {
            case "Acoustic":
                SliderMicVol.Value = 50;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = true;
                break;
            case "Karaoke":
                SliderMicVol.Value = 60;
                ChkBassBoost.IsChecked = true;
                ChkEnhancer.IsChecked = true;
                break;
            case "Stage":
                SliderMicVol.Value = 65;
                ChkBassBoost.IsChecked = true;
                ChkEnhancer.IsChecked = false;
                break;
            case "Dance":
                SliderMicVol.Value = 50;
                ChkBassBoost.IsChecked = true;
                ChkEnhancer.IsChecked = true;
                break;
            case "Bolero":
                SliderMicVol.Value = 65;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = false;
                break;
            case "Pop":
                SliderMicVol.Value = 55;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = true;
                break;
            case "Flat":
                SliderMicVol.Value = 50;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = false;
                break;
        }

        // Cập nhật text hiển thị MIC
        TxtMicVal.Text = SliderMicVol.Value.ToString("F0");

        _suppressSliderEvents = false;

        // Chỉ gửi âm lượng MIC và các toggle (các kênh khác bắt buộc vặn trực tiếp nút cơ trên loa)
        QueueCommand("MicVol", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMasterVol, (ushort)SliderMicVol.Value, "Mic Vol");
        if (_writer.IsConnected)
        {
            _ = _writer.SendPacketAsync(SoncaBleProtocol.BuildBassBoostPacket(ChkBassBoost.IsChecked == true), "Bass Boost");
            _ = _writer.SendPacketAsync(SoncaBleProtocol.BuildEnhancerPacket(ChkEnhancer.IsChecked == true), "Enhancer");
        }
    }
    #endregion

    #region Settings Drawer & Bluetooth Management
    private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpenedForConnection = false;
        PanelSettings.Visibility = PanelSettings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        ToggleModelFilter();
        if (IsLoaded && !_ble.IsScanning && IsAudioMeasurementBusy?.Invoke() != true)
        {
            try { _ble.StartScanning(); }
            catch (Exception ex) { AppendLog("Quét BLE: " + ex.Message); }
        }
    }

    private void OnDeviceDiscovered(BleDeviceInfo info)
    {
        _pendingDiscovery[info.Address] = info;
    }

    private void CmbDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnConnect.IsEnabled = CmbDevices.SelectedItem != null && !_ble.IsConnected && !_ble.IsConnecting;
        RefreshModelIdentity();
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDevices.SelectedItem is not ComboBoxItem selected || selected.Tag is not ulong address)
        {
            MessageBox.Show("Vui lòng chọn thiết bị trong danh sách.", "Chưa chọn thiết bị", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnConnect.IsEnabled = false;
        BtnScan.IsEnabled = false;
        _autoConnectEnabled = false;

        _ble.EnableAutomaticConnection();
        bool success = await Task.Run(() => _ble.ConnectAsync(address));

        BtnScan.IsEnabled = true;
        BtnConnect.IsEnabled = !success;
        if (success) await ReadMixerStateAsync();
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _autoConnectEnabled = false;
        ClearPendingCommands();
        _ble.SuppressAutomaticConnection();
    }

    private void OnConnectionStatusChanged(bool connected, string message)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateConnectionUi(connected, message);
            if (connected) _ = EnsureBluetoothAudioAsync();
            else CancelBluetoothAudioConnection(forgetAttempt: true);
        });
    }

    private void UpdateConnectionUi(bool connected, string message)
    {
        TxtStatus.Text = connected ? "BLE ĐÃ KẾT NỐI" : message;
        DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#10B981" : "#EF4444"));

        BtnConnect.IsEnabled = !connected && CmbDevices.SelectedItem != null && !_ble.IsConnecting;
        BtnDisconnect.IsEnabled = connected;
        BtnRetryBluetoothAudio.IsEnabled = connected && !_pairingBluetoothAudio && IsAudioMeasurementBusy?.Invoke() != true;
        SetControlAvailability(connected);
        try { _benchPreferences = AudioBenchPreferences.Load() with { BleNameFilter = _benchPreferences.BleNameFilter,
            BleModelFilter = _benchPreferences.BleModelFilter, BleFilterByModel = _benchPreferences.BleFilterByModel }; }
        catch (Exception ex) { AppendLog("Đọc model đã lưu: " + ex.Message); }
        UpdateDeviceCombo();
        UpdateFilterModeUi();
        if (!connected) ClearPendingCommands();
    }
    #endregion

    #region Tone Controls & Hardware Config
    private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!_writer.IsConnected)
        {
            MessageBox.Show("Vui lòng kết nối Bluetooth LE trước.", "Chưa kết nối", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        byte[] packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelMusic, 0x6001, 1);
        bool ok = await _writer.SendPacketAsync(packet, "Lưu cấu hình vào EEPROM");
        if (ok)
        {
            MessageBox.Show("Đã gửi lệnh lưu thông số vào bộ nhớ loa (EEPROM) thành công!", "Lưu cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (!_writer.IsConnected)
        {
            MessageBox.Show("Vui lòng kết nối Bluetooth LE trước.", "Chưa kết nối", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var res = MessageBox.Show("Khôi phục toàn bộ thông số phần cứng về mặc định ban đầu?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            byte[] packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelMusic, 0x6003, 1);
            await _writer.SendPacketAsync(packet, "Khôi phục cấu hình gốc");
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtTrafficLog.Clear();
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            if (TxtTrafficLog.Text.Length > 20000)
            {
                TxtTrafficLog.Text = TxtTrafficLog.Text.Substring(10000);
            }
            TxtTrafficLog.AppendText(line + Environment.NewLine);
            TxtTrafficLog.ScrollToEnd();
        });
    }
    #endregion

    #region Shortcut Handling
    private void SoncaBleMixerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (PanelSettings.Visibility == Visibility.Visible)
            {
                PanelSettings.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            Close();
            e.Handled = true;
            return;
        }

        if (MainWindow.TryHandleShortcutsHelpShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, OpenShortcutsHelp))
        {
            e.Handled = true;
        }
    }

    private void OpenShortcutsHelp()
    {
        var helpWindow = new ShortcutsHelpWindow
        {
            Owner = this
        };
        helpWindow.ShowDialog();
    }
    #endregion
}
