using System.IO;
using System.Reflection;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class C360UsbRouteChecks
{
    public static async Task Run(string directory, string configFile, ulong address)
    {
        Directory.CreateDirectory(directory);
        var json = new JsonSerializerOptions { WriteIndented = true };
        using var enumeration = new MMDeviceEnumerator();
        var config = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configFile))!;
        var io = config.models.Single(m => C360ModelDefinition.Matches(m.testItems?.InOut)).testItems.InOut;
        var devices = enumeration.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToArray();
        try
        {
            using var output = enumeration.GetDevice(devices.Single(d => d.DataFlow == DataFlow.Render && d.FriendlyName == io.Devices.Input["USB A"]).ID);
            using var input = enumeration.GetDevice(devices.Single(d => d.DataFlow == DataFlow.Capture && d.FriendlyName == io.Devices.Output["LINE OUT"]).ID);
            int initialOutputRate = AudioSampleRateSetup.ReadDefaultFormat(output.ID).SampleRate;
            int initialInputRate = AudioSampleRateSetup.ReadDefaultFormat(input.ID).SampleRate;
            void SetRate(string id, int rate) => typeof(AudioEngine).Assembly.GetType("SoncaAudioInspector.WindowsEndpointPolicy")!
                .GetMethod("SetSampleRate", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { id, rate });
            using var manager = new SoncaBleManager();
            var logGate = new object();
            manager.LogMessage += s => { lock (logGate) File.AppendAllText(Path.Combine(directory, "ble.log"), s + Environment.NewLine); };
            if (!await manager.ConnectAsync(address) || !manager.SupportsMixerControls) throw new Exception("C360 control not verified; no stimulus.");
            await Task.Delay(1500); // Initial INFO/mixer notifications must finish before the first GET.
            using var engine = new AudioEngine { UseExclusivePlayback = false, RecordingGain = 1 };
            var bench = new C360AudioBench(engine, manager, directory);
            var baseline = await bench.ReadAsync(default);
            var registers = new[] { SoncaBleMixerProfile.MicMute, SoncaBleMixerProfile.MicVolume, SoncaBleMixerProfile.EchoEnable,
                SoncaBleMixerProfile.ReverbEnable, SoncaBleMixerProfile.MusicMute, SoncaBleMixerProfile.InputSource };
            foreach (var reg in registers) if (!baseline.ContainsKey(reg)) throw new Exception("Missing baseline; no stimulus.");
            var rows = new List<object>();
            var errors = new List<string>();
            try
            {
                using var levels = new WindowsAudioLevels(output, input, new TestConfig { WindowsPlaybackPercent = 100, WindowsRecordingPercent = 100 });
                await bench.WriteAsync(SoncaBleMixerProfile.MicMute, 1, default);
                await bench.WriteAsync(SoncaBleMixerProfile.MicVolume, 0, default);
                await bench.WriteAsync(SoncaBleMixerProfile.EchoEnable, 0, default);
                await bench.WriteAsync(SoncaBleMixerProfile.ReverbEnable, 0, default);
                await bench.WriteAsync(SoncaBleMixerProfile.MusicMute, 0, default);
                foreach (var rates in new[] { (Output: initialOutputRate, Input: initialInputRate), (Output: 48000, Input: 48000), (Output: 44100, Input: 44100) }.Distinct())
                {
                    try { SetRate(output.ID, rates.Output); SetRate(input.ID, rates.Input); }
                    catch (Exception ex) { errors.Add($"{rates}: {ex.GetBaseException().Message}"); continue; }
                    foreach (ushort source in new ushort[] { 1, 4, 0 })
                    {
                        if (!SoncaBleMixerProfile.CanSelectInputSource(manager.MixerState, (byte)source)) continue;
                        await bench.WriteAsync(SoncaBleMixerProfile.InputSource, source, default);
                        var state = await bench.ReadAsync(default);
                        if (state[SoncaBleMixerProfile.InputSource] != source) throw new Exception("Source not confirmed; no stimulus.");
                        engine.PlaybackSampleRate = rates.Output;
                        var a = new List<float>(); var b = new List<float>(); var computer = new List<float>();
                        using var loopback = new WasapiLoopbackCapture(output);
                        loopback.DataAvailable += (_, e) => { lock (computer) for (int i = 0; i + 3 < e.BytesRecorded; i += 4) computer.Add(BitConverter.ToSingle(e.Buffer, i)); };
                        loopback.StartRecording();
                        var tone = Enumerable.Range(0, rates.Output * 2).Select(i => (float)(Math.Pow(10, -24.0/20) * Math.Sin(2*Math.PI*997*i/rates.Output))).ToArray();
                        float[] captured;
                        try { captured = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 997, 2,
                            customSweepSamples: tone, stereoObserver: (x,y) => { a.AddRange(x); b.AddRange(y); }); }
                        finally { loopback.StopRecording(); }
                        double Db(IEnumerable<float> samples) { var values = samples.ToArray(); return values.Length == 0 ? -180 : 10*Math.Log10(Math.Max(1e-18, values.Select(v=>(double)v*v).Average())); }
                        var row = new { source, rates.Output, rates.Input, actualInputRate = engine.RecordingSampleRate,
                            mixedDbFs = Db(captured), channel1DbFs = Db(a), channel2DbFs = Db(b), computerRenderDbFs = Db(computer) };
                        rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                        using var wave = new WaveFileWriter(Path.Combine(directory, $"usb-{rates.Output}-{rates.Input}-source-{source}.wav"), WaveFormat.CreateIeeeFloatWaveFormat(engine.RecordingSampleRate, 1));
                        wave.WriteSamples(captured, 0, captured.Length);
                    }
                }
            }
            finally
            {
                engine.StopAllAudio();
                foreach (var reg in registers.Reverse()) try { await bench.WriteAsync(reg, baseline[reg], default); } catch (Exception ex) { errors.Add("Restore " + reg + ": " + ex.Message); }
                SetRate(output.ID, initialOutputRate); SetRate(input.ID, initialInputRate);
                var restored = await bench.ReadAsync(default);
                var changed = baseline.Where(p => restored.GetValueOrDefault(p.Key) != p.Value).ToDictionary(p=>p.Key.ToString("X4"), p=>new { before=p.Value, after=restored.GetValueOrDefault(p.Key) });
                bool formatsRestored = AudioSampleRateSetup.ReadDefaultFormat(output.ID).SampleRate == initialOutputRate && AudioSampleRateSetup.ReadDefaultFormat(input.ID).SampleRate == initialInputRate;
                File.WriteAllText(Path.Combine(directory, "route-check.json"), JsonSerializer.Serialize(new { initialOutputRate, initialInputRate, formatsRestored, changed, rows, errors }, json));
                if (changed.Count > 0 || !formatsRestored) throw new Exception("USB diagnostic restoration failed.");
                Console.WriteLine("PASS diagnostic cleanup: DSP and original rates restored. Signal results recorded separately.");
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }
}
