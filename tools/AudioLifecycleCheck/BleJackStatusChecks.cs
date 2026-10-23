using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

internal static class BleJackStatusChecks
{
    public static async Task Run(string output, ulong address, int seconds)
    {
        Directory.CreateDirectory(output);
        var clock = Stopwatch.StartNew();
        object gate = new();
        void Write(object value)
        {
            string json = JsonSerializer.Serialize(new { utc = DateTime.UtcNow, ms = clock.ElapsedMilliseconds, value });
            lock (gate) File.AppendAllText(Path.Combine(output, "events.jsonl"), json + Environment.NewLine);
            Console.WriteLine(json);
        }
        using var manager = new SoncaBleManager();
        manager.LogMessage += message => Write(new { log = message });
        manager.MixerParametersReceived += state => Write(new { notification = state.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) });
        var previous = new Dictionary<ushort, ushort>();
        manager.MixerStateReceived += state =>
        {
            var changes = state.Where(p => !previous.TryGetValue(p.Key, out ushort old) || old != p.Value)
                .ToDictionary(p => p.Key.ToString("X4"), p => p.Value);
            previous = state.ToDictionary(p => p.Key, p => p.Value);
            Write(new { snapshot = state.Count, changes });
        };
        int controlWrites = 0;
        manager.DataSent += (packet, description) =>
        {
            if (SoncaBleProtocol.TryParsePacket(packet, out byte command, out _) && command == SoncaBleProtocol.CmdSetParam)
                Interlocked.Increment(ref controlWrites);
        };
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DeviceDiscovered += device =>
        {
            if (device.Address == address && device.ServiceUuids.Contains(SoncaBleProtocol.PrimaryServiceUuid)) seen.TrySetResult(true);
        };
        manager.StartScanning();
        await Task.WhenAny(seen.Task, Task.Delay(6000));
        manager.StopScanning();
        Write(new { advertised = manager.DiscoveredDevices.Select(d => new { d.FormattedAddress, d.Name }) });
        if (!await manager.ConnectAsync(address) || !manager.SupportsMixerControls
            || !await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Read jack-state baseline only"))
            throw new Exception("Cannot open verified C360 BLE status channel; no input controls written.");
        File.WriteAllText(Path.Combine(output, "initial.json"), JsonSerializer.Serialize(previous.ToDictionary(p => p.Key.ToString("X4"), p => p.Value), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "ready.json"), JsonSerializer.Serialize(new { manager.ConnectedDeviceAddress, controlWrites = 0, startedUtc = DateTime.UtcNow }));
        Write(new { ready = true, manager.ConnectedDeviceAddress });
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 180));
        while (DateTime.UtcNow < deadline && manager.IsConnected && !File.Exists(Path.Combine(output, "stop")))
        {
            await Task.Delay(750);
            if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Read jack-state changes only")) break;
        }
        File.WriteAllText(Path.Combine(output, "final.json"), JsonSerializer.Serialize(previous.ToDictionary(p => p.Key.ToString("X4"), p => p.Value), new JsonSerializerOptions { WriteIndented = true }));
        Write(new { complete = true, controlWrites });
        if (controlWrites != 0) throw new Exception("Read-only observation unexpectedly sent a control command.");
    }
}
