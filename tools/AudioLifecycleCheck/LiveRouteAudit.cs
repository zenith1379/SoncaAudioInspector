using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class LiveRouteAudit
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static async Task Run(string directory, string configPath, ulong address, bool noiseIsolation = false)
    {
        Directory.CreateDirectory(directory);
        using var manager = new SoncaBleManager();
        var watch = Stopwatch.StartNew();
        manager.LogMessage += message => { File.AppendAllText(Path.Combine(directory, "ble.log"), $"{watch.Elapsed.TotalSeconds:F3} {message}\n"); if (!message.StartsWith("[RX] HEX")) Console.WriteLine(message); };
        manager.MixerParametersReceived += state => File.AppendAllText(Path.Combine(directory, "feedback.jsonl"), JsonSerializer.Serialize(new { ms = watch.ElapsedMilliseconds, values = state.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) }) + "\n");
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DeviceDiscovered += device => { if (device.Address == address) seen.TrySetResult(true); };
        manager.StartScanning(); await Task.WhenAny(seen.Task, Task.Delay(6000)); manager.StopScanning();
        if (!await manager.ConnectAsync(address) || !manager.SupportsMixerControls || !await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket())) throw new Exception("Cannot read verified C360; no test emitted.");
        var before = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
        File.WriteAllText(Path.Combine(directory, "before.json"), JsonSerializer.Serialize(before.ToDictionary(p => p.Key.ToString("X4"), p => p.Value), Options));
        if (!SoncaBleMixerProfile.TryReadInputSource(before[SoncaBleMixerProfile.InputSource], out byte original)) throw new Exception("No verified source.");
        bool attempted = false;
        try
        {
            attempted = true;
            if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(4), "Select wired LINE IN for physical audit")) throw new Exception("LINE IN not confirmed; no playback.");
            var config = JsonNode.Parse(File.ReadAllText(configPath))!;
            using var enumerator = new MMDeviceEnumerator();
            using var output = enumerator.GetDevice(config["UsbPlaybackDeviceId"]!.GetValue<string>());
            using var input = enumerator.GetDevice(config["RecordingDeviceId"]!.GetValue<string>());
            if (noiseIsolation) { await IsolateNoise(manager, input, directory, before); return; }
            double outputVolume = output.AudioEndpointVolume.MasterVolumeLevelScalar;
            using var engine = new AudioEngine { PlaybackSampleRate = config["PlaybackSampleRate"]!.GetValue<int>(), UseExclusivePlayback = false,
                PlaybackChannel = config["LastPlaybackChannel"]?.GetValue<int?>(), RecordingChannel = config["LastRecordingChannel"]?.GetValue<int?>() };
            AudioEngine.flagSaveFile = false; AudioEngine.flagGenerateSeperateSine = false;
            var runner = new TestRunner(engine) { PlaybackLevelDbFs = config["PlaybackLevelDbfs"]!.GetValue<double>(), UseLogSweepFrequencyResponse = true, EnableNoiseDiagnostics = true,
                LogSweepDurationSeconds = config["LogSweepDurationSeconds"]!.GetValue<double>(), LogSweepVerificationRuns = 1,
                ApplyDistortionCalibration = config["ApplyDistortionCalibration"]!.GetValue<bool>(), UseHarmonicFrequencyAsDistortionReference = config["UseHarmonicFrequencyAsDistortionReference"]!.GetValue<bool>(),
                NormalizeFrequencyResponseToOneKilohertz = config["NormalizeFrequencyResponseToOneKilohertz"]!.GetValue<bool>() };
            int round = 0, captures = 0;
            runner.OnLogMessage += (source, message) => { string line = $"{watch.Elapsed.TotalSeconds:F3} [{source}] {message}"; Console.WriteLine(line); File.AppendAllText(Path.Combine(directory, "runner.log"), line + "\n"); };
            runner.OnMeasurementCaptured += (samples, _, _, _, _) => { captures++; Save(Path.Combine(directory, $"round-{round}-capture-{captures}.wav"), samples, engine.RecordingSampleRate); };
            runner.OnSweepResultReady += result => File.WriteAllText(Path.Combine(directory, $"round-{round}-summary.json"), JsonSerializer.Serialize(new { result.Validity, result.SignalLevelDbFs, result.NoiseFloorDbFs, result.EstimatedSnrDb, result.DirectArrivalMs, result.EstimatedClockDriftPpm, result.IsClipped, result.SweepHarmonics, summary = runner.BuildSweepDistortionSummary(result) }, Options));
            for (round = 1; round <= 2; round++)
            {
                captures = 0;
                using var loopback = new WasapiLoopbackCapture(output);
                using var rendered = new WaveFileWriter(Path.Combine(directory, $"round-{round}-render.wav"), loopback.WaveFormat);
                loopback.DataAvailable += (_, e) => rendered.Write(e.Buffer, 0, e.BytesRecorded);
                loopback.StartRecording();
                var timer = Stopwatch.StartNew();
                try { await runner.RunTestAsync(output, input); }
                finally { loopback.StopRecording(); }
                File.WriteAllText(Path.Combine(directory, $"round-{round}-completion.json"), JsonSerializer.Serialize(new { seconds = timer.Elapsed.TotalSeconds, captures, runner.LastMeasuredThdPercent, runner.LastPreflightWarning, runner.LastFeqInvalidReason, runner.ThdAcquisitionInvalid, steps = runner.Steps }, Options));
            }
            await LiveDistortionCheck.Run(Path.Combine(directory, "tone-and-sweep"), configPath);
            if (Math.Abs(outputVolume - output.AudioEndpointVolume.MasterVolumeLevelScalar) > 1e-6) throw new Exception("Windows level changed externally during audit.");
        }
        finally
        {
            bool restored = !attempted || await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Restore original source after physical audit");
            if (!restored) restored = await manager.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Retry original source restore");
            var after = manager.MixerState;
            File.WriteAllText(Path.Combine(directory, "restoration.json"), JsonSerializer.Serialize(new { restored, original, changed = before.Where(p => after.GetValueOrDefault(p.Key) != p.Value).ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = after.GetValueOrDefault(p.Key) }) }, Options));
            if (!restored) throw new Exception("Source restore not confirmed.");
        }
    }
    internal static async Task IsolateNoise(SoncaBleManager manager, MMDevice input, string directory, Dictionary<ushort, ushort> before)
    {
        var rows = new List<object>();
        async Task Capture(string label)
        {
            using var capture = new WasapiCapture(input, false, 100);
            var blocks = new List<byte>(); object gate = new();
            capture.DataAvailable += (_, e) => { lock (gate) blocks.AddRange(e.Buffer.Take(e.BytesRecorded)); };
            capture.StartRecording(); await Task.Delay(2200); capture.StopRecording();
            byte[] bytes; lock (gate) bytes = blocks.ToArray();
            using (var wav = new WaveFileWriter(Path.Combine(directory, label + ".wav"), capture.WaveFormat)) wav.Write(bytes, 0, bytes.Length);
            var channels = Enumerable.Range(0, capture.WaveFormat.Channels).Select(channel => {
                float[] samples = CaptureSampleDecoder.Decode(bytes, bytes.Length, capture.WaveFormat, 1, channel);
                return new { channel, rmsDbFs = 10 * Math.Log10(samples.Select(v => (double)v * v).Average() + 1e-20), peak = samples.Select(Math.Abs).Max() };
            }).ToArray();
            rows.Add(new { label, channels }); Console.WriteLine(JsonSerializer.Serialize(new { label, channels }));
        }
        await Capture("line-baseline");
        foreach (ushort mute in new[] { SoncaBleMixerProfile.MusicMute, SoncaBleMixerProfile.LiveMute, SoncaBleMixerProfile.MasterMute })
        {
            try
            {
                if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildParamPacket(0, mute, 1), "Isolate background by mute")) throw new Exception("Mute unconfirmed.");
                await Capture($"mute-{mute:X4}");
            }
            finally
            {
                if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildParamPacket(0, mute, before[mute]), "Restore baseline mute")) throw new Exception("Mute restoration failed.");
            }
        }
        File.WriteAllText(Path.Combine(directory, "noise-isolation.json"), JsonSerializer.Serialize(rows, Options));
    }
    static void Save(string path, float[] samples, int rate) { using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)); writer.WriteSamples(samples, 0, samples.Length); }
}
