namespace SoncaAudioInspector;

public sealed record BluetoothAudioPeer(ulong Address, string Name);

public interface ISoncaMixerWriter
{
    bool IsConnected { get; }
    bool SupportsMixerControls { get; }
    bool SupportsInputRouting => SupportsMixerControls;
    byte[] FirmwareInfoPayload => Array.Empty<byte>();
    string InputSourceName(byte source) => SoncaBleMixerProfile.InputSourceName(source);
    bool TryMuteRegister(string channel, out ushort register, out bool inverted) =>
        SoncaBleMixerProfile.TryMuteRegister(channel, out register, out inverted);
    IReadOnlyDictionary<ushort, ushort> MixerState { get; }
    Task<bool> SetOptionAsync(SoncaMixerOption option, bool enabled);
    Task<bool> SendPacketAsync(byte[] data, string? description = null);
}

public static class SoncaBleDiscovery
{
    // Firmware: ble_LocalDeviceAddr[0] ^= 0x01. In Windows' displayed address
    // this byte is the first octet, e.g. 43:9F:... <-> 42:9F:... .
    public const ulong AudioBleAddressMask = 0x010000000000UL;
    public static bool IsSameSpeaker(ulong first, ulong second) => first is > 0 and <= 0xFFFFFFFFFFFFUL
        && second is > 0 and <= 0xFFFFFFFFFFFFUL && (first == second || (first ^ second) == AudioBleAddressMask);
    public static bool MatchesAudioPeer(ulong address, IEnumerable<BluetoothAudioPeer> peers) => peers.Any(p => IsSameSpeaker(address, p.Address));

    public static BleDeviceInfo? Merge(ulong address, string? name, short rssi, IEnumerable<Guid> serviceUuids, BleDeviceInfo? previous = null)
    {
        Guid[] services = (previous?.ServiceUuids ?? Array.Empty<Guid>()).Union(serviceUuids).ToArray();
        bool soncaService = services.Contains(SoncaBleProtocol.PrimaryServiceUuid);
        string label = string.IsNullOrWhiteSpace(name) ? previous?.Name ?? "" : name.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            if (!soncaService) return null;
            label = $"Sonca BLE {address:X12}";
        }
        return new(address, label, rssi, soncaService || SoncaBleProtocol.IsLikelySoncaDevice(label), DateTime.Now)
        { ServiceUuids = services };
    }

    public static BleDeviceInfo? Preferred(IEnumerable<BleDeviceInfo> devices, IEnumerable<BluetoothAudioPeer> connectedAudio)
    {
        // The observed Sonca dual-radio pair differs by 01 in the first octet.
        // Require its advertised Sonca service and all other address bytes to
        // match the connected Windows peer; never choose just by RSSI/name.
        var matches = devices.Where(d => d.ServiceUuids.Contains(SoncaBleProtocol.PrimaryServiceUuid)
            && MatchesAudioPeer(d.Address, connectedAudio)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static BleDeviceInfo? UniqueNewController(IEnumerable<BleDeviceInfo> devices, ushort expectedModel, AudioBenchPreferences preferences)
    {
        var matches = devices.Where(device => device.ServiceUuids.Contains(SoncaBleProtocol.PrimaryServiceUuid)
            && DateTime.Now - device.LastSeen < TimeSpan.FromSeconds(10))
            .Where(device =>
            {
                var identity = SoncaBleDeviceIdentity.Resolve(device, Array.Empty<BluetoothAudioPeer>(), preferences);
                return identity.ModelId.HasValue ? identity.ModelId == expectedModel && preferences.MatchesBleDevice(device, Array.Empty<BluetoothAudioPeer>())
                    : true; // INFO must confirm both model ID and filter before control is enabled.
            }).GroupBy(device => device.Address).Select(group => group.First()).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
