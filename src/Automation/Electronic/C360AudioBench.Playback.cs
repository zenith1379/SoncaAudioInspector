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

internal sealed class C360PlaybackPreparation
{
    private C360ElectronicSetup? _setup;
    private C360ElectronicStage _stage;
    private int _generation;
    public void Remember(C360ElectronicStage stage, C360ElectronicSetup setup, int generation)
    { _setup = setup; _stage = stage; _generation = generation; }
    public bool TryConsume(C360ElectronicStage stage, C360ElectronicSetup setup, int generation, bool connected)
    {
        bool valid = connected && ReferenceEquals(_setup, setup) && _stage == stage && _generation == generation;
        _setup = null;
        return valid;
    }
}

public sealed partial class C360AudioBench
{
    private readonly C360PlaybackPreparation _playbackPreparation = new();
    public async Task<C360ElectronicSetup> PrepareAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int generation = ble.ConnectionGeneration;
        var resolved = await ResolvePlaybackAsync(stage, setup, token);
        token.ThrowIfCancellationRequested();
        if (!ble.IsConnected || generation != ble.ConnectionGeneration)
            throw new InvalidOperationException("BLE đã ngắt/đổi trong lúc dò ngõ phát; chưa phát tín hiệu.");
        if (stage == C360ElectronicStage.Bluetooth) ble.RememberBluetoothPlayback(resolved.BluetoothPlaybackId);
        _playbackPreparation.Remember(stage, resolved, generation);
        return resolved;
    }
    private async Task<C360ElectronicSetup> ResolvePlaybackAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        if (stage == C360ElectronicStage.Bluetooth)
        {
            if (ble.ConnectedBluetoothAddress is not ulong controller) throw new InvalidOperationException("Chưa kết nối BLE với loa cần đo Bluetooth.");
            ble.AllowBluetoothAudioAutoPair(controller); // A new explicit BT measurement may pair again.
            var endpoint = await BluetoothPlaybackConnection.EnsureAsync(controller, setup.BluetoothPlaybackName, setup.BluetoothAutoPair,
                () => WindowsBluetoothPlayback.Read(), (allowPair, ct) => WindowsBluetoothPairing.PrepareSpeakerAsync(controller, allowPair, ct), token,
                message => ble.ReportBluetoothPreparation(message));
            ble.ReportBluetoothPreparation($"Đã ghép ngõ BT: {endpoint.Name} · {endpoint.Address:X12} · {endpoint.Id}");
            return setup with { BluetoothPlaybackId = endpoint.Id, BluetoothPlaybackName = endpoint.Name };
        }
        string? name = stage == C360ElectronicStage.UsbA ? setup.UsbPlaybackName : null;
        if (string.IsNullOrWhiteSpace(name)) return setup;
        // USB playback can enumerate only after the suite has switched and confirmed BLE source 1.
        // Re-enumerate the configured render name here; never substitute the C360 recording interface.
        string id = await C360PlaybackEndpoints.WaitForAsync(name, () =>
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
