using System.Collections.Concurrent;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoncaAudioInspector;

public static class WindowsBluetoothPairing
{
    // Microsoft DeviceEnumerationAndPairing sample: protocol-only AQS includes
    // both paired and unpaired radios. GetDeviceSelector() suppresses inquiry.
    internal const string DiscoverySelector = "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\"";
    private static readonly SemaphoreSlim PairingGate = new(1, 1);
    internal static async Task WaitForIdleAsync(CancellationToken token)
    {
        await PairingGate.WaitAsync(token);
        PairingGate.Release();
    }
    public static Task<Guid?> PrepareSpeakerAsync(ulong controller, bool allowPair, CancellationToken token, Action<string>? progress = null)
        => RunExclusiveAsync(() => PrepareCoreAsync(controller, allowPair, token, progress), token);

    internal static async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        await PairingGate.WaitAsync(token);
        try { return await operation(); }
        finally { PairingGate.Release(); }
    }

    private static async Task<Guid?> PrepareCoreAsync(ulong controller, bool allowPair, CancellationToken token, Action<string>? progress)
    {
        progress?.Invoke("[BT] Đang tìm Bluetooth âm thanh của loa BLE " + controller.ToString("X12") + ".");
        var candidates = await DiscoverAsync(controller, allowPair, token);
        var distinct = candidates.GroupBy(item => item.Address).ToArray();
        if (distinct.Length == 0)
            throw new BluetoothPlaybackUnavailableException("Windows chưa tìm thấy Bluetooth âm thanh ghép với địa chỉ BLE của loa. Bật chế độ BT/ghép đôi trên loa rồi thử lại.");
        if (distinct.Length != 1)
            throw new InvalidOperationException("Có hai thiết bị Bluetooth cùng khớp địa chỉ BLE; chọn đúng loa trong Bluetooth Windows trước khi đo.");
        var candidate = distinct[0].First();
        token.ThrowIfCancellationRequested();
        progress?.Invoke($"[BT] Tìm thấy {candidate.Info.Name} · {candidate.Address:X12}; paired={candidate.Info.Pairing.IsPaired}, canPair={candidate.Info.Pairing.CanPair}.");
        // Pair the discovered AEP first. Opening a BluetoothDevice can fail
        // before pairing, even when Windows has already discovered its address.
        var pairing = candidate.Info.Pairing;
        if (!pairing.IsPaired)
        {
            if (!allowPair) throw new BluetoothPlaybackUnavailableException("Loa chưa ghép Bluetooth với Windows. Bluetooth auto pair đang tắt; kết nối loa trong Windows rồi đo lại.");
            // Windows owns any PIN/confirmation ceremony. Do not guess a PIN,
            // lower protection, or accept a different nearby device by name.
            var result = await pairing.PairAsync().AsTask(token);
            progress?.Invoke("[BT] Kết quả ghép đôi Windows: " + result.Status);
            if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                throw new BluetoothPlaybackUnavailableException("Windows chưa ghép Bluetooth với loa: " + result.Status + ". Chưa phát tín hiệu đo.");
        }
        if (allowPair)
        {
            token.ThrowIfCancellationRequested();
            ulong audioAddress = candidate.Address;
            uint status = await Task.Run(() => NativeBluetoothAudioPeers.EnableStereoService(audioAddress), token);
            progress?.Invoke(status is 0 or 0x80070057 ? "[BT] Dịch vụ âm thanh stereo đã có trên Windows; đang chờ ngõ phát."
                : "[BT] Windows chưa bật được dịch vụ stereo, mã " + status + "; tiếp tục kiểm tra ngõ âm thanh thực tế.");
        }
        return candidate.Container;
    }

    private sealed record Candidate(ulong Address, DeviceInformation Info, Guid? Container);
    private static async Task<Candidate[]> DiscoverAsync(ulong controller, bool discoverNew, CancellationToken token)
    {
        if (!SoncaBleDiscovery.IsSameSpeaker(controller, controller)) throw new ArgumentException("Địa chỉ BLE không hợp lệ.");
        string[] properties = { "System.Devices.Aep.DeviceAddress", "System.Devices.ContainerId", "System.Devices.Aep.ContainerId", "System.Devices.Aep.IsPaired", "System.Devices.Aep.IsPresent" };
        var found = new ConcurrentDictionary<string, Candidate>();
        void Add(DeviceInformation info)
        {
            // Updated AEP records can acquire (or lose) their address after Added.
            // Never retain an earlier match when Windows changes that identity.
            found.TryRemove(info.Id, out _);
            if (!info.Properties.TryGetValue(properties[0], out object? raw)) return;
            string hex = raw?.ToString()?.Replace(":", "").Replace("-", "") ?? "";
            if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
                || !SoncaBleDiscovery.IsSameSpeaker(controller, address)) return;
            Guid? container = null;
            foreach (string key in properties.Skip(1))
                if (info.Properties.TryGetValue(key, out object? value) && Guid.TryParse(value?.ToString(), out Guid id)
                    && BluetoothPlaybackMatcher.IsPhysicalContainer(id)) { container = id; break; }
            found[info.Id] = new(address, info, container);
        }
        using (var cached = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            cached.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true), properties).AsTask(cached.Token)) Add(info);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        if (found.Values.Any(item => item.Info.Pairing.IsPaired) || !discoverNew) return found.Values.ToArray();
        var watcher = DeviceInformation.CreateWatcher(DiscoverySelector, properties, DeviceInformationKind.AssociationEndpoint);
        var observed = new ConcurrentDictionary<string, DeviceInformation>();
        var updateLock = new object();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        void Ready()
        {
            if (found.Values.Any(item => item.Info.Pairing.IsPaired || item.Info.Pairing.CanPair)) completed.TrySetResult();
        }
        void OnAdded(DeviceWatcher _, DeviceInformation info)
        {
            lock (updateLock)
            {
                observed[info.Id] = info;
                Add(info);
                Ready();
            }
        }
        void OnUpdated(DeviceWatcher _, DeviceInformationUpdate update)
        {
            lock (updateLock)
            {
                if (!observed.TryGetValue(update.Id, out var info)) return;
                info.Update(update);
                Add(info);
                Ready();
            }
        }
        void OnRemoved(DeviceWatcher _, DeviceInformationUpdate update)
        {
            lock (updateLock)
            {
                observed.TryRemove(update.Id, out var ignored);
                found.TryRemove(update.Id, out var ignoredCandidate);
            }
        }
        try
        {
            watcher.Start();
            try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(12), token); }
            catch (TimeoutException) { }
            if (found.Count > 0) await Task.Delay(250, token); // Collect a competing match before choosing.
            token.ThrowIfCancellationRequested();
            return found.Values.ToArray();
        }
        finally
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
            watcher.Added -= OnAdded; watcher.Updated -= OnUpdated; watcher.Removed -= OnRemoved;
        }
    }
}
