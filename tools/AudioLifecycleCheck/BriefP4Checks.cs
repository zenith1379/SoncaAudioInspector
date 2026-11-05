using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SoncaAudioInspector;

internal static class BriefP4Checks
{
    private const int Rate = 44100;
    public static async Task Run(string output, string configPath, string baselineAssembly)
    {
        if (!File.Exists(configPath) || !File.Exists(baselineAssembly)) throw new ArgumentException("P4 requires config and P3 baseline assembly.");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); File.WriteAllLines(Path.Combine(output, "progress.txt"), checks); }
        void Reject(Action operation, string fragment, string why)
        {
            try { operation(); } catch (ArgumentException ex) { Check(ex.Message.Contains(fragment), why + ": " + ex.Message); return; }
            throw new Exception("Accepted invalid configuration: " + why);
        }
        var test = new TestConfig { id = "P4", TestRubBuzz = true, RubBuzzPeakLimitDb = -25, RubBuzzRmsLimitPercent = 1.5 };
        var additional = AdditionalTestRegistry.GetEnabled(test).Single();
        var context = new AdditionalTestContext();
        var plan = additional.Plan(test, context);
        var background = Noise(Rate / 2, -75);
        Check(additional.Metadata.Id == "RubBuzz" && additional.Metadata.Version == 1 && additional.Metadata.SupportedStages.Count == 0
            && AdditionalTestRegistry.RegisteredJsonPropertyNames.Count == 8 && test.HasAdditionalTests, "Registry metadata, eight keys and enable hook");
        try { AdditionalTestRegistry.Register(new DuplicateRubBuzz()); throw new Exception("Duplicate accepted"); }
        catch (InvalidOperationException) { Check(true, "Registry rejects case-insensitive duplicate IDs"); }
        Check(new AutoTestCaseItem { Config = test }.AdditionalStatus.Contains("Rè (Rub & Buzz): CHƯA ĐO"), "Registry test displayed before capture");

        var cleanCaptures = new List<float[]>();
        foreach (var step in plan.Steps)
        {
            var signal = step.Samples;
            cleanCaptures.Add(signal);
            var result = RubBuzzAnalysis.AnalyzeStep(signal, background, Rate, step.NominalFrequencyHz, step.PhysicalFrequencyHz,
                step.DurationSeconds, -25, 1.5);
            Check(result.Status == "PASS" && result.PeakDb < -40 && result.RmsPercent < .2,
                $"C7.1 clean {step.NominalFrequencyHz} Hz: {result}");
        }
        Check(additional.Analyze(test, new(Rate, background, cleanCaptures), context).Status == "PASS", "C7.1 four-step aggregate PASS");
        float[] impulses = RubBuzzAnalysis.GenerateStepTone(50, -18, 1, Rate);
        // One sample per period: a large peak with very little total residual energy.
        foreach (double time in Enumerable.Range(2, 46).Select(i => i / 50.0)) impulses[(int)Math.Round(time * Rate)] += .012f;
        var peak = RubBuzzAnalysis.AnalyzeStep(impulses, background, Rate, 50, 50, 1, -25, 1.5);
        Check(peak.Reason.Contains("Peak " + peak.PeakDb.ToString("F1") + " dB") && peak.Reason.Contains("RMS " + peak.RmsPercent.ToString("F2") + "%"), "Step reason formats measured peak and RMS with decimal precision");
        Check(peak.Status == "FAIL" && peak.PeakDb > -25 && peak.RmsPercent < 1.5 && peak.Reason.Contains("nghi rè/cọ coil"), "C7.2 peak-only failure: " + peak);
        Check(RubBuzzAnalysis.AnalyzeStep(cleanCaptures[0], Noise(Rate / 2, -45), Rate, 50, 50, 1, -25, 1.5).Reason.Contains("NỀN CAO"), "C7.3 broadband noise INVALID");
        float[] bandBackground = Enumerable.Range(0, Rate / 2).Select(i => (float)(.0007 * Math.Sin(2 * Math.PI * 7000 * i / Rate))).ToArray();
        var bandInvalid = RubBuzzAnalysis.AnalyzeStep(cleanCaptures[0], bandBackground, Rate, 50, 50, 1, -50, null);
        Check(bandInvalid.Status == "INVALID" && bandInvalid.Reason.Contains("nền ồn trong dải rè quá cao để kết luận"), "C7.3 quiet full-band but excessive in-band noise");
        var clipped = cleanCaptures[0].ToArray(); clipped[100] = .995f;
        Check(RubBuzzAnalysis.AnalyzeStep(clipped, background, Rate, 50, 50, 1, -25, 1.5).Status == "INVALID", "C7.3 clipping INVALID");
        clipped[100] = float.NaN;
        Check(RubBuzzAnalysis.AnalyzeStep(clipped, background, Rate, 50, 50, 1, -25, 1.5).Reason.Contains("DỮ LIỆU LỖI"), "Nonfinite capture INVALID");
        Check(RubBuzzAnalysis.AnalyzeStep([], background, Rate, 50, 50, 1, -25, 1.5).Status == "INVALID", "Missing capture INVALID");

        var scaledContext = context with { PlaybackFrequencyScale = 48000.0 / 44100.0 };
        var scaledPlan = additional.Plan(test, scaledContext);
        Check(additional.Analyze(test, new(Rate, background, scaledPlan.Steps.Select(s => s.Samples).ToArray()), scaledContext).Status == "PASS", "C7.4 physical scaled tones lock and PASS");
        Check(additional.Analyze(test, new(Rate, background, cleanCaptures.Take(2).ToArray()), context).Status == "INVALID", "C7.5 two of four steps INVALID");
        int capturedSteps = 0;
        ConfiguredTestResult cancelled;
        try
        {
            cancelled = await AdditionalTestRegistry.RunAsync(additional, test, context, (p, token) =>
            {
                foreach (var step in p.Steps)
                {
                    if (++capturedSteps == 3) throw new OperationCanceledException();
                }
                return Task.FromResult(new AdditionalTestCaptureData(Rate, background, cleanCaptures));
            });
        }
        catch (OperationCanceledException) { cancelled = AdditionalTestRegistry.CancelledResult(); }
        Check(capturedSteps == 3 && cancelled.Status == "INVALID" && cancelled.Reason.Contains("Đã hủy"), "C7.5 RubBuzz cancellation during third step maps to INVALID without analysis of partial captures");
        using (var tokenSource = new CancellationTokenSource())
        {
            var cancelledAfterCapture = false;
            try
            {
                await AdditionalTestRegistry.RunAsync(additional, test, context, (p, token) =>
                {
                    tokenSource.Cancel();
                    return Task.FromResult(new AdditionalTestCaptureData(Rate, background, cleanCaptures));
                }, tokenSource.Token);
            }
            catch (OperationCanceledException) { cancelledAfterCapture = AdditionalTestRegistry.CancelledResult().Invalid; }
            Check(cancelledAfterCapture, "C7.5 cancellation after complete capture cannot produce PASS");
        }
        var mixed = cleanCaptures.ToArray(); mixed[0] = impulses; mixed[1] = [];
        Check(additional.Analyze(test, new(Rate, background, mixed), context).Status == "FAIL", "FAIL takes priority over INVALID across complete step list");

        var mechanicalRequest = new MechanicalTestRequest(MechanicalTestKind.LooseParticle);
        foreach (var (times, expected) in new[] {
            (Enumerable.Range(4, 52).Select(i => i / 40.0).ToArray(), "CÓ XUNG KHÓA PHA (RÈ)"),
            (new[] { .23, .391, .678, .918, 1.187, 1.311 }, "CÓ XUNG PHI CHU KỲ"),
            (new[] { .391 }, "CẦN ĐO LẠI"), (new[] { .391, .918 }, "CẦN ĐO LẠI") })
        {
            var result = MechanicalDiagnostics.Analyze(MechanicalSignal(times), background, Rate, mechanicalRequest);
            string configured = ConfiguredAcousticTests.Mechanical(result).Status;
            Check(result.IsValid && result.Status == expected && configured == (expected == "CẦN ĐO LẠI" ? "INVALID" : "FAIL"),
                $"C7.6 {expected}: {result.Status}, events={result.Events.Count}, configured={configured}");
            if (expected == "CÓ XUNG KHÓA PHA (RÈ)") Check(result.Message.Contains("nghi rè/cọ coil, không phải hạt rác; xác nhận bằng TestRubBuzz"), "D5 explanatory message");
        }

        var invalidTest = Clone(test); invalidTest.RubBuzzPeakLimitDb = null; invalidTest.RubBuzzRmsLimitPercent = null;
        Reject(() => additional.Validate(invalidTest), "nhập ít nhất một giới hạn", "C7.7 missing limits");
        var model = Model(invalidTest);
        Reject(() => ModelConfigEditorWindow.ParseModel(model.model, JsonSerializer.Serialize(model)), "nhập ít nhất một giới hạn", "C7.7 ParseModel");
        var item = new AutoTestCaseItem { Config = invalidTest };
        Check(!AudioRouting.ValidateAutoTestCaseConfiguration(item) && item.AdditionalResults.Single().Reason.Contains("nhập ít nhất một giới hạn"), "C7.7 AutoTest validator rejects missing limits");
        invalidTest = Clone(test); invalidTest.RubBuzzFrequenciesHz = [30, 50];
        Reject(() => additional.Validate(invalidTest), "24 chu kỳ", "C7.7 duration needs stable cycles");
        invalidTest = Clone(test); invalidTest.RubBuzzFrequenciesHz = [50, 750];
        Reject(() => additional.Validate(invalidTest, scaledContext), "dải lọc rỗng hoặc quá hẹp", "C7.7 physical bandwidth after scale");
        invalidTest = Clone(test); invalidTest.RubBuzzFrequenciesHz = [50, 70];
        Reject(() => additional.Validate(invalidTest, context with { PlaybackFrequencyScale = .5 }), "24 chu kỳ", "Physical low scale also needs 24 cycles");
        foreach (var mutate in new Action<TestConfig>[] {
            t => t.RubBuzzPeakLimitDb = double.NaN, t => t.RubBuzzRmsLimitPercent = 0,
            t => t.RubBuzzFrequenciesHz = [50], t => t.RubBuzzFrequenciesHz = [50, 1001],
            t => t.RubBuzzLevelDbFs = -2, t => t.RubBuzzStepSeconds = .5,
            t => t.RubBuzzHighPassMultiple = 9, t => t.RubBuzzNoiseMarginDb = 31 })
        {
            var bad = Clone(test); mutate(bad);
            Reject(() => additional.Validate(bad), "P4", "Validator bounds");
        }

        Check(!JsonDocument.Parse(JsonSerializer.Serialize(new TestConfig())).RootElement.EnumerateObject().Any(p => AdditionalTestRegistry.RegisteredJsonPropertyNames.Contains(p.Name)), "C7.8 null RubBuzz fields absent from JSON");
        Check(JsonDocument.Parse(JsonSerializer.Serialize(new TestConfig { TestRubBuzz = false, RubBuzzPeakLimitDb = 0 })).RootElement.GetProperty("RubBuzzPeakLimitDb").GetDouble() == 0, "Explicit false/zero retained in JSON");
        int identityCount = BriefP2IdentityChecks.Run(configPath, baselineAssembly, output);
        Check(identityCount == 88, "C7.8 88 identity assertions against P3 baseline");
        var config = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!;
        C360ModelDefinition.EnsureSeed(config);
        foreach (var entry in config.models)
        {
            var io = entry.testItems.InOut;
            var setup = new C360ElectronicSetup("mock-out", "mock-usb", "mock-in") { BluetoothPlaybackId = "mock-bt" };
            var referencePlan = new C360ModelPlan(io, setup, []);
            foreach (var t in io.Tests)
            {
                string original = Fingerprint(io, t);
                string? originalKey = t.IsEnabled && Enum.TryParse<C360ElectronicStage>(t.SpeakerStage, out var stage)
                    ? C360Reference.ConfigurationKey(entry.model, referencePlan, stage, "offline-mixer", setup) : null;
                t.TestRubBuzz = true; t.RubBuzzFrequenciesHz = [60, 90, 140]; t.RubBuzzPeakLimitDb = -24; t.RubBuzzRmsLimitPercent = 1.2;
                t.RubBuzzLevelDbFs = -22; t.RubBuzzStepSeconds = 1.2; t.RubBuzzNoiseMarginDb = 9; t.RubBuzzHighPassMultiple = 15;
                Check(original == Fingerprint(io, t), "C7.8 RubBuzz acoustic identity invariant " + entry.model + "/" + t.id);
                if (originalKey != null) Check(originalKey == C360Reference.ConfigurationKey(entry.model, referencePlan, Enum.Parse<C360ElectronicStage>(t.SpeakerStage!), "offline-mixer", setup), "C7.8 RubBuzz C360 identity invariant " + entry.model + "/" + t.id);
                t.TestRubBuzz = null; t.RubBuzzFrequenciesHz = null; t.RubBuzzPeakLimitDb = null; t.RubBuzzRmsLimitPercent = null;
                t.RubBuzzLevelDbFs = null; t.RubBuzzStepSeconds = null; t.RubBuzzNoiseMarginDb = null; t.RubBuzzHighPassMultiple = null;
            }
        }
        foreach (string profile in new[] { C360ModelDefinition.Profile, Acoustic3ModelDefinition.Profile })
        foreach (string stage in new[] { "LineIn", "AcousticMic" })
        {
            var guard = Model(Clone(test)); guard.testItems.InOut.AutomationProfile = profile;
            guard.testItems.InOut.Tests[0].SpeakerStage = stage;
            guard.testItems.InOut.Tests[0].FrequencyResponseMethod = "LogSweep";
            guard.testItems.InOut.Tests[0].AcousticSetupPending = false;
            Reject(() => C360ModelDefinition.Validate(guard.testItems.InOut), "TestRubBuzz", "C7.9 " + profile + "/" + stage + " guard");
        }

        var soundConfig = new TestConfig { FrequencyRange = [100, 4000], DipWindowsEnabled = true,
            DipWindows = [new() { MinHz = 600, MaxHz = 800 }, new() { MinHz = 1400, MaxHz = 1800, Enabled = false }] };
        var points = new[] { Point(60, 90), Point(12000, 90), Point(100, 0), Point(1000, 0), Point(4000, 0), Point(650, 90), Point(700, 90), Point(750, 90), Point(1500, 90) };
        ConfiguredTestResult Sound(IReadOnlyList<DiagnosisResponsePoint> values, ResponseLimitSettings? settings = null) =>
            ConfiguredAcousticTests.SoundLevel(values, true, true, false, 0, 0, 3, soundConfig.EvaluationRange, settings);
        var settings = ResponseLimitSettings.Capture(soundConfig, 3);
        Check(Sound(points, settings).Status == "PASS" && Sound(points, settings).Reason.Contains("trung vị 4 điểm 100 Hz–4000 Hz, không tính khoảng xét hõm"), "C7.10 exclude only active dip windows and outside range");
        Check(Sound(points).Status == "FAIL", "C7.10 disabled dip filtering retains large offsets");
        Check(Sound(new[] { Point(100, 0), Point(1000, 0), Point(2000, double.NaN) }, settings).Status == "INVALID", "C7.10 in-range NaN invalid");
        Check(Sound(new[] { Point(100, 0), Point(1000, 0) }, settings).Status == "INVALID", "C7.10 fewer than 3 points invalid");

        int calls = 0; var logs = new List<string>();
        Task<MechanicalCaptureData> Capture(MechanicalTestRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls++;
            return Task.FromResult(new MechanicalCaptureData(MechanicalDiagnostics.GenerateTone(request, Rate), background, Rate, "offline"));
        }
        var both = new TestConfig { TestAirLeak = true, TestLooseParticle = true };
        var sharedResults = await ConfiguredAcousticTests.RunMechanicalTestsAsync(both, 1, Capture, (_, message) => logs.Add(message));
        Check(calls == 1 && sharedResults.Select(r => r.Name).SequenceEqual(["Rò khí", "Hạt rác"]) && logs.Any(l => l.Contains("dùng chung 1 lần phát")), "C7.11 both tests share exactly one capture");
        foreach (var changed in new[] { mechanicalRequest with { FrequencyHz = 50 }, mechanicalRequest with { LevelDbFs = -20 }, mechanicalRequest with { DurationSeconds = 2 } })
        {
            calls = 0;
            await ConfiguredAcousticTests.RunMechanicalTestsAsync(new[] { mechanicalRequest with { Kind = MechanicalTestKind.AirLeak }, changed }, Capture);
            Check(calls == 2, "C7.11 unequal frequency/level/duration capture separately: " + changed);
        }
        calls = 0;
        var unavailable = await ConfiguredAcousticTests.RunMechanicalTestsAsync(both, 1, (request, _) =>
        { calls++; return Task.FromResult(new MechanicalCaptureData(null, null, Rate, PrebuiltUnavailable: MechanicalTestResult.Unavailable(request.Kind, "NỀN CHƯA ĐẠT", "mock nền"))); });
        Check(calls == 1 && unavailable.Count == 2 && unavailable.All(r => r.Invalid), "Shared unavailable background makes both INVALID");
        foreach (bool air in new[] { false, true })
        {
            calls = 0;
            await ConfiguredAcousticTests.RunMechanicalTestsAsync(new TestConfig { TestAirLeak = air, TestLooseParticle = !air }, 1, Capture);
            Check(calls == 1, "Single mechanical test captures once");
        }
        try
        {
            await ConfiguredAcousticTests.RunMechanicalTestsAsync(both, 1, (_, _) => throw new OperationCanceledException());
            throw new Exception("Cancellation returned completed results");
        }
        catch (OperationCanceledException) { Check(true, "C7.5 cancellation propagates without partial mechanical results"); }
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P4 assertions and 88 P3 identity assertions; offline only."));
        Console.WriteLine($"PASS {checks.Count} P4 assertions; no audio/BLE/server.");
    }
    private static TestConfig Clone(TestConfig test) => JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(test))!;
    private static ModelConfig Model(TestConfig test)
    {
        test.name = "P4 fixture"; test.PlaybackOut = "OUT"; test.RecordingIn = "IN";
        return new ModelConfig { model = "P4", testItems = new() { InOut = new() { Devices = new() { Input = new() { ["OUT"] = "mock-out" }, Output = new() { ["IN"] = "mock-in" } }, Tests = [test] } } };
    }
    private static DiagnosisResponsePoint Point(double hz, double offset) => new(hz, -20 + offset, -20, 3);
    private static float[] Noise(int length, double db)
    {
        var random = new Random(704); double peak = Math.Sqrt(3) * Math.Pow(10, db / 20);
        return Enumerable.Range(0, length).Select(_ => (float)((2 * random.NextDouble() - 1) * peak)).ToArray();
    }
    private static float[] MechanicalSignal(double[] times)
    {
        var signal = new float[(int)(Rate * 2.1)];
        for (int i = 0; i < Rate * 1.5; i++) signal[i + 500] = (float)(.2 * Math.Sin(2 * Math.PI * 40 * i / Rate));
        foreach (double time in times)
            for (int j = 0; j < 150; j++) signal[(int)(time * Rate) + 500 + j] += (float)(.08 * Math.Exp(-j / 24.0) * Math.Sin(j * 2 * Math.PI * 5700 / Rate));
        return signal;
    }
    private static string Fingerprint(InOutConfig io, TestConfig test)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var routing = RuntimeHelpers.GetUninitializedObject(typeof(AudioRouting));
        typeof(AudioRouting).GetField("_appliedTestConfig", hidden)!.SetValue(routing, test);
        typeof(AudioRouting).GetField("_activeInOutConfig", hidden)!.SetValue(routing, io);
        return (string)typeof(AudioRouting).GetMethod("AppliedModelTestFingerprint", hidden)!.Invoke(routing, [false])!;
    }
    private sealed class DuplicateRubBuzz : IAdditionalAcousticTest
    {
        public AdditionalTestDescriptor Metadata => RubBuzzAdditionalTest.Instance.Metadata with { Id = "rUbBuZz" };
        public bool IsEnabled(TestConfig config) => false;
        public void Validate(TestConfig config, AdditionalTestContext? context = null) { }
        public AdditionalTestPlan Plan(TestConfig config, AdditionalTestContext context) => throw new NotSupportedException();
        public ConfiguredTestResult Analyze(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context) => throw new NotSupportedException();
    }
}
