using System.IO;
using System.Reflection;
using SoncaAudioInspector;

internal static class BriefP1Checks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Capture = typeof(TestRunner).GetMethod("RunRubBuzzCaptureAsync", Hidden)!;
    private static readonly MethodInfo Owned = typeof(TestRunner).GetMethod("RunOwnedTestAsync", Hidden)!;

    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }

        foreach (int rate in new[] { 44100, 48000 })
        foreach (double? level in new double?[] { null, -41.9, -18, -3, -1.94, 0 })
        {
            using var engine = new AudioEngine();
            var runner = new TestRunner(engine) { RubBuzzPlaybackLevelDbFs = level };
            var logs = new List<string>(); runner.OnLogMessage += (_, message) => logs.Add(message);
            double scale = (double)typeof(TestRunner).GetMethod("ResolveRubBuzzSignalSampleScale", Hidden)!.Invoke(runner, new object[] { true })!;
            // Exercise the real provider, including its 0.8 factor and scale clamp; no device is opened.
            var provider = new SignalSampleProvider(rate, SignalType.Sine, rate / 4.0, 1, signalSampleScale: scale);
            var samples = new float[128]; provider.Read(samples, 0, samples.Length);
            double expected = level is null || level > -1.94 ? .8 : Math.Pow(10, level.Value / 20);
            Check(Math.Abs(samples.Max(v => Math.Abs(v)) - expected) < 1e-7, $"D3 provider peak {rate} Hz / {level?.ToString() ?? "legacy"}");
            Check(logs.Any(s => s.Contains("kẹp")) == (level > -1.94), "D3 clamping log matches explicit high-level request");
        }

        foreach (double level in new[] { -41.9001, -60, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            using var engine = new AudioEngine();
            var runner = new TestRunner(engine) { IsRubBuzzTest = true, RubBuzzPlaybackLevelDbFs = level };
            bool called = false;
            await RunCapture(runner, (_, _, _) => { called = true; return Task.FromResult((Tone(44100, 70), 44100)); });
            Check(!called && runner.RubBuzzAcquisitionInvalid && runner.Steps[1].Status == "Invalid", "D3 invalid level never reaches capture");
            // Also verify the production preflight rejects it before even checking device handles.
            await runner.RunTestAsync(null!, null!);
            Check(runner.RubBuzzAcquisitionInvalid && runner.Steps[0].Status == "Waiting", "D3 preflight rejects before any probe/device access");
        }

        foreach (int rate in new[] { 44100, 48000 })
        foreach (double frequencyScale in new[] { 1.0, 48000.0 / 44100 })
        {
            using var engine = new AudioEngine();
            var runner = new TestRunner(engine) { IsRubBuzzTest = true, PlaybackFrequencyScale = frequencyScale, RubBuzzPlaybackLevelDbFs = -18 };
            bool? completed = null; runner.OnTestCompleted += passed => completed = passed;
            await RunCapture(runner, (frequency, scale, token) =>
            {
                Check(Math.Abs(frequency - 70 * frequencyScale) < 1e-9 && token.CanBeCanceled, "D4 physical frequency and owned cancellation reach capture");
                Check(Math.Abs(.8 * scale - Math.Pow(10, -18.0 / 20)) < 1e-12, "D3 capture receives exact peak scale");
                return Task.FromResult((Tone(rate, frequency), rate));
            });
            Check(completed == true && runner.Steps[1].Status == "Pass" && !runner.RubBuzzAcquisitionInvalid, "D2/D4 clean scaled tone passes");
            Check(runner.LastRubBuzzValue < .05 && runner.LastRubBuzzSpectrum?.FrequenciesHz.Length > 0, "Legacy RMS and real spectrum retained");
            Check(Math.Abs(runner.LastRubBuzzPlaybackLevelDbFs + 18) < 1e-10, "D3 actual peak level retained for QA acquisition snapshot");
            Check(!runner.Steps.Any(s => s.Name.Contains("hở khí") || s.Details.Contains("hở khí")), "D11 no air-leak claim in legacy steps");
        }

        foreach (string scenario in new[] { "wrong-tone", "silence", "short", "nonfinite", "clipping", "clipped-tail", "residual-fail" })
        {
            const int rate = 48000;
            using var engine = new AudioEngine();
            var runner = new TestRunner(engine) { IsRubBuzzTest = true };
            float[] samples = scenario switch
            {
                "wrong-tone" => Tone(rate, 997), "silence" => new float[rate * 2], "short" => new float[rate / 2],
                "clipping" => Tone(rate, 70, amplitude: 1.01), "residual-fail" => Tone(rate, 70, residual: .02), _ => Tone(rate, 70)
            };
            if (scenario == "nonfinite") samples[100] = float.NaN;
            if (scenario == "clipped-tail") samples[^1] = 1;
            bool? completed = null; runner.OnTestCompleted += passed => completed = passed;
            await RunCapture(runner, (_, _, _) => Task.FromResult((samples, rate)));
            bool validFailure = scenario == "residual-fail";
            Check(completed == false && runner.Steps[1].Status == (validFailure ? "Fail" : "Invalid"), "D2 distinct outcome: " + scenario);
            Check(runner.RubBuzzAcquisitionInvalid != validFailure, "D2 acquisition flag: " + scenario);
            if (validFailure)
                Check(Math.Abs(runner.LastRubBuzzValue - 4) < .15 && runner.BuildFailureDiagnosis().Contains("nghi rè/cọ coil")
                    && !runner.BuildFailureDiagnosis().Contains("rò khí"), "D11 valid 4 percent residual fails without air-leak diagnosis");
            else Check(runner.Steps[3].Status == "Invalid" && TestRunVote.Count(new[] { "INVALID", "INVALID", "INVALID" }, 3, 2).Status == "INVALID", "D2 invalid capture never becomes a failed DUT vote");
        }

        using (var engine = new AudioEngine())
        {
            var runner = new TestRunner(engine) { IsRubBuzzTest = true };
            var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool passed = false; runner.OnTestCompleted += value => passed |= value;
            Task work = RunCapture(runner, async (_, _, token) =>
            {
                started.SetResult(token); await Task.Delay(Timeout.Infinite, token); return (Tone(48000, 70), 48000);
            });
            CancellationToken token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runner.Cancel();
            try { await work.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancelled capture completed normally"); }
            catch (OperationCanceledException) { }
            Check(token.IsCancellationRequested && runner.RubBuzzAcquisitionInvalid && !passed
                && runner.LastRubBuzzMetrics == null && runner.LastRubBuzzSpectrum == null, "D3 cancellation discards partial metrics/spectrum and cannot PASS");
        }

        var points = new[] { Summary(80, 5), Summary(1000, .2), Summary(4000, 5) };
        var config = new TestConfig { Thd1000Hz = .5 };
        Check(MeasurementCriteria.ThdVerdict(points, config) == "PASS", "D1 disabled 80/4000 Hz remain diagnostic");
        config.EvaluateThd80 = true;
        Check(MeasurementCriteria.ThdVerdict(points, config) == "FAIL", "D1 enabled 80 Hz must fail despite low 1 kHz");
        config.EvaluateThd80 = false; config.EvaluateThd4000 = true;
        Check(MeasurementCriteria.ThdVerdict(points, config) == "FAIL", "D1 enabled 4 kHz must fail despite low 1 kHz");
        Check(MeasurementCriteria.ThdVerdict(points.Select(p => p.ReferenceFrequencyHz == 4000 ? p with { IsValid = false } : p), config) == "INVALID", "D1 enabled invalid point remains INVALID");

        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P1 assertions; synthetic/offline only, no devices, BLE or server."));
        Console.WriteLine($"PASS {checks.Count} P1 offline assertions; no audio devices opened, no BLE/server.");
    }

    private static SweepDistortionSummary Summary(double frequency, double thd) =>
        new(frequency, -20, new Dictionary<int, double>(), thd, .01, -100, "fixture", true, "fixture");

    private static float[] Tone(int rate, double frequency, double amplitude = .5, double residual = 0) =>
        Enumerable.Range(0, rate * 5 / 2).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate)
            + residual * Math.Sin(2 * Math.PI * 3000 * i / rate))).ToArray();

    private static Task RunCapture(TestRunner runner, Func<double, double, CancellationToken, Task<(float[], int)>> capture)
    {
        runner.InitializeSteps(); runner.Steps[0].Status = "Pass";
        Func<CancellationToken, Task> operation = _ => (Task)Capture.Invoke(runner, new object[] { capture })!;
        return (Task)Owned.Invoke(runner, new object[] { operation })!;
    }
}
