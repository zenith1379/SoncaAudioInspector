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

        _sliderDebounceTimer.Tick += OnSliderDebounceTick;
        _audioRefreshTimer.Tick += async (_, _) => await RefreshAudioPeersAsync();

        InitializeComponent();
        UpdateConnectionUi(false, "BLE chưa kết nối — mở Cài đặt để chọn ngõ điều khiển.");

        Loaded += SoncaBleMixerWindow_Loaded;
        Closing += SoncaBleMixerWindow_Closing;
        PreviewKeyDown += SoncaBleMixerWindow_PreviewKeyDown;
    }

    public void SetTargetModel(string? modelKeyword)
    {
        _targetModelKeyword = modelKeyword?.Trim() ?? "";
        if (!string.IsNullOrEmpty(_targetModelKeyword))
        {
            TxtTargetModelIndicator.Text = $"Model mục tiêu: {_targetModelKeyword}";
        }
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

        // Cho phép nhận sự kiện slider sau khi UI đã nạp đầy đủ
        _suppressSliderEvents = false;
        _windowClosing = false;
        _audioRefreshTimer.Start();
        SetTargetModel(_targetModelKeyword);
        if (!_writer.IsConnected) { PanelSettings.Visibility = Visibility.Visible; _settingsOpenedForConnection = true; }
        foreach (var device in _ble.DiscoveredDevices) OnDeviceDiscovered(device);
        // Advertisement discovery and Windows association can run together.
        if (!_writer.IsConnected) try
        {
            _ble.StartScanning();
            BtnScan.Content = "⏹ Dừng";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
        }
        catch (Exception ex)
        {
            AppendLog($"Lỗi khởi động quét BLE: {ex.Message}");
        }
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        if (!_windowClosing) await RefreshAudioPeersAsync();
    }

    private void SoncaBleMixerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _windowClosing = true;
        _audioRefreshTimer.Stop();
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

        // CHÍNH SÁCH BẢO VỆ PHẦN CỨNG:
        // Chỉ cho phép tăng/giảm âm lượng của MIC trên app Sonca Mixer.
        // Tất cả các kênh khác (ECHO, REVERB, MUSIC, MASTER, LIVE) chỉ được bật/tắt tiếng,
        // nếu muốn tăng/giảm âm lượng bắt buộc phải chỉnh trực tiếp nút trên loa!
        if (sender == SliderMicVol)
        {
            TxtMicVal.Text = SliderMicVol.Value.ToString("F0");
            QueueCommand("MicVol", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMasterVol, SoncaBleMixerProfile.VolumeToRaw(SliderMicVol.Value), "Mic Vol");
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
            else if (!SliderMicVol.IsMouseCaptureWithin) RestoreKnownMicLevel();
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
        if (!_writer.IsConnected || !SoncaBleMixerProfile.TryMuteRegister(channelTag, out ushort register, out bool inverted))
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
        if (_ble.IsScanning)
        {
            _ble.StopScanning();
            BtnScan.Content = "🔍 Quét";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
        }
        else
        {
            _autoConnectEnabled = true;
            _ble.EnableAutomaticConnection();
            _autoAttempted.Clear();
            _ = RefreshAudioPeersAsync();
            _ble.StartScanning();
            BtnScan.Content = "⏹ Dừng";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
        }
    }

    private void OnDeviceDiscovered(BleDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            int index = _deviceList.FindIndex(d => d.Address == info.Address);
            if (index >= 0)
            {
                _deviceList[index] = info;
            }
            else
            {
                _deviceList.Add(info);
            }
            UpdateDeviceCombo();
            _ = TryAutoConnectAsync();
        });
    }

    private void UpdateDeviceCombo()
    {
        ulong? selectedAddress = (CmbDevices.SelectedItem as ComboBoxItem)?.Tag as ulong?;
        CmbDevices.Items.Clear();

        ComboBoxItem? selectedItem = null;

        foreach (var dev in _deviceList.OrderByDescending(d => d.Rssi))
        {
            string display = $"{dev.Name} ({dev.Rssi} dBm) [{dev.FormattedAddress}]";
            var item = new ComboBoxItem
            {
                Content = display,
                Tag = dev.Address,
                Foreground = Brushes.White
            };
            CmbDevices.Items.Add(item);

            if (dev.Address == selectedAddress || selectedAddress == null && selectedItem == null && IsMatchingModel(dev.Name))
            {
                selectedItem = item;
            }
        }

        if (selectedItem != null)
        {
            CmbDevices.SelectedItem = selectedItem;
        }
        else if (CmbDevices.Items.Count > 0 && CmbDevices.SelectedIndex == -1)
        {
            CmbDevices.SelectedIndex = 0;
        }
    }

    private bool IsMatchingModel(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(_targetModelKeyword)) return false;
        return BluetoothDeviceRemoval.MatchesModel(deviceName, _targetModelKeyword);
    }

    private void CmbDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnConnect.IsEnabled = CmbDevices.SelectedItem != null && !_ble.IsConnected && !_ble.IsConnecting;
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
        });
    }

    private void UpdateConnectionUi(bool connected, string message)
    {
        TxtStatus.Text = connected ? _writer.SupportsMixerControls ? "BLE ĐÃ KẾT NỐI — C360" : "BLE ĐÃ KẾT NỐI — chưa có profile điều khiển đã xác minh" : message;
        DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#10B981" : "#EF4444"));

        BtnConnect.IsEnabled = !connected && CmbDevices.SelectedItem != null && !_ble.IsConnecting;
        BtnDisconnect.IsEnabled = connected;
        SetControlAvailability(connected);
        if (connected)
        {
            BtnScan.Content = "🔍 Quét";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
        }
        if (!connected) ClearPendingCommands();
    }
    #endregion

    #region Tone Controls & Hardware Config
    private void SliderTone_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;

        if (sender == SliderMusicBass)
        {
            TxtMusicBassVal.Text = $"{SliderMusicBass.Value:+0;-0;0} dB";
            QueueCommand("MusicBass", SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicBass, (ushort)(SliderMusicBass.Value + 10), "Music Bass");
        }
        else if (sender == SliderMusicMid)
        {
            TxtMusicMidVal.Text = $"{SliderMusicMid.Value:+0;-0;0} dB";
            QueueCommand("MusicMid", SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicMid, (ushort)(SliderMusicMid.Value + 10), "Music Mid");
        }
        else if (sender == SliderMusicTreble)
        {
            TxtMusicTrebleVal.Text = $"{SliderMusicTreble.Value:+0;-0;0} dB";
            QueueCommand("MusicTreble", SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicTreble, (ushort)(SliderMusicTreble.Value + 10), "Music Treble");
        }
        else if (sender == SliderMicBass)
        {
            TxtMicBassVal.Text = $"{SliderMicBass.Value:+0;-0;0} dB";
            QueueCommand("MicBass", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicBass, (ushort)(SliderMicBass.Value + 10), "Mic Bass");
        }
        else if (sender == SliderMicMid)
        {
            TxtMicMidVal.Text = $"{SliderMicMid.Value:+0;-0;0} dB";
            QueueCommand("MicMid", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMid, (ushort)(SliderMicMid.Value + 10), "Mic Mid");
        }
        else if (sender == SliderMicTreble)
        {
            TxtMicTrebleVal.Text = $"{SliderMicTreble.Value:+0;-0;0} dB";
            QueueCommand("MicTreble", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicTreble, (ushort)(SliderMicTreble.Value + 10), "Mic Treble");
        }
    }

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
