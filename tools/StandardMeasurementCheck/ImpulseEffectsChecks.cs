using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MathNet.Numerics.IntegralTransforms;
using SoncaAudioInspector;

internal static class ImpulseEffectsChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        CheckShortAndNoisyTails();
        foreach (int rate in new[] { 44100, 48000 })
        foreach (var state in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            double[] ir = Impulse(rate, state.Item1, state.Item2);
            var config = new ImpulseEffectSettings { ExpectEcho = state.Item1, ExpectReverb = state.Item2, ExpectedEchoDelayMs = 200 };
            var result = ImpulseEffects.Analyze(ir, rate, 128, true, config);
            Console.WriteLine($"{rate} Hz {state}: echo={result.EchoDetected}, reverb={result.ReverbDetected}, delay={result.EchoDelayMs:F3}, occupancy={result.TailOccupancy:F3}, wet T60={result.TailDecay60Seconds:F3}, {result.Verdict}");
            Require(result.IsValid && result.EchoDetected == state.Item1 && result.ReverbDetected == state.Item2 && result.Verdict == "PASS", "Four effect states were not separated.");
            Require(result.CanClassify(config), "Reliable full IR did not permit classification.");
            if (state.Item1) Require(Math.Abs(result.EchoDelayMs - 200) < 1, "Echo delay mismatch.");
            if (state.Item2) Require(result.TailDecay60Seconds > 2.5 && result.TailDecay60Seconds < 3.5, "Wet-tail decay mismatch.");
            var quiet = ImpulseEffects.Analyze(ir.Select(v => v * .1).ToArray(), rate, 128, true, config);
            Require(quiet.EchoDetected == result.EchoDetected && quiet.ReverbDetected == result.ReverbDetected && Math.Abs(quiet.LateEnergyDb - result.LateEnergyDb) < 1e-7, "Effect ratios changed with overall gain.");
        }
        var dry = Impulse(48000, false, false);
        var missing = ImpulseEffects.Analyze(dry, 48000, 128, true, new() { ExpectEcho = true, ExpectReverb = true });
        Require(missing.Verdict == "FAIL" && missing.Findings.Any(f => f.StartsWith("ECHO_MISSING")) && missing.Findings.Any(f => f.StartsWith("REVERB_MISSING")), "Missing-effect requirements ignored.");
        Require(ImpulseEffects.Analyze(dry, 48000, 128, true).Verdict == "OBSERVED", "Unconfigured effects became an acceptance verdict.");
        var baselineSettings = new ImpulseEffectSettings();
        var baseline = ImpulseEffectReference.From(ImpulseEffects.Analyze(Impulse(48000, true, true), 48000, 128, true), baselineSettings)!;
        Require(baseline.Compare(ImpulseEffects.Analyze(dry, 48000, 128, true), baselineSettings).Count(p => p.Contains("MISSING_REFERENCE")) == 2,
            "Saved golden effect baseline did not expose missing echo and reverb.");
        Require(!ImpulseEffects.Analyze(dry, 48000, 128, false).IsValid, "Invalid capture produced an effect verdict.");
        Require(ImpulseEffects.Analyze(dry.Take(24000).ToArray(), 48000, 128, true, new() { ExpectEcho = true, ExpectReverb = true }).Verdict == "INCONCLUSIVE", "Short capture was called missing effects.");
        var noise = new Random(12); var noisy = dry.Select(v => v + (noise.NextDouble() - .5)).ToArray();
        Require(!ImpulseEffects.Analyze(noisy, 48000, 128, true).IsValid, "Noisy IR allowed absence diagnosis.");
        var moderateNoise = dry.Select(v => v + .01 * (noise.NextDouble() - .5)).ToArray();
        var uncertain = ImpulseEffects.Analyze(moderateNoise, 48000, 128, true, new() { ExpectEcho = true, ExpectReverb = true });
        Require(uncertain.Verdict == "INCONCLUSIVE" && !uncertain.TailTruncated
            && uncertain.Findings.All(f => !f.Contains("MISSING")), "IR noise was mislabeled as missing effects or a truncated tail.");
        Require(!uncertain.CanClassify(new()) && !ImpulseEffects.Analyze(dry.Take(24000).ToArray(), 48000, 128, true).CanClassify(new()),
            "Noisy or short IR allowed an absence classification.");
        Console.WriteLine("PASS invalid/noisy/short IR, missing requirements, gain invariance and no unconfigured product verdict.");

        var source = Enumerable.Range(0, 2200).ToDictionary(i => 20 * Math.Pow(2, i / 192.0), _ => -20.0);
        source[1000] = 0;
        var centers = Enumerable.Range(0, 205).Select(i => 50 * Math.Pow(2, i / 24.0)).Where(f => f <= 18000).Append(1000).Append(18000).ToArray();
        var smooth = ResponseSmoothing.Octave24(source, centers);
        Require(source[1000] == 0 && smooth[1000] > -20 && smooth[1000] < 0, "Raw curve lost its unsmoothed peak or smoothing is not 1/24 averaging.");
        var quieter = ResponseSmoothing.Octave24(source.ToDictionary(p => p.Key, p => p.Value - 6.020599913), centers);
        Require(smooth.All(p => Math.Abs(quieter[p.Key] - p.Value + 6.020599913) < 1e-8), "Smoothing normalized away level changes.");
        Console.WriteLine("PASS 1/24 power averaging, untouched source peak and retained -6.0206 dB offset.");

        const int fs = 48000;
        var settings = new LogSweepSettings(fs, 18, 22000, 131072.0 / fs, .2, 192, 3,
            EffectsSettings: new() { ExpectEcho = true, ExpectReverb = true, ExpectedEchoDelayMs = 200 });
        var sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        var impulse = Impulse(fs, true, true);
        var capture = Convolve(sweep, impulse).Select(v => v * .4f).ToArray();
        var measured = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings, measuredPreSweepNoiseFloorDbFs: -100);
        Console.WriteLine("End-to-end " + measured.ImpulseEffects?.Describe());
        Require(measured.Validity != "INVALID" && measured.ImpulseEffects is { IsValid: true, EchoDetected: true, ReverbDetected: true }, "Same-sweep end-to-end effect analysis failed.");
        var saved = measured with { AlignedTransferFunction = Array.Empty<Complex>(), HarmonicTransferFunctions = Array.Empty<Complex[]>() };
        File.WriteAllText(Path.Combine(output, "effect-example.json"), JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        File.WriteAllText(Path.Combine(output, "effect-config-example.json"), JsonSerializer.Serialize(settings.EffectsSettings, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS echo + reverb extracted from one deconvolved sweep, with UI fixture saved.");
        foreach(int rate in new[]{44100,48000})
        {
            var shortSettings=settings with{SampleRate=rate,DurationSeconds=65536d/rate};
            var shortSweep=StandardAcousticMeasurement.GenerateLogSweep(shortSettings);
            var shortCapture=Convolve(shortSweep,Impulse(rate,true,true)).Select(v=>v*.4f).ToArray();
            var shortResult=StandardAcousticMeasurement.AnalyzeLogSweep(shortSweep,shortCapture,shortSettings,measuredPreSweepNoiseFloorDbFs:-100);
            Require(shortSweep.Length==65536 && shortResult.Validity!="INVALID"
                && shortResult.ImpulseEffects is{IsValid:true,EchoDetected:true,ReverbDetected:true},
                $"64K sweep did not retain echo/reverb at {rate} Hz");
            Console.WriteLine($"PASS 64K generated sweep + full tail, {rate} Hz: echo/reverb recovered from synthetic convolution; not physical qualification.");
        }
    }

    private static double[] Impulse(int rate, bool echo, bool reverb)
    {
        var samples = new double[rate * 3 + 129]; samples[128] = 1;
        if (echo) for (int n = 1; n <= 10; n++) samples[128 + (int)Math.Round(rate * .2 * n)] += Math.Pow(.25, n);
        if (reverb)
        {
            var random = new Random(94);
            for (int i = 128 + rate / 50; i < samples.Length; i++)
                samples[i] += .03 * (random.NextDouble() - .5) * Math.Exp(-Math.Log(1000) * (i - 128) / (3.0 * rate));
        }
        return samples;
    }

    private static void CheckShortAndNoisyTails()
    {
        foreach (int fs in new[] { 44100, 48000 })
        foreach (double t60 in new[] { .6, .9, 1.5 })
        foreach (bool echo in new[] { false, true })
        {
            var ir = Impulse(fs, echo, false);
            var random = new Random(731);
            for (int i = 128 + fs / 50; i < ir.Length; i++)
                ir[i] += .03 * (random.NextDouble() - .5) * Math.Exp(-Math.Log(1000) * (i - 128) / (t60 * fs));
            var result = ImpulseEffects.Analyze(ir, fs, 128, true);
            Require(result.ReverbDetected && result.EchoDetected == echo, $"Short wet tail missed: {fs}, {t60}, echo={echo}.");
        }
        for (int seed = 0; seed < 64; seed++)
        {
            var random = new Random(seed);
            var ir = Impulse(48000, seed % 2 == 0, false);
            for (int i = 0; i < ir.Length; i++) ir[i] += .003 * (random.NextDouble() - .5);
            Require(!ImpulseEffects.Analyze(ir, 48000, 128, true).ReverbDetected, "Stationary noise or echo was called reverb.");
        }
        foreach (bool flat in new[] { false, true })
        {
            var ir = Impulse(48000, false, false);
            var random = new Random(941);
            for (int i = 128 + 48000 / 20; i < 128 + 48000; i++)
            {
                double t = (i - 128) / 48000.0;
                if (flat || (t > .20 && t < .21) || (t > .40 && t < .41)) ir[i] += .03 * (random.NextDouble() - .5);
            }
            Require(!ImpulseEffects.Analyze(ir, 48000, 128, true).ReverbDetected, "Flat noise burst or isolated reflections were called a decaying diffuse tail.");
        }
        foreach (double frequency in new[] { 700.0, 2000.0, 3500.0 })
        {
            var ir = Impulse(48000, false, false);
            for (int i = 128 + 960; i < ir.Length; i++)
            {
                double t = (i - 128) / 48000.0;
                ir[i] += .02 * Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-Math.Log(1000) * t);
            }
            Require(!ImpulseEffects.Analyze(ir, 48000, 128, true).ReverbDetected, "A narrow decaying resonance was called diffuse reverb.");
        }
        Console.WriteLine("PASS 12 short wet tails, 64 noisy dry/echo controls, stationary burst, sparse reflections and three narrow resonances.");
    }

    private sealed record CaptureCase(string Directory, bool Echo, bool Reverb);

    public static void RunCaptured(string manifestPath, string output)
    {
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var cases = JsonSerializer.Deserialize<CaptureCase[]>(File.ReadAllText(manifestPath), options)!;
        string root = Path.GetDirectoryName(manifestPath)!;
        var rows = new List<object>();
        foreach (var item in cases)
        {
            var saved = JsonSerializer.Deserialize<StandardAcousticResult>(File.ReadAllText(Path.Combine(root, item.Directory, "effects-result.json")), options)!;
            Require(saved.Validity != "INVALID" && saved.ImpulsePlot?.Samples.Length > 0, "Physical capture is invalid or lacks full IR.");
            var ir = saved.ImpulsePlot!;
            foreach (int seconds in new[] { 3, 5 }.Where(s => (ir.Samples.Length - ir.DirectIndex) / (double)saved.SampleRate >= s - .01))
            {
                var samples = ir.Samples.Take(Math.Min(ir.Samples.Length, ir.DirectIndex + seconds * saved.SampleRate + 1)).ToArray();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var measured = ImpulseEffects.Analyze(samples, saved.SampleRate, ir.DirectIndex, true);
                timer.Stop();
                rows.Add(new { item.Directory, seconds, expectedEcho = item.Echo, expectedReverb = item.Reverb, elapsedMs = timer.Elapsed.TotalMilliseconds, measured });
                Console.WriteLine($"{item.Directory}, {seconds}s: echo={measured.EchoDetected}, reverb={measured.ReverbDetected}, diffuse {measured.DiffuseTailDurationMs:F0} ms, SNR {measured.DiffuseTailSnrDb:F2} dB, R2 {measured.TailFitRSquared:F3}.");
                File.WriteAllText(Path.Combine(output, "captured-regression.json"), JsonSerializer.Serialize(rows, options));
                Require(measured.EchoDetected == item.Echo && measured.ReverbDetected == item.Reverb, "Physical ON/OFF capture classification mismatch.");
            }
        }
        Console.WriteLine($"PASS {rows.Count} replayed physical IR cases with user-confirmed effect states. No audio emitted by replay.");
    }

    private static float[] Convolve(float[] signal, double[] impulse)
    {
        int size = 1; while (size < signal.Length + impulse.Length - 1) size *= 2;
        var a = new Complex[size]; var b = new Complex[size];
        for (int i = 0; i < signal.Length; i++) a[i] = signal[i];
        for (int i = 0; i < impulse.Length; i++) b[i] = impulse[i];
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int i = 0; i < size; i++) a[i] *= b[i];
        Fourier.Inverse(a, FourierOptions.Matlab);
        return a.Take(signal.Length + impulse.Length - 1).Select(v => (float)v.Real).ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
