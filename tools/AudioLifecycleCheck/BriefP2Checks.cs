using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoncaAudioInspector;

internal static class BriefP2Checks
{
    public static async Task Run(string output, string configPath, string baselineAssembly)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }
        void Reject(Action action, string why) { try { action(); } catch (ArgumentException) { checks.Add(why); return; } throw new Exception(why); }
        var test = new TestConfig { id = "IO001", PlaybackOut = "OUT", RecordingIn = "IN", DipWindowsEnabled = true,
            DipWindows = [new() { MinHz = 56, MaxHz = 75 }] };
        double[] frequencies = [50, 56, 60, 65, 70, 75, 80, 1000, 4000, 18000];
        var target = frequencies.ToDictionary(f => f, f => f is 60 or 65 or 70 ? -10d : 0);
        var measured = frequencies.ToDictionary(f => f, _ => 0d); measured[60] = -12;
        ResponseLimitPolicy Policy(IReadOnlyDictionary<double, FrequencyLimitPoint>? limits = null) => new(ResponseLimitSettings.Capture(test, 3), target, limits);
        var policy = Policy();
        var assessment = policy.Assess(measured, 0);
        Check(assessment.Passed && assessment.Total == 5 && assessment.Excluded == 5 && assessment.Outside == 0, "LowerOnly dip is excluded from both X and N");
        Check(policy.Evaluate(65, -12) is { LowerDb: null, UpperDb: 3, Outside: false, CountInQuota: false }, "Upper bridge spans a dip in the reference, lower bound absent");
        measured[65] = 4; assessment = policy.Assess(measured, 100);
        Check(!assessment.Passed && !assessment.IsInvalid && assessment.Total == 5 && assessment.Outside == 0, "Upper failure in dip cannot spend even 100 percent outlier quota");
        Check(assessment.Describe().Contains("vượt biên trên trong khoảng xét hõm #1 (65 Hz, +1 dB)")
            && assessment.Describe().Contains("0/5 điểm (không tính khoảng xét hõm; đã loại 5 điểm)"), "Separate upper failure and exact X/N/K reason");
        measured[65] = 0; measured[80] = 4;
        Check(!policy.Assess(measured, 10).Passed && policy.Assess(measured, 20).Passed, "Quota denominator is outside-window N=5, not original N=10");
        var window = test.DipWindows![0]; window.Mode = "Exclude"; policy = Policy(); measured[65] = 100;
        Check(policy.Evaluate(65, 100) is { Evaluated: false, IsValid: true, Outside: false, CountInQuota: false }, "Exclude suppresses both bounds, preserves valid data state");
        Check(policy.Assess(measured, 20).Passed && policy.Assess(measured, 20).Excluded == 5, "Exclude denominator equals LowerOnly denominator");
        Check(policy.Evaluate(65, double.NaN).IsValid == false && policy.Assess(measured, 20, frequencies.Append(69)).IsInvalid, "Excluded windows cannot hide NaN or missing samples");
        var missingTarget = new Dictionary<double, double> { [1000] = 0 };
        Check(!new ResponseLimitPolicy(ResponseLimitSettings.Capture(test, 3), missingTarget, null).Evaluate(65, 0).IsValid, "Excluded point with missing reference is not a judged PASS");
        Check(policy.Evaluate(20, 999) is { Evaluated: false, IsValid: true }, "Outside freq_range is distinct from missing data");
        foreach (string? mode in new string?[] { null, "Exclude" })
        {
            window.Mode = mode;
            var limits = target.ToDictionary(p => p.Key, p => new FrequencyLimitPoint(p.Key, p.Value, p.Value - 3, p.Value + 3, p.Key == 65));
            var critical = Policy(limits).Evaluate(65, -12);
            Check(critical.Critical && !critical.Outside && critical.LowerDb == -13 && critical.UpperDb == -7,
                "CSV CRITICAL restores original two bounds in " + (mode ?? "LowerOnly"));
            // Go below the actual critical lower limit, rather than the already-deep reference.
            critical = Policy(limits).Evaluate(65, -20);
            Check(critical.HardFailure && !critical.CountInQuota, "CSV CRITICAL hard failure still respects all-window X/N exclusion");
        }
        window.Mode = null;
        test.CriticalZones = [new() { MinHz = 60, MaxHz = 70 }];
        Check(Policy().Evaluate(65, -20).HardFailure, "Runtime model CRITICAL wins even for a direct unvalidated object");
        Reject(test.ValidateDipWindows, "Editor rejects overlap with model CRITICAL"); test.CriticalZones = null;
        test.DipWindowsEnabled = false;
        Check(Policy().Evaluate(60, -20).Outside && Policy().Assess(measured, 0).Excluded == 0, "Master off restores original lower limits and denominator");
        test.DipWindowsEnabled = true; window.Enabled = false;
        Check(Policy().Evaluate(60, -20).Outside, "Individual off restores original lower limit"); window.Enabled = true;
        measured = frequencies.ToDictionary(f => f, _ => 0d); measured[50] = measured[60] = measured[70] = measured[80] = -6;
        Check(!Policy().Assess(measured, 0).Passed, "Broad loss outside the dip still fails");
        var nonFlat = new Dictionary<double, double> { [50] = 0, [56] = 0, [65] = -10, [75] = 6, [80] = 6, [1000] = 6 };
        double center = Math.Sqrt(56 * 75);
        Check(Math.Abs(new ResponseLimitPolicy(ResponseLimitSettings.Capture(test, 3), nonFlat, null).Evaluate(center, 0).UpperDb!.Value - 6) < 1e-8, "Bridge interpolation is logarithmic in frequency");
        var onlyWindow = new Dictionary<double, double> { [60] = 0, [65] = 0, [70] = 0 };
        Check(Policy().Assess(onlyWindow, 0).Passed, "LowerOnly can judge upper bounds when quota N is zero");
        window.Mode = "Exclude";
        Check(Policy().Assess(onlyWindow, 100).IsInvalid, "All Exclude leaves no evaluated evidence, hence INVALID"); window.Mode = null;
        var csv = ResponseLimitPolicy.CsvRow(Policy().Evaluate(65, 4), -10, 4);
        Check(csv.Contains(",,3.00,False,FAIL,1,False,") && csv.Contains("\"vượt biên trên"), "CSV has nullable bounds, dip number, quota mask and quoted reason");
        Check(ResponseLimitPolicy.CsvRow(Policy().Evaluate(1000, 0), 0, 0, false) == "1000,0.00,0.00,-3.00,3.00,False,PASS", "Legacy seven-column CSV stays unchanged without active windows");
        File.WriteAllText(Path.Combine(output, "feq-policy-fixture.csv"), assessment.Describe() + Environment.NewLine + csv + Environment.NewLine);

        foreach (var invalid in new[] { new DipWindowConfig { MinHz = 0, MaxHz = 75 }, new() { MinHz = 75, MaxHz = 56 },
            new() { MinHz = 56, MaxHz = double.NaN }, new() { MinHz = 56, MaxHz = 75, Mode = "unknown" } })
            Reject(() => new TestConfig { DipWindows = [invalid] }.ValidateDipWindows(), "Invalid dip configuration rejected");
        Reject(() => new TestConfig { DipWindows = Enumerable.Range(0, 13).Select(_ => new DipWindowConfig { MinHz = 56, MaxHz = 75 }).ToList() }.ValidateDipWindows(), "13 windows rejected");
        Reject(() => new TestConfig { DipWindowsEnabled = true, DipWindows = [new() { MinHz = 56, MaxHz = 75 }, new() { MinHz = 75, MaxHz = 80 }] }.ValidateDipWindows(), "Inclusive touching windows rejected");
        Check(test.DipWindowWarnings().Contains("0.42") || test.DipWindowWarnings().Contains("0,42"), "Width over one third octave warns");
        var json = JsonSerializer.Serialize(new TestConfig());
        Check(!json.Contains("DipWindows"), "New nullable properties are absent from legacy JSON");
        var legacyBool = JsonSerializer.Deserialize<TestConfig>("{\"DipWindowsEnabled\":\"True\",\"DipWindows\":[{\"Enabled\":\"False\",\"MinHz\":56,\"MaxHz\":75}]}")!;
        Check(legacyBool.DipWindowsEnabled == true && !legacyBool.DipWindows![0].Enabled, "Legacy string booleans work for nullable master and row");
        var model = new ModelConfig { model = "P2 offline", testItems = new() { InOut = new() {
            Devices = new() { Input = new() { ["OUT"] = "fixture-out" }, Output = new() { ["IN"] = "fixture-in" } }, Tests = [test] } } };
        var saved = ModelConfigEditorWindow.ParseModel(model.model, JsonSerializer.Serialize(model, ModelConfigEditorWindow.JsonOptions));
        Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(saved, ModelConfigEditorWindow.JsonOptions),
            JsonSerializer.SerializeToNode(ModelConfigEditorWindow.ParseModel(saved.model, JsonSerializer.Serialize(saved, ModelConfigEditorWindow.JsonOptions)), ModelConfigEditorWindow.JsonOptions)), "Offline model save/readback round trip is stable");

        var first = frequencies.ToDictionary(f => f, _ => 0d); var second = new Dictionary<double, double>(first); second[65] = -12;
        policy = Policy();
        var pair = ReferenceCurveSelector.SelectClosestPair([first, second], evaluationMinHz: 50, evaluationMaxHz: 18000, evaluateFrequency: f => !policy.SkipRepeatabilityAt(f));
        Check(pair.MaximumDifferenceDb == 0 && pair.AverageCurve[65] == -6, "Reference selector masks only stability, retains full average curve");
        var repeat = TestRunner.AssessFeqRepeatabilityInRange([first, second], false, 3, [], 18000, 50, policy.SkipRepeatabilityAt);
        Check(!repeat.IsInvalid && repeat.CheckedPointCount == 5, "FEQ repeatability masks dip in numerator and denominator");
        second[65] = double.NaN;
        Reject(() => ReferenceCurveSelector.SelectClosestPair([first, second], evaluateFrequency: f => !policy.SkipRepeatabilityAt(f)), "Reference selector never hides nonfinite data under mask");
        Check(TestRunner.AssessFeqRepeatabilityInRange([first, second], false, 3, [], 18000, 50, policy.SkipRepeatabilityAt).IsInvalid, "Repeatability never hides nonfinite data under mask");
        using (var engine = new AudioEngine())
        {
            var runner = new TestRunner(engine) { StandardCurve = target, ResponseLimits = ResponseLimitSettings.Capture(test, 3) };
            var pointMethod = typeof(TestRunner).GetMethod("EvaluateFrequencyPoint", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object[] args = [65d, -12d, 0d, 0d, 0d, false];
            Check(!(bool)pointMethod.Invoke(runner, args)! && double.IsNaN((double)args[3]) && (double)args[4] == 3, "TestRunner point adapter uses common nullable lower/bridged upper");
        }
        var diagnosis = new MeasurementDiagnosisEvidence { ResponseValid = true, HasComparableReference = true,
            ResponseLimits = ResponseLimitSettings.Capture(test, 3), ResponseAssessment = Policy().Assess(frequencies.ToDictionary(f => f, _ => 0d), 0),
            Response = frequencies.Select(f => new DiagnosisResponsePoint(f, f == 65 ? -40 : 0, target[f], 3)).ToArray() };
        Check(!SpeakerMeasurementDiagnosis.DescribeMeasurement(diagnosis).Any(s => s.Contains("Loa yếu")), "Local diagnosis level median ignores dip mask");
        CheckP2Fixes(test, frequencies, target, Check);
        await CheckC360(output, Check);
        int identities = BriefP2IdentityChecks.Run(configPath, baselineAssembly, output);
        checks.Add($"PASS {identities} identity assertions for the three checked-in models");
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P2 behaviour checks plus {identities} identity assertions; offline/mock only."));
        Console.WriteLine($"PASS {checks.Count} P2 behaviour checks plus {identities} identity assertions; no audio/BLE/server.");
    }

    private static async Task CheckC360(string output, Action<bool, string> check)
    {
        var model = C360ModelDefinition.Create(); var io = model.testItems.InOut;
        foreach (var t in io.Tests) { t.Enable = true; t.AcousticSetupPending = false; }
        var endpoints = io.Devices.Input.Values.Distinct().Select((name, i) => new C360ModelEndpoint("out" + i, name, false))
            .Concat(io.Devices.Output.Values.Distinct().Select((name, i) => new C360ModelEndpoint("in" + i, name, true))).ToArray();
        var plan = C360ModelPlan.Create(io, endpoints, new("a", "u", "c"));
        var bench = new FixtureBench();
        string folder = Path.Combine(output, "reference-" + Guid.NewGuid().ToString("N"));
        var pipeline = new C360ReferenceBench(bench, plan, "C360", folder, "mock", false, 3);
        foreach (var stage in plan.Stages)
        {
            var result = await pipeline.CaptureAsync(stage, plan.Setup, default);
            bool expectation = stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift;
            check(result.Status == (expectation ? "PASS" : "INVALID"), "D13 missing reference: " + stage);
            if (!expectation) check(result.Summary.Contains("Tìm LINE CHUẨN") && result.Summary.Contains("không kết luận"), "D13 recovery instruction: " + stage);
        }
        var line = io.Tests.Single(t => t.SpeakerStage == "LineIn");
        line.DipWindows = [new() { MinHz = 75, MaxHz = 56 }];
        try { C360ModelDefinition.Validate(io); throw new Exception("C360 accepted invalid dip configuration"); }
        catch (ArgumentException) { check(true, "C360 validates loaded dip configuration before running stages"); }
        line.DipWindows = null;
        var reference = await new C360ReferenceBench(bench, plan, "C360", folder, "mock", true, 3).CaptureAsync(C360ElectronicStage.LineIn, plan.Setup, default);
        check(reference.Reference?.IsUsable == true, "D13 finding a reference remains possible without an existing reference");
        var compared = await pipeline.CaptureAsync(C360ElectronicStage.LineIn, plan.Setup, default);
        check(compared.Status == "PASS" && compared.Reference != null, "D13 matching reference restores response verdict");
        line.DipWindowsEnabled = true; line.DipWindows = [new() { MinHz = 56, MaxHz = 75 }];
        var curve = new Dictionary<double, double> { [50] = 0, [56] = 0, [65] = -10, [75] = 0, [80] = 0, [1000] = 0 };
        var golden = new C360Reference("synthetic", DateTimeOffset.UtcNow, curve, null, null, 2);
        var current = bench.Result(C360ElectronicStage.LineIn, new Dictionary<double, double>(curve) { [65] = -20 });
        check(golden.Compare(current, line, 3).Status == "PASS", "C360 comparison shares LowerOnly rule");
        current = bench.Result(C360ElectronicStage.LineIn, new Dictionary<double, double>(curve) { [65] = 4 }); line.AllowLineOutliers = true; line.AllowedLineOutlierPercent = 100;
        check(golden.Compare(current, line, 3).Status == "FAIL", "C360 comparison upper-in-dip failure is outside quota");
        var stable = C360Reference.Create("repeat", [bench.Result(C360ElectronicStage.LineIn, curve), bench.Result(C360ElectronicStage.LineIn, new(curve) { [65] = -20 })], line);
        check(stable.Curve[65] == -15, "C360 creation ignores dip variability, retains average at dip");
        try
        {
            var invalid = bench.Result(C360ElectronicStage.LineIn, new(curve) { [65] = double.NaN });
            C360Reference.Create("bad", [invalid, invalid], line);
            throw new Exception("C360 accepted nonfinite samples in both captures");
        }
        catch (InvalidOperationException) { check(true, "C360 creation rejects nonfinite dip even when missing in both filtered curves"); }
        string emptyFolder = Path.Combine(output, "reference-d13-" + Guid.NewGuid().ToString("N"));
        bench.NextStatus = "FAIL"; bench.NextSummary = "Lệch gain kênh";
        var failNoRef = await new C360ReferenceBench(bench, plan, "C360", emptyFolder, "mock", false, 3).CaptureAsync(C360ElectronicStage.LineIn, plan.Setup, default);
        check(failNoRef.Status == "FAIL" && failNoRef.Summary.Contains("Lệch gain kênh") && failNoRef.Summary.Contains("Chưa có LINE CHUẨN khớp cấu hình; đáp tuyến chưa được so."), "D13 preserves genuine FAIL when reference is missing");
        bench.NextStatus = "NO_SIGNAL"; bench.NextSummary = "Không có tín hiệu đường vào";
        var noSignalNoRef = await new C360ReferenceBench(bench, plan, "C360", emptyFolder, "mock", false, 3).CaptureAsync(C360ElectronicStage.LineIn, plan.Setup, default);
        check(noSignalNoRef.Status == "NO_SIGNAL" && !noSignalNoRef.Summary.Contains("Chưa có LINE CHUẨN — không kết luận"), "D13 preserves NO_SIGNAL when reference is missing");
        bench.NextStatus = "PASS"; bench.NextSummary = "synthetic acquisition";
        line.AllowLineOutliers = false; line.AllowedLineOutlierPercent = 0;
        line.DipWindowsEnabled = true; line.DipWindows = [new() { MinHz = 56, MaxHz = 75 }];
        var devCurrent = bench.Result(C360ElectronicStage.LineIn, new Dictionary<double, double>(curve) { [65] = -22, [1000] = 4.0 });
        var cmpActive = golden.Compare(devCurrent, line, 3);
        check(cmpActive.Status == "FAIL" && (cmpActive.Summary.Contains("4.00 dB (không tính khoảng xét hõm)") || cmpActive.Summary.Contains("4,00 dB (không tính khoảng xét hõm)")), "C360 Compare Max Dev excludes dip window points when active");
        line.DipWindowsEnabled = false;
        var cmpInactive = golden.Compare(devCurrent, line, 3);
        check(cmpInactive.Status == "FAIL" && (cmpInactive.Summary.Contains("12.00 dB") || cmpInactive.Summary.Contains("12,00 dB")) && !cmpInactive.Summary.Contains("(không tính khoảng xét hõm)"), "C360 Compare Max Dev unchanged when dip windows disabled");
    }

    private static void CheckP2Fixes(TestConfig baseTest, double[] frequencies, Dictionary<double, double> target, Action<bool, string> check)
    {
        double[] denseGrid = Enumerable.Range(0, 207).Select(i => Math.Round(50.0 * Math.Pow(18000.0 / 50.0, i / 206.0), 2)).ToArray();
        var denseMeasured = denseGrid.ToDictionary(f => f, _ => 0d);
        var noRefPolicy = new ResponseLimitPolicy(ResponseLimitSettings.Capture(baseTest, 3), null, null);
        var noRefAssess = noRefPolicy.Assess(denseMeasured, 0, denseGrid);
        string noRefDescribe = noRefAssess.Describe();
        string noRefReasonPart = string.Join("; ", noRefAssess.IssueSummaries());
        check(noRefAssess.InvalidKind == ResponseLimitInvalidKind.MissingReference && noRefReasonPart.Length <= 300
            && noRefReasonPart.Contains("thiếu LINE CHUẨN/biên: 207 điểm"), "F3 missing-reference Describe reason part is grouped and <= 300 chars");
        var multiDipMeasured = frequencies.ToDictionary(f => f, _ => 0d);
        multiDipMeasured[60] = 4.0; multiDipMeasured[65] = 5.0; multiDipMeasured[70] = 3.5;
        var multiDipAssess = new ResponseLimitPolicy(ResponseLimitSettings.Capture(baseTest, 3), target, null).Assess(multiDipMeasured, 100);
        check(multiDipAssess.Describe().Contains("vượt biên trên trong khoảng xét hõm #1 (65 Hz, +2 dB) (3 điểm)")
            && string.Join("; ", multiDipAssess.IssueSummaries()).Length <= 300, "F3 LowerOnly multi-point upper failure reports worst point per window plus point count");
        var diagEvidence = new MeasurementDiagnosisEvidence
        {
            ResponseValid = false, HasComparableReference = false,
            ResponseLimits = ResponseLimitSettings.Capture(baseTest, 3), ResponseAssessment = noRefAssess,
            Response = denseGrid.Select(f => new DiagnosisResponsePoint(f, 0, 0, 3)).ToArray()
        };
        check(SpeakerMeasurementDiagnosis.DescribeMeasurement(diagEvidence).All(line => line.Length <= 300), "F3 SpeakerMeasurementDiagnosis uses grouped <= 300 char summaries");

        using var engine = new AudioEngine();
        TestRunner NewRunner(TestConfig cfg, Dictionary<double, double>? std = null) => new(engine)
        {
            StandardCurve = std,
            FreqResponseToleranceDb = 3,
            AllowedFrequencyOutlierPercent = 0,
            ResponseLimits = ResponseLimitSettings.Capture(cfg, 3)
        };
        void CheckSingleDescribe(TestRunner r, string label)
        {
            string desc = r.LastResponseLimitAssessment?.Describe() ?? "";
            if (!string.IsNullOrEmpty(desc))
                check(r.Steps[1].Details.Split(desc).Length - 1 == 1, "F2 Details does not duplicate Describe(): " + label);
        }

        TestRunner.FeqFinalOutcome EvaluateMeasuredRuns(
            TestRunner r,
            IReadOnlyList<Dictionary<double, double>> runs,
            bool verifyFeqRepeatability,
            int? targetValidRunsOverride = null,
            bool referenceAcquisition = false,
            bool anySweepAcquisitionInvalid = false,
            string lastSweepInvalidReason = "",
            bool isSilent = false,
            bool inputClipping = false,
            bool repeatabilityInvalid = false,
            bool phaseAlignmentInvalid = false,
            string? preExistingInvalidReason = null)
        {
            r.InitializeSteps();
            r.InputClippingDetected = inputClipping;
            r.FeqRepeatabilityInvalid = repeatabilityInvalid || phaseAlignmentInvalid;
            r.ComplexPhaseAlignmentInvalid = phaseAlignmentInvalid;
            r.LastFeqInvalidReason = preExistingInvalidReason ?? string.Empty;
            int targetValidRuns = targetValidRunsOverride ?? (verifyFeqRepeatability ? Math.Max(2, runs.Count) : 1);
            double[]? evalGrid = runs.FirstOrDefault()?.Keys.OrderBy(f => f).ToArray();
            r.MissingFrequencyLimits = !referenceAcquisition && r.FindMissingFrequencyLimits(evalGrid).Length != 0;

            ResponseLimitAssessment? lastValidRunLimits = null;
            double maxFreqDev = 0.0;
            string compareMsg = "";
            int passedRuns = 0;

            foreach (var run in runs)
            {
                var assessment = r.EvaluateResponseCurveMetrics(
                    run,
                    isSilent,
                    referenceAcquisition,
                    updateStandardComparison: !isSilent && !anySweepAcquisitionInvalid,
                    emitTrebleLogs: false,
                    out double dev,
                    out string cmp,
                    out _);
                if (!isSilent && !anySweepAcquisitionInvalid)
                {
                    lastValidRunLimits = assessment;
                    maxFreqDev = dev;
                    compareMsg = cmp;
                }
                if (!isSilent && (referenceAcquisition || assessment.Passed))
                {
                    passedRuns++;
                }
            }

            Dictionary<double, double>? median = null;
            if (verifyFeqRepeatability)
            {
                median = new Dictionary<double, double>();
                if (evalGrid != null)
                {
                    foreach (double f in evalGrid)
                    {
                        var vals = runs.Where(cr => cr.ContainsKey(f)).Select(cr => cr[f]).ToList();
                        if (vals.Count > 0) median[f] = TestRunner.CalculateMedian(vals);
                    }
                }
            }

            bool freqResponsePass = !verifyFeqRepeatability ? (!isSilent && passedRuns >= 1 && runs.Count >= targetValidRuns) : false;
            bool insufficientValidFeqRuns = false;
            return r.FinalizeFeqOutcomeFromRuns(
                median,
                lastValidRunLimits,
                verifyFeqRepeatability,
                referenceAcquisition,
                validRunCount: (isSilent || anySweepAcquisitionInvalid) ? 0 : runs.Count,
                targetValidRuns: targetValidRuns,
                runAttemptsUsed: runs.Count,
                anyFeqSilentAttempt: isSilent,
                anySweepAcquisitionInvalid: anySweepAcquisitionInvalid,
                lastSweepInvalidReason: lastSweepInvalidReason,
                ref isSilent,
                ref insufficientValidFeqRuns,
                singleRunMaxDev: maxFreqDev,
                singleRunCompareMsg: compareMsg,
                ref freqResponsePass);
        }

        var passCurve = frequencies.ToDictionary(f => f, _ => 0d); passCurve[65] = -22; passCurve[1000] = 0.5;

        // R1: anySweepAcquisitionInvalid with sufficient valid runs does not mark outcome AcquisitionInvalid
        var rR1Pass = NewRunner(baseTest, target);
        bool passFlag = true;
        var outR1Pass = rR1Pass.ApplyFeqFinalOutcome(
            referenceAcquisition: false,
            insufficientValidFeqRuns: false,
            anySweepAcquisitionInvalid: true,
            lastSweepInvalidReason: "INVALID_PHASE_LOCK: test",
            isSilent: false,
            validRunCount: 2,
            targetValidRuns: 2,
            maxFreqDev: 0.5,
            compareMsg: "",
            ref passFlag);
        check(outR1Pass == TestRunner.FeqFinalOutcome.Pass && rR1Pass.Steps[1].Status == "Pass",
            "R1 case 1: sufficient valid runs with sweep invalid returns Pass");

        var rR1Invalid = NewRunner(baseTest, target);
        bool invalidFlag = true;
        var outR1Invalid = rR1Invalid.ApplyFeqFinalOutcome(
            referenceAcquisition: false,
            insufficientValidFeqRuns: true,
            anySweepAcquisitionInvalid: true,
            lastSweepInvalidReason: "INVALID_PHASE_LOCK: test",
            isSilent: false,
            validRunCount: 1,
            targetValidRuns: 2,
            maxFreqDev: 0.5,
            compareMsg: "",
            ref invalidFlag);
        check(outR1Invalid == TestRunner.FeqFinalOutcome.AcquisitionInvalid && rR1Invalid.Steps[1].Status == "Invalid"
            && rR1Invalid.Steps[1].Details.StartsWith("INVALID_PHASE_LOCK: test", StringComparison.Ordinal),
            "R1 case 2: insufficient valid runs with sweep invalid returns AcquisitionInvalid and shows sweep invalid reason");

        // 1. clipping + dictionary4 empty -> keep clipping message
        var rClip = NewRunner(baseTest, target);
        var outClip = EvaluateMeasuredRuns(rClip, [], true, targetValidRunsOverride: 2, anySweepAcquisitionInvalid: true, lastSweepInvalidReason: "CLIP_RAW", inputClipping: true);
        check(outClip == TestRunner.FeqFinalOutcome.AcquisitionInvalid && rClip.Steps[1].Status == "Invalid"
            && rClip.Steps[1].Details.StartsWith("Bản thu FEQ quá tải; giảm gain phần cứng/volume rồi đo lại.", StringComparison.Ordinal),
            "F2 case 1: clipping with empty dictionary4 keeps clipping message");
        CheckSingleDescribe(rClip, "clipping");

        // 2. existing noise reason in LastFeqInvalidReason is not overwritten
        var rNoise = NewRunner(baseTest, target);
        var nanCurve = new Dictionary<double, double>(passCurve) { [65] = double.NaN };
        var outNoise = EvaluateMeasuredRuns(rNoise, [nanCurve], true, targetValidRunsOverride: 1, preExistingInvalidReason: "Nền ngõ thu -40.0 dBFS vượt ngưỡng trước khi phát");
        check(outNoise == TestRunner.FeqFinalOutcome.AcquisitionInvalid
            && rNoise.LastFeqInvalidReason == "Nền ngõ thu -40.0 dBFS vượt ngưỡng trước khi phát"
            && rNoise.Steps[1].Details.StartsWith("Nền ngõ thu -40.0 dBFS vượt ngưỡng trước khi phát", StringComparison.Ordinal),
            "F2 case 2: pre-existing noise reason in LastFeqInvalidReason is not overwritten");

        // 3. insufficient valid runs
        var rInsuff = NewRunner(baseTest, target);
        var outInsuff = EvaluateMeasuredRuns(rInsuff, [passCurve], true, targetValidRunsOverride: 2);
        check(outInsuff == TestRunner.FeqFinalOutcome.AcquisitionInvalid && rInsuff.Steps[1].Status == "Invalid"
            && rInsuff.Steps[1].Details.StartsWith("Không đủ lượt thu FEQ hợp lệ; kiểm tra SNR từng dải rồi đo lại.", StringComparison.Ordinal),
            "F2 case 3: insufficient valid runs keeps acquisition invalid message");

        // 4. repeatability invalid (and phase alignment invalid)
        var rRepeat = NewRunner(baseTest, target);
        var outRepeat = EvaluateMeasuredRuns(rRepeat, [passCurve, passCurve], true, repeatabilityInvalid: true);
        var rPhase = NewRunner(baseTest, target);
        var outPhase = EvaluateMeasuredRuns(rPhase, [passCurve, passCurve], true, repeatabilityInvalid: true, phaseAlignmentInvalid: true);
        check(outRepeat == TestRunner.FeqFinalOutcome.AcquisitionInvalid
            && rRepeat.Steps[1].Details.StartsWith("Đáp tuyến không ổn định giữa các lượt", StringComparison.Ordinal)
            && outPhase == TestRunner.FeqFinalOutcome.AcquisitionInvalid
            && rPhase.Steps[1].Details.StartsWith("Biên độ ổn định nhưng pha log-sweep chưa đồng bộ", StringComparison.Ordinal),
            "F2 case 4: repeatability and phase alignment invalid keep exact messages");

        // 5. missing reference on multi-run and 1-run paths -> MissingFrequencyLimits, LastFeqInvalidReason empty
        var rMissMulti = NewRunner(baseTest, null);
        var outMissMulti = EvaluateMeasuredRuns(rMissMulti, [passCurve, passCurve], true);
        var rMissSingle = NewRunner(baseTest, null);
        var outMissSingle = EvaluateMeasuredRuns(rMissSingle, [passCurve], false);
        check(outMissMulti == TestRunner.FeqFinalOutcome.MissingReference && rMissMulti.MissingFrequencyLimits && string.IsNullOrEmpty(rMissMulti.LastFeqInvalidReason) && rMissMulti.Steps[1].Status == "Invalid"
            && outMissSingle == TestRunner.FeqFinalOutcome.MissingReference && rMissSingle.MissingFrequencyLimits && string.IsNullOrEmpty(rMissSingle.LastFeqInvalidReason) && rMissSingle.Steps[1].Status == "Invalid",
            "F1/F2/F4 case 5: missing reference on multi-run and 1-run sets MissingFrequencyLimits and keeps LastFeqInvalidReason empty");
        CheckSingleDescribe(rMissMulti, "missing-ref-multi");
        CheckSingleDescribe(rMissSingle, "missing-ref-single");

        // 6. NaN -> acquisition/response data invalid (multi-run and 1-run)
        var rNanMulti = NewRunner(baseTest, target);
        var outNanMulti = EvaluateMeasuredRuns(rNanMulti, [nanCurve, nanCurve], true);
        var rNanSingle = NewRunner(baseTest, target);
        var outNanSingle = EvaluateMeasuredRuns(rNanSingle, [nanCurve], false);
        check(outNanMulti == TestRunner.FeqFinalOutcome.ResponseDataInvalid && !string.IsNullOrWhiteSpace(rNanMulti.LastFeqInvalidReason) && rNanMulti.Steps[1].Status == "Invalid"
            && outNanSingle == TestRunner.FeqFinalOutcome.ResponseDataInvalid && !string.IsNullOrWhiteSpace(rNanSingle.LastFeqInvalidReason) && rNanSingle.Steps[1].Status == "Invalid",
            "F1/F2/F4 case 6: NaN in response is treated as acquisition/data invalid on multi-run and 1-run");
        CheckSingleDescribe(rNanMulti, "nan-multi");

        // 7. all Exclude -> configuration INVALID (no LastFeqInvalidReason)
        var allExcludeCfg = new TestConfig { id = "IO001", DipWindowsEnabled = true, DipWindows = [new() { MinHz = 20, MaxHz = 20000, Mode = "Exclude" }] };
        var rExclMulti = NewRunner(allExcludeCfg, target);
        var outExclMulti = EvaluateMeasuredRuns(rExclMulti, [passCurve, passCurve], true);
        var rExclSingle = NewRunner(allExcludeCfg, target);
        var outExclSingle = EvaluateMeasuredRuns(rExclSingle, [passCurve], false);
        check(outExclMulti == TestRunner.FeqFinalOutcome.NothingToEvaluate && string.IsNullOrEmpty(rExclMulti.LastFeqInvalidReason) && !rExclMulti.MissingFrequencyLimits && rExclMulti.Steps[1].Status == "Invalid"
            && outExclSingle == TestRunner.FeqFinalOutcome.NothingToEvaluate && string.IsNullOrEmpty(rExclSingle.LastFeqInvalidReason) && rExclSingle.Steps[1].Status == "Invalid",
            "F1/F2/F4 case 7: all-Exclude is configuration INVALID without setting LastFeqInvalidReason");
        CheckSingleDescribe(rExclMulti, "exclude-all");

        // 8. PASS / FAIL / hard failure in dip window (plus F6 Max Dev active vs inactive)
        var rPassMulti = NewRunner(baseTest, target);
        var outPassMulti = EvaluateMeasuredRuns(rPassMulti, [passCurve, passCurve], true);
        var rPassSingle = NewRunner(baseTest, target);
        var outPassSingle = EvaluateMeasuredRuns(rPassSingle, [passCurve], false);
        var failOutCurve = new Dictionary<double, double>(passCurve) { [80] = -6 };
        var rFailOut = NewRunner(baseTest, target);
        var outFailOut = EvaluateMeasuredRuns(rFailOut, [failOutCurve], false);
        var hardDipCurve = new Dictionary<double, double>(passCurve) { [65] = 4 };
        var rHardDip = NewRunner(baseTest, target); rHardDip.AllowedFrequencyOutlierPercent = 100;
        var outHardDip = EvaluateMeasuredRuns(rHardDip, [hardDipCurve], false);
        check(outPassMulti == TestRunner.FeqFinalOutcome.Pass && rPassMulti.Steps[1].Status == "Pass"
            && outPassSingle == TestRunner.FeqFinalOutcome.Pass && rPassSingle.Steps[1].Status == "Pass" && rPassSingle.LastFrequencyPointCount == 5
            && outFailOut == TestRunner.FeqFinalOutcome.Fail && rFailOut.Steps[1].Status == "Fail"
            && outHardDip == TestRunner.FeqFinalOutcome.Fail && rHardDip.Steps[1].Status == "Fail"
            && rHardDip.Steps[1].Details.Contains("vượt biên trên trong khoảng xét hõm #1 (65 Hz, +1 dB)"),
            "F2/F4 case 8: PASS, outside-window FAIL, and dip upper hard failure work on multi-run and 1-run");
        CheckSingleDescribe(rPassMulti, "pass-multi");
        CheckSingleDescribe(rHardDip, "hard-dip-fail");

        var disabledCfg = new TestConfig { id = "IO001", DipWindowsEnabled = false, DipWindows = [new() { MinHz = 56, MaxHz = 75 }] };
        var rDisabled = NewRunner(disabledCfg, target);
        EvaluateMeasuredRuns(rDisabled, [passCurve, passCurve], true);
        check(Math.Abs(rPassMulti.LastFrequencyMaxDeviationDb - 0.5) < 1e-9
            && rPassMulti.Steps[1].Details.Contains("(không tính khoảng xét hõm)")
            && Math.Abs(rDisabled.LastFrequencyMaxDeviationDb - 12.0) < 1e-9
            && !rDisabled.Steps[1].Details.Contains("(không tính khoảng xét hõm)"),
            "F6 Max Dev excludes dip window points when active and stays unchanged when inactive");

        var hoverNoRef = AudioRouting.FormatFrequencyHoverLimitLine(noRefPolicy.Evaluate(1000, 0), 0, "dBr");
        var hoverWithRef = AudioRouting.FormatFrequencyHoverLimitLine(new ResponseLimitPolicy(ResponseLimitSettings.Capture(baseTest, 3), target, null).Evaluate(65, -8), -8, "dBr");
        check(hoverNoRef == "\nChưa có line chuẩn/giới hạn tại điểm này"
            && hoverWithRef.Contains("Δ") && hoverWithRef.Contains("[không xét,") && hoverWithRef.Contains("khoảng xét hõm #1"),
            "F8 hover keeps legacy string without reference and shows delta/bounds/reason with reference");

        var badAutoCase = new AutoTestCaseItem { Id = "IO999", Name = "Bad dip", Config = new TestConfig { id = "IO999", DipWindowsEnabled = true, DipWindows = [new() { MinHz = 75, MaxHz = 56 }] } };
        check(!AudioRouting.ValidateAutoTestCaseConfiguration(badAutoCase) && badAutoCase.Status == "INVALID" && badAutoCase.AdditionalResults.Any(r => r.Status == "INVALID"),
            "F9 Auto Test precheck catches invalid dip window config as INVALID");

        var emptyQaCase = new AutoTestCaseItem { Id = "IO001", ResponseLimits = ResponseLimitSettings.Capture(new TestConfig { id = "IO001" }, 3) };
        string qaEmpty = AudioRouting.AppendResponseLimitQaDetails("base", emptyQaCase);
        var activeQaCase = new AutoTestCaseItem { Id = "IO001", ResponseLimits = ResponseLimitSettings.Capture(baseTest, 3), ResponseAssessment = rPassMulti.LastResponseLimitAssessment };
        string qaActive = AudioRouting.AppendResponseLimitQaDetails("base", activeQaCase);
        check(qaEmpty == "base" && qaActive.Contains("Ngoài biên:") && qaActive.Contains("Khoảng xét hõm:"),
            "F10 QA details avoids blank lines when assessment is null and omits dip window line when no windows configured");
    }

    private sealed class FixtureBench : IC360ElectronicBench
    {
        public string DeviceIdentity => "P2-mock";
        public string NextStatus { get; set; } = "PASS";
        public string NextSummary { get; set; } = "synthetic acquisition";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => throw new NotSupportedException("No BLE");
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => throw new NotSupportedException("No BLE");
        public void StopAudio() { }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
            => Task.FromResult(Result(stage, new() { [50] = 0, [65] = 0, [80] = 0, [1000] = 0 }, NextStatus, NextSummary));
        public C360ElectronicResult Result(C360ElectronicStage stage, Dictionary<double, double> curve, string status = "PASS", string summary = "synthetic acquisition")
        {
            var measurement = JsonSerializer.Deserialize<StandardAcousticResult>(JsonSerializer.Serialize(new {
                FrequencyResponse = curve.Select(p => new { FrequencyHz = p.Key, LevelDb = p.Value }), ExcitationRmsDbFs = 0, Validity = "DIAGNOSTIC"
            }, C360Reference.Json), C360Reference.Json)!;
            return new(stage, status, summary, measurement);
        }
    }
}
