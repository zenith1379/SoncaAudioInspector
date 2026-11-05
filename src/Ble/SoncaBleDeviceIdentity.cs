using System.Text;

namespace SoncaAudioInspector;

public sealed record SoncaBleDeviceIdentity(string BleName, string AudioName, ushort? ModelId,
    IReadOnlyList<string> ModelNames, string FirmwareName, bool FromLiveInfo)
{
    public string[] SearchNames => new[] { BleName, AudioName, FirmwareName }.Concat(ModelNames).ToArray();
    public string ModelLabel => ModelId is ushort id
        ? $"{(ModelNames.Count > 0 ? string.Join(" / ", ModelNames) : "Chưa có tên trong bảng")} · ID {id}"
            + (ModelNames.Count > 1 ? " (nhiều tên)" : "")
        : "Chưa có mã model từ INFO";

    public static SoncaBleDeviceIdentity Resolve(BleDeviceInfo device, IEnumerable<BluetoothAudioPeer> peers,
        AudioBenchPreferences preferences, byte[]? liveInfo = null)
    {
        string audioName = string.Join(" / ", peers.Where(p => SoncaBleDiscovery.IsSameSpeaker(device.Address, p.Address))
            .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
        bool live = liveInfo is { Length: > 0 };
        ushort? modelId = null;
        if (live)
        {
            if (SoncaBleMixerProfile.TryReadModelId(liveInfo!, out ushort read)) modelId = read;
        }
        else if (preferences.KnownDeviceModels?.TryGetValue(device.Address, out ushort saved) == true) modelId = saved;
        int end = live ? Array.IndexOf(liveInfo!, (byte)0) : -1;
        string firmware = end > 0 ? Encoding.ASCII.GetString(liveInfo!, 0, end) : "";
        return new(device.Name, audioName, modelId, modelId is ushort id ? SoncaFirmwareModels.Current.Find(id) : Array.Empty<string>(), firmware, live);
    }
}
