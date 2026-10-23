using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed record SoncaUsbRecordingEndpoint(string Id, string Name, int SampleRate, int Channels)
{
    public static bool MatchesC360(string name) => name.Contains("C360", StringComparison.OrdinalIgnoreCase)
        && name.Contains("usb", StringComparison.OrdinalIgnoreCase);

    public static SoncaUsbRecordingEndpoint? Read()
    {
        var matches = new List<SoncaUsbRecordingEndpoint>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    if (!MatchesC360(device.FriendlyName)) continue;
                    var format = device.AudioClient.MixFormat;
                    if (format.SampleRate > 0 && format.Channels > 0) matches.Add(new(device.ID, device.FriendlyName, format.SampleRate, format.Channels));
                }
                catch { /* Unknown or unplugged is not an available USB-C input. */ }
            }
        }
        return matches.Count == 1 ? matches[0] : null;
    }
}
