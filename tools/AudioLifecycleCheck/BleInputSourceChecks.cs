using System.IO;
using System.Text.Json;
using SoncaAudioInspector;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

internal static class BleInputSourceChecks
{
    public static async Task Run(string output, bool exercise, string? portProbeConfigPath = null, int holdSeconds = 0, byte holdSource = 4, ulong? knownAddress = null, bool deferSourceRead = false, bool probeAvailability = false, string? noiseConfigPath = null, string? distortionConfigPath = null, byte? leaveSource = null, string? c360SetupPath = null, bool probeC360Line = false, bool exerciseOptions = false)
    {
        Directory.CreateDirectory(output);
        using var manager = new SoncaBleManager();
        manager.LogMessage += Console.WriteLine;
        var peers = await SoncaBleManager.ConnectedAudioPeersAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { audioPeers = peers }));
        manager.StartScanning(); await Task.Delay(6000); manager.StopScanning();
        var preferred = SoncaBleDiscovery.Preferred(manager.DiscoveredDevices, peers);
        var connected = new HashSet<ulong>();
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        foreach (var info in infos.Where(i => SoncaBleProtocol.IsLikelySoncaDevice(i.Name)))
        {
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device?.ConnectionStatus == BluetoothConnectionStatus.Connected) connected.Add(device.BluetoothAddress);
            }
            catch (Exception ex) { Console.WriteLine("Unavailable LE peer: " + ex.Message); }
        }
        ulong address = preferred?.Address ?? (connected.Count == 1 ? connected.Single() : 0);
        if (address == 0 && knownAddress.HasValue && (peers.Any(p => p.Address == knownAddress.Value || (p.Address ^ knownAddress.Value) == 0x010000000000UL)
            || manager.DiscoveredDevices.Any(d => d.Address == knownAddress.Value && d.ServiceUuids.Contains(SoncaBleProtocol.PrimaryServiceUuid))))
            address = knownAddress.Value;
        Console.WriteLine(JsonSerializer.Serialize(new { connectedBle = connected.Select(a => a.ToString("X12")),
            advertised = manager.DiscoveredDevices.Where(d => d.IsLikelySonca).Select(d => new { d.Name, d.FormattedAddress }), selected = address.ToString("X12") }));
        if (address == 0 || !await manager.ConnectAsync(address)) throw new Exception("No unique associated/connected Sonca BLE controller; no control written.");
        if (!manager.SupportsMixerControls || !await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Read input source and capabilities"))
            throw new Exception("No verified C360 state; no control written.");
        var baseline = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
        File.WriteAllText(Path.Combine(output, "before.json"), JsonSerializer.Serialize(baseline.ToDictionary(p => p.Key.ToString("X4"), p => p.Value), new JsonSerializerOptions { WriteIndented = true }));
        if (!baseline.TryGetValue(SoncaBleMixerProfile.InputSource, out ushort raw)
            || !SoncaBleMixerProfile.TryReadInputSource(raw, out byte original)) throw new Exception("No valid source readback; no write.");
        byte[] available = Enumerable.Range(0, 6).Select(i => (byte)i).Where(i => SoncaBleMixerProfile.CanSelectInputSource(baseline, i)).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { original, raw, capabilities = baseline[SoncaBleMixerProfile.InputCapabilities], available,
            names = available.Select(SoncaBleMixerProfile.InputSourceName) }));
        if (exerciseOptions)
        {
            Console.WriteLine("Firmware: " + manager.FirmwareIdentity);
            var changes = new List<object>();
            foreach (var option in Enum.GetValues<SoncaMixerOption>())
            {
                ushort mask = SoncaBleMixerProfile.OptionMask(option), flags = baseline[SoncaBleMixerProfile.InputCapabilities];
                if (!SoncaBleMixerProfile.SupportsOption(flags, option)) continue;
                bool initial = (flags & mask) != 0;
                try
                {
                    bool confirmed = await manager.SetOptionAsync(option, !initial);
                    changes.Add(new { option = option.ToString(), confirmed, before = flags, after = manager.MixerState.GetValueOrDefault(SoncaBleMixerProfile.InputCapabilities) });
                    if (!confirmed) throw new Exception("Option readback failed: " + option);
                }
                finally { if (!await manager.SetOptionAsync(option, initial)) throw new Exception("Option restoration failed: " + option); }
            }
            var diff = baseline.Where(p => manager.MixerState.GetValueOrDefault(p.Key) != p.Value).ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = manager.MixerState.GetValueOrDefault(p.Key) });
            if(manager.FirmwareIdentity!=SoncaBuildIdentity.Firmware(new(SoncaBleProtocol.CmdInfo,0,manager.FirmwareInfoPayload)))throw new Exception("Firmware label differs from raw INFO");
            File.WriteAllText(Path.Combine(output, "options.json"), JsonSerializer.Serialize(new { manager.FirmwareIdentity, infoPayloadHex=Convert.ToHexString(manager.FirmwareInfoPayload), changes, diff }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS options and restoration diff=" + diff.Count); if (c360SetupPath == null) return;
        }
        if (c360SetupPath != null)
        {
            var setup = JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(c360SetupPath))!;
            using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var bench = new C360AudioBench(engine, manager, output);
            try
            {
                var results = await new C360ElectronicSuite().RunAsync(probeC360Line ? new C360LineProbe(bench, engine, output) : bench, setup, new InlineProgress(), timeout.Token);
                File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { setup,
                    results = results.Select(r => new { r.StageName, r.Status, r.Summary }) }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket());
                var changed = baseline.Where(p => manager.MixerState.GetValueOrDefault(p.Key) != p.Value)
                    .ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = manager.MixerState.GetValueOrDefault(p.Key) });
                File.WriteAllText(Path.Combine(output, "restoration.json"), JsonSerializer.Serialize(new { changed }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Restoration changes: " + changed.Count);
            }
            return;
        }
        if (leaveSource.HasValue)
        {
            bool confirmed = false;
            try
            {
                byte target = leaveSource.Value;
                if (!SoncaBleMixerProfile.CanSelectInputSource(baseline, target)
                    || !await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(target), "Select measurement input")) throw new Exception("Input selection not confirmed.");
                await Task.Delay(350);
                confirmed = await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket())
                    && SoncaBleMixerProfile.TryReadInputSource(manager.MixerState[SoncaBleMixerProfile.InputSource], out byte current) && current == target;
                if (!confirmed) throw new Exception("Input did not hold; restoring original.");
                var changed = baseline.Where(p => manager.MixerState.GetValueOrDefault(p.Key) != p.Value).ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = manager.MixerState.GetValueOrDefault(p.Key) });
                File.WriteAllText(Path.Combine(output, "selection.json"), JsonSerializer.Serialize(new { original, selected = target, confirmed, changed }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (!confirmed && !await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore after unconfirmed selection")) throw new Exception("Restore not confirmed."); }
            return;
        }
        if (noiseConfigPath != null || distortionConfigPath != null)
        {
            try
            {
                if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(4), "Select LINE IN for background isolation")) throw new Exception("LINE IN not confirmed.");
                if (distortionConfigPath != null) await LiveDistortionCheck.Run(Path.Combine(output, "measurement"), distortionConfigPath);
                else
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var config = JsonNode.Parse(File.ReadAllText(noiseConfigPath!))!;
                    using var input = enumerator.GetDevice(config["RecordingDeviceId"]!.GetValue<string>());
                    await LiveRouteAudit.IsolateNoise(manager, input, output, baseline);
                }
            }
            finally
            {
                bool restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore source after background isolation");
                File.WriteAllText(Path.Combine(output, "restoration.json"), JsonSerializer.Serialize(new { restored, original,
                    changed = baseline.Where(p => manager.MixerState.GetValueOrDefault(p.Key) != p.Value).ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = manager.MixerState.GetValueOrDefault(p.Key) }) }));
                if (!restored) throw new Exception("Source restore unconfirmed.");
            }
            return;
        }
        if (portProbeConfigPath != null) { await ProbePorts(manager, output, portProbeConfigPath, original); return; }
        if (probeAvailability) { await ProbeAvailability(manager, output, original, available, baseline); return; }
        if (holdSeconds > 0) { await ObserveSource(manager, output, original, holdSource, holdSeconds, deferSourceRead); return; }
        if (!exercise) return;
        var verified = new List<byte>();
        bool attempted = false;
        try
        {
            foreach (byte target in available.Where(i => i != original))
            {
                attempted = true;
                if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(target), "Verify input source: " + SoncaBleMixerProfile.InputSourceName(target)))
                    throw new Exception("Source switch was not confirmed: " + target);
                verified.Add(target);
                Console.WriteLine("VERIFIED SOURCE: " + SoncaBleMixerProfile.InputSourceName(target));
            }
        }
        finally
        {
            if (attempted)
            {
                bool restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore original input source");
                if (!restored) restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Retry original source restore");
                if (!restored) throw new Exception("Original input source restore failed; check hardware source.");
            }
            if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Verify final source state")) throw new Exception("Final state read failed.");
            var after = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
            var changed = baseline.Where(p => !after.TryGetValue(p.Key, out ushort value) || p.Value != value).ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = after.GetValueOrDefault(p.Key) });
            File.WriteAllText(Path.Combine(output, "after.json"), JsonSerializer.Serialize(after.ToDictionary(p => p.Key.ToString("X4"), p => p.Value), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { original, verified, changed }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { originalRestored = manager.MixerState[SoncaBleMixerProfile.InputSource] == raw, verified, changed }));
        }
    }

    private static async Task ProbeAvailability(SoncaBleManager manager, string output, byte original, byte[] candidates, Dictionary<ushort, ushort> baseline)
    {
        var rows = new List<object>();
        bool restored = false;
        try
        {
            foreach (byte source in candidates)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool opens = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(source), "Check source availability: " + SoncaBleMixerProfile.InputSourceName(source));
                var row = new { source, name = SoncaBleMixerProfile.InputSourceName(source), opens, actual = manager.MixerState.GetValueOrDefault(SoncaBleMixerProfile.InputSource), seconds = watch.Elapsed.TotalSeconds };
                rows.Add(row); Console.WriteLine("SOURCE AVAILABILITY " + JsonSerializer.Serialize(row));
            }
        }
        finally
        {
            restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore original source after availability check");
            if (!restored) restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Retry source restore");
            var after = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
            var changed = baseline.Where(p => !after.TryGetValue(p.Key, out ushort value) || p.Value != value).ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = after.GetValueOrDefault(p.Key) });
            File.WriteAllText(Path.Combine(output, "source-availability.json"), JsonSerializer.Serialize(new { original, restored, rows, changed }, new JsonSerializerOptions { WriteIndented = true }));
            if (!restored) throw new Exception("Source restore failed after availability test.");
        }
    }

    private sealed class InlineProgress : IProgress<C360ElectronicResult>
    {
        public void Report(C360ElectronicResult r) => Console.WriteLine(JsonSerializer.Serialize(new { r.StageName, r.Status, r.Summary }));
    }

    private static async Task ObserveSource(SoncaBleManager manager, string output, byte original, byte target, int seconds, bool deferRead)
    {
        if (!SoncaBleMixerProfile.CanSelectInputSource(manager.MixerState, target)) throw new Exception("Unsupported hold target; no write.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var events = new System.Collections.Concurrent.ConcurrentQueue<object>();
        manager.MixerParametersReceived += updates =>
        {
            if (updates.TryGetValue(SoncaBleMixerProfile.InputSource, out ushort value))
            {
                var row = new { kind = "feedback", elapsed = watch.Elapsed.TotalSeconds, value };
                events.Enqueue(row); Console.WriteLine("SOURCE OBSERVATION " + JsonSerializer.Serialize(row));
            }
        };
        bool confirmed = false, restored = false;
        try
        {
            if (deferRead)
            {
                // Diagnostic isolation of SET versus GET side effects, guarded by the
                // verified C360 profile/state/capabilities above. No arbitrary register.
                var transport = typeof(SoncaBleManager).GetMethod("WriteTransportAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                confirmed = await (Task<bool>)transport.Invoke(manager, new object[] { SoncaBleProtocol.BuildInputSourcePacket(target), "Observe source SET with no immediate config query" })!;
                await Task.Delay(5000);
            }
            else confirmed = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(target), "Observe sustained input source");
            Console.WriteLine("INITIAL CONFIRMATION " + confirmed);
            var started = DateTime.UtcNow;
            foreach (int delay in new[] { 500, 1000, 2000, 4000, 8000, 12000, seconds * 1000 }.Distinct().Order().Where(v => v <= seconds * 1000))
            {
                var wait = started.AddMilliseconds(delay) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                bool read = await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Observe source without control write");
                var row = new { kind = "snapshot", elapsed = watch.Elapsed.TotalSeconds, read, value = manager.MixerState.GetValueOrDefault(SoncaBleMixerProfile.InputSource) };
                events.Enqueue(row); Console.WriteLine("SOURCE OBSERVATION " + JsonSerializer.Serialize(row));
            }
        }
        finally
        {
            restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore original source after sustained observation");
            if (!restored) restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Retry source restore");
            File.WriteAllText(Path.Combine(output, "source-retention.json"), JsonSerializer.Serialize(new { original, target, deferRead, confirmed, restored, events }, new JsonSerializerOptions { WriteIndented = true }));
            if (!restored) throw new Exception("Source restoration unconfirmed.");
        }
    }

    private static async Task ProbePorts(SoncaBleManager manager, string output, string configPath, byte original)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            using (endpoint) Console.WriteLine(JsonSerializer.Serialize(new { endpoint.FriendlyName, endpoint.ID, flow = endpoint.DataFlow.ToString(), format = endpoint.AudioClient.MixFormat.ToString() }));
        }
        using var playback = enumerator.GetDevice(config["UsbPlaybackDeviceId"]!.GetValue<string>());
        using var recording = enumerator.GetDevice(config["RecordingDeviceId"]!.GetValue<string>());
        if (playback.State != DeviceState.Active || recording.State != DeviceState.Active || playback.AudioClient.MixFormat.Channels < 2
            || AudioEngine.IsWindowsMonoAudioEnabled()) throw new Exception("Need the selected active stereo USB output and input, with Windows Mono audio off; no test emitted.");
        int rate = config["PlaybackSampleRate"]!.GetValue<int>();
        double level = Math.Min(-24, config["PlaybackLevelDbfs"]!.GetValue<double>());
        using var engine = new AudioEngine { PlaybackSampleRate = rate, UseExclusivePlayback = false,
            RecordingChannel = config["LastRecordingChannel"]?.GetValue<int?>() };
        AudioEngine.flagSaveFile = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var quiet = await engine.CaptureSilenceAsync(recording, null, 1, cancellation.Token);
        double noise = 10 * Math.Log10(quiet.DutSamples.Select(v => (double)v * v).Average() + 1e-18);
        var rows = new List<object>();
        try
        {
            foreach (byte source in new byte[] { 0, 4 })
            {
                if (!SoncaBleMixerProfile.CanSelectInputSource(manager.MixerState, source)
                    || !await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(source), "Probe source: " + SoncaBleMixerProfile.InputSourceName(source)))
                    throw new Exception("Source was not available/confirmed for port probe.");
                await Task.Delay(350, cancellation.Token);
                foreach (int channel in new[] { 0, 1 })
                {
                    engine.PlaybackChannel = channel;
                    double frequency = channel == 0 ? 503 : 1271;
                    var tone = new float[rate * 2]; double amplitude = Math.Pow(10, level / 20);
                    for (int i = 0; i < tone.Length; i++) tone[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate)
                        * Math.Min(1, Math.Min(i, tone.Length - 1 - i) / (rate * .01)));
                    float[] captured = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, frequency, 2,
                        cancellationToken: cancellation.Token, customSweepSamples: tone);
                    int captureRate = engine.RecordingSampleRate, length = Math.Min(captureRate, captured.Length);
                    var stable = captured.Skip(Math.Max(0, (captured.Length - length) / 2)).Take(length).ToArray();
                    var toneResult = AdvancedAudioMeasurement.AnalyzeTone(stable, captureRate, frequency, maxHarmonic: 5);
                    double toneDbFs = 20 * Math.Log10(Math.Max(1e-12, toneResult.FundamentalRms));
                    bool detected = toneResult.IsValid && toneResult.PeakSample < .995 && Math.Abs(toneResult.FundamentalFrequencyHz - frequency) < 10 && toneDbFs - noise >= 12;
                    var row = new { source = SoncaBleMixerProfile.InputSourceName(source), channel = channel + 1, expectedHz = frequency,
                        measuredHz = toneResult.FundamentalFrequencyHz, toneDbFs, noiseDbFs = noise, snrDb = toneDbFs - noise,
                        detected, playback = playback.FriendlyName, recording = recording.FriendlyName, level };
                    rows.Add(row); Console.WriteLine("PORT PROBE " + JsonSerializer.Serialize(row));
                    using var wav = new WaveFileWriter(Path.Combine(output, $"source-{source}-out-{channel + 1}.wav"), WaveFormat.CreateIeeeFloatWaveFormat(captureRate, 1));
                    wav.WriteSamples(captured, 0, captured.Length);
                    if (toneResult.PeakSample >= .995) throw new Exception("Capture clipped; stop probing.");
                }
            }
        }
        finally
        {
            engine.StopAllAudio();
            bool restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore original source after port probe");
            if (!restored) restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Retry original source restore");
            File.WriteAllText(Path.Combine(output, "port-probe.json"), JsonSerializer.Serialize(new { originalSource = original, restored, rows }, new JsonSerializerOptions { WriteIndented = true }));
            if (!restored) throw new Exception("Original source restore failed after probe.");
        }
    }
}
