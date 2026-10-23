using System.Diagnostics;

namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    private readonly object _preparationLock = new();
    private Task<bool>? _preparation;
    private ulong? _preparationTarget;
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
        bool userInitiated = false, IReadOnlyList<BluetoothAudioPeer>? connectedPeers = null)
    {
        Task<bool> pending;
        bool retryWithTarget;
        lock (_preparationLock)
        {
            if (userInitiated) EnableAutomaticConnection();
            if (AutomaticConnectionSuppressed) return false;
            if (IsConnected && SupportsMixerControls && (connectedPeers is { Count: > 0 }
                ? ConnectedBluetoothAddress is ulong current && SoncaBleDiscovery.MatchesAudioPeer(current, connectedPeers)
                : !savedController.HasValue || ConnectedBluetoothAddress == savedController)) return true;
            if (_preparation == null || _preparation.IsCompleted)
            {
                var lifetime = _preparationCancellation.Token;
                _preparationTarget = savedController;
                _preparation = Task.Run(() => PrepareConnectionCoreAsync(savedController, connectedPeers, lifetime));
            }
            pending = _preparation;
            retryWithTarget = savedController.HasValue && _preparationTarget != savedController;
        }
        bool ready = await pending.WaitAsync(token);
        return !ready && retryWithTarget && !AutomaticConnectionSuppressed
            ? await EnsureConnectedAsync(savedController, token, userInitiated, connectedPeers) : ready;
    }

    private async Task<bool> PrepareConnectionCoreAsync(ulong? savedController, IReadOnlyList<BluetoothAudioPeer>? connectedPeers, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
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
            if (targets.Count == 0) return false;
            BleDeviceInfo? Candidate(IEnumerable<BleDeviceInfo> known)
            {
                var advertised = SoncaBleDiscovery.Preferred(DiscoveredDevices.Where(d => DateTime.Now - d.LastSeen < TimeSpan.FromSeconds(30)), targets);
                if (advertised != null) return advertised;
                var paired = known.Where(d => SoncaBleDiscovery.MatchesAudioPeer(d.Address, targets)
                    && (peers.Count > 0 || d.Address == savedController)).GroupBy(d => d.Address).Select(g => g.First()).ToArray();
                return paired.Length == 1 ? paired[0] : null;
            }
            var preferred = Candidate(Array.Empty<BleDeviceInfo>());
            if (preferred == null && savedController is ulong remembered
                && (peers.Count == 0 || SoncaBleDiscovery.MatchesAudioPeer(remembered, peers)))
                preferred = new(remembered, "C360 đã xác minh trước đó", 0, true, DateTime.Now);
            Task<IReadOnlyList<BleDeviceInfo>>? knownTask = preferred == null ? Task.Run(() => KnownAssociatedControllersAsync(targets, verifyService: false)) : null;
            IReadOnlyList<BleDeviceInfo> known = Array.Empty<BleDeviceInfo>();
            var deadline = Task.Delay(TimeSpan.FromSeconds(4), token);
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
            if (IsConnected && ConnectedBluetoothAddress == preferred.Address) return SupportsMixerControls;
            if (IsConnecting) return false;
            LogMessage?.Invoke($"[BLE] Chọn {preferred.FormattedAddress} sau {watch.Elapsed.TotalMilliseconds:F0}ms theo {(peers.Count > 0 ? "Bluetooth đang nối" : "loa đã chọn")}.");
            // WinRT can block synchronously before returning IAsyncOperation;
            // keep that work off the WPF dispatcher.
            bool ready = await ConnectAsync(preferred.Address);
            if (token.IsCancellationRequested && AutomaticConnectionSuppressed) Disconnect();
            token.ThrowIfCancellationRequested();
            LogMessage?.Invoke($"[BLE] Chuẩn bị {(ready ? "xong" : "chưa xong")} sau {watch.Elapsed.TotalSeconds:F2}s.");
            return ready && SupportsMixerControls;
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
