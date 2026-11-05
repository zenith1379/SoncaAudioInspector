using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed record BluetoothPlaybackLevel(string Name, double Percent, bool Muted);

public static class SoncaBluetoothPlaybackState
{
    /// <summary>Read-only Windows playback level, independent of the speaker's physical Master.</summary>
    public static IReadOnlyList<BluetoothPlaybackLevel> Read(IReadOnlyList<BluetoothAudioPeer> peers)
    {
        var levels = new List<BluetoothPlaybackLevel>();
        if (peers.Count == 0) return levels;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var endpoint in WindowsBluetoothPlayback.Read().Where(endpoint => endpoint.Address is ulong address
                && peers.Any(peer => SoncaBleDiscovery.IsSameSpeaker(peer.Address, address))))
            {
                using var device = enumerator.GetDevice(endpoint.Id);
                levels.Add(new(device.FriendlyName, device.AudioEndpointVolume.MasterVolumeLevelScalar * 100, device.AudioEndpointVolume.Mute));
            }
        }
        catch { /* No numeric fallback: an unavailable level is not 0%. */ }
        return levels;
    }
}
