using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SoncaAudioInspector;

public sealed record BleDeviceInfo(
    ulong Address,
    string Name,
    short Rssi,
    bool IsLikelySonca,
    DateTime LastSeen)
{
    public IReadOnlyList<Guid> ServiceUuids { get; init; } = Array.Empty<Guid>();
    public string FormattedAddress =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public string DisplayText =>
        $"{Name} (RSSI: {Rssi} dBm) [{FormattedAddress}]" + (IsLikelySonca ? " ★ [Sonca/Acnos]" : "");
}

public sealed partial class SoncaBleManager : IDisposable, ISoncaMixerWriter
{
    private static readonly Lazy<SoncaBleManager> _instance = new(() => new SoncaBleManager());
    public static SoncaBleManager Instance => _instance.Value;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _connectedDevice;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private readonly ConcurrentDictionary<ulong, BleDeviceInfo> _discoveredDevices = new();

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    private bool CanWriteTransport => _controlReady && _writeCharacteristic != null && _connectedDevice?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public bool IsConnected => _protocolReady && CanWriteTransport;

    public string ConnectedDeviceName => _connectedDevice?.Name ?? "";
    public string ConnectedDeviceAddress => _connectedDevice != null
        ? string.Join(":", Enumerable.Range(0, 6).Select(i => ((_connectedDevice.BluetoothAddress >> (8 * (5 - i))) & 0xFF).ToString("X2")))
        : "";

    public string ActiveServiceUuid => _writeCharacteristic?.Service?.Uuid.ToString() ?? "";
    public string ActiveWriteCharUuid => _writeCharacteristic?.Uuid.ToString() ?? "";
    public string ActiveNotifyCharUuid => _notifyCharacteristic?.Uuid.ToString() ?? "";

    // Events
    public event Action<BleDeviceInfo>? DeviceDiscovered;
    public event Action<bool, string>? ConnectionStatusChanged;
    public event Action<byte[], string>? DataSent;
    public event Action<byte[], string>? DataReceived;
    public event Action<SoncaBleReply>? ReplyReceived;
    public event Action<IReadOnlyDictionary<ushort, ushort>>? MixerStateReceived;
    public event Action<IReadOnlyDictionary<ushort, ushort>>? MixerParametersReceived;
    public event Action<string>? LogMessage;

    public IReadOnlyCollection<BleDeviceInfo> DiscoveredDevices => _discoveredDevices.Values.ToList();

    public void StartScanning()
    {
        StopScanning();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += (_, e) =>
        {
            LogMessage?.Invoke($"[BLE] Đã dừng quét (Lý do: {e.Error}).");
        };

        try
        {
            _watcher.Start();
            LogMessage?.Invoke("[BLE] Đang quét các thiết bị Bluetooth Low Energy gần đây...");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[BLE] Lỗi khi bật quét BLE: {ex.Message}");
        }
    }

    public void StopScanning()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.Received -= OnAdvertisementReceived;
                _watcher.Stop();
            }
            catch { }
            _watcher = null;
        }
    }

    public void ClearDiscoveredDevices()
    {
        _discoveredDevices.Clear();
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        _discoveredDevices.TryGetValue(args.BluetoothAddress, out var previous);
        var info = SoncaBleDiscovery.Merge(args.BluetoothAddress, args.Advertisement.LocalName,
            args.RawSignalStrengthInDBm, args.Advertisement.ServiceUuids, previous);
        if (info == null) return;
        _discoveredDevices[args.BluetoothAddress] = info;
        DeviceDiscovered?.Invoke(info);
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!ReferenceEquals(sender, _connectedDevice)) return;
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            // A later Windows reconnection must handshake/read state again;
            // old config and in-flight commands cannot carry across it.
            Disconnect();
            return;
        }
        bool connected = IsConnected;
        ConnectionStatusChanged?.Invoke(connected, connected ? $"BLE sẵn sàng: {sender.Name}"
            : IsConnecting ? "Đang mở dịch vụ điều khiển BLE..." : "BLE chưa sẵn sàng / đã ngắt kết nối.");
        LogMessage?.Invoke($"[BLE] Trạng thái kết nối đổi: {sender.ConnectionStatus}");
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _notifyCharacteristic)) return;
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] bytes = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(bytes);

            string hex = BitConverter.ToString(bytes).Replace("-", " ");
            string ascii = Encoding.ASCII.GetString(bytes.Select(b => (b >= 32 && b <= 126) ? b : (byte)'.').ToArray());

            string desc = $"HEX: {hex} | ASCII: {ascii}";
            if (SoncaBleProtocol.TryParsePacket(bytes, out byte cmd, out byte[] payload))
            {
                desc += $" [Sonca CMD 0x{cmd:X2}, Len {payload.Length}]";
            }

            DataReceived?.Invoke(bytes, desc);
            LogMessage?.Invoke($"[RX] {desc}");
            foreach (var reply in _replyDecoder.Push(bytes))
            {
                // INFO's first data byte is an info flag (00 on C360), not the
                // status=01 used by parameter replies. Validate its signature.
                if (reply.Command == SoncaBleProtocol.CmdInfo && reply.Payload.AsSpan().StartsWith("SCA_"u8)) _infoReply?.TrySetResult(reply);
                if (SoncaBleMixerProfile.TryReadSnapshot(reply, out var state)) _stateReply?.TrySetResult(state);
                if (IsConnected && SoncaBleMixerProfile.TryReadParameterUpdate(reply, out var updates)) MixerParametersReceived?.Invoke(updates);
                ReplyReceived?.Invoke(reply);
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[BLE RX Error] {ex.Message}");
        }
    }

    private async Task<bool> WriteTransportAsync(byte[] data, string? description = null)
    {
        bool infoQuery = data.AsSpan().SequenceEqual(SoncaBleProtocol.BuildInfoPacket());
        if (!CanWriteTransport || (!IsConnected && !infoQuery) || _writeCharacteristic == null)
        {
            LogMessage?.Invoke("[BLE TX] Chưa kết nối tới loa hoặc thiếu Write Characteristic.");
            return false;
        }
        int generation = _connectionGeneration;
        var characteristic = _writeCharacteristic;
        bool writeLockTaken = false;
        try
        {
            await _writeGate.WaitAsync();
            writeLockTaken = true;
            if (generation != _connectionGeneration || !CanWriteTransport || (!IsConnected && !infoQuery) || !ReferenceEquals(characteristic, _writeCharacteristic)) return false;
            using var writer = new DataWriter();
            writer.WriteBytes(data);
            var buffer = writer.DetachBuffer();

            var writeOption = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;

            var result = await characteristic.WriteValueWithResultAsync(buffer, writeOption).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            if (generation != _connectionGeneration) return false;
            if (result.Status == GattCommunicationStatus.Success)
            {
                string hex = BitConverter.ToString(data).Replace("-", " ");
                string desc = description ?? $"Packet {data.Length} bytes";
                DataSent?.Invoke(data, $"{desc} -> {hex}");
                LogMessage?.Invoke($"[TX] {desc} -> {hex}");
                return true;
            }
            else
            {
                LogMessage?.Invoke($"[BLE TX] Gửi thất bại: {result.Status}");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[BLE TX Error] {ex.Message}");
            if(ex is TimeoutException && generation==_connectionGeneration) Disconnect();
            return false;
        }
        finally { if (writeLockTaken) _writeGate.Release(); }
    }

    public async Task<bool> SendInfoQueryAsync()
    {
        byte[] packet = SoncaBleProtocol.BuildInfoPacket();
        return await SendPacketAsync(packet, "SCA=INFO (Bắt tay & Đọc thông tin loa)");
    }

    public async Task<bool> SetParamAsync(byte channelType, ushort regAddr, ushort value, string paramName)
    {
        byte[] packet = SoncaBleProtocol.BuildParamPacket(channelType, regAddr, value);
        return await SendPacketAsync(packet, $"{paramName} = {value} (Reg 0x{regAddr:X4}, Ch 0x{channelType:X2})");
    }

    public void Disconnect()
    {
        Interlocked.Increment(ref _connectionGeneration);
        _controlReady = false;
        _protocolReady = false;
        _infoReply?.TrySetCanceled(); _infoReply = null;
        _stateReply?.TrySetCanceled(); _stateReply = null;
        FirmwareIdentity = "";
        BluetoothPlaybackEndpointId = "";
        FirmwareInfoPayload=Array.Empty<byte>();
        MixerState = new Dictionary<ushort, ushort>();
        _replyDecoder.Clear();
        if (_notifyCharacteristic != null)
        {
            try
            {
                _notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
            }
            catch { }
            _notifyCharacteristic = null;
        }

        _writeCharacteristic = null;
        ReleaseGattServices();

        if (_connectedDevice != null)
        {
            try
            {
                _connectedDevice.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _connectedDevice.Dispose();
            }
            catch { }
            _connectedDevice = null;
            ConnectionStatusChanged?.Invoke(false, "Đã ngắt kết nối.");
            LogMessage?.Invoke("[BLE] Đã ngắt kết nối thiết bị.");
        }
    }

    public void Dispose()
    {
        SuppressAutomaticConnection();
        StopScanning();
    }
}
