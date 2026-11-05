using SoncaAudioInspector;

internal static class C360RunPolicyChecks
{
    public static async Task Run()
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        var policy = new AudioBenchPreferences();
        Check(!new InOutConfig().BluetoothAutoRemove, "Model Bluetooth removal must be opt-in");
        Check(System.Text.Json.JsonSerializer.Deserialize<InOutConfig>("{\"BluetoothAutoRemove\":true}")!.BluetoothAutoRemove, "Model Bluetooth config variable missing");
        foreach (var scenario in new[] {
            (new[] { "PASS", "PASS", "FAIL" }, "PASS", 2),
            (new[] { "FAIL", "PASS", "PASS" }, "PASS", 3),
            (new[] { "PASS", "FAIL", "PASS" }, "PASS", 3),
            (new[] { "FAIL", "FAIL", "PASS" }, "FAIL", 2),
            (new[] { "NO_SIGNAL", "NO_SIGNAL", "PASS" }, "INVALID", 2),
            (new[] { "PASS", "INVALID", "FAIL" }, "INVALID", 3) })
        {
            var bench = new VoteBench(scenario.Item1);
            var repeat = new C360RepeatedBench(bench, policy, _ => bench);
            var result = await repeat.CaptureAsync(C360ElectronicStage.LineIn, new("a", "u", "c"), default);
            Check(result.Status == scenario.Item2 && result.Attempts.Count == scenario.Item3 && bench.Count == scenario.Item3, "Voting/early termination invalid: " + result.Summary);
        }
        var once=new VoteBench(["PASS","FAIL"]);
        var individual=await new C360RepeatedBench(once,policy,_=>once).CaptureAsync(C360ElectronicStage.LineIn,new("a","u","c"){TestConfiguration=new TestConfig{MaximumRuns=1,RequiredPasses=1}},default);
        Check(individual.Status=="PASS" && once.Count==1,"Per-test policy ignored in favor of global policy");
        var four=new VoteBench(["PASS","FAIL","PASS","PASS","PASS"]);
        individual=await new C360RepeatedBench(four,policy,_=>four).CaptureAsync(C360ElectronicStage.LineIn,new("a","u","c"){TestConfiguration=new TestConfig{MaximumRuns=5,RequiredPasses=4}},default);
        Check(individual.Status=="PASS" && four.Count==5,"Independent 5/4 test policy not applied");
        var oldVolume=C360ModelDefinition.Create(); oldVolume.testItems.InOut.AutomationProfileVersion=6;oldVolume.testItems.InOut.TestVolume=null;
        Check(C360ModelDefinition.EnsureSeed(new(){models=new(){oldVolume}}) && oldVolume.testItems.InOut.TestVolume is {BleMasterPercent:100,BleMusicPercent:50,BleMicPercent:50,WindowsPlaybackPercent:75,WindowsRecordingPercent:100},"Volume defaults not migrated into model");
        var cancelled = new CancellationToken(true); var empty = new VoteBench(["PASS"]);
        try { await new C360RepeatedBench(empty, policy, _ => empty).CaptureAsync(C360ElectronicStage.LineIn, new("a", "u", "c"), cancelled); throw new Exception("Cancelled vote captured"); }
        catch (OperationCanceledException) { Check(empty.Count == 0, "Cancellation emitted audio"); }
        Check(new AudioBenchPreferences { BleNameFilter = "c360, KBEAT" }.MatchesBleName("kbeatbox"), "Name filter case/alternatives");
        Check(!new AudioBenchPreferences { BleNameFilter = "c360" }.MatchesBleName("other speaker"), "Filter admitted wrong speaker");
        var migrated = C360ModelDefinition.Create();
        migrated.testItems.InOut.Tests.RemoveAll(t => t.SpeakerStage is "MicDry" or "Bluetooth");
        migrated.testItems.InOut.AutomationProfileVersion = 5;
        migrated.testItems.InOut.Tests.Single(t => t.SpeakerStage == "UsbA").id = "MY_USB";
        var checking = new CheckingConfig { models = new() { migrated } };
        Check(C360ModelDefinition.EnsureSeed(checking), "Migration not reported");
        Check(migrated.testItems.InOut.Tests.Count == 7 && migrated.testItems.InOut.Tests.Single(t => t.SpeakerStage == "UsbA").ReferenceTestId == "MY_USB", "Migration lost original reference identity");
        Check(!C360ModelDefinition.EnsureSeed(checking), "Migration not idempotent");
        var io = migrated.testItems.InOut;
        var endpoints = io.Devices.Input.Select(p => new C360ModelEndpoint(p.Key, p.Value, false))
            .Concat(io.Devices.Output.Select(p => new C360ModelEndpoint(p.Key, p.Value, true))).DistinctBy(e => (e.Name, e.Capture)).ToArray();
        var plan = C360ModelPlan.Create(io, endpoints, new("a", "u", "c"));
        string key=C360Reference.ConfigurationKey("C360",plan,C360ElectronicStage.LineIn);
        var identityTest=plan.Configuration.Tests.Single(t=>t.SpeakerStage=="LineIn");
        string oldIdentity=identityTest.id;identityTest.id="IO099";
        identityTest.ReferenceTestId=oldIdentity;
        Check(C360Reference.ConfigurationKey("C360",plan,C360ElectronicStage.LineIn)==key,"Display ID migration invalidated the same physical reference");
        identityTest.id=oldIdentity;identityTest.ReferenceTestId=null;
        var line=plan.Configuration.Tests.Single(t=>t.SpeakerStage=="LineIn");line.MaximumRuns=5;line.RequiredPasses=4;
        Check(C360Reference.ConfigurationKey("C360",plan,C360ElectronicStage.LineIn)==key,"Voting-only change invalidated line reference");
        line.AllowLineOutliers=true;line.AllowedLineOutlierPercent=10;
        Check(C360Reference.ConfigurationKey("C360",plan,C360ElectronicStage.LineIn)==key,"Judgement-only quota edit invalidated an identical acquisition reference");
        line.SweepLengthSamples=131072;
        Check(C360Reference.ConfigurationKey("C360",plan,C360ElectronicStage.LineIn)!=key,"Changed acquisition length reused the old line reference");
        line.SweepLengthSamples=65536;line.AllowLineOutliers=false;
        Check(plan.Configuration.Tests.Where(t=>t.SpeakerStage!="MicFrequencyShift").All(t=>t.ResolveSweepSeconds(44100)*44100==65536),"Default C360 sweep is not 64K");
        var legacySweep=C360ModelDefinition.Create();legacySweep.testItems.InOut.AutomationProfileVersion=8;
        foreach(var t in legacySweep.testItems.InOut.Tests){t.SweepLengthSamples=null;t.LogSweepDurationSeconds=262144d/44100;}
        legacySweep.testItems.InOut.Tests[1].LogSweepDurationSeconds=7.2;
        Check(C360ModelDefinition.EnsureSeed(new(){models=new(){legacySweep}})
            && legacySweep.testItems.InOut.Tests[0].SweepLengthSamples==65536
            && legacySweep.testItems.InOut.Tests[1].LogSweepDurationSeconds==7.2,"Factory sweep migration lost a custom duration or retained the old 256K default");
        Check(!plan.RequiresStopOnEndpointLoss(C360ElectronicStage.LineIn, "USB A"), "USB A disappearance cancels LINE IN");
        Check(plan.RequiresStopOnEndpointLoss(C360ElectronicStage.LineIn, "LINE IN") && plan.RequiresStopOnEndpointLoss(C360ElectronicStage.LineIn, "LINE OUT"), "Active playback/capture unplug not detected");
        var method = typeof(C360ModelDefinition).Assembly.GetType("SoncaAudioInspector.BluetoothDeviceRemoval")!.GetMethod("IsMeasuredAddress", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Match(ulong a) => (bool)method.Invoke(null, new object[] { a, 0x423E3F752ECCUL })!;
        Check(Match(0x423E3F752ECCUL) && Match(0x433E3F752ECCUL) && !Match(0x433E3F75A93AUL), "Unpair could target a different speaker");
        Console.WriteLine("PASS 3/2 voting, early success/failure, invalid capture distinction, cancellation, BLE name filter, v5 migration/custom IDs/idempotence and exact-address unpair scope. No Windows unpair executed.");
    }
    private sealed class VoteBench(string[] statuses) : IC360ElectronicBench
    {
        public int Count;
        public string DeviceIdentity => "mock";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => Task.FromResult<IReadOnlyDictionary<ushort, ushort>>(new Dictionary<ushort, ushort>());
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => Task.CompletedTask;
        public void StopAudio() { }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token) => Task.FromResult(new C360ElectronicResult(stage, statuses[Count++], "mock"));
    }
}
