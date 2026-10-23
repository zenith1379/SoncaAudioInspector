using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

internal static class FrequencyShiftChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Logic()
    {
        const int rate = 44100;
        var random = new Random(71);
        float[] Noise(double amplitude) => Enumerable.Range(0, (int)(rate * .6)).Select(_ => (float)((random.NextDouble() - .5) * amplitude)).ToArray();
        float[] Tone(double ppm, double shift, bool missing = false) => Enumerable.Range(0, (int)(rate * 2.7)).Select(i =>
            (float)(.08 / 3 * FrequencyShiftDiagnostics.CarriersHz.Take(missing ? 2 : 3).Sum(f => Math.Sin(2 * Math.PI * (f * (1 + ppm / 1e6) + shift) * i / rate)))).ToArray();
        var quiet = Noise(1e-5); var settings = new FrequencyShiftSettings();
        var off = FrequencyShiftDiagnostics.Measure(Tone(100, 0), quiet, rate);
        var on = FrequencyShiftDiagnostics.Measure(Tone(250, -5), quiet, rate);
        var result = FrequencyShiftDiagnostics.Compare(off, on, settings);
        Check(result.IsValid && result.Detected == true && Math.Abs(result.ShiftHz + 5) < .05 && Math.Abs(result.RelativeClockPpm - 150) < 20,
            "Negative Hz shift with clock change not separated: " + result.Summary);
        result = FrequencyShiftDiagnostics.Compare(off, FrequencyShiftDiagnostics.Measure(Tone(-200, 4), quiet, rate), settings);
        Check(result.IsValid && result.Detected == true && Math.Abs(result.ShiftHz - 4) < .05, "Positive shift missed");
        result = FrequencyShiftDiagnostics.Compare(off, FrequencyShiftDiagnostics.Measure(Tone(600, 0), quiet, rate), settings);
        Check(result.IsValid && result.Detected == false && Math.Abs(result.ShiftHz) < .05, "Clock-only change reported as frequency shifting");
        var commonOff = FrequencyShiftDiagnostics.Measure(Tone(100, -3), quiet, rate);
        var commonOn = FrequencyShiftDiagnostics.Measure(Tone(300, -3), quiet, rate);
        result = FrequencyShiftDiagnostics.Compare(commonOff, commonOn, settings);
        Check(result.IsValid && result.Detected == true && result.FbxChangeDetected == false && Math.Abs(result.OnShiftHz + 3) < .05,
            "Common path shift was confused with the change caused by FBX");
        Check(FrequencyShiftDiagnostics.Status(result, settings with { ExpectedDetected = true }) == "PASS"
            && FrequencyShiftDiagnostics.Status(result, settings with { ExpectedFbxChange = true }) == "FAIL",
            "Common MIC shift incorrectly passes a requirement for an FBX-caused change");
        Check(!FrequencyShiftDiagnostics.Compare(off, FrequencyShiftDiagnostics.Measure(Tone(0, 0, true), quiet, rate), settings).IsValid, "Missing carrier accepted");
        Check(!FrequencyShiftDiagnostics.Compare(off, on.Select((p,i) => p with { MeasuredHz = p.MeasuredHz + (i == 1 ? 3 : 0) }).ToArray(), settings).IsValid, "Nonuniform carrier changes called constant shift");
        Check(!FrequencyShiftDiagnostics.Compare(off, on.Select(p => p with { NarrowBandSnrDb = 0 }).ToArray(), settings).IsValid, "Weak carriers accepted");
        Check(FrequencyShiftDiagnostics.Measure(new float[rate * 3], quiet, rate).Count == 0, "Silence accepted");
        Check(FrequencyShiftDiagnostics.Measure(Enumerable.Repeat(1f, rate * 3).ToArray(), quiet, rate).Count == 0, "Clipping accepted");
        Check(!FrequencyShiftDiagnostics.Compare(off, off.Select(p => p with { MeasuredHz = p.MeasuredHz + .5 }).ToArray(), settings).IsValid, "Threshold uncertainty hidden");
        var stimulus = FrequencyShiftDiagnostics.Generate(rate, -18, settings);
        Check(stimulus.Max(Math.Abs) <= Math.Pow(10, -18.0 / 20), "Multitone exceeded configured peak level");
        float[] Varying(bool additive) => Enumerable.Range(0, (int)(rate * 2.7)).Select(i =>
        {
            double t = i / (double)rate, clockTime = t + .0004 * Math.Sin(2 * Math.PI * .7 * t) / (2 * Math.PI * .7);
            double shiftPhase = -3 * t + (additive ? .8 * Math.Sin(2 * Math.PI * .7 * t) / (2 * Math.PI * .7) : 0);
            return (float)(.08 / 3 * FrequencyShiftDiagnostics.CarriersHz.Sum(f => Math.Sin(2 * Math.PI * (f * clockTime + shiftPhase))));
        }).ToArray();
        var clockVarying = FrequencyShiftDiagnostics.Measure(Varying(false), quiet, rate);
        result = FrequencyShiftDiagnostics.Compare(clockVarying, clockVarying, settings);
        Check(result.IsValid && result.Detected == true && Math.Abs(result.OnShiftHz + 3) < .1, "Common segment clock variation confused with additive shift instability");
        var modulated = FrequencyShiftDiagnostics.Measure(Varying(true), quiet, rate);
        Check(!FrequencyShiftDiagnostics.Compare(modulated, modulated, settings).IsValid, "Actual additive frequency modulation was corrected away");
        float[] UnequalClockTransient() => Enumerable.Range(0, (int)(rate * 5.8)).Select(i =>
        {
            double t = i / (double)rate;
            if (t > 5) return 0f;
            double clockTime = t + (t > 3.6 ? .001 * (t - 3.6) : 0);
            return (float)(.08 / 3 * FrequencyShiftDiagnostics.CarriersHz.Select((f,c) =>
                Math.Pow(10, -(c == 2 ? 28 : 10) * Math.Min(1,t/4) / 20) * Math.Sin(2*Math.PI*(f*clockTime-3*t))).Sum());
        }).ToArray();
        var transient = FrequencyShiftDiagnostics.Measure(UnequalClockTransient(), quiet, rate, 5);
        result = FrequencyShiftDiagnostics.Compare(transient, transient, settings);
        Check(result.IsValid && result.Detected == true && Math.Abs(result.OnShiftHz + 3) < .05,
            "Unequal carrier attenuation biased the common clock transient into additive-shift instability");
        var wobbling = off.Select(p => p with { StabilityHz = .281 }).ToArray();
        string reason = FrequencyShiftDiagnostics.Compare(wobbling, on, settings).Summary;
        Check(reason.Contains("FBX TẮT") && reason.Contains("dao động") && !reason.Contains("SNR cục bộ"), "Stable-SNR frequency jitter was incorrectly called missing/noisy tones");
        var good = new FrequencyShiftCaptureAnalysis(off, [], 5, .8, -6, false);
        var bad = good with { Carriers = wobbling, Problems = FrequencyShiftDiagnostics.CarrierProblems(wobbling) };
        int calls = 0;
        var selected = FrequencyShiftDiagnostics.CaptureQualifiedAsync((attempt, _) => { calls++; return Task.FromResult(attempt == 1 ? bad : good); }, default).GetAwaiter().GetResult();
        Check(calls == 2 && selected.IsValid, "Transient state was not reacquired");
        calls = 0;
        selected = FrequencyShiftDiagnostics.CaptureQualifiedAsync((_, _) => { calls++; return Task.FromResult(bad); }, default).GetAwaiter().GetResult();
        Check(calls == 2 && !selected.IsValid, "Persistent bad capture was accepted or retry is unbounded");
        calls = 0;
        selected = FrequencyShiftDiagnostics.CaptureQualifiedAsync((_, _) => { calls++; return Task.FromResult(bad with { Clipped = true }); }, default).GetAwaiter().GetResult();
        Check(calls == 1 && !selected.IsValid, "Clipping caused an automatic repeat at the same level");
        var attenuated = FrequencyShiftDiagnostics.Compare(commonOff, commonOn.Select(p => p with { LevelDbFs = p.LevelDbFs - 20 }).ToArray(), settings);
        Check(attenuated.IsValid && attenuated.FbxChangeDetected == false && attenuated.ToneLevelChanges.All(p => Math.Abs(p.ChangeDb + 20) < .01)
            && attenuated.Summary.Contains("Mức tone"), "FBX attenuation confused with an additive frequency shift");
        Console.WriteLine("PASS frequency shift ±Hz, additive shift versus clock ratio, multi-carrier consistency, narrow-band SNR, missing carrier, silence, clipping, boundary uncertainty and stimulus peak limit. Synthetic only.");
    }
    public static void Replay(string directory, string output)
    {
        float[] Read(string name) { using var reader = new NAudio.Wave.WaveFileReader(Path.Combine(directory,name)); var values = new List<float>(); float[]? frame; while ((frame = reader.ReadNextSampleFrame()) != null) values.Add(frame[0]); return values.ToArray(); }
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"MicFrequencyShift-result.json")));
        double duration = saved.RootElement.GetProperty("settings").GetProperty("DurationSeconds").GetDouble();
        int rate = saved.RootElement.GetProperty("sampleRate").GetInt32();
        var off = FrequencyShiftDiagnostics.AnalyzeCapture(Read("MicFrequencyShift-off.wav"), Read("MicFrequencyShift-off-quiet.wav"), rate, duration);
        var on = FrequencyShiftDiagnostics.AnalyzeCapture(Read("MicFrequencyShift-on.wav"), Read("MicFrequencyShift-on-quiet.wav"), rate, duration);
        var result = FrequencyShiftDiagnostics.Compare(off.Carriers, on.Carriers, new());
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output,"replayed.json"), JsonSerializer.Serialize(new { off, on, result }, C360Reference.Json)); Console.WriteLine(result.Summary);
    }
    public static void Audit(string directory)
    {
        float[] Read(string name) { using var reader = new NAudio.Wave.WaveFileReader(Path.Combine(directory,name)); var values = new List<float>(); float[]? frame; while ((frame = reader.ReadNextSampleFrame()) != null) values.Add(frame[0]); return values.ToArray(); }
        var measured = new List<IReadOnlyList<FrequencyShiftCarrier>>();
        foreach (string state in new[] { "off", "on" })
        {
            var capture = Read("MicFrequencyShift-" + state + ".wav"); var quiet = Read("MicFrequencyShift-" + state + "-quiet.wav");
            Console.WriteLine(state + ": " + JsonSerializer.Serialize(FrequencyShiftDiagnostics.Measure(capture, quiet, 44100, 5), C360Reference.Json));
            measured.Add(FrequencyShiftDiagnostics.Measure(capture, quiet, 44100, 5));
            int size = (int)(44100 * .6), start = (int)Math.Round((5 - .05) * 44100) - size * 3;
            foreach (double f in FrequencyShiftDiagnostics.CarriersHz)
            for (int s = 0; s < 3; s++)
            {
                var samples = capture.AsSpan(start + s * size, size).ToArray(); var spectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(samples, 44100);
                int peak = Enumerable.Range(0, spectrum.FrequenciesHz.Length).Where(i => Math.Abs(spectrum.FrequenciesHz[i] - f) <= 30).MaxBy(i => spectrum.Magnitudes[i]);
                var tone = AdvancedAudioMeasurement.AnalyzeTone(samples, 44100, spectrum.FrequenciesHz[peak], 1);
                Console.WriteLine(JsonSerializer.Serialize(new { state, f, s, peak = spectrum.FrequenciesHz[peak], tone.FundamentalFrequencyHz, tone.FundamentalRms }, C360Reference.Json));
            }
        }
        Console.WriteLine("COMPARE " + JsonSerializer.Serialize(FrequencyShiftDiagnostics.Compare(measured[0], measured[1], new()), C360Reference.Json));
    }
    public static async Task Live(string output, string setupPath, string stage = "MicFrequencyShift", bool dryEffects = false, bool findReference = false)
    {
        Directory.CreateDirectory(output);
        using var manager = new SoncaBleManager(); manager.LogMessage += Console.WriteLine;
        var saved = JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(setupPath))!;
        if (!await manager.EnsureConnectedAsync(saved.BleControllerAddress, userInitiated: true)) throw new Exception("BLE unavailable; no audio/control test.");
        var model = C360ModelDefinition.Create();
        foreach (var test in model.testItems.InOut.Tests) test.Enable = test.SpeakerStage == stage;
        if (dryEffects) model.testItems.InOut.Tests[0].ImpulseEffects = new() { ExpectEcho = false, ExpectReverb = false };
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.All, NAudio.CoreAudioApi.DeviceState.Active)) using (device)
            endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == NAudio.CoreAudioApi.DataFlow.Capture));
        var plan = C360ModelPlan.Create(model.testItems.InOut, endpoints, saved);
        using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
        var bench = new C360AudioBench(engine, manager, output) { VerifyReverbAlways = stage == "MicEffects" };
        var baseline = await bench.ReadAsync(CancellationToken.None);
        try
        {
            IC360ElectronicBench runBench = bench;
            if (findReference) runBench = new C360ReferenceBench(bench, plan, "C360", Path.Combine(output, "standards"), "diagnostic-reference", true, 3,
                run => new C360AudioBench(engine, manager, Path.Combine(output, "reference-run-" + run)));
            var results = await new C360ElectronicSuite().RunAsync(plan.Bind(runBench), plan.Setup, stages: plan.Stages);
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(results.Select(r => new { r.StageName, r.Status, r.Summary, r.FrequencyShift }),
                new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            Console.WriteLine(results.Single().Summary);
        }
        finally
        {
            var restored = await bench.ReadAsync(CancellationToken.None);
            var changed = baseline.Where(p => restored.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key.ToString("X4")).ToArray();
            File.WriteAllText(Path.Combine(output, "restoration.json"), JsonSerializer.Serialize(new { registers = baseline.Count, changed }));
            Check(changed.Length == 0, "Speaker configuration not fully restored");
            Console.WriteLine("PASS full register restoration: " + baseline.Count);
        }
    }
}
