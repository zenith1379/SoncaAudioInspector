using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    private AudioBenchPreferences _benchPreferences = new();
    private bool _preferencesReady;
    private readonly ConcurrentDictionary<ulong, BleDeviceInfo> _pendingDiscovery = new();
    private readonly DispatcherTimer _discoveryUiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private void InitializeBenchPreferences()
    {
        try { _benchPreferences = AudioBenchPreferences.Load(); }
        catch (Exception ex) { TxtBenchSettingsStatus.Text = ex.Message; }
        TxtBleNameFilter.Text = _benchPreferences.BleFilterByModel ? _benchPreferences.BleModelFilter : _benchPreferences.BleNameFilter;
        UpdateFilterModeUi();
        _preferencesReady = true;
        _discoveryUiTimer.Tick += (_, _) =>
        {
            bool changed = false;
            foreach (ulong address in _pendingDiscovery.Keys)
            {
                if (!_pendingDiscovery.TryRemove(address, out var info)) continue;
                int index = _deviceList.FindIndex(d => d.Address == address);
                if (index < 0) _deviceList.Add(info); else _deviceList[index] = info;
                changed = true;
            }
            if (changed) { UpdateDeviceCombo(); if (!_pairingBluetoothAudio) _ = TryAutoConnectAsync(); }
            UpdateSignalStrength();
        };
    }
    private void BleFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!_preferencesReady) return;
        _benchPreferences = _benchPreferences.BleFilterByModel
            ? _benchPreferences with { BleModelFilter = TxtBleNameFilter.Text.Trim() }
            : _benchPreferences with { BleNameFilter = TxtBleNameFilter.Text.Trim() };
        UpdateDeviceCombo();
        TxtBenchSettingsStatus.Text = "Bộ lọc xem trước; bấm Lưu để áp dụng cho tự kết nối.";
    }
    private void SaveBenchPreferences(object sender, RoutedEventArgs e)
    {
        if (IsAudioMeasurementBusy?.Invoke() == true) { TxtBenchSettingsStatus.Text = "Chờ đo xong để đổi cài đặt."; return; }
        try
        {
            var value = AudioBenchPreferences.Load() with { BleNameFilter = _benchPreferences.BleNameFilter,
                BleModelFilter = _benchPreferences.BleModelFilter, BleFilterByModel = _benchPreferences.BleFilterByModel };
            value.Save(); _benchPreferences = value; _nextKnownPeerLookup = default;
            TxtBenchSettingsStatus.Text = "Đã lưu chế độ lọc, tên BLE và model/ID trên máy này.";
        }
        catch (Exception ex) { TxtBenchSettingsStatus.Text = ex.Message; }
    }

    private void ToggleModelFilter()
    {
        _preferencesReady = false;
        _benchPreferences = _benchPreferences with { BleFilterByModel = !_benchPreferences.BleFilterByModel };
        TxtBleNameFilter.Text = _benchPreferences.BleFilterByModel ? _benchPreferences.BleModelFilter : _benchPreferences.BleNameFilter;
        _preferencesReady = true;
        UpdateFilterModeUi(); UpdateDeviceCombo();
        TxtBenchSettingsStatus.Text = _benchPreferences.BleFilterByModel
            ? "Đang lọc model/ID đã nhận diện. Bấm Dừng để tìm lại theo tên BLE."
            : "Đã dừng lọc model/ID; đang tìm theo tên BLE.";
    }

    private void UpdateFilterModeUi()
    {
        bool model = _benchPreferences.BleFilterByModel;
        BtnScan.Content = model ? "Dừng" : "Lọc";
        BtnScan.Background = model ? new SolidColorBrush(Color.FromRgb(153, 27, 27)) : new SolidColorBrush(Color.FromRgb(2, 132, 199));
        BtnScan.ToolTip = model ? "Dừng lọc model/ID, trở lại tìm theo tên BLE. Quét BLE vẫn tiếp tục." : "Lọc theo tên model hoặc ID từ model_profile.txt.";
        TxtBleFilterLabel.Text = model ? "Tên model / ID (ví dụ ACOUSTIC 3, 1071, 0x042F)" : "Tên BLE / Bluetooth (dấu phẩy để thêm tên)";
        TxtBleFilterHelp.Text = model ? "Chỉ hiện model/ID đã nhận diện có trong model_profile.txt. Loa chưa có INFO: bấm Dừng để tìm theo tên BLE và kết nối lần đầu."
            : "Tìm theo tên BLE/Bluetooth đang quảng bá. Bấm Lọc để chuyển sang tên model/ID đã nhận diện.";
    }
    private void UpdateSignalStrength()
    {
        ulong? address = _ble.ConnectedBluetoothAddress ?? (CmbDevices.SelectedItem as ComboBoxItem)?.Tag as ulong?;
        var info = _deviceList.FirstOrDefault(d => d.Address == address);
        double age = info == null ? double.PositiveInfinity : Math.Max(0, (DateTime.Now - info.LastSeen).TotalSeconds);
        bool valid = info is { Rssi: < 0 and >= -127 };
        int bars = valid && age <= 10 ? info!.Rssi >= -60 ? 4 : info.Rssi >= -75 ? 3 : info.Rssi >= -90 ? 2 : 1 : 0;
        BleSignalBar.Value = bars;
        BleSignalBar.Foreground = bars >= 3 ? Brushes.MediumSpringGreen : Brushes.Gold;
        TxtBleSignal.Text = valid ? $"RSSI quảng bá: {info!.Rssi} dBm · {(age > 10 ? $"mẫu cũ {age:F0}s" : bars >= 3 ? "mạnh / khá" : "yếu")}"
            : "RSSI: chưa có mẫu quảng bá hợp lệ";
        TxtBleSignal.ToolTip = "Ước lượng độ mạnh sóng từ gói quảng bá BLE, không phải chất lượng âm thanh. Khi loa ngừng quảng bá, mẫu sẽ cũ.";
    }
}
