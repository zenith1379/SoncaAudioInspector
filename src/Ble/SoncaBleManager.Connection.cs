using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    private readonly SemaphoreSlim _connectGate = new(1, 1), _writeGate = new(1, 1);
    private readonly List<GattDeviceService> _openServices = new();
    private GattSession? _session;
    private volatile bool _controlReady, _protocolReady;
    private readonly SoncaBleReplyDecoder _replyDecoder = new();
    private TaskCompletionSource<SoncaBleReply>? _infoReply;
    private int _connectionGeneration;
    internal int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);
    public bool IsConnecting { get; private set; }
    public string FirmwareIdentity { get; private set; } = "";
    public byte[] FirmwareInfoPayload {get;private set;}=Array.Empty<byte>();
    public ulong? ConnectedBluetoothAddress => _connectedDevice?.BluetoothAddress;

    public static async Task<IReadOnlyList<BleDeviceInfo>> KnownAssociatedControllersAsync(IReadOnlyList<BluetoothAudioPeer> peers, bool verifyService = true)
    {
        var matches = new List<BleDeviceInfo>();
        if (peers.Count == 0) return matches;
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector(), new[] { "System.Devices.Aep.DeviceAddress" });
        foreach (var info in infos)
        {
            ulong listed = 0;
            bool hasAddress = info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object? raw)
                && ulong.TryParse(raw?.ToString()?.Replace(":", "").Replace("-", ""), System.Globalization.NumberStyles.HexNumber, null, out listed);
            if (hasAddress && !SoncaBleDiscovery.MatchesAudioPeer(listed, peers)) continue;
            if (!verifyService)
            {
                // Address association is only a candidate. ConnectAsync validates
                // the exact service, characteristics and live INFO before use.
                if (hasAddress) matches.Add(new(listed, info.Name, 0, true, DateTime.Now));
                continue;
            }
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                if (device == null || !SoncaBleDiscovery.MatchesAudioPeer(device.BluetoothAddress, peers)) continue;
                var services = await device.GetGattServicesForUuidAsync(SoncaBleProtocol.PrimaryServiceUuid, BluetoothCacheMode.Cached)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                try
                {
                    if (services.Status == GattCommunicationStatus.Success && services.Services.Any(s => s.Uuid == SoncaBleProtocol.PrimaryServiceUuid))
                        matches.Add(new(device.BluetoothAddress, string.IsNullOrWhiteSpace(info.Name) ? $"Sonca BLE {device.BluetoothAddress:X12}" : info.Name,
                            0, true, DateTime.Now) { ServiceUuids = new[] { SoncaBleProtocol.PrimaryServiceUuid } });
                }
                finally { foreach (var service in services.Services) service.Dispose(); }
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or TimeoutException)
            { System.Diagnostics.Debug.WriteLine("Unavailable associated BLE peer: " + ex.Message); }
        }
        return matches.GroupBy(d => d.Address).Select(g => g.First()).ToArray();
    }

    public static async Task<IReadOnlyList<BluetoothAudioPeer>> ConnectedAudioPeersAsync()
    {
        var peers = new List<BluetoothAudioPeer>();
        // AEPs can retain IsConnected=true for a removed adapter and reject
        // FromIdAsync. Enumerate current radios as well; never trust that flag alone.
        try { peers.AddRange(await Task.Run(NativeBluetoothAudioPeers.ReadConnected)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.SEHException)
        { System.Diagnostics.Debug.WriteLine("Native Bluetooth read unavailable: " + ex.Message); }
        if (peers.Count > 0) return peers.GroupBy(p => p.Address).Select(g => g.First()).ToArray();
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        foreach (var info in infos.Where(i => SoncaBleProtocol.IsLikelySoncaDevice(i.Name)))
        {
            try
            {
                using var device = await BluetoothDevice.FromIdAsync(info.Id);
                if (device?.ConnectionStatus == BluetoothConnectionStatus.Connected) peers.Add(new(device.BluetoothAddress, info.Name.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // A stale/LE-only Windows AEP may satisfy the classic selector
                // yet reject BluetoothDevice.FromIdAsync. Keep the other peers.
                System.Diagnostics.Debug.WriteLine("Skipping unavailable audio peer: " + ex.Message);
            }
        }
        return peers.GroupBy(p => p.Address).Select(g => g.First()).ToArray();
    }

    public Task<bool> ConnectAsync(ulong bluetoothAddress) => ConnectCoreAsync(bluetoothAddress);

    private async Task<bool> ConnectCoreAsync(ulong bluetoothAddress, Func<byte[], string, bool>? acceptIdentity = null)
    {
        if (!await _connectGate.WaitAsync(0)) return false;
        Disconnect();
        int generation = _connectionGeneration;
        IsConnecting = true;
        StopScanning();
        ConnectionStatusChanged?.Invoke(false, "Đang mở dịch vụ điều khiển BLE...");
        void CheckCurrent()
        {
            if (_connectionGeneration != generation) throw new OperationCanceledException("Đã hủy kết nối BLE.");
        }
        try
        {
            LogMessage?.Invoke($"[BLE] Kết nối điều khiển 0x{bluetoothAddress:X12}; Bluetooth âm thanh là kết nối riêng.");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            if (device == null) throw new InvalidOperationException("Không mở được thiết bị BLE.");
            if (_connectionGeneration != generation) { device.Dispose(); CheckCurrent(); }
            _connectedDevice = device;
            device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
            LogMessage?.Invoke("[BLE] Đang mở phiên GATT...");
            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            CheckCurrent();
            if (_session?.CanMaintainConnection == true) _session.MaintainConnection = true;
            LogMessage?.Invoke("[BLE] Đang lấy dịch vụ điều khiển Sonca...");
            // Reuse Windows' discovery of the exact Sonca service. A live INFO
            // handshake below still verifies the speaker before enabling writes.
            var services = await device.GetGattServicesForUuidAsync(SoncaBleProtocol.PrimaryServiceUuid, BluetoothCacheMode.Cached).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            if (services.Status == GattCommunicationStatus.Success && services.Services.Count == 0)
                services = await device.GetGattServicesForUuidAsync(SoncaBleProtocol.PrimaryServiceUuid, BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            if (services.Status == GattCommunicationStatus.Success && services.Services.Count == 0)
                services = await device.GetGattServicesForUuidAsync(SoncaBleProtocol.StandardUartServiceUuid, BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            _openServices.AddRange(services.Services);
            CheckCurrent();
            if (services.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Không đọc được dịch vụ BLE: {services.Status}.");
            var service = services.Services.FirstOrDefault(s => s.Uuid == SoncaBleProtocol.PrimaryServiceUuid)
                ?? services.Services.FirstOrDefault(s => s.Uuid == SoncaBleProtocol.StandardUartServiceUuid);
            if (service == null) throw new InvalidOperationException("Thiết bị không có dịch vụ điều khiển Sonca 5343/FFF0; không dùng kênh ghi bất kỳ.");
            LogMessage?.Invoke("[BLE] Đang lấy kênh ghi và phản hồi...");
            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Cached).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            if (chars.Status == GattCommunicationStatus.Success && (!chars.Characteristics.Any(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid1 || c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid2)
                || !chars.Characteristics.Any(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid1 || c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid2)))
                chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            CheckCurrent();
            if (chars.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Không đọc được kênh điều khiển: {chars.Status}.");
            bool Writable(GattCharacteristic c) => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
            _writeCharacteristic = chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid1 && Writable(c))
                ?? chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid2 && Writable(c));
            _notifyCharacteristic = chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid1)
                ?? chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid2);
            if (_writeCharacteristic == null || _notifyCharacteristic == null) throw new InvalidOperationException("Thiếu kênh ghi/nhận phản hồi Sonca; BLE chưa sẵn sàng điều khiển.");
            var properties = _notifyCharacteristic.CharacteristicProperties;
            var notifyMode = properties.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : properties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : throw new InvalidOperationException("Kênh nhận không hỗ trợ Notify/Indicate.");
            _notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;
            LogMessage?.Invoke("[BLE] Đang đăng ký phản hồi trực tiếp từ loa...");
            var subscription = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(notifyMode).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            CheckCurrent();
            if (subscription != GattCommunicationStatus.Success) throw new InvalidOperationException($"Không đăng ký được phản hồi loa: {subscription}.");
            _controlReady = true;
            _infoReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!await SendInfoQueryAsync()) throw new InvalidOperationException("GATT đã mở nhưng không gửi được truy vấn thông tin loa.");
            var info = await _infoReply.Task.WaitAsync(TimeSpan.FromSeconds(6));
            if (!SoncaBleMixerProfile.SupportsRoutingInfo(info)) throw new InvalidOperationException("INFO chưa xác nhận giao thức Sonca tương thích.");
            FirmwareIdentity = SoncaBuildIdentity.Firmware(info);
            FirmwareInfoPayload=info.Payload.ToArray();
            RememberFirmwareModel(bluetoothAddress);
            if (acceptIdentity?.Invoke(FirmwareInfoPayload, device.Name) == false)
                throw new InvalidOperationException("Tên model/BLE trong INFO không khớp bộ lọc đã lưu.");
            _protocolReady = true;
            _mixerReadNotBefore = DateTime.UtcNow.AddMilliseconds(1500);
            LogMessage?.Invoke($"[BLE] INFO được loa xác nhận: {info.Payload.Length} byte.");
            CheckCurrent();
            if (!await SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Đọc các điều khiển firmware hỗ trợ"))
                throw new InvalidOperationException("Đã nhận INFO nhưng chưa đọc được cấu hình điều khiển loa.");
            CheckCurrent();
            LogMessage?.Invoke($"[BLE] Đọc {MixerState.Count} thanh ghi; mixer={SupportsMixerControls}, ngõ={SupportsInputRouting}.");
            LogMessage?.Invoke($"[BLE] Sẵn sàng: service {service.Uuid}, write {_writeCharacteristic.Uuid}, notify {_notifyCharacteristic.Uuid}. Gửi thành công qua GATT chưa thay thế việc đọc lại trạng thái DSP.");
            ConnectionStatusChanged?.Invoke(IsConnected, $"BLE sẵn sàng: {device.Name}");
            return IsConnected;
        }
        catch (Exception ex)
        {
            bool currentAttempt = _connectionGeneration == generation;
            // The connect gate still excludes a subsequent attempt, including
            // when a manual disconnect cancelled this one while WinRT awaited.
            Disconnect();
            if (currentAttempt)
            {
                ConnectionStatusChanged?.Invoke(false, ex.Message);
                LogMessage?.Invoke("[BLE] " + ex.Message);
            }
            return false;
        }
        finally { IsConnecting = false; _connectGate.Release(); }
    }

    private void ReleaseGattServices()
    {
        if (_session != null)
        {
            try { _session.MaintainConnection = false; _session.Dispose(); } catch { }
            _session = null;
        }
        foreach (var service in _openServices) { try { service.Dispose(); } catch { } }
        _openServices.Clear();
    }
}
