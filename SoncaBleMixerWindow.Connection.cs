using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    public static readonly DependencyProperty IsPortInUseProperty = DependencyProperty.RegisterAttached(
        "IsPortInUse", typeof(bool), typeof(SoncaBleMixerWindow), new PropertyMetadata(false));
    public static bool GetIsPortInUse(DependencyObject element) => (bool)element.GetValue(IsPortInUseProperty);
    public static void SetIsPortInUse(DependencyObject element, bool value) => element.SetValue(IsPortInUseProperty, value);

    public Func<bool>? IsAudioMeasurementBusy { get; set; }
    private IReadOnlyList<BluetoothAudioPeer> _audioPeers = Array.Empty<BluetoothAudioPeer>();
    private bool _windowClosing, _autoConnectEnabled = true, _settingsOpenedForConnection;
    private bool _refreshingAudio, _readingMixer;
    private int _commandEpoch;
    private readonly System.Windows.Threading.DispatcherTimer _audioRefreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<ulong, DateTime> _autoAttempted = new();
    private readonly HashSet<string> _knownMuteChannels = new(), _busyMuteChannels = new();
    private ushort? _knownMicLevel;
    private bool _hasLiveMasterLevel;
    private byte? _knownInputSource;
    private ushort? _inputCapabilities;
    private int? _inputSwitchEpoch;
    private string? _inputSourceFailure;
    private byte? _lastRequestedInputSource;
    private bool _findingAutoPeer;
    private DateTime _nextKnownPeerLookup;
    internal Func<Task<IReadOnlyList<BluetoothAudioPeer>>> ReadConnectedAudioPeers = SoncaBleManager.ConnectedAudioPeersAsync;

    private async Task RefreshAudioPeersAsync()
    {
        if (_refreshingAudio || _windowClosing) return;
        _refreshingAudio = true;
        try
        {
            // The mixer snapshot does not depend on Windows Bluetooth enumeration.
            Task mixerRead = ReadMixerStateAsync();
            _audioPeers = await Task.Run(ReadConnectedAudioPeers);
            if (_windowClosing) return;
            TxtAudioConnection.Text = _audioPeers.Count > 0 ? "Bluetooth Windows: " + string.Join(", ", _audioPeers.Select(p => p.Name))
                : "Bluetooth Windows: chưa thấy loa Sonca đang kết nối.";
            Task connection = TryAutoConnectAsync();
            var windowsLevels = await Task.Run(() => SoncaBluetoothPlaybackState.Read(_audioPeers));
            if (_windowClosing) return;
            TxtPlaybackLevel.Text = windowsLevels.Count > 0 ? "Windows BT: " + string.Join(" / ", windowsLevels.Select(p => $"{p.Percent:F0}%{(p.Muted ? " (mute)" : "")}")) : "Windows BT: —";
            if (windowsLevels.Count > 0) TxtAudioConnection.Text += "\nÂm lượng phát Windows: " + string.Join("; ", windowsLevels.Select(p => $"{p.Name}: {p.Percent:F0}%{(p.Muted ? " (mute)" : "")}"));
            await connection;
            if (_windowClosing) return;
            await mixerRead;
            if (_writer.IsConnected && _writer.SupportsMixerControls) await ReadMixerStateAsync();
            UpdateInputSourceUi(_writer.IsConnected);
        }
        catch (Exception ex) { AppendLog("Không đọc được kết nối Bluetooth Windows: " + ex.Message); }
        finally { _refreshingAudio = false; }
    }

    private async Task TryAutoConnectAsync()
    {
        if (_windowClosing || !_autoConnectEnabled || _ble.AutomaticConnectionSuppressed || _ble.IsConnecting || _findingAutoPeer || _audioPeers.Count == 0
            || IsAudioMeasurementBusy?.Invoke() == true) return;
        if (_ble.IsConnected && _ble.ConnectedBluetoothAddress is ulong current && SoncaBleDiscovery.MatchesAudioPeer(current, _audioPeers)) return;
        _findingAutoPeer = true;
        try
        {
            if (DateTime.UtcNow < _nextKnownPeerLookup) return;
            _nextKnownPeerLookup = DateTime.UtcNow.AddSeconds(15);
            BtnConnect.IsEnabled = BtnScan.IsEnabled = false;
            bool connected = await _ble.EnsureConnectedAsync(connectedPeers: _audioPeers);
            if (_windowClosing) return;
            BtnScan.IsEnabled = true;
            BtnConnect.IsEnabled = !connected && CmbDevices.SelectedItem != null;
            if (connected) await ReadMixerStateAsync();
        }
        finally { _findingAutoPeer = false; }
    }

    private void ClearPendingCommands()
    {
        _sliderDebounceTimer.Stop();
        lock (_pendingCommands) _pendingCommands.Clear();
    }

    private void SetControlAvailability(bool connected)
    {
        // Old tone/preset addresses were guesses; keep these unavailable
        // until a device profile validates their encoding and readback.
        foreach (FrameworkElement control in new FrameworkElement[] {
            BtnPreset, SliderMusicBass, SliderMusicMid, SliderMusicTreble,
            SliderMicBass, SliderMicMid, SliderMicTreble, BtnSaveSpeakerConfig, BtnResetSpeakerConfig })
        {
            control.IsEnabled = false;
            control.Opacity = 0.4;
            control.ToolTip = "Chức năng này chưa được xác minh cho firmware đang nối.";
            if (control is CheckBox toggle) toggle.IsChecked = null;
            if (control is RadioButton input) input.IsChecked = false;
        }
        if (!connected)
        {
            _lastMixerReadUtc = default;
            _commandEpoch++;
            _knownMuteChannels.Clear(); _knownMicLevel = null;
            _hasLiveMasterLevel = false;
            _knownInputSource = null; _inputCapabilities = null; _inputSwitchEpoch = null; _inputSourceFailure = null;
            _lastRequestedInputSource = null;
            SliderMasterVol.Visibility = Visibility.Hidden;
            foreach (var text in new[] { TxtMicVal, TxtMusicVal, TxtMasterVal, TxtEchoVal, TxtReverbVal, TxtLiveVal }) text.Text = "—";
            foreach (var channel in _channelMuted.Keys.ToArray()) _channelMuted[channel] = false;
            foreach (var button in new[] { BtnMuteMic, BtnMuteMusic, BtnMuteMaster, BtnMuteEcho, BtnMuteReverb, BtnMuteLive }) button.Content = "?";
        }
        TxtAppBuild.Text = SoncaBuildIdentity.Current;
        TxtFirmwareIdentity.Text = _ble.IsConnected ? _ble.FirmwareIdentity : "Firmware loa: chưa kết nối BLE";
        TxtFirmwareIdentity.ToolTip = TxtFirmwareIdentity.Text;
        foreach (var pair in new[] { (SoncaMixerOption.MicFbx, ChkFbx), (SoncaMixerOption.BassBoost, ChkBassBoost), (SoncaMixerOption.Enhancer, ChkEnhancer) })
        {
            bool known = connected && _writer.SupportsMixerControls && _inputCapabilities.HasValue && SoncaBleMixerProfile.SupportsOption(_inputCapabilities.Value, pair.Item1);
            pair.Item2.IsEnabled = known && !_settingOption && IsAudioMeasurementBusy?.Invoke() != true;
            pair.Item2.IsChecked = known ? (_inputCapabilities!.Value & SoncaBleMixerProfile.OptionMask(pair.Item1)) != 0 : null;
            pair.Item2.Opacity = known ? 1 : .4;
            pair.Item2.ToolTip = known ? "Bật/tắt và xác nhận lại từ loa." : "Loa chưa báo hỗ trợ hoặc chưa đọc được trạng thái tùy chọn.";
        }
        SliderMicVol.IsEnabled = IsAudioMeasurementBusy?.Invoke() != true && connected && _writer.SupportsMixerControls && _knownMicLevel.HasValue;
        UpdateInputSourceUi(connected);
        foreach (var pair in new[] { ("Mic", BtnMuteMic), ("Music", BtnMuteMusic), ("Master", BtnMuteMaster), ("Echo", BtnMuteEcho), ("Reverb", BtnMuteReverb), ("Live", BtnMuteLive) })
        {
            pair.Item2.IsEnabled = IsAudioMeasurementBusy?.Invoke() != true && connected && _writer.SupportsMixerControls && _knownMuteChannels.Contains(pair.Item1) && !_busyMuteChannels.Contains(pair.Item1);
            pair.Item2.ToolTip = pair.Item2.IsEnabled ? "Tắt/bật tiếng; giữ nguyên mức âm lượng của loa."
                : "Chưa đọc được trạng thái điều khiển từ loa.";
        }
    }

    private async Task ReadMixerStateAsync()
    {
        if (_windowClosing || !_writer.IsConnected || _readingMixer || DateTime.UtcNow - _lastMixerReadUtc < TimeSpan.FromSeconds(2)) return;
        int epoch = _commandEpoch;
        _readingMixer = true;
        try
        {
            if (await _writer.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Đọc trạng thái mixer"))
            {
                if (epoch == _commandEpoch) _lastMixerReadUtc = DateTime.UtcNow;
            }
            else
                AppendLog("Chưa đọc được trạng thái mixer; bấm Quét/Kết nối để thử lại.");
        }
        catch (Exception ex) { if (!_windowClosing) AppendLog("Đọc mixer: " + ex.Message); }
        finally { _readingMixer = false; }
    }

    private void OnMixerStateReceived(IReadOnlyDictionary<ushort, ushort> state)
    {
        int epoch = _commandEpoch;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_windowClosing || epoch != _commandEpoch || !_writer.IsConnected) return;
            ApplyMixerSnapshot(state, liveUpdate: false);
            if (_settingsOpenedForConnection && _knownMuteChannels.Count > 0)
            { PanelSettings.Visibility = Visibility.Collapsed; _settingsOpenedForConnection = false; }
        }));
    }

    private void OnMixerParametersReceived(IReadOnlyDictionary<ushort, ushort> updates)
    {
        int epoch = _commandEpoch;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_windowClosing || epoch != _commandEpoch || !_writer.IsConnected) return;
            ApplyMixerSnapshot(updates, liveUpdate: true);
        }));
    }

    private void ApplyParameterReadback(ushort register, ushort value) => ApplyParameterReadbackCore(register, value, liveUpdate: false);

    private void ApplyParameterReadbackCore(ushort register, ushort value, bool liveUpdate, bool refreshUi = true)
    {
        if (_windowClosing) return;
        if (register == SoncaBleMixerProfile.InputSource)
        {
            byte? source = SoncaBleMixerProfile.TryReadInputSource(value, out byte decoded) ? decoded : null;
            if (source != _knownInputSource && !_inputSwitchEpoch.HasValue)
            {
                if (source.HasValue && _knownInputSource.HasValue && _lastRequestedInputSource == _knownInputSource)
                    _inputSourceFailure = $"Loa chuyển về {SoncaBleMixerProfile.InputSourceName(source.Value)}; không giữ ngõ {SoncaBleMixerProfile.InputSourceName(_knownInputSource.Value)}.";
                else _inputSourceFailure = null;
            }
            _knownInputSource = source;
        }
        if (register == SoncaBleMixerProfile.InputCapabilities) _inputCapabilities = value;
        if (register == SoncaBleMixerProfile.MasterVolume && !liveUpdate)
        {
            if (_hasLiveMasterLevel) return; // Do not overwrite MCU updates with a config cache.
            if (value == 0)
            {
                TxtMasterVal.Text = "—";
                TxtMasterVal.ToolTip = "Chưa có mức Master đồng bộ từ loa. Giá trị cấu hình 0 chưa xác nhận vị trí nút Master.";
                SliderMasterVol.Visibility = Visibility.Hidden;
                return;
            }
        }
        var volume = register switch
        {
            SoncaBleMixerProfile.MicVolume => (SliderMicVol, TxtMicVal), SoncaBleMixerProfile.MusicVolume => (SliderMusicVol, TxtMusicVal),
            SoncaBleMixerProfile.MasterVolume => (SliderMasterVol, TxtMasterVal), SoncaBleMixerProfile.EchoVolume => (SliderEcho, TxtEchoVal),
            SoncaBleMixerProfile.ReverbVolume => (SliderReverb, TxtReverbVal), SoncaBleMixerProfile.LiveVolume => (SliderLiveVol, TxtLiveVal), _ => (null, null)
        };
        if (volume.Item1 != null && value <= 8192)
        {
            bool deferMic = register == SoncaBleMixerProfile.MicVolume
                && (_sendingSliderCommand || _pendingCommands.ContainsKey("MicVol") || SliderMicVol.IsMouseCaptureWithin);
            if (register == SoncaBleMixerProfile.MicVolume) _knownMicLevel = value;
            if (!deferMic)
            {
                _suppressSliderEvents = true;
                volume.Item1.Maximum = 100;
                volume.Item1.Value = SoncaBleMixerProfile.VolumeFromRaw(value);
                volume.Item2!.Text = volume.Item1.Value.ToString("F0");
                if (register == SoncaBleMixerProfile.MasterVolume)
                {
                    SliderMasterVol.Visibility = Visibility.Visible;
                    TxtMasterVal.ToolTip = liveUpdate ? "Mức Master cập nhật từ loa." : "Mức Master trong cấu hình loa.";
                }
                _suppressSliderEvents = false;
            }
        }
        foreach (var pair in new[] { ("Mic", BtnMuteMic), ("Music", BtnMuteMusic), ("Master", BtnMuteMaster), ("Echo", BtnMuteEcho), ("Reverb", BtnMuteReverb), ("Live", BtnMuteLive) })
        {
            if (!SoncaBleMixerProfile.TryMuteRegister(pair.Item1, out ushort muteRegister, out bool inverted) || muteRegister != register || value > 1) continue;
            bool muted = inverted ? value == 0 : value == 1;
            _knownMuteChannels.Add(pair.Item1); _channelMuted[pair.Item1] = muted;
            pair.Item2.Content = muted ? "🔇" : "🔊";
            pair.Item2.Foreground = muted ? Brushes.IndianRed : Brushes.LightGreen;
        }
        if (refreshUi) SetControlAvailability(_writer.IsConnected);
    }

    private void RestoreKnownMicLevel()
    {
        if (_knownMicLevel.HasValue) ApplyParameterReadback(SoncaBleMixerProfile.MicVolume, _knownMicLevel.Value);
    }

    private RadioButton[] InputSourceButtons() => new[]
        { RadioInputBt, RadioInputUsb, RadioInputOpt, RadioInputHdmi, RadioInputLineIn, RadioInputKaraoke };

    private void UpdateInputSourceUi(bool connected)
    {
        bool suppress = _suppressSliderEvents;
        _suppressSliderEvents = true;
        bool controlsReady = connected && _writer.SupportsMixerControls;
        try
        {
            foreach (var button in InputSourceButtons())
            {
                byte source = byte.Parse((string)button.Tag);
                bool selectable = _inputCapabilities.HasValue && SoncaBleMixerProfile.SupportsInputSource(_inputCapabilities.Value, source);
                button.IsChecked = controlsReady && _knownInputSource == source;
                bool inUse = controlsReady && _knownInputSource == source && (source != 0
                    || _ble.ConnectedBluetoothAddress is ulong address && SoncaBleDiscovery.MatchesAudioPeer(address, _audioPeers));
                SetIsPortInUse(button, inUse);
                button.IsEnabled = controlsReady && _knownInputSource.HasValue && selectable && !_inputSwitchEpoch.HasValue
                    && IsAudioMeasurementBusy?.Invoke() != true;
                button.Opacity = controlsReady && selectable ? 1 : .4;
                button.ToolTip = !controlsReady ? "Kết nối BLE và đọc trạng thái loa để chuyển ngõ."
                    : !selectable ? "Loa chưa báo cho phép chọn ngõ này."
                    : inUse ? "Ngõ vào (IN) được xác nhận đang dùng."
                    : "Loa cho phép chọn ngõ này; bấm để chuyển và xác nhận trạng thái.";
            }
        }
        finally { _suppressSliderEvents = suppress; }
        if (!_inputSwitchEpoch.HasValue)
        {
            TxtInputSourceStatus.Text = controlsReady && _knownInputSource.HasValue
                ? "Đang dùng: " + SoncaBleMixerProfile.InputSourceName(_knownInputSource.Value) : "Chưa đọc được ngõ đang dùng.";
            if (_inputSourceFailure != null) TxtInputSourceStatus.Text += "\n" + _inputSourceFailure;
            else if (controlsReady && _inputCapabilities.HasValue)
                TxtInputSourceStatus.Text += "\nCó thể chọn: " + string.Join(", ", Enumerable.Range(0, 6).Select(i => (byte)i)
                    .Where(i => SoncaBleMixerProfile.SupportsInputSource(_inputCapabilities.Value, i)).Select(SoncaBleMixerProfile.InputSourceName)) + ".";
            TxtInputSourceStatus.Foreground = controlsReady && _knownInputSource.HasValue && _inputSourceFailure == null ? Brushes.LightGreen : Brushes.Gold;
        }
    }

    private async Task SwitchInputSourceAsync(byte source)
    {
        bool available = _inputCapabilities.HasValue && SoncaBleMixerProfile.SupportsInputSource(_inputCapabilities.Value, source);
        // Checked fires before the command completes. Restore the last readback
        // so a failed write never appears to have changed the physical source.
        UpdateInputSourceUi(_writer.IsConnected);
        if (!_writer.IsConnected || !_writer.SupportsMixerControls || !_knownInputSource.HasValue
            || !available || _inputSwitchEpoch.HasValue || IsAudioMeasurementBusy?.Invoke() == true || _knownInputSource.Value == source) return;
        int epoch = _commandEpoch;
        _inputSourceFailure = null;
        _lastRequestedInputSource = source;
        _inputSwitchEpoch = epoch;
        UpdateInputSourceUi(true);
        string name = SoncaBleMixerProfile.InputSourceName(source);
        TxtInputSourceStatus.Text = "Đang chuyển sang " + name + "…";
        TxtInputSourceStatus.Foreground = Brushes.DeepSkyBlue;
        try
        {
            bool confirmed = await _writer.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(source), "Chuyển ngõ nhạc: " + name);
            if (_windowClosing || epoch != _commandEpoch || !_writer.IsConnected) return;
            if (confirmed)
            {
                _knownInputSource = source;
                AppendLog("Loa đã xác nhận chuyển ngõ nhạc sang " + name + ".");
            }
            else
            {
                await ReadMixerStateAsync();
                if (epoch != _commandEpoch || _windowClosing) return;
                _inputSourceFailure = "Chưa xác nhận giữ ngõ " + name + (_knownInputSource.HasValue ? "; loa đang ở " + SoncaBleMixerProfile.InputSourceName(_knownInputSource.Value) : "") + ".";
                AppendLog("Chưa xác nhận chuyển ngõ sang " + name + "; hiển thị ngõ đọc lại từ loa.");
            }
        }
        catch (Exception ex)
        {
            if (epoch == _commandEpoch && !_windowClosing)
            {
                _inputSourceFailure = "Không chuyển được sang " + name + "; thử lại.";
                AppendLog("Không chuyển được ngõ: " + ex.Message);
            }
        }
        finally
        {
            if (epoch == _commandEpoch && !_windowClosing)
            {
                _inputSwitchEpoch = null;
                UpdateInputSourceUi(_writer.IsConnected);
            }
        }
    }
}
