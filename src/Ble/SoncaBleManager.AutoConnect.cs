using System.Diagnostics;

namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    private readonly object _preparationLock = new();
    private Task<bool>? _preparation;
    private ulong? _preparationTarget;
    private ushort? _preparationExpectedModel;
    private CancellationTokenSource _preparationCancellation = new();
    public bool AutomaticConnectionSuppressed { get; private set; }

    public void SuppressAutomaticConnection()
    {
        lock (_preparationLock)
        {
            AutomaticConnectionSuppressed = true;
            _preparationCancellation.Cancel();
            Disconnect();
        }
    }

    public void ForgetMeasuredSpeaker(ulong address)
    {
        BluetoothPlaybackEndpointId = "";
        foreach (var key in _discoveredDevices.Keys.Where(k => BluetoothDeviceRemoval.IsMeasuredAddress(k, address))) _discoveredDevices.TryRemove(key, out _);
        var preferences = AudioBenchPreferences.Load();
        var controllers = preferences.KnownControllers?.Where(p => !SoncaBleDiscovery.IsSameSpeaker(p.Value, address)).ToDictionary(p => p.Key, p => p.Value);
        var models = preferences.KnownDeviceModels?.Where(p => !SoncaBleDiscovery.IsSameSpeaker(p.Key, address)).ToDictionary(p => p.Key, p => p.Value);
        if (controllers?.Count != preferences.KnownControllers?.Count || models?.Count != preferences.KnownDeviceModels?.Count)
            (preferences with { KnownControllers = controllers, KnownDeviceModels = models }).Save();
    }

    public void EnableAutomaticConnection()
    {
        lock (_preparationLock)
        {
            AutomaticConnectionSuppressed = false;
            if (_preparationCancellation.IsCancellationRequested)
            { _preparationCancellation.Dispose(); _preparationCancellation = new(); }
        }
    }

    // All entry points share one discovery/handshake. A cancelled UI waiter does
    // not cancel another waiter's preparation; explicit Disconnect cancels both.
    public async Task<bool> EnsureConnectedAsync(ulong? savedController = null, CancellationToken token = default,
        bool userInitiated = false, IReadOnlyList<BluetoothAudioPeer>? connectedPeers = null, ushort? expectedModelId = null)
    {
        Task<bool> pending;
        bool retryWithTarget;
        lock (_preparationLock)
        {
            if (userInitiated) EnableAutomaticConnection();
            if (AutomaticConnectionSuppressed) return false;
            // INFO enables protocol traffic before the initial mixer read has
            // completed. Reuse only a finished connection; concurrent callers
            // must wait for the shared preparation through that first snapshot.
            if (!IsConnecting && IsConnected && (!expectedModelId.HasValue || SoncaBleMixerProfile.TryReadModelId(FirmwareInfoPayload, out ushort actualModel) && actualModel == expectedModelId)
                && (connectedPeers is { Count: > 0 }
                ? ConnectedBluetoothAddress is ulong current && SoncaBleDiscovery.MatchesAudioPeer(current, connectedPeers)
                : !savedController.HasValue || ConnectedBluetoothAddress == savedController)) return true;
            if (_preparation == null || _preparation.IsCompleted)
            {
                var lifetime = _preparationCancellation.Token;
                _preparationTarget = savedController;
                _preparationExpectedModel = expectedModelId;
                _preparation = Task.Run(() => PrepareConnectionCoreAsync(savedController, connectedPeers, expectedModelId, lifetime));
            }
            pending = _preparation;
            retryWithTarget = savedController.HasValue && _preparationTarget != savedController
                || expectedModelId.HasValue && _preparationExpectedModel != expectedModelId;
        }
        bool ready = await pending.WaitAsync(token);
        ready = ready && (!expectedModelId.HasValue || SoncaBleMixerProfile.TryReadModelId(FirmwareInfoPayload, out ushort actual) && actual == expectedModelId);
        return !ready && retryWithTarget && !AutomaticConnectionSuppressed
            ? await EnsureConnectedAsync(savedController, token, userInitiated, connectedPeers, expectedModelId) : ready;
    }

    private async Task<bool> PrepareConnectionCoreAsync(ulong? savedController, IReadOnlyList<BluetoothAudioPeer>? connectedPeers, ushort? expectedModelId, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var preferences = AudioBenchPreferences.Load();
        bool startedScan = !IsScanning;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFound(BleDeviceInfo _) => changed.TrySetResult();
        DeviceDiscovered += OnFound;
        try
        {
            if (startedScan) StartScanning();
            var peers = connectedPeers ?? await ConnectedAudioPeersAsync().WaitAsync(token);
            // A saved controller may only match the connected Windows peer or
            // be used when no peer is connected. Connect still verifies INFO.
            var targets = peers.Count > 0 ? peers : savedController is ulong saved
                ? new[] { new BluetoothAudioPeer(saved, "C360 đã chọn") } : Array.Empty<BluetoothAudioPeer>();
            if (targets.Count == 0 && !expectedModelId.HasValue) return false;
            BleDeviceInfo? Candidate(IEnumerable<BleDeviceInfo> known)
            {
                if (targets.Count == 0) return SoncaBleDiscovery.UniqueNewController(DiscoveredDevices, expectedModelId!.Value, preferences);
                bool CanReadIdentity(BleDeviceInfo device) => preferences.MatchesBleDevice(device, targets)
                    // An unnamed controller cannot reveal its model until INFO. Only
                    // probe the unique address associated with the connected audio peer.
                    || peers.Count == 1 && SoncaBleDeviceIdentity.Resolve(device, targets, preferences).ModelId == null;
                var advertised = SoncaBleDiscovery.Preferred(DiscoveredDevices.Where(d => CanReadIdentity(d) && DateTime.Now - d.LastSeen < TimeSpan.FromSeconds(30)), targets);
                if (advertised != null) return advertised;
                var paired = known.Where(d => CanReadIdentity(d) && SoncaBleDiscovery.MatchesAudioPeer(d.Address, targets)
                    && (peers.Count > 0 || d.Address == savedController)).GroupBy(d => d.Address).Select(g => g.First()).ToArray();
                return paired.Length == 1 ? paired[0] : null;
            }
            var preferred = Candidate(Array.Empty<BleDeviceInfo>());
            if (preferred == null && !preferences.BleFilterByModel && string.IsNullOrWhiteSpace(preferences.BleNameFilter) && savedController is ulong remembered
                && (peers.Count == 0 || SoncaBleDiscovery.MatchesAudioPeer(remembered, peers)))
                preferred = new(remembered, "C360 đã xác minh trước đó", 0, true, DateTime.Now);
            Task<IReadOnlyList<BleDeviceInfo>>? knownTask = preferred == null && targets.Count > 0 ? Task.Run(() => KnownAssociatedControllersAsync(targets, verifyService: false)) : null;
            IReadOnlyList<BleDeviceInfo> known = Array.Empty<BleDeviceInfo>();
            var deadline = Task.Delay(TimeSpan.FromSeconds(4), token);
            if (targets.Count == 0)
            {
                // On a new PC there is no address association yet. Collect a
                // whole scan window so the first advertisement does not win.
                LogMessage?.Invoke("[BLE] Máy chưa có loa đã ghép: đang kiểm tra có duy nhất một bo Sonca để đọc INFO…");
                await deadline;
                preferred = Candidate(Array.Empty<BleDeviceInfo>());
            }
            while (preferred == null && !deadline.IsCompleted)
            {
                var pulse = changed.Task;
                var completed = await Task.WhenAny(pulse, knownTask ?? deadline, deadline);
                token.ThrowIfCancellationRequested();
                if (completed == knownTask)
                {
                    try { known = await knownTask!; } catch (Exception ex) { LogMessage?.Invoke("[BLE] Danh sách đã ghép: " + ex.Message); }
                    knownTask = null;
                }
                if (pulse.IsCompleted) changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                preferred = Candidate(known);
            }
            token.ThrowIfCancellationRequested();
            if (preferred == null) return false;
            if (peers.Count > 0 && !SoncaBleDiscovery.MatchesAudioPeer(preferred.Address, await ConnectedAudioPeersAsync().WaitAsync(token))) return false;
            if (!IsConnecting && IsConnected && ConnectedBluetoothAddress == preferred.Address) return true;
            if (IsConnecting) return false;
            LogMessage?.Invoke($"[BLE] Chọn {preferred.FormattedAddress} sau {watch.Elapsed.TotalMilliseconds:F0}ms theo {(peers.Count > 0 ? "Bluetooth đang nối" : "loa đã chọn")}.");
            // WinRT can block synchronously before returning IAsyncOperation;
            // keep that work off the WPF dispatcher.
            bool ready = await ConnectCoreAsync(preferred.Address, (info, name) =>
                (!expectedModelId.HasValue || SoncaBleMixerProfile.TryReadModelId(info, out ushort actual) && actual == expectedModelId)
                && preferences.MatchesBleDevice(preferred with { Name = preferred.Name + " · " + name }, targets, info));
            if (token.IsCancellationRequested && AutomaticConnectionSuppressed) Disconnect();
            token.ThrowIfCancellationRequested();
            LogMessage?.Invoke($"[BLE] Chuẩn bị {(ready ? "xong" : "chưa xong")} sau {watch.Elapsed.TotalSeconds:F2}s.");
            return ready;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { LogMessage?.Invoke("[BLE] Tự kết nối: " + ex.Message); return false; }
        finally
        {
            DeviceDiscovered -= OnFound;
            if (startedScan) StopScanning();
        }
    }
}
