using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    private bool _refreshingModelCatalogue;
    private SoncaBleDeviceIdentity IdentityFor(BleDeviceInfo device) => SoncaBleDeviceIdentity.Resolve(device, _audioPeers,
        _benchPreferences, _writer.IsConnected && _ble.ConnectedBluetoothAddress == device.Address ? _writer.FirmwareInfoPayload : null);

    private void UpdateDeviceCombo()
    {
        ulong? selectedAddress = (CmbDevices.SelectedItem as ComboBoxItem)?.Tag as ulong?;
        ulong? connectedAddress = _writer.IsConnected ? _ble.ConnectedBluetoothAddress : null;
        selectedAddress ??= connectedAddress;
        var devices = _deviceList.ToList();
        if (connectedAddress is ulong connected && !devices.Any(device => device.Address == connected))
            devices.Add(new(connected, _ble.ConnectedDeviceName, 0, true, DateTime.Now));
        var visible = devices.Select(device => (Device: device, Identity: IdentityFor(device)))
            .Where(item => item.Device.Address == connectedAddress || (_benchPreferences.BleFilterByModel
                ? SoncaBleModelFilter.Matches(item.Identity, _benchPreferences.BleModelFilter)
                : _benchPreferences.MatchesBleName(item.Identity.BleName, item.Identity.AudioName))).ToArray();
        var addresses = visible.Select(item => item.Device.Address).ToHashSet();
        foreach (var old in CmbDevices.Items.OfType<ComboBoxItem>()
            .Where(item => item.Tag is ulong address && !addresses.Contains(address)).ToArray())
            CmbDevices.Items.Remove(old);

        foreach (var (device, identity) in visible.OrderByDescending(item => item.Device.Rssi < 0 ? item.Device.Rssi : short.MinValue))
        {
            var item = CmbDevices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is ulong address && address == device.Address);
            if (item == null)
            {
                item = new ComboBoxItem { Tag = device.Address, Foreground = Brushes.White };
                CmbDevices.Items.Add(item);
            }
            string signal = device.Rssi < 0 ? $"{device.Rssi} dBm" : "RSSI chưa rõ";
            string model = identity.ModelId.HasValue ? identity.ModelLabel + " · " : "";
            item.Content = $"{model}{device.Name} ({signal}) [{device.FormattedAddress}]";
            item.ToolTip = IdentityDetails(device, identity);
            if (device.Address == selectedAddress) CmbDevices.SelectedItem = item;
        }
        if (CmbDevices.SelectedItem == null)
        {
            var target = visible.FirstOrDefault(item => !string.IsNullOrWhiteSpace(_targetModelKeyword)
                && item.Identity.SearchNames.Any(name => BluetoothDeviceRemoval.MatchesModel(name, _targetModelKeyword))).Device;
            CmbDevices.SelectedItem = CmbDevices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => target != null && Equals(item.Tag, target.Address))
                ?? CmbDevices.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        RefreshModelIdentity();
    }

    private static string IdentityDetails(BleDeviceInfo device, SoncaBleDeviceIdentity identity) =>
        $"{identity.ModelLabel}\nTên BLE: {device.Name}\nBLE: {device.FormattedAddress}"
        + (identity.AudioName.Length > 0 ? $"\nBluetooth cùng loa (địa chỉ XOR 0x01): {identity.AudioName}" : "")
        + (identity.ModelId.HasValue ? identity.FromLiveInfo ? "\nNguồn: INFO vừa đọc từ loa → model_profile.txt."
            : "\nNguồn: mã INFO đã lưu lần kết nối trước → model_profile.txt. Cần kết nối để xác nhận lại."
            : "\nKết nối BLE để đọc mã model; tên quảng bá có thể là tên chung.");

    private void RefreshModelIdentity()
    {
        ulong? address = _writer.IsConnected ? _ble.ConnectedBluetoothAddress : (CmbDevices.SelectedItem as ComboBoxItem)?.Tag as ulong?;
        var device = _deviceList.FirstOrDefault(device => device.Address == address)
            ?? new BleDeviceInfo(address ?? 0, _ble.ConnectedDeviceName, 0, true, DateTime.Now);
        var identity = SoncaBleDeviceIdentity.Resolve(device, _audioPeers, _benchPreferences,
            _writer.IsConnected ? _writer.FirmwareInfoPayload : null);
        string status = identity.ModelId.HasValue ? (identity.FromLiveInfo ? "Model: " : "Model đã lưu: ") + identity.ModelLabel
            : "Model: chờ INFO từ loa";
        TxtDetectedModel.Text = status;
        TxtDeviceModelDetails.Text = device.Address == 0 ? identity.ModelLabel : IdentityDetails(device, identity);
        TxtDetectedModel.ToolTip = TxtDeviceModelDetails.Text;
    }

    private void ImportModelCatalogue(object sender, RoutedEventArgs e)
    {
        if (IsAudioMeasurementBusy?.Invoke() == true) { TxtBenchSettingsStatus.Text = "Chờ đo xong để nạp bảng model."; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Chọn model_profile.txt từ firmware Sonca", Filter = "Bảng model (*.txt;*.json)|*.txt;*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SoncaFirmwareModels.Import(dialog.FileName);
            UpdateDeviceCombo();
            TxtBenchSettingsStatus.Text = $"Đã nạp {SoncaFirmwareModels.Current.EntryCount} tên model. Chỉ lưu mã/tên, không áp preset âm thanh.";
        }
        catch (Exception ex) { TxtBenchSettingsStatus.Text = "Không nạp bảng model: " + ex.Message; }
    }

    private async void RefreshModelCatalogue(object sender, RoutedEventArgs e)
    {
        if (_refreshingModelCatalogue || IsAudioMeasurementBusy?.Invoke() == true) return;
        _refreshingModelCatalogue = true;
        BtnRefreshModelCatalogue.IsEnabled = false;
        TxtBenchSettingsStatus.Text = "Đang cập nhật tên model từ Sonca…";
        try
        {
            await SoncaFirmwareModels.RefreshAsync();
            if (_windowClosing) return;
            UpdateDeviceCombo();
            TxtBenchSettingsStatus.Text = $"Đã cập nhật {SoncaFirmwareModels.Current.EntryCount} tên model từ Sonca.";
        }
        catch (Exception ex)
        {
            if (!_windowClosing) TxtBenchSettingsStatus.Text = "Chưa cập nhật được; tiếp tục dùng bảng đã lưu. " + ex.Message;
        }
        finally { _refreshingModelCatalogue = false; if (!_windowClosing) BtnRefreshModelCatalogue.IsEnabled = true; }
    }
}
