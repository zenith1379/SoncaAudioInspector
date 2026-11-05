using SoncaAudioInspector;

internal static class BluetoothRemovalChecks
{
    public static async Task Run()
    {
        const ulong ble = 0x423E3F752ECC, audio = 0x433E3F752ECC;
        var target = new BluetoothPairingRecord("audio", "KBEATBOX", audio);
        var other = new BluetoothPairingRecord("other", "KBEATBOX", 0x433E3F75A93A);
        var controller = new BluetoothPairingRecord("ble", "Sonca BLE", ble, IsLowEnergy: true);
        static void Check(bool condition, string why) { if (!condition) throw new Exception(why); }
        int reads = 0;
        var removed = new List<string>();
        var report = await BluetoothRemovalWorkflow.RunAsync(ble,
            _ => Task.FromResult(++reads < 3
                ? new BluetoothRemovalSnapshot(new[] { other, target, target, controller }, true)
                : new BluetoothRemovalSnapshot(new[] { other, controller }, false)),
            (record, _) => { removed.Add(record.Id); return Task.CompletedTask; },
            pollInterval: TimeSpan.FromMilliseconds(1));
        Check(report.Success && report.Removed == 1 && report.Matched == 1 && reads == 3 && removed.SequenceEqual(new[] { target.Id }),
            "Audio removal touched BLE/same-name peers, duplicated records or skipped disconnect verification");
        var policy = new BluetoothAudioAutoPairPolicy();
        policy.Suppress(ble);
        Check(policy.IsSuppressed(ble) && policy.IsSuppressed(audio) && !policy.IsSuppressed(other.Address),
            "Audio suppression targeted the wrong controller or ignored firmware XOR identity");
        policy.Allow(audio);
        Check(!policy.IsSuppressed(ble), "An explicit new audio request could not pair the speaker again");

        report = await BluetoothRemovalWorkflow.RunAsync(ble,
            _ => Task.FromResult(new BluetoothRemovalSnapshot(Array.Empty<BluetoothPairingRecord>(), true)),
            (_, _) => throw new Exception("Unpair without a matched record"), verificationTimeout: TimeSpan.Zero);
        Check(!report.Success && report.Connected && report.Removed == 0, "No records / still connected falsely reported success");

        report = await BluetoothRemovalWorkflow.RunAsync(ble,
            _ => Task.FromResult(new BluetoothRemovalSnapshot(new[] { target }, false)),
            (_, _) => Task.CompletedTask, verificationTimeout: TimeSpan.Zero);
        Check(!report.Success && report.Remaining == 1, "Unpair return value bypassed paired-record verification");

        report = await BluetoothRemovalWorkflow.RunAsync(ble,
            _ => Task.FromResult(new BluetoothRemovalSnapshot(new[] { target }, true)),
            (_, _) => throw new InvalidOperationException("AccessDenied fixture"), verificationTimeout: TimeSpan.Zero);
        Check(!report.Success && report.Removed == 0 && report.Message.Contains("AccessDenied fixture"), "Unpair error was hidden");

        report = await BluetoothRemovalWorkflow.RunAsync(ble,
            _ => Task.FromResult(new BluetoothRemovalSnapshot(new[] { other }, false)),
            (_, _) => throw new Exception("Already unpaired target modified another speaker"));
        Check(report.Success && report.Matched == 0 && report.Removed == 0, "Verified already-unpaired speaker was rejected");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel(); reads = 0;
        try
        {
            await BluetoothRemovalWorkflow.RunAsync(ble,
                _ => { reads++; return Task.FromResult(new BluetoothRemovalSnapshot(new[] { target }, true)); },
                (_, _) => Task.CompletedTask, cancelled.Token);
            throw new Exception("Cancelled cleanup continued");
        }
        catch (OperationCanceledException) { Check(reads == 0, "Cancelled cleanup accessed Windows"); }

        bool queryFailed = false;
        try
        {
            await BluetoothRemovalWorkflow.RunAsync(ble,
                _ => throw new InvalidOperationException("Windows query unavailable"), (_, _) => Task.CompletedTask);
        }
        catch (InvalidOperationException) { queryFailed = true; }
        Check(queryFailed, "Unavailable Windows inventory became a successful empty result");
        Console.WriteLine("PASS Bluetooth removal: exact MAC/XOR and same-name isolation, duplicate records, delayed disconnect, no-record/still-connected rejection, paired readback, AccessDenied, idempotence, cancellation and query failure. Injected Windows only.");
    }
}
