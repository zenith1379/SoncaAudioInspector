using System.Diagnostics;

namespace SoncaAudioInspector;

public sealed record BluetoothPairingRecord(string Id, string Name, ulong Address, bool IsLowEnergy = false);
public sealed record BluetoothRemovalSnapshot(IReadOnlyList<BluetoothPairingRecord> Paired, bool Connected);
public sealed record BluetoothRemovalReport(bool Success, int Removed, int Matched, int Remaining, bool Connected, string Message);

/// <summary>Remove only the measured speaker, and confirm Windows has released it.</summary>
public static class BluetoothRemovalWorkflow
{
    public static async Task<BluetoothRemovalReport> RunAsync(ulong controller,
        Func<CancellationToken, Task<BluetoothRemovalSnapshot>> read,
        Func<BluetoothPairingRecord, CancellationToken, Task> unpair,
        CancellationToken token = default, TimeSpan? verificationTimeout = null, TimeSpan? pollInterval = null)
    {
        if (!SoncaBleDiscovery.IsSameSpeaker(controller, controller))
            throw new ArgumentException("Chưa xác định được địa chỉ BLE của loa vừa đo.");

        BluetoothPairingRecord[] Targets(BluetoothRemovalSnapshot snapshot) => snapshot.Paired
            .Where(record => !record.IsLowEnergy && SoncaBleDiscovery.IsSameSpeaker(record.Address, controller))
            .DistinctBy(record => record.Id).ToArray();

        token.ThrowIfCancellationRequested();
        var before = await read(token);
        var targets = Targets(before);
        int removed = 0;
        var failures = new List<string>();
        foreach (var record in targets)
        {
            token.ThrowIfCancellationRequested();
            try { await unpair(record, token); removed++; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failures.Add(record.Name + ": " + ex.Message); }
        }

        var timer = Stopwatch.StartNew();
        var timeout = verificationTimeout ?? TimeSpan.FromSeconds(6);
        var pause = pollInterval ?? TimeSpan.FromMilliseconds(250);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var after = await read(token);
            int remaining = Targets(after).Length;
            if (remaining == 0 && !after.Connected)
                return new(true, removed, targets.Length, 0, false,
                    $"Loa BLE {controller:X12}: Windows xác nhận đã gỡ Bluetooth âm thanh ({removed}/{targets.Length} bản ghi).");

            var remainingTime = timeout - timer.Elapsed;
            if (remainingTime <= TimeSpan.Zero)
                return new(false, removed, targets.Length, remaining, after.Connected,
                    $"Loa BLE {controller:X12}: chưa gỡ hết Bluetooth; còn {remaining} bản ghi ghép đôi, "
                    + (after.Connected ? "vẫn đang kết nối." : "đã ngắt âm thanh.")
                    + (failures.Count == 0 ? "" : " " + string.Join("; ", failures)));
            await Task.Delay(pause < remainingTime ? pause : remainingTime, token);
        }
    }
}
