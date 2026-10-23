namespace SoncaAudioInspector;

public sealed record BluetoothAudioPeer(ulong Address, string Name);

public interface ISoncaMixerWriter
{
    bool IsConnected { get; }
    bool SupportsMixerControls { get; }
    IReadOnlyDictionary<ushort, ushort> MixerState { get; }
    Task<bool> SetOptionAsync(SoncaMixerOption option, bool enabled);
    Task<bool> SendPacketAsync(byte[] data, string? description = null);
}

public static class SoncaBleDiscovery
{
    public static bool MatchesAudioPeer(ulong address, IEnumerable<BluetoothAudioPeer> peers) => address > 0 && address <= 0xFFFFFFFFFFFFUL
        && peers.Any(p => p.Address == address || (p.Address ^ address) == 0x010000000000UL);

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
}
