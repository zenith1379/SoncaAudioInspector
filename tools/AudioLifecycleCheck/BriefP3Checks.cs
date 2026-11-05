using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using SoncaAudioInspector;

internal static class BriefP3Checks
{
    public static async Task Run(string output, string configPath = "", string baselineAssembly = "")
    {
        if (!File.Exists(configPath) || !File.Exists(baselineAssembly)) throw new ArgumentException("P3 requires --config and --baseline-assembly for identity checks.");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }

        // T1: [IO001 A, IO002 B, IO003 C, IO004 D], drag D (3) to top (0) -> [IO001 D (ref IO004), IO002 A (ref IO001), IO003 B (ref IO002), IO004 C (ref IO003)]
        var list1 = new List<TestConfig>
        {
            new() { id = "IO001", name = "A", PlaybackOut = "OUT", RecordingIn = "IN" },
            new() { id = "IO002", name = "B", PlaybackOut = "OUT", RecordingIn = "IN" },
            new() { id = "IO003", name = "C", PlaybackOut = "OUT", RecordingIn = "IN" },
            new() { id = "IO004", name = "D", PlaybackOut = "OUT", RecordingIn = "IN" }
        };
        Check(TestSequenceOrder.MoveTest(list1, 3, 0), "T1: MoveTest(3, 0) returns true");
        Check(list1[0].name == "D" && list1[0].id == "IO001" && list1[0].ReferenceTestId == "IO004", "T1: D at index 0 has id=IO001, ReferenceTestId=IO004");
        Check(list1[1].name == "A" && list1[1].id == "IO002" && list1[1].ReferenceTestId == "IO001", "T1: A at index 1 has id=IO002, ReferenceTestId=IO001");
        Check(list1[2].name == "B" && list1[2].id == "IO003" && list1[2].ReferenceTestId == "IO002", "T1: B at index 2 has id=IO003, ReferenceTestId=IO002");
        Check(list1[3].name == "C" && list1[3].id == "IO004" && list1[3].ReferenceTestId == "IO003", "T1: C at index 3 has id=IO004, ReferenceTestId=IO003");

        // T2: Multiple consecutive moves never overwrite existing ReferenceTestId
        Check(TestSequenceOrder.MoveTest(list1, 0, 2), "T2: Move D from 0 to 2");
        Check(list1[2].name == "D" && list1[2].id == "IO003" && list1[2].ReferenceTestId == "IO004", "T2: D at index 2 has id=IO003 and preserves ReferenceTestId=IO004");
        Check(TestSequenceOrder.MoveTest(list1, 2, 3), "T2: Move D back to index 3");
        Check(list1[0].name == "A" && list1[0].id == "IO001" && list1[0].ReferenceTestId == "IO001", "T2: A back at 0 keeps ReferenceTestId=IO001");
        Check(list1[3].name == "D" && list1[3].id == "IO004" && list1[3].ReferenceTestId == "IO004", "T2: D back at 3 keeps ReferenceTestId=IO004");

        // T3: Adding a new test at a position whose IO code collides with an existing ReferenceTestId gets -N2, -N3
        var list3 = new List<TestConfig>
        {
            new() { id = "IO001", name = "D", ReferenceTestId = "IO004", PlaybackOut = "OUT", RecordingIn = "IN" },
            new() { id = "IO002", name = "A", ReferenceTestId = "IO001", PlaybackOut = "OUT", RecordingIn = "IN" },
            new() { id = "IO003", name = "B", ReferenceTestId = "IO002", PlaybackOut = "OUT", RecordingIn = "IN" }
        };
        var added1 = new TestConfig { id = "IO004", name = "New1", PlaybackOut = "OUT", RecordingIn = "IN" };
        list3.Add(added1);
        TestSequenceOrder.RenumberByPosition(list3);
        TestSequenceOrder.EnsureUniqueIdentityForAddedTest(list3, added1);
        Check(added1.id == "IO004" && added1.ReferenceTestId == "IO004-N2", "T3: Added test at IO004 gets ReferenceTestId=IO004-N2 when IO004 is already taken by D");
        var added2 = new TestConfig { id = "IO004", name = "New2", PlaybackOut = "OUT", RecordingIn = "IN" };
        list3.Insert(3, added2);
        TestSequenceOrder.RenumberByPosition(list3);
        added2.ReferenceTestId = null;
        TestSequenceOrder.EnsureUniqueIdentityForAddedTest(list3, added2);
        Check(added2.ReferenceTestId == "IO004-N3", "T3: Second collision on IO004 gets IO004-N3");

        // T4: Deleting a middle test renumbers remaining tests and preserves their ReferenceTestId
        list3.RemoveAt(1);
        TestSequenceOrder.RenumberByPosition(list3);
        Check(list3.Select(t => t.id).SequenceEqual(["IO001", "IO002", "IO003", "IO004"]), "T4: Deleting middle test renumbers contiguous IO001..IO004");
        Check(list3[0].ReferenceTestId == "IO004" && list3[1].ReferenceTestId == "IO002", "T4: Deleting middle test preserves ReferenceTestId of remaining tests");
        var deleteMiddle = list1.Select(t => JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(t))!).ToList();
        deleteMiddle.RemoveAt(1); TestSequenceOrder.RenumberByPosition(deleteMiddle);
        Check(deleteMiddle.Select(t => t.id).SequenceEqual(["IO001", "IO002", "IO003"])
            && deleteMiddle.Select(t => t.ReferenceTestId).SequenceEqual(["IO001", "IO003", "IO004"]), "T4: deleting B from four tests produces IO001..IO003 and preserves A/C/D identities");

        // T5: Legacy non-contiguous IDs [IO001, IO003, IO005] -> RenumberByPosition
        var legacyList = new List<TestConfig>
        {
            new() { id = "IO001", name = "L1" },
            new() { id = "IO003", name = "L2" },
            new() { id = "IO005", name = "L3" }
        };
        TestSequenceOrder.RenumberByPosition(legacyList);
        Check(legacyList[0].id == "IO001" && legacyList[0].ReferenceTestId == null, "T5: IO001 stays IO001 with null ReferenceTestId");
        Check(legacyList[1].id == "IO002" && legacyList[1].ReferenceTestId == "IO003", "T5: IO003 becomes IO002 with ReferenceTestId=IO003");
        Check(legacyList[2].id == "IO003" && legacyList[2].ReferenceTestId == "IO005", "T5: IO005 becomes IO003 with ReferenceTestId=IO005");

        // T6: Disabled tests (Enable = false) still participate in positional numbering
        var disabledList = new List<TestConfig>
        {
            new() { id = "IO001", name = "On1", Enable = true },
            new() { id = "IO002", name = "Off2", Enable = false },
            new() { id = "IO003", name = "On3", Enable = true }
        };
        TestSequenceOrder.MoveTest(disabledList, 2, 0);
        Check(disabledList.Select(t => t.id).SequenceEqual(["IO001", "IO002", "IO003"]) && !disabledList[2].IsEnabled && disabledList[2].ReferenceTestId == "IO002",
            "T6: Disabled test keeps positional numbering and ReferenceTestId");

        // T7: ParseModel rejects duplicate id, duplicate ReferenceTestId, and cross-collision between ReferenceTestId and id
        foreach (var (label, t1Json, t2Json) in new[]
        {
            ("duplicate id", """{"id":"IO001","name":"T1","PlaybackOut":"OUT","RecordingIn":"IN"}""", """{"id":"IO001","name":"T2","PlaybackOut":"OUT","RecordingIn":"IN"}"""),
            ("duplicate ReferenceTestId", """{"id":"IO001","name":"T1","PlaybackOut":"OUT","RecordingIn":"IN","ReferenceTestId":"IO009"}""", """{"id":"IO002","name":"T2","PlaybackOut":"OUT","RecordingIn":"IN","ReferenceTestId":"IO009"}"""),
            ("ReferenceTestId collides with another test's id", """{"id":"IO001","name":"T1","PlaybackOut":"OUT","RecordingIn":"IN","ReferenceTestId":"IO002"}""", """{"id":"IO002","name":"T2","PlaybackOut":"OUT","RecordingIn":"IN"}""")
        })
        {
            string badJson = $$"""
            {
              "model": "BadModel",
              "testItems": {
                "InOut": {
                  "Devices": { "Input": { "OUT": "Speaker" }, "Output": { "IN": "Mic" } },
                  "Tests": [ {{t1Json}}, {{t2Json}} ]
                }
              }
            }
            """;
            bool threw = false;
            try { ModelConfigEditorWindow.ParseModel("BadModel", badJson); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "T7: ParseModel rejects " + label);
        }

        // T9 exercises the production loader, with no InitializeRouting/Loaded/device discovery.
        string appDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "app"));
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(appDirectory, name.Name + ".dll");
            return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(appDirectory, "runtimes", "win-x64", "native", "libSkiaSharp.dll"));
        Exception? staError = null;
        var sta = new Thread(() =>
        {
            string? previousDataDirectory = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
            var app = new App(); app.InitializeComponent();
            typeof(System.Windows.Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, null);
            app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            using var engine = new AudioEngine();
            var runner = new TestRunner(engine) { UseLogSweepFrequencyResponse = true };
            AudioRouting? routing = null;
            try
            {
                string fixtureRoot = Path.GetFullPath(Path.Combine(output, "temp-standards-" + Guid.NewGuid().ToString("N")));
                Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", fixtureRoot);
                routing = new AudioRouting();
                const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string name, object value) => typeof(AudioRouting).GetField(name, hidden)!.SetValue(routing, value);
                object? Call(string name, params object?[] values) => typeof(AudioRouting).GetMethod(name, hidden)!.Invoke(routing, values);
                Set("_audioEngine", engine); Set("_testRunner", runner); Set("_legacyStandardsImported", true);
                Call("InitCharts");
                var before = new TestConfig { id = "IO004", name = "Acoustic Test", PlaybackOut = "OUT1", RecordingIn = "IN1", FrequencyResponseMethod = "LogSweep" };
                Set("_appliedTestConfig", before);
                string fpBefore = (string)Call("AppliedModelTestFingerprint", false)!;
                string keyBefore = (string)Call("GetStandardDeviceKey", false)!;
                var after = JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(before))!;
                after.id = "IO001"; after.ReferenceTestId = "IO004"; Set("_appliedTestConfig", after);
                string fpPrimary = (string)Call("AppliedModelTestFingerprint", false)!;
                string fpFallback = (string)Call("AppliedModelTestFingerprint", true)!;
                string primaryKey = (string)Call("GetStandardDeviceKey", false)!;
                string fallbackKey = (string)Call("GetStandardDeviceKey", true)!;
                Check(fpBefore == fpPrimary && primaryKey == keyBefore, "T9: primary fingerprint/key preserve original IO004 after reorder");
                Check(fpFallback != fpPrimary && fallbackKey != primaryKey, "T9: one-way fallback uses current display id IO001");
                void WriteFixture(string path, string fingerprint)
                {
                    Check(Path.GetFullPath(path).StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "T9: fixture path stays inside artifact");
                    string csv = string.Join("\r\n", "# LevelBasis=" + runner.CurrentFrequencyResponseLevelBasis,
                        "# ResponseAnalysis=" + ResponseSmoothing.AnalysisId,
                        "# SweepSamples=" + Math.Round(runner.LogSweepDurationSeconds * engine.PlaybackSampleRate),
                        "# ModelTestConfig=" + fingerprint, "# Reference1kHzDbFS=-12.5",
                        "Frequency (Hz),Standard dB,Lower Limit,Upper Limit",
                        "100,-10,-13,-7", "1000,-12.5,-15.5,-9.5", "10000,-15,-18,-12") + "\r\n";
                    File.WriteAllText(path, csv, new UTF8Encoding(false));
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new {
                        analysisId = ResponseSmoothing.AnalysisId, levelBasis = runner.CurrentFrequencyResponseLevelBasis,
                        csvSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                        rawPoints = new[] { new { frequencyHz = 100d, levelDb = -10d }, new { frequencyHz = 1000d, levelDb = -12.5 }, new { frequencyHz = 10000d, levelDb = -15d } }
                    }));
                }
                void VerifyLoaded(string label, double expectedAt1k)
                {
                    Call("CheckAndLoadStandardDevice");
                    var curve = (Dictionary<double, double>?)typeof(AudioRouting).GetField("_standardCurve", hidden)!.GetValue(routing);
                    var raw = (IReadOnlyDictionary<double, double>)typeof(AudioRouting).GetField("_rawStandardCurve", hidden)!.GetValue(routing)!;
                    Check(curve is { Count: 3 } && raw.Count == 3 && curve[1000] == expectedAt1k && raw[1000] == expectedAt1k
                        && (bool)Call("HasComparableStandardCurve")!, label);
                }
                string primaryFile = (string)Call("GetStandardDeviceFileNameForKey", primaryKey)!;
                WriteFixture(primaryFile, fpPrimary); VerifyLoaded("T9a: real primary CSV + validated raw sidecar load after reorder", -12.5);
                Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(fixtureRoot, "fallback-only"));
                string fallbackFile = (string)Call("GetStandardDeviceFileNameForKey", fallbackKey)!;
                WriteFixture(fallbackFile, fpFallback);
                string csvHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fallbackFile)));
                string sidecarHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(fallbackFile, ".json"))));
                VerifyLoaded("T9b: real loader accepts display-id fallback when primary absent", -12.5);
                runner.NormalizeFrequencyResponseToOneKilohertz = true;
                VerifyLoaded("T9c: raw display-id fallback normalizes CSV and sidecar around 1 kHz", 0);
                Check(csvHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fallbackFile)))
                    && sidecarHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.ChangeExtension(fallbackFile, ".json")))), "T9: fallback and normalization leave original fixture files byte-identical");
                runner.NormalizeFrequencyResponseToOneKilohertz = false;
                string sidecar = Path.ChangeExtension(fallbackFile, ".json");
                string originalSidecar = File.ReadAllText(sidecar);
                File.WriteAllText(sidecar, originalSidecar.Replace(csvHash, new string('0', 64)));
                Call("CheckAndLoadStandardDevice");
                Check(((IReadOnlyDictionary<double, double>)typeof(AudioRouting).GetField("_rawStandardCurve", hidden)!.GetValue(routing)!).Count == 0, "T9: mismatched csvSha256 sidecar is rejected by real loader");
                File.WriteAllText(sidecar, originalSidecar);
                Check(!routing.IsLoaded, "T9: reference checks never trigger Loaded/device discovery");
            }
            catch (Exception ex) { staError = ex; }
            finally
            {
                if (routing != null)
                    foreach (string name in new[] { "_routingMeterTimer", "_frequencyChartRefreshTimer" })
                        (typeof(AudioRouting).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(routing) as System.Windows.Threading.DispatcherTimer)?.Stop();
                Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", previousDataDirectory);
                app.Shutdown();
            }
        });
        sta.SetApartmentState(ApartmentState.STA); sta.Start(); sta.Join();
        if (staError != null) throw new Exception("STA reference fixture failed", staError);

        // T8: Identity preservation against baseline assembly (84 checks)
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            int idCount = BriefP2IdentityChecks.Run(configPath, baselineAssembly, output);
            Check(idCount > 0, $"T8: PASS {idCount} identities matched baseline");

        }

        // T10: C360 full 7-stage plan & suite when ACOUSTIC 3 (index 6) is moved to index 0 (tests[0])
        var c360Model = C360ModelDefinition.Create("CS-360");
        var c360Config = c360Model.testItems.InOut;
        c360Config.Devices.Input["MIC IN"] = "1-2";
        c360Config.Devices.Input["LINE IN"] = "3-4";
        c360Config.Devices.Input["USB A"] = "USB Audio";
        c360Config.Devices.Input["BT"] = "BT Speaker";
        c360Config.Devices.Output["LINE OUT"] = "1-2";
        c360Config.Devices.Output["Measurement Mic"] = "Mic";
        var acousticTest = c360Config.Tests.Single(t => t.SpeakerStage == "AcousticMic");
        c360Config.Tests.Remove(acousticTest);
        c360Config.Tests.Add(acousticTest);
        TestSequenceOrder.RenumberByPosition(c360Config.Tests);
        foreach (var t in c360Config.Tests)
        {
            t.Enable = true;
            t.RecordingChannel = 1;
            t.ReferenceTestId = null;
        }
        c360Config.Tests.Single(t => t.SpeakerStage == "AcousticMic").AcousticSetupPending = false;
        var c360Endpoints = new[]
        {
            new C360ModelEndpoint("1-2", "1-2", false),
            new C360ModelEndpoint("3-4", "3-4", false),
            new C360ModelEndpoint("usb", "USB Audio", false),
            new C360ModelEndpoint("line-capture", "1-2", true),
            new C360ModelEndpoint("mic-capture", "Mic", true)
        };
        var c360Defaults = new C360ElectronicSetup("1-2", "usb", "line-capture", CaptureChannel: 0);
        var planBefore = C360ModelPlan.Create(c360Config, c360Endpoints, c360Defaults);
        var keysBefore = Enum.GetValues<C360ElectronicStage>()
            .ToDictionary(s => s, s => C360Reference.ConfigurationKey("CS-360", planBefore, s, "mixer-key"));

        Check(TestSequenceOrder.MoveTest(c360Config.Tests, 6, 0), "T10: Move ACOUSTIC 3 from index 6 to index 0");
        Check(c360Config.Tests[0].SpeakerStage == "AcousticMic" && c360Config.Tests[0].id == "IO001" && c360Config.Tests[0].ReferenceTestId == "IO007",
            "T10: ACOUSTIC 3 at index 0 has id=IO001 and ReferenceTestId=IO007");

        var planAfter = C360ModelPlan.Create(c360Config, c360Endpoints, c360Defaults);
        Check(planAfter.Stages[0] == C360ElectronicStage.AcousticMic, "T10: Plan first stage is AcousticMic");
        Check(planAfter.Setup.LineOutCaptureId == "line-capture" && planAfter.Setup.CaptureChannel == 0,
            "T10: Plan default LineOutCaptureId and CaptureChannel still come from first electronic test, not AcousticMic at tests[0]");
        var acousticTestAfter = planAfter.Configuration.Tests.Single(t => t.SpeakerStage == "AcousticMic");
        Check(planAfter.SetupFor(acousticTestAfter).LineOutCaptureId == "mic-capture",
            "T10: SetupFor(AcousticMic) resolves mic-capture");

        foreach (var stage in Enum.GetValues<C360ElectronicStage>())
        {
            string keyAfter = C360Reference.ConfigurationKey("CS-360", planAfter, stage, "mixer-key");
            Check(keyAfter == keysBefore[stage], $"T10: C360 ConfigurationKey for {stage} is 100% preserved after reordering ACOUSTIC 3 to top");
        }

        var fakeBench = new SequenceBench();
        var initialState = await fakeBench.ReadAsync(default);
        var results = await new C360ElectronicSuite().RunAsync(planAfter.Bind(fakeBench), planAfter.Setup, stages: planAfter.Stages);
        Check(results.Count == 7 && results.All(r => r.Status == "PASS") && fakeBench.Captures.Select(p => p.Stage).SequenceEqual(planAfter.Stages), "T10: real suite executes all seven stages in reordered model order with fake bench");
        Check(fakeBench.Captures[0].Setup.LineOutCaptureId == "mic-capture" && fakeBench.Captures.Skip(1).All(p => p.Setup.LineOutCaptureId == "line-capture"), "T10: fake captures use independent acoustic mic and electronic LINE OUT");
        Check(initialState.All(p => fakeBench.State[p.Key] == p.Value), "T10: real suite restores complete in-memory mixer state");
        CheckFeqFinalizer(Check);

        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P3 sequence logic checks."));
        Console.WriteLine($"PASS {checks.Count} P3 sequence logic checks.");
        await Task.CompletedTask;
    }
    private static void CheckFeqFinalizer(Action<bool, string> check)
    {
        check(typeof(TestRunner).GetProperty(nameof(TestRunner.ReferenceAcquisitionValid))!.GetSetMethod(true)!.IsPrivate, "P3F-4: reference-valid flag retains private setter");
        using var engine = new AudioEngine();
        Dictionary<double, double> target = new() { [100] = 0, [1000] = 0, [5000] = 0 };
        foreach (bool fail in new[] { false, true })
        {
            var runner = new TestRunner(engine) { StandardCurve = target };
            var curve = new Dictionary<double, double>(target) { [1000] = fail ? 4 : 2 };
            var limits = runner.EvaluateResponseCurveMetrics(curve, false, false, true, false, out double max, out string cmp, out var counts);
            check(counts == (0, 1, fail ? 1 : 0, 1, 0, 1), "P3F-4: real curve metrics preserve per-band failure/point counts");
            var logs = new List<string>(); runner.OnLogMessage += (source, message) => { if (source == "Step 2") logs.Add(message); };
            bool silent = true, insufficient = true, passed = !fail;
            var outcome = runner.FinalizeFeqOutcomeFromRuns(curve, limits, true, false, 2, 2, 3, false, true, "prior sweep failed; replacement accepted", ref silent, ref insufficient, max, cmp, ref passed);
            check(outcome == (fail ? TestRunner.FeqFinalOutcome.Fail : TestRunner.FeqFinalOutcome.Pass) && !insufficient && !silent,
                "P3F-4 R1: failed attempt followed by sufficient valid replacement captures still judges " + (fail ? "FAIL" : "PASS"));
            check(logs.Contains($"FEQ Median (valid runs: 2/2, attempts: 3): {(fail ? "FAIL" : "PASS")}. Bass: 0/1, Mid: {(fail ? 1 : 0)}/1, Treble: 0/1, Max Dev: {max:F2} dB"), "P3F-4: exact production Step 2 median log retains all band counters");
        }
        foreach (bool multiple in new[] { false, true })
        {
            var runner = new TestRunner(engine); bool silent = false, insufficient = false, passed = true;
            var outcome = runner.FinalizeFeqOutcomeFromRuns(null, null, multiple, true, 0, multiple ? 2 : 1, 1, false, true, "SNR thấp", ref silent, ref insufficient, 0, "", ref passed);
            check(outcome == TestRunner.FeqFinalOutcome.AcquisitionInvalid && insufficient && !silent && !passed,
                "P3F-4: insufficient reference captures propagate caller flag for noise retry; multi=" + multiple);
        }
    }

    private sealed class SequenceBench : IC360ElectronicBench
    {
        public string DeviceIdentity => "P3-memory-only";
        public Dictionary<ushort, ushort> State { get; } = new() {
            [SoncaBleMixerProfile.InputCapabilities] = 73, [SoncaBleMixerProfile.InputSource] = 4,
            [SoncaBleMixerProfile.MasterVolume] = 8160, [SoncaBleMixerProfile.MusicVolume] = 4064, [SoncaBleMixerProfile.MicVolume] = 4097,
            [SoncaBleMixerProfile.MicMute] = 1, [SoncaBleMixerProfile.MusicMute] = 0, [SoncaBleMixerProfile.MasterMute] = 0,
            [SoncaBleMixerProfile.LiveMute] = 0, [SoncaBleMixerProfile.EchoEnable] = 0, [SoncaBleMixerProfile.ReverbEnable] = 0 };
        public List<(C360ElectronicStage Stage, C360ElectronicSetup Setup)> Captures { get; } = [];
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => Task.FromResult<IReadOnlyDictionary<ushort, ushort>>(new Dictionary<ushort, ushort>(State));
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) { State[register] = value; return Task.CompletedTask; }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        { Captures.Add((stage, setup)); return Task.FromResult(new C360ElectronicResult(stage, "PASS", "synthetic")); }
        public void StopAudio() { }
    }
}
