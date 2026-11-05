namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    internal void ReportBluetoothPreparation(string message) => LogMessage?.Invoke("[BT] " + message);
    public string BluetoothPlaybackEndpointId { get; private set; } = "";
    internal void RememberBluetoothPlayback(string id) => BluetoothPlaybackEndpointId = id;
    private readonly BluetoothAudioAutoPairPolicy _bluetoothAudioAutoPair = new();
    internal bool IsBluetoothAudioAutoPairSuppressed(ulong address) => _bluetoothAudioAutoPair.IsSuppressed(address);
    internal void AllowBluetoothAudioAutoPair(ulong address) => _bluetoothAudioAutoPair.Allow(address);
    internal void SuppressBluetoothAudioAutoPair(ulong address)
    {
        _bluetoothAudioAutoPair.Suppress(address);
        BluetoothPlaybackEndpointId = "";
    }
    private void RememberFirmwareModel(ulong address)
    {
        if (!SoncaBleMixerProfile.TryReadModelId(FirmwareInfoPayload, out ushort modelId)) return;
        try
        {
            var preferences = AudioBenchPreferences.Load();
            var controllers = new Dictionary<ushort, ulong>(preferences.KnownControllers ?? new());
            var models = new Dictionary<ulong, ushort>(preferences.KnownDeviceModels ?? new());
            bool changed = !controllers.TryGetValue(modelId, out ulong previousAddress) || previousAddress != address
                || !models.TryGetValue(address, out ushort previousModel) || previousModel != modelId;
            if (!changed) return;
            controllers[modelId] = address;
            models[address] = modelId;
            (preferences with { KnownControllers = controllers, KnownDeviceModels = models }).Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        { LogMessage?.Invoke("[BLE] Chưa nhớ được bo theo model: " + ex.Message); }
    }
}
