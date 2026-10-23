using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public static class C360PlaybackEndpoints
{
    public static async Task<string> WaitForAsync(string name, Func<IReadOnlyList<C360ModelEndpoint>> enumerate,
        CancellationToken token, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var maximum = timeout ?? TimeSpan.FromSeconds(5);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var endpoints = enumerate().Where(e => !e.Capture).ToArray();
            var matches = endpoints.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) matches = endpoints.Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0].Id;
            if (matches.Length > 1) throw new InvalidOperationException($"Có nhiều ngõ phát {name}; chọn tên cụ thể trong cấu hình model.");
            var remaining = maximum - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return "";
            var pause = interval ?? TimeSpan.FromMilliseconds(125);
            await Task.Delay(pause < remaining ? pause : remaining, token);
        }
    }
}

public sealed partial class C360AudioBench
{
    public async Task<C360ElectronicSetup> PrepareAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        if (stage != C360ElectronicStage.UsbA || string.IsNullOrWhiteSpace(setup.UsbPlaybackName)) return setup;
        // USB playback can enumerate only after the suite has switched and confirmed BLE source 1.
        // Re-enumerate the configured render name here; never substitute the C360 recording interface.
        string id = await C360PlaybackEndpoints.WaitForAsync(setup.UsbPlaybackName, () =>
        {
            using var enumeration = new MMDeviceEnumerator();
            var result = new List<C360ModelEndpoint>();
            foreach (var device in enumeration.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device) result.Add(new(device.ID, device.FriendlyName, false));
            return result;
        }, token);
        return setup with { UsbPlaybackId = id };
    }
}
