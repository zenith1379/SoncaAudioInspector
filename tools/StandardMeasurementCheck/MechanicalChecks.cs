using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoncaAudioInspector;

internal static class MechanicalChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, WriteIndented = true };
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        foreach (int rate in new[] { 44100, 48000 })
        {
            var settings = new LogSweepSettings(rate, 18, rate * .46, 3, .2, 24);
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[sweep.Length + rate];
            for (int i = 0; i < sweep.Length; i++)
            {
                double phase = Math.Acos(Math.Clamp(sweep[i] / settings.Amplitude, -1, 1));
                capture[i + 200] = (float)(sweep[i] * .5 + settings.Amplitude * .5 * (.01 * Math.Cos(10 * phase) + .005 * Math.Cos(35 * phase)));
            }
            var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings, measuredPreSweepNoiseFloorDbFs: -110);
            var high = result.HighOrderHarmonics ?? throw new Exception("Sweep did not calculate H10–H35.");
            Require(high.Points.Any(p => p.FundamentalHz == 40 && p.Order == 35 && p.IsUsable), "40 Hz bass high-order analysis was lost below the FEQ display range.");
            foreach (var expected in new[] { (10, -40.0), (35, 20 * Math.Log10(.005)) })
            {
                var point = high.Points.Single(p => p.Order == expected.Item1 && p.FundamentalHz == 100);
                Console.WriteLine($"{rate} Hz H{point.Order}: {point.LevelDbc:F4} dBc; {point.Status}; gate {point.WindowMs:F3} ms.");
                Require(point.IsUsable && Math.Abs(point.LevelDbc - expected.Item2) < 1.0, "Injected harmonic magnitude is wrong: " + point);
            }
            Require(high.Points.Any(p => p.Order == 35 && p.OutputHz > high.OutputBandwidthHz && !p.IsUsable && double.IsNaN(p.LevelDbc)), "Out-of-band H35 was reported as measured.");
            var ninth = result.SweepHarmonics.MinBy(p => Math.Abs(p.FundamentalFrequencyHz - 100))!;
            Require(ninth.H9Dbc < -60, "H10 leaked into the H9 gate: " + ninth.H9Dbc);
            if (rate == 44100)
            {
                var alternative = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture,
                    settings with { UseHarmonicFrequencyAsDistortionReference = true }, measuredPreSweepNoiseFloorDbFs: -110).HighOrderHarmonics!;
                Require(alternative.ReferenceAtHarmonicFrequency && alternative.Points.Any(p => p.Order == 35 && p.IsUsable),
                    "Harmonic-frequency reference setting was not retained.");
            }
            Require(high.Points.All(p => p.WindowMs <= p.SeparationMs * 1.1), "Harmonic gates overlap neighbouring centres.");
            var invalid = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, new float[capture.Length], settings);
            Require(invalid.HighOrderHarmonics?.Points.Count == 0, "Invalid acquisition exposed high-order values.");
            File.WriteAllText(Path.Combine(output, $"high-order-{rate}.json"), JsonSerializer.Serialize(result with
                { AlignedTransferFunction = Array.Empty<Complex>(), HarmonicTransferFunctions = Array.Empty<Complex[]>() }, options));
        }
        foreach (int fs in new[] { 44100, 48000 })
        {
        var random = new Random(458);
        var background = Enumerable.Range(0, fs / 2).Select(_ => (float)((random.NextDouble() - .5) * 1e-5)).ToArray();
        var lp = new MechanicalTestRequest(MechanicalTestKind.LooseParticle);
        float[] Stimulus(string kind)
        {
            var signal = new float[(int)(fs * 2.1)];
            for (int i = 0; i < fs * 1.5; i++)
            {
                double t = i / (double)fs;
                double value = .2 * Math.Sin(2 * Math.PI * 40 * t) + (random.NextDouble() - .5) * 1e-5;
                if (kind == "periodic") value += .005 * Math.Sin(2 * Math.PI * 4000 * t);
                if (kind is "air" or "uniform") value += .012 * (random.NextDouble() - .5) * (kind == "air" ? 1 + .9 * Math.Cos(2 * Math.PI * 80 * t) : 1);
                signal[i + 500] = (float)value;
            }
            if (kind == "particles")
                foreach (double time in new[] { .23, .391, .678, .918, 1.187, 1.311 })
                    for (int j = 0; j < 150; j++) signal[(int)(time * fs) + 500 + j] += (float)(.08 * Math.Exp(-j / 24.0) * Math.Sin(j * 2 * Math.PI * 5700 / fs));
            return signal;
        }
        var clean = MechanicalDiagnostics.Analyze(Stimulus("clean"), background, fs, lp);
        var periodic = MechanicalDiagnostics.Analyze(Stimulus("periodic"), background, fs, lp);
        var particles = MechanicalDiagnostics.Analyze(Stimulus("particles"), background, fs, lp);
        Console.WriteLine($"{fs} Hz loose particle: clean={clean.Status}/{clean.Events.Count}; periodic={periodic.Status}/{periodic.Events.Count}; particle={particles.Status}/{particles.Events.Count}");
        Require(clean.IsValid && clean.Status != "CÓ XUNG PHI CHU KỲ", "Clean tone called loose particles.");
        Require(periodic.IsValid && periodic.Status != "CÓ XUNG PHI CHU KỲ", "Synchronous harmonics called loose particles.");
        Require(particles.IsValid && particles.Status == "CÓ XUNG PHI CHU KỲ" && particles.Events.Count >= 3 && particles.Events.Count <= 7, "Asynchronous impacts missed or filter ringing counted as extra impacts.");
        var airRequest = lp with { Kind = MechanicalTestKind.AirLeak };
        var uniform = MechanicalDiagnostics.Analyze(Stimulus("uniform"), background, fs, airRequest);
        var air = MechanicalDiagnostics.Analyze(Stimulus("air"), background, fs, airRequest);
        var airPeriodic = MechanicalDiagnostics.Analyze(Stimulus("periodic"), background, fs, airRequest);
        Console.WriteLine($"Air: uniform={uniform.Status}, {uniform.ModulationPercent:F1}%/{uniform.ModulationProminenceDb:F1} dB; AM={air.Status}, {air.ModulationPercent:F1}%/{air.ModulationProminenceDb:F1} dB");
        Require(uniform.IsValid && uniform.Status != "CÓ NHIỄU ĐIỀU BIÊN", "Uniform noise called a leak symptom.");
        Require(air.IsValid && air.Status == "CÓ NHIỄU ĐIỀU BIÊN", "Bass-synchronous modulated noise was missed.");
        Require(airPeriodic.IsValid && airPeriodic.Status != "CÓ NHIỄU ĐIỀU BIÊN", "Stationary harmonic carrier was called leak modulation.");
        foreach (var invalid in new[] { new float[fs * 2], Enumerable.Repeat(1f, fs * 2).ToArray(), Enumerable.Repeat(float.NaN, fs * 2).ToArray(), Stimulus("clean").Take(100).ToArray() })
            Require(!MechanicalDiagnostics.Analyze(invalid, background, fs, lp).IsValid, "Invalid acquisition produced a mechanical diagnosis.");
        Require(!MechanicalDiagnostics.Analyze(Stimulus("clean"), Enumerable.Repeat(.02f, fs / 2).ToArray(), fs, lp).IsValid, "Loud preflight allowed diagnosis.");
        float[] wrong = Stimulus("clean"); for (int i = 0; i < wrong.Length; i++) wrong[i] = (float)(.2 * Math.Sin(2 * Math.PI * 70 * i / fs));
        Require(!MechanicalDiagnostics.Analyze(wrong, background, fs, lp).IsValid, "Wrong tone allowed diagnosis.");
        foreach (var item in new[] { ("loose-particle", particles), ("air-leak", air), ("periodic", periodic), ("uniform", uniform) })
            File.WriteAllText(Path.Combine(output, item.Item1 + ".json"), JsonSerializer.Serialize(item.Item2, options));
        var generated = MechanicalDiagnostics.GenerateTone(lp, fs);
        Require(generated.Length == fs * 1.5 && generated[0] == 0 && generated[^1] == 0 && generated.Max(Math.Abs) <= Math.Pow(10, lp.LevelDbFs / 20) * 1.00001, "Tone duration/fade/peak changed.");
        }
        Console.WriteLine("PASS ESS H10/H35 levels/bandwidth, clean/periodic/asynchronous/AM noise, acquisition guards and finite faded stimulus; diagnostic evidence only.");
    }
}
