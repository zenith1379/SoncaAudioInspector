using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using SoncaAudioInspector;

internal static class BriefP5Checks
{
    public static async Task Run(string output, string configPath, string baselineAssembly)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); File.WriteAllLines(Path.Combine(output, "progress.txt"), checks); }
        void Reject(Action action, string why)
        {
            try { action(); } catch (ArgumentException ex) { Check(ex.Message.Contains("P5"), why); return; }
            throw new Exception("Accepted invalid value: " + why);
        }
        var test = new TestConfig { id = "P5", FrequencyRange = [20, 20000] };
        var frequencies = new[] { 20d, 100, 249, 250, 1000, 3999, 4000, 8000, 20000 };
        var target = frequencies.ToDictionary(f => f, _ => 0d);
        ResponseLimitAssessment Assess(Dictionary<double, double> measured) => new ResponseLimitPolicy(ResponseLimitSettings.Capture(test, 3), target, null).Assess(measured, test.EffectiveLineOutlierPercent);
        var measured = frequencies.ToDictionary(f => f, _ => 2.1);
        var old = Assess(measured);
        Check(old.Passed && old.BandAverageSummary == "", "BandAverage absent: legacy point verdict and no summary");
        using var csvOff = new StringWriter(); TestRunner.WriteBandAverageCsvHeader(csvOff, old);
        Check(csvOff.ToString() == "" && !JsonSerializer.Serialize(test).Contains("BandAverage") && !JsonSerializer.Serialize(test).Contains("SingleRunPass"), "Untouched options do not change CSV or JSON");
        test.BandAverageEnabled = true; test.BandAverageToleranceDb = 1.5;
        var avg = Assess(measured);
        Check(!avg.Passed && avg.BandAverages.Count == 3 && avg.IssueSummaries().Any(s => s.Contains("Treble") && s.Contains("2.10")), "Signed band average fails independently of per-point ±3 dB");
        Check(avg.BandAverages.Single(b => b.Name == "Bass").PointCount == 3
            && avg.BandAverages.Single(b => b.Name == "Mid").PointCount == 3
            && avg.BandAverages.Single(b => b.Name == "Treble").PointCount == 3, "D12 250 belongs to Mid, 4000 to Treble, 20000 included");
        using var csvOn = new StringWriter(); TestRunner.WriteBandAverageCsvHeader(csvOn, avg);
        Check(csvOn.ToString().Contains(avg.BandAverageSummary) && avg.Describe().Contains("Trung bình dải (ngoài hõm)"), "Band average present in production CSV writer and Describe");
        foreach (string mode in new[] { "LowerOnly", "Exclude" })
        {
            test.DipWindowsEnabled = true; test.DipWindows = [new() { MinHz = 7900, MaxHz = 8100, Mode = mode }];
            var dipped = target.ToDictionary(p => p.Key, _ => .2); dipped[8000] = -50;
            var result = Assess(dipped);
            Check(result.Passed && Math.Abs(result.BandAverages.Single(b => b.Name == "Treble").AverageDeltaDb - .2) < 1e-12,
                mode + " points excluded from signed band mean");
        }
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, .09, 12.01 })
        { test.BandAverageToleranceDb = value; Reject(test.ValidateDipWindows, "Band average rejects " + value); }
        test.BandAverageToleranceDb = 1.5; test.DipWindowsEnabled = false;
        using var engine = new AudioEngine();
        var runner = new TestRunner(engine) { StandardCurve = target, EvaluationMinFrequencyHz = 20, EvaluationMaxFrequencyHz = 20000,
            ResponseLimits = ResponseLimitSettings.Capture(test, 3) };
        var assessment = runner.EvaluateResponseCurveMetrics(measured, false, false, true, false, out double max, out string comparison, out var counts);
        Check(!runner.BassPassed && !runner.MidPassed && !runner.TreblePassed && !assessment.Passed && max == 2.1, "Real TestRunner metrics honor band average separately for all bands");
        bool passed = false, silent = false, insufficient = false;
        runner.FinalizeFeqOutcomeFromRuns(null, assessment, false, false, 1, 1, 1, false, false, "", ref silent, ref insufficient, max, comparison, ref passed);
        var qaRow = new AutoTestCaseItem { Id = "IO001", Name = "P5", Status = "FAIL", Config = test, ResponseAssessment = assessment,
            ResponseLimits = runner.ResponseLimits, ResponseFrequencies = frequencies, ResponseLevels = measured.Values.ToArray() };
        Check(runner.Steps[1].Details.Contains(assessment.BandAverageSummary)
            && AudioRouting.AppendResponseLimitQaDetails("fixture", qaRow).Contains(assessment.BandAverageSummary), "BandAverage reaches actual FEQ Details and QA details");
        foreach (var (frequency, expected) in new[] { (250d, "Mid"), (4000d, "Treble") })
        {
            Check(TestRunner.FrequencyBandName(frequency) == expected, "D12 TestRunner band at " + frequency);
            var band = TestRunner.TestFrequencyBands.Single(b => b.Frequencies.Any(f => Math.Abs(f - frequency) < 1e-8));
            Check(band.Name.Contains(expected, StringComparison.OrdinalIgnoreCase), "D12 multitone grid band at " + frequency);
            var diag = SpeakerMeasurementDiagnosis.DescribeMeasurement(new() { ResponseValid = true, HasComparableReference = true,
                Response = frequencies.Select(f => new DiagnosisResponsePoint(f, f == frequency ? 4 : 0, 0, 3)).ToArray() });
            Check(diag.Any(s => s.Contains("dung sai ở " + expected.ToLowerInvariant())), "D12 diagnosis uses correct boundary band " + frequency);
        }
        test.BandAverageEnabled = false; test.DipWindowsEnabled = true;
        test.DipWindows = [new() { MinHz = 7900, MaxHz = 8100 }];
        test.CriticalZones = [new() { MinHz = 990, MaxHz = 1010 }];
        var safe = Assess(target);
        Check(safe.TryGetMinimumEffectiveMarginDb(out double margin) && margin == 3, "Margin includes normal, CRITICAL and bridged LowerOnly limits");
        Check(SingleRunMarginPolicy.CanAccept(true, 1, 1, "PASS", false, safe, out _), "First valid complete PASS with >= M accepts one run");
        runner.SingleRunPassWhenMarginEnabled = true; runner.SingleRunPassMarginDb = 1;
        int targetRuns = 2; bool repeat = true;
        Check(runner.TryAcceptFirstResponseRun(safe, 1, 1, true, false, ref targetRuns, ref repeat)
            && targetRuns == 1 && !repeat && runner.LastSingleRunEarlyPassMarginDb == 3,
            "Production TestRunner response-loop hook sets target=1 and bypasses two-run repeatability");
        foreach (var (attempt, count, valid, reference) in new[] { (1, 1, false, false), (1, 1, true, true), (2, 1, true, false), (1, 0, true, false) })
        {
            targetRuns = 2; repeat = true;
            Check(!runner.TryAcceptFirstResponseRun(safe, attempt, count, valid, reference, ref targetRuns, ref repeat) && targetRuns == 2 && repeat,
                $"Production response-loop hook preserves configured runs: attempt={attempt}, validCount={count}, acquisitionValid={valid}, reference={reference}");
        }
        foreach (double f in new[] { 100d, 1000, 8000 })
        {
            var close = new Dictionary<double, double>(target) { [f] = 2.5 };
            Check(!SingleRunMarginPolicy.CanAccept(true, 1, 1, "PASS", false, Assess(close), out _), "Near normal/CRITICAL/LowerOnly upper limit continues voting: " + f);
        }
        foreach (string status in new[] { "FAIL", "INVALID", "NO_SIGNAL" })
            Check(!SingleRunMarginPolicy.CanAccept(true, 1, 1, status, false, safe, out _), "No shortcut for overall " + status + " (including THD/additional/effect failures)");
        Check(!SingleRunMarginPolicy.CanAccept(true, 1, 1, "PASS", true, safe, out _)
            && !SingleRunMarginPolicy.CanAccept(true, 1, 2, "PASS", false, safe, out _)
            && !SingleRunMarginPolicy.CanAccept(false, 1, 1, "PASS", false, safe, out _), "No shortcut for reference acquisition, later attempts or disabled option");
        var invalid = new Dictionary<double, double>(target) { [100] = double.NaN };
        Check(!Assess(invalid).TryGetMinimumEffectiveMarginDb(out _), "Invalid data never supplies an early-pass margin");
        test.AllowLineOutliers = true; test.AllowedLineOutlierPercent = 100;
        var tolerated = new Dictionary<double, double>(target) { [100] = 4 };
        Check(Assess(tolerated).Passed && !Assess(tolerated).TryGetMinimumEffectiveMarginDb(out _), "Tolerated ordinary outlier still prevents one-run pass");
        test.DipWindows![0].Mode = "Exclude";
        var excluded = new Dictionary<double, double>(target) { [8000] = 50 };
        Check(Assess(excluded).TryGetMinimumEffectiveMarginDb(out margin) && margin == 3, "Exclude contributes no margin");
        foreach (double value in new[] { double.NaN, double.NegativeInfinity, 0, -1, 12.01 })
        { test.SingleRunPassMarginDb = value; Reject(test.ValidateRunPolicy, "Run policy rejects " + value); }
        test.SingleRunPassMarginDb = 1;
        await CheckRepeatedBench(Check, test, safe);
        await CheckRestore(Check);
        await CheckPendingQa(Check, output, qaRow);
        foreach (string? status in new string?[] { "INVALID", "NO_SIGNAL", "WAITING", "RUNNING", "RETRY", "PENDING", "UNKNOWN", "", null })
        {
            try { ServerEngine.MapStepStatus(status); throw new Exception("Mapped " + status); } catch (InvalidOperationException) { }
            bool upload = await ServerEngine.UploadAudioQaResultAsync(new() { Id = "synthetic-P5" }, true,
                [new("P5 blocked step", status!, "offline")], [Path.Combine(output, "does-not-exist.png")], true);
            Check(!upload && ServerEngine.LastAudioQaPendingPath == null && ServerEngine.LastError!.Contains("P5 blocked step")
                && ServerEngine.LastError.Contains(status ?? ""), "D16 blocks before disk/network with named step: " + (status ?? "null"));
        }
        foreach (var (overall, statuses) in new[] { (true, new[] { "FAIL" }), (false, new[] { "PASS" }), (true, Array.Empty<string>()) })
        {
            bool upload = await ServerEngine.UploadAudioQaResultAsync(new() { Id = "synthetic" }, overall, statuses.Select(s => new ServerEngine.AudioQaStepResult("mismatch", s, "")), [], true);
            Check(!upload && ServerEngine.LastAudioQaPendingPath == null, "D16 empty/mismatched overall cannot create pending or HTTP");
        }
        Check(ServerEngine.ValidateAndMapQaSteps([new("a", "passed", ""), new("b", "fAiLeD", "")], false).Select(s => s.status).SequenceEqual(["PASS", "FAIL"]), "D16 strict case-insensitive final statuses and step ordering");
        int identities = BriefP2IdentityChecks.Run(configPath, baselineAssembly, output);
        Check(identities == 88, "88 baseline P4 identity assertions across three models");
        CheckNewOptionsIdentity(Check, configPath);
        ReplayBaseline(Check, baselineAssembly, target, output);
        ReplaySweepBaseline(Check, baselineAssembly, output);
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P5 assertions plus {identities} P4 identity assertions; offline only."));
        Console.WriteLine($"PASS {checks.Count} P5 assertions + {identities} baseline P4 identities; no audio/BLE/HTTP.");
    }
    private static async Task CheckRepeatedBench(Action<bool, string> check, TestConfig test, ResponseLimitAssessment safe)
    {
        test.SingleRunPassWhenMarginDbEnabled = true;
        var bench = new MemoryBench();
        var reference = new C360Reference("offline", DateTimeOffset.UtcNow, new() { [100] = 0, [1000] = 0, [8000] = 0 }, null, null, 2);
        bench.Result = new(C360ElectronicStage.LineIn, "PASS", "full criteria passed") { Reference = reference, ResponseAssessment = safe };
        var setup = new C360ElectronicSetup("out", "usb", "in") { TestConfiguration = test };
        var result = await new C360RepeatedBench(bench, test.RunPolicy, _ => bench).CaptureAsync(C360ElectronicStage.LineIn, setup, default);
        check(result.Status == "PASS" && bench.Captures == 1 && result.Summary.Contains("1/1 lượt đạt") && result.Summary.Contains("1 lượt: cách biên tối thiểu"), "Real C360 vote stops after one complete PASS");
        bench.Captures = 0; bench.Result = bench.Result with { Reference = null };
        result = await new C360RepeatedBench(bench, test.RunPolicy, _ => bench).CaptureAsync(C360ElectronicStage.LineIn, setup, default);
        check(bench.Captures == 2 && result.Attempts.Count == 2, "C360 without reference never takes margin shortcut");
    }
    private static async Task CheckRestore(Action<bool, string> check)
    {
        foreach (string fault in new[] { "write", "read", "mismatch" })
        {
            var bench = new MemoryBench { RestoreFault = fault };
            var baseline = new Dictionary<ushort, ushort>(bench.State);
            var state = new C360BleRestoreState { BaselineState = baseline, DeviceIdentity = bench.DeviceIdentity };
            var completed = new Dictionary<C360ElectronicStage, C360ElectronicResult>();
            var progress = new InlineProgress(r => { if (r.Status == "PASS") completed[r.Stage] = r; });
            bool finalized = false, uploaded = false;
            try
            {
                await new C360ElectronicSuite().RunResumableAsync(bench, new("out", "usb", "in"), state, progress, default, [C360ElectronicStage.LineIn]);
                finalized = true; uploaded = true;
            }
            catch (InvalidOperationException) { }
            check(completed.Count == 1 && state.BleRestorePending && !finalized && !uploaded, "D14 " + fault + ": all stages PASS but restore pending, completion/upload unreachable");
            bench.RestoreFault = ""; int captures = bench.Captures, writes = bench.Writes;
            var results = await new C360ElectronicSuite().RunResumableAsync(bench, new("out", "usb", "in"), state, progress, default, []);
            check(!state.BleRestorePending && results.Count == 0 && bench.Captures == captures && bench.Writes > writes
                && baseline.All(p => p.Value == bench.State[p.Key]), "D14 " + fault + ": empty remaining restores original baseline/readback without capture");
        }
    }
    private static async Task CheckPendingQa(Action<bool, string> check, string output, AutoTestCaseItem row)
    {
        string folder = Path.GetFullPath(Path.Combine(output, "qa-retry"));
        Directory.CreateDirectory(folder);
        var saved = CompletedMeasurementPendingQaSession.Create("synthetic", "config", "P5-serial", folder, false, true, [row], []);
        foreach (string graph in saved.GraphPaths) if (File.Exists(graph)) File.Delete(graph);
        row.ResponseLevels[0] = 99;
        check(saved.Cases[0].ResponseLevels[0] != 99, "D15 completed measurement snapshot independent of mutable live row");
        int repairs = 0, submits = 0;
        bool complete = await saved.TryCompleteAsync((r, p) => { repairs++; File.WriteAllBytes(p, [137, 80, 78, 71]); }, () => { submits++; throw new IOException("mock before SavePending"); });
        check(!complete && repairs == 1 && saved.LastQaError!.Contains("before SavePending") && File.Exists(Path.Combine(folder, CompletedMeasurementPendingQaSession.FileName)), "D15 missing graph repaired; pre-SavePending failure retains durable QA completion state");
        var reloaded = CompletedMeasurementPendingQaSession.Load(Path.Combine(folder, CompletedMeasurementPendingQaSession.FileName));
        check(reloaded.Cases[0].Status == "FAIL" && reloaded.Cases[0].ResponseAssessment!.BandAverageEnabled && reloaded.StateDescription.Contains("QA chưa xong"), "D15 restart round trip retains measurement, assessment and separate pending-QA state");
        complete = await reloaded.TryCompleteAsync((r, p) => throw new Exception("unnecessary repair"), () =>
        {
            submits++;
            return Task.FromResult(LocalQaStorage.SavePending(new("offline", "staff", "P5-serial", false,
                [new("P5", "FAIL", "fixture")], reloaded.GraphPaths.Select(Path.GetFileName).Select(n => n!).ToArray(), true,
                "P5-session", DateTimeOffset.UtcNow), reloaded.GraphPaths, output) != null);
        });
        check(complete && submits == 2 && !File.Exists(Path.Combine(folder, CompletedMeasurementPendingQaSession.FileName))
            && File.Exists(Path.Combine(folder, ".pending-audio-qa.json")), "D15 retry enters LocalQaStorage queue without repeating measurement or graph generation");
        string root = Path.Combine(output, "qa-lookup");
        foreach (string dir in new[] { "20261104/P5Q_090000000", "20261105/P5Q_100000000", "20261105/P5Q_100000000/attempt-1", "20261105/OTHER_100000000" })
        { Directory.CreateDirectory(Path.Combine(root, dir)); File.WriteAllText(Path.Combine(root, dir, CompletedMeasurementPendingQaSession.FileName), "{}"); }
        File.SetLastWriteTimeUtc(Path.Combine(root, "20261104/P5Q_090000000", CompletedMeasurementPendingQaSession.FileName), DateTime.UtcNow.AddDays(-1));
        var found = CompletedMeasurementPendingQaSession.FindPendingFiles(root, "P5Q");
        check(found.Select(p => Path.GetFileName(Path.GetDirectoryName(p))).SequenceEqual(["P5Q_100000000", "P5Q_090000000"])
            && CompletedMeasurementPendingQaSession.FindPendingFiles(root, "").Length == 0 && CompletedMeasurementPendingQaSession.FindPendingFiles(root, "P5*").Length == 0,
            "D15 start lookup reads only <day>/<serial>_* session roots, newest first; no capture subfolders or other serials");
        check(AudioRouting.ShouldDeferAuxiliaryViews(true, false) && !AudioRouting.ShouldDeferAuxiliaryViews(true, true)
            && !AudioRouting.ShouldDeferAuxiliaryViews(false, false), "Group A defers auxiliary views only during suite with Details closed");
    }
    private static void CheckNewOptionsIdentity(Action<bool, string> check, string configPath)
    {
        var config = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!; C360ModelDefinition.EnsureSeed(config);
        foreach (var model in config.models)
        {
            var io = model.testItems.InOut;
            var setup = new C360ElectronicSetup("out", "usb", "in") { BluetoothPlaybackId = "bt" };
            var plan = new C360ModelPlan(io, setup, []);
            foreach (var test in io.Tests)
            {
                string original = Fingerprint(io, test);
                bool hasStage = Enum.TryParse<C360ElectronicStage>(test.SpeakerStage, out var stage) && test.IsEnabled;
                string? key = hasStage ? C360Reference.ConfigurationKey(model.model, plan, stage, "offline", setup) : null;
                test.BandAverageEnabled = true; test.BandAverageToleranceDb = 2;
                test.SingleRunPassWhenMarginDbEnabled = true; test.SingleRunPassMarginDb = 1.2;
                test.DipWindowsEnabled = true; test.DipWindows = [new() { MinHz = 56, MaxHz = 75 }];
                if (!C360ModelDefinition.Matches(io)) { test.TestRubBuzz = true; test.RubBuzzPeakLimitDb = -25; }
                check(Fingerprint(io, test) == original && (!hasStage || key == C360Reference.ConfigurationKey(model.model, plan, stage, "offline", setup)), model.model + "/" + test.id + " all new judgement options preserve acquisition identity");
                var roundtrip = JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(test))!;
                check(roundtrip.IsBandAverageEnabled && roundtrip.IsSingleRunMarginEnabled && roundtrip.SingleRunPassMarginDb == 1.2, model.model + "/" + test.id + " new options JSON round trip");
            }
        }
    }
    private static string Fingerprint(InOutConfig io, TestConfig test)
    {
        var type = typeof(AudioRouting); var owner = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_activeInOutConfig", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, io);
        type.GetField("_appliedTestConfig", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, test);
        return (string)type.GetMethod("AppliedModelTestFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, [false])!;
    }
    private static void ReplayBaseline(Action<bool, string> check, string baselineAssembly, Dictionary<double, double> target, string output)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(baselineAssembly))!;
        var context = new AssemblyLoadContext("P5-replay", true);
        context.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll")) ? context.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var baseline = context.LoadFromAssemblyPath(Path.GetFullPath(baselineAssembly));
        var timings = new List<string>();
        foreach (double delta in new[] { .2, 4d, double.NaN })
        {
            var curve = target.ToDictionary(p => p.Key, _ => delta);
            string Replay(Assembly assembly)
            {
                var type = assembly.GetType("SoncaAudioInspector.TestRunner")!;
                var engine = (IDisposable)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
                try
                {
                    object runner = Activator.CreateInstance(type, engine)!;
                    type.GetProperty("StandardCurve")!.SetValue(runner, target);
                    type.GetProperty("EvaluationMinFrequencyHz")!.SetValue(runner, 20d);
                    type.GetProperty("EvaluationMaxFrequencyHz")!.SetValue(runner, 20000d);
                    object?[] args = [curve, false, false, true, false, 0d, "", null];
                    object assessment = type.GetMethod("EvaluateResponseCurveMetrics")!.Invoke(runner, args)!;
                    return JsonSerializer.Serialize(new { Passed = assessment.GetType().GetProperty("Passed")!.GetValue(assessment),
                        Points = assessment.GetType().GetProperty("Points")!.GetValue(assessment),
                        Bass = type.GetProperty("BassPassed")!.GetValue(runner), Mid = type.GetProperty("MidPassed")!.GetValue(runner),
                        Treble = type.GetProperty("TreblePassed")!.GetValue(runner) }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
                }
                finally { engine.Dispose(); }
            }
            var watch = Stopwatch.StartNew(); string before = Replay(baseline); watch.Stop(); timings.Add($"P4 delta={delta}: {watch.Elapsed.TotalMilliseconds:F3} ms");
            watch.Restart(); string after = Replay(typeof(TestRunner).Assembly); watch.Stop(); timings.Add($"P5 delta={delta}: {watch.Elapsed.TotalMilliseconds:F3} ms");
            check(before == after, "P4/P5 offline curve replay identical verdict, all response points and band flags delta=" + delta);
        }
        File.WriteAllLines(Path.Combine(output, "replay-timing.txt"), timings);
    }
    private static void ReplaySweepBaseline(Action<bool, string> check, string baselineAssembly, string output)
    {
        var settings = new LogSweepSettings(44100, 18, 20000, 65536d / 44100, .1, 24, 3, 1);
        var excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
        var captured = new float[4096 + excitation.Length + 44100 * 3];
        for (int i = 0; i < excitation.Length; i++) captured[4096 + i] = excitation[i] * .4f + .0001f * excitation[i] * excitation[i];
        string folder = Path.GetDirectoryName(Path.GetFullPath(baselineAssembly))!;
        var context = new AssemblyLoadContext("P5-sweep-replay", true);
        context.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll")) ? context.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
        var baseline = context.LoadFromAssemblyPath(Path.GetFullPath(baselineAssembly));
        var oldSettings = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings), baseline.GetType("SoncaAudioInspector.LogSweepSettings")!)!;
        var method = baseline.GetType("SoncaAudioInspector.StandardAcousticMeasurement")!.GetMethods().Single(m => m.Name == "AnalyzeLogSweep" && m.GetParameters().Length == 5);
        var watch = Stopwatch.StartNew(); var old = method.Invoke(null, [excitation, captured, oldSettings, null, -80d])!;
        watch.Stop(); double beforeMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); var current = StandardAcousticMeasurement.AnalyzeLogSweep(excitation, captured, settings, null, -80); watch.Stop();
        var json = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        foreach (string property in new[] { "Validity", "FrequencyResponse", "SweepHarmonics", "RewDistortion", "EstimatedSnrDb", "IsClipped", "ImpulseEffects" })
            check(JsonSerializer.Serialize(old.GetType().GetProperty(property)!.GetValue(old), json)
                == JsonSerializer.Serialize(current.GetType().GetProperty(property)!.GetValue(current), json), "P4/P5 identical synthetic 64K capture replay: " + property);
        File.AppendAllLines(Path.Combine(output, "replay-timing.txt"), [$"Synthetic 64K sweep P4 wall={beforeMs:F3} ms; P5 wall={watch.Elapsed.TotalMilliseconds:F3} ms (unchanged analysis, separate cold contexts)"]);
        string qaFolder = Path.GetFullPath(Path.Combine(output, "sweep-snapshot"));
        var row = new AutoTestCaseItem { Id = "IO001", Name = "sweep", Status = "PASS", ImpulseResult = current,
            ResponseFrequencies = current.FrequencyResponse.Select(p => p.FrequencyHz).ToArray(),
            ResponseLevels = current.FrequencyResponse.Select(p => p.LevelDb).ToArray() };
        var session = CompletedMeasurementPendingQaSession.Create("synthetic", "sweep", "serial", qaFolder, true, true, [row], []);
        session.Persist(); var loaded = CompletedMeasurementPendingQaSession.Load(Path.Combine(qaFolder, CompletedMeasurementPendingQaSession.FileName));
        check(loaded.Cases[0].ImpulseResult != null && loaded.Cases[0].ImpulseResult!.FrequencyResponse.Count == current.FrequencyResponse.Count,
            "D15 full synthetic sweep/THD/IR snapshot persists and reloads for QA repair");
    }
    private sealed class InlineProgress(Action<C360ElectronicResult> report) : IProgress<C360ElectronicResult>
    { public void Report(C360ElectronicResult value) => report(value); }
    private sealed class MemoryBench : IC360ElectronicBench
    {
        public string DeviceIdentity => "P5-memory-only";
        public Dictionary<ushort, ushort> State { get; } = new() {
            [SoncaBleMixerProfile.InputCapabilities] = 73, [SoncaBleMixerProfile.InputSource] = 4,
            [SoncaBleMixerProfile.MasterVolume] = 8160, [SoncaBleMixerProfile.MusicVolume] = 4064, [SoncaBleMixerProfile.MicVolume] = 4097,
            [SoncaBleMixerProfile.MicMute] = 1, [SoncaBleMixerProfile.MusicMute] = 0, [SoncaBleMixerProfile.MasterMute] = 0,
            [SoncaBleMixerProfile.LiveMute] = 0, [SoncaBleMixerProfile.EchoEnable] = 1, [SoncaBleMixerProfile.ReverbEnable] = 1 };
        public int Captures, Writes;
        public string RestoreFault = "";
        public C360ElectronicResult? Result;
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token)
        {
            if (Captures > 0 && RestoreFault == "read") throw new InvalidOperationException("mock restore read");
            return Task.FromResult<IReadOnlyDictionary<ushort, ushort>>(new Dictionary<ushort, ushort>(State));
        }
        public Task WriteAsync(ushort register, ushort value, CancellationToken token)
        {
            Writes++;
            if (Captures > 0 && register == SoncaBleMixerProfile.MicVolume && RestoreFault == "write") throw new InvalidOperationException("mock restore write");
            if (!(Captures > 0 && register == SoncaBleMixerProfile.MicVolume && RestoreFault == "mismatch")) State[register] = value;
            return Task.CompletedTask;
        }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        { Captures++; return Task.FromResult(Result ?? new(stage, "PASS", "offline complete")); }
        public void StopAudio() { }
    }
}
