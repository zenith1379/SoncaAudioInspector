using SoncaAudioInspector;
using System.IO;

internal static class C360ElectronicChecks
{
    private static readonly C360ElectronicSetup Setup = new("analog", "usb", "line-out");
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static async Task Run()
    {
        var suite = new C360ElectronicSuite();
        var bench = new Bench();
        var results = await suite.RunAsync(bench, Setup);
        Check(results.Count == 4 && bench.Stages.SequenceEqual(new[] { C360ElectronicStage.MicEffects, C360ElectronicStage.MicFrequencyShift, C360ElectronicStage.LineIn, C360ElectronicStage.UsbA }), "Stage order");
        Check(bench.Restored && bench.Stops >= 4, "Completion restoration");
        Check(bench.Writes.All(p => p.Key != SoncaBleMixerProfile.MasterVolume && p.Key != SoncaBleMixerProfile.MusicVolume), "Unrelated gain changed");

        var volumeTest = new TestConfig { ModelVolume=new ModelTestVolume{BleMasterPercent=100,BleMusicPercent=50,BleMicPercent=50} };
        bench=new Bench();
        await suite.RunAsync(bench,Setup with {TestConfiguration=volumeTest});
        Check(bench.Restored && bench.Stages.Count==4,"Matching model volume blocked normal isolated stages");
        volumeTest.ModelVolume.BleMusicPercent=60; bench=new Bench();
        await ExpectFailure(()=>suite.RunAsync(bench,Setup with {TestConfiguration=volumeTest}));
        Check(bench.Writes.Count==0 && bench.Stages.Count==0,"Wrong model volume wrote state or emitted audio");

        bench = new Bench { NoSignal = true };
        results = await suite.RunAsync(bench, Setup);
        Check(results.Count == 4 && results.All(r => r.Status == "NO_SIGNAL") && bench.Restored, "Missing capture on one input hid other inputs or failed restoration");
        bench = new Bench { AllowModel = true, BluetoothUnavailable = true };
        results = await suite.RunAsync(bench, Setup, stages: [C360ElectronicStage.LineIn, C360ElectronicStage.Bluetooth, C360ElectronicStage.UsbA]);
        Check(results.Select(r => r.Status).SequenceEqual(new[] { "PASS", "NO_SIGNAL", "PASS" }) && bench.Restored,
            "Unavailable Bluetooth hid completed/remaining stages or failed restoration");
        bench = new Bench { CaptureFailure = true };
        await ExpectFailure(() => suite.RunAsync(bench, Setup));
        Check(bench.Restored && bench.Stages.Count == 1, "Unexpected capture/control failure was converted into missing Bluetooth audio");
        bench = new Bench { FailWrite = true };
        await ExpectFailure(() => suite.RunAsync(bench, Setup));
        Check(bench.Restored && bench.Stages.Count == 0, "Applied write with lost ACK not restored");
        bench = new Bench { BadReadback = true };
        await ExpectFailure(() => suite.RunAsync(bench, Setup));
        Check(bench.Restored && bench.Stages.Count == 0, "Unconfirmed stage played audio");

        using var cancel = new CancellationTokenSource();
        bench = new Bench { HoldCapture = true };
        var pending = suite.RunAsync(bench, Setup, token: cancel.Token);
        await bench.Entered.Task;
        await ExpectFailure(() => suite.RunAsync(new Bench(), Setup));
        cancel.Cancel();
        await ExpectFailure(() => pending);
        Check(bench.Restored, "Cancellation did not restore");
        Check((await suite.RunAsync(new Bench(), Setup)).Count == 4, "Ownership stuck after cancellation");
        bench = new Bench();
        await suite.RunAsync(bench, Setup, stages: new[] { C360ElectronicStage.LineIn, C360ElectronicStage.MicEffects });
        Check(bench.Stages.SequenceEqual(new[] { C360ElectronicStage.LineIn, C360ElectronicStage.MicEffects }) && bench.Restored,
            "Disabled/reordered stages or restored MIC gain failed");
        await CheckModel(suite);
        await C360RunPolicyChecks.Run();
        await CheckConnectionWaiters();
        var separate = Setup with { LinePlaybackId = "line-card", LineLevelDbFs = -30, UsbLevelDbFs = -18 };
        separate.Validate();
        Check(separate.PlaybackId(C360ElectronicStage.LineIn) == "line-card" && separate.PlaybackId(C360ElectronicStage.MicEffects) == "analog"
            && separate.PlaybackLevel(C360ElectronicStage.LineIn) == -30 && separate.PlaybackLevel(C360ElectronicStage.MicEffects) == -24
            && separate.PlaybackLevel(C360ElectronicStage.UsbA) == -18, "Per-input output/level configuration mixed");
        Console.WriteLine("PASS C360 ordered MIC effects / LINE IN / USB A; fixed capture; MIC/effects off for music; per-input missing-signal results; readback gate; lost ACK restoration; cancellation; concurrent ownership and rerun.");
    }
    private static async Task CheckConnectionWaiters()
    {
        using var manager = new SoncaBleManager();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SoncaBleManager).GetField("_preparation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(manager, pending.Task);
        using var cancelled = new CancellationTokenSource();
        Task<bool> first = manager.EnsureConnectedAsync(token: cancelled.Token), second = manager.EnsureConnectedAsync();
        cancelled.Cancel(); await ExpectFailure(async () => await first);
        Check(!second.IsCompleted, "One cancelled waiter cancelled shared BLE preparation");
        pending.SetResult(true); Check(await second, "Second BLE waiter lost shared result");
        manager.SuppressAutomaticConnection();
        Check(!await manager.EnsureConnectedAsync(), "Manual disconnect permitted automatic reconnect");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(SoncaBleManager).GetField("_preparation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(manager, pending.Task);
        var explicitRun = manager.EnsureConnectedAsync(userInitiated: true);
        Check(!manager.AutomaticConnectionSuppressed && !explicitRun.IsCompleted, "Explicit Auto Test did not resume preparation");
        pending.SetResult(false); Check(!await explicitRun, "Connection failure converted to success");
        Console.WriteLine("PASS shared BLE preparation waiters; individual cancellation; explicit disconnect prevents background reconnect; explicit Auto Test resumes preparation; failure retained. No radio opened.");
    }
    private static async Task CheckModel(C360ElectronicSuite suite)
    {
        var all = new CheckingConfig();
        Check(C360ModelDefinition.EnsureSeed(all) && all.models.Count == 1, "Model not seeded");
        Check(all.models[0].testItems.InOut.Tests.Select(t => t.id).SequenceEqual(Enumerable.Range(1,7).Select(i=>$"IO{i:000}")), "IDs must ascend in configured order including the disabled draft");
        var old = C360ModelDefinition.Create();
        old.testItems.InOut.Tests.RemoveAll(t => t.SpeakerStage is "MicDry" or "Bluetooth"); old.testItems.InOut.AutomationProfileVersion = 2;
        old.testItems.InOut.Tests[1].id = "IO004"; old.testItems.InOut.Tests[2].id = "IO002"; old.testItems.InOut.Tests[3].id = "IO003";
        old.testItems.InOut.Tests[2].ThdLimit = .27;
        Check(C360ModelDefinition.EnsureSeed(new CheckingConfig { models = new() { old } })
            && old.testItems.InOut.Tests.Where(t => t.IsEnabled).Select(t => t.id).SequenceEqual(new[] { "IO001", "IO002", "IO003", "IO004", "IO005", "IO007" }) && old.testItems.InOut.Tests.Single(t => t.SpeakerStage == "LineIn").ThdLimit == .27,
            "ID migration lost configuration");
        var levels = new TestConfig { WindowsPlaybackPercent = 100, WindowsRecordingPercent = 50 }; levels.ValidateWindowsLevels();
        foreach (string text in new[] { "{\"Playback Level\":-12,\"Playback Level dBFS\":-6}", "{\"Playback Level dBFS\":-6,\"Playback Level\":-12}" })
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<TestConfig>(text)!;
            Check(parsed.PlaybackLevelDbfs == -6 && !System.Text.Json.JsonSerializer.Serialize(parsed).Contains("\"Playback Level\":"), "Legacy level alias overrides current config or is duplicated on write");
        }
        Check(System.Text.Json.JsonSerializer.Deserialize<TestConfig>("{\"Playback Level\":-12}")!.PlaybackLevelDbfs == -12, "Legacy-only config lost compatibility");
        levels.WindowsRecordingPercent = 101; await ExpectFailure(() => Task.Run(levels.ValidateWindowsLevels));
        all.models[0].model = "Tên tự đặt";
        Check(!C360ModelDefinition.EnsureSeed(all) && all.models.Count == 1, "Renamed model duplicated on reload");
        var config = all.models[0].testItems.InOut;
        Check(config.Devices.Input["USB A"] == "CS3600 MUSIC"
            && config.Tests.Where(t => t.SpeakerStage is "LineIn" or "UsbA").All(t => t.BassThdLimit == 1 && t.ThdLimit == .5),
            "C360 USB A render name or independent 80 Hz limit is incorrect");
        var endpoints = new[] { new C360ModelEndpoint("analog", config.Devices.Input["MIC IN"], false),
            new C360ModelEndpoint("analog", config.Devices.Input["LINE IN"], false), new C360ModelEndpoint("line-out", config.Devices.Output["LINE OUT"], true) };
        var line = config.Tests.Single(t => t.SpeakerStage == "LineIn");
        config.Tests[0].PlaybackLevelDbfs = -18; line.PlaybackLevelDbfs = -27; line.ThdLimit = .23;
        var plan = C360ModelPlan.Create(config, endpoints, Setup);
        Check(plan.Setup.UsbPlaybackId == "" && plan.Stages.Length == 6, "Missing USB A blocked other stages or reused the capture endpoint");
        var usbPlan = C360ModelPlan.Create(config, endpoints.Append(new C360ModelEndpoint("usb-music", "Speakers (CS3600 MUSIC)", false)).ToArray(), Setup);
        Check(usbPlan.Setup.UsbPlaybackId == "usb-music" && usbPlan.Setup.LineOutCaptureId == "line-out", "USB A render/capture roles were confused");
        int polls = 0;
        var unrelated = new[] { new C360ModelEndpoint("wrong", "Speakers (C360 usb audio)", false), new C360ModelEndpoint("capture", "CS3600 MUSIC", true) };
        string appeared = await C360PlaybackEndpoints.WaitForAsync("CS3600 MUSIC", () => ++polls < 3 ? unrelated
            : unrelated.Append(new C360ModelEndpoint("arrived", "Speakers (CS3600 MUSIC)", false)).ToArray(), default, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        Check(appeared == "arrived" && polls == 3, "USB endpoint enumerated before switching was not refreshed");
        var resolved = plan.SetupFor(config.Tests.Single(t => t.SpeakerStage == "UsbA")) with { UsbPlaybackId = appeared };
        Check(C360Reference.ConfigurationKey("C360", plan, C360ElectronicStage.UsbA) != C360Reference.ConfigurationKey("C360", plan, C360ElectronicStage.UsbA, resolvedSetup: resolved),
            "Reference identity ignored the endpoint resolved after source switching");
        Check(await C360PlaybackEndpoints.WaitForAsync("CS3600 MUSIC", () => unrelated, default, TimeSpan.Zero) == "", "USB recording/wrong render used as fallback");
        await ExpectFailure(() => C360PlaybackEndpoints.WaitForAsync("CS3600 MUSIC", () => new[] { new C360ModelEndpoint("a", "CS3600 MUSIC", false), new C360ModelEndpoint("b", "CS3600 MUSIC", false) }, default));
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await ExpectFailure(() => C360PlaybackEndpoints.WaitForAsync("CS3600 MUSIC", () => unrelated, stopped.Token));
        var migration = C360ModelDefinition.Create(); migration.testItems.InOut.AutomationProfileVersion = 3;
        migration.testItems.InOut.Devices.Input["USB A"] = "Speakers (C360 usb audio)"; migration.testItems.InOut.Tests[0].id = "CUSTOM";
        C360ModelDefinition.EnsureSeed(new CheckingConfig { models = new() { migration } });
        Check(migration.testItems.InOut.Devices.Input["USB A"] == "CS3600 MUSIC" && migration.testItems.InOut.Tests[0].id == "IO001"
            && migration.testItems.InOut.Tests[0].ReferenceTestId == "CUSTOM", "ID migration lost the original reference identity");
        line.PlaybackLevelDbfs = -6;
        var bench = new Bench { AllowModel = true };
        await suite.RunAsync(plan.Bind(bench), plan.Setup, stages: plan.Stages);
        Check(bench.CapturedSetups[0].LevelDbFs == -18 && bench.CapturedSetups[3].LevelDbFs == -27 && bench.CapturedSetups[3].ThdLimitPercent == .23,
            "Per-test levels/limit not frozen/applied");
        config.Tests.Single(t => t.SpeakerStage == "UsbA").Enable = false;
        Check(C360ModelPlan.Create(config, endpoints, Setup).Stages.Length == 5, "Disabled test still selected");
        var shiftTest = config.Tests.Single(t => t.SpeakerStage == "MicFrequencyShift");
        config.Devices.Input["FBX OUT"] = "Separate FBX output"; shiftTest.PlaybackOut = "FBX OUT";
        var separatePlan = C360ModelPlan.Create(config, endpoints.Append(new C360ModelEndpoint("fbx-card", "Separate FBX output", false)).ToArray(), Setup);
        Check(separatePlan.SetupFor(shiftTest).PlaybackId(C360ElectronicStage.MicFrequencyShift) == "fbx-card", "FBX-specific route replaced by MIC route");
        shiftTest.PlaybackOut = "MIC IN";
        line.RecordingChannel = 2;
        await ExpectFailure(() => Task.Run(() => C360ModelPlan.Create(config, endpoints, Setup)));
        line.RecordingChannel = null;
        await ExpectFailure(() => Task.Run(() => C360ModelPlan.Create(config, endpoints.Concat(new[] { endpoints[0] with { Id = "ambiguous" } }).ToArray(), Setup)));
        Console.WriteLine("PASS editable model seed/rename without duplicates; frozen test configuration; per-stage level/limit; disabled/reordered tests; fixed capture; missing USB A; ambiguous devices rejected.");
    }
    private static async Task ExpectFailure(Func<Task> action)
    {
        bool failed = false;
        try { await action(); } catch { failed = true; }
        Check(failed, "Expected failure missing");
    }
    private sealed class Bench : IC360ElectronicBench
    {
        public string DeviceIdentity => "C360-test";
        public readonly List<KeyValuePair<ushort, ushort>> Writes = new();
        public readonly List<C360ElectronicStage> Stages = new();
        private readonly Dictionary<ushort, ushort> initial = new()
        {
            [SoncaBleMixerProfile.InputCapabilities] = 73,
            [SoncaBleMixerProfile.InputSource] = 4,
            [SoncaBleMixerProfile.MasterVolume] = 8160,
            [SoncaBleMixerProfile.MusicVolume] = 4064,
            [SoncaBleMixerProfile.MicVolume] = 4097,
            [SoncaBleMixerProfile.MicMute] = 1,
            [SoncaBleMixerProfile.MusicMute] = 0,
            [SoncaBleMixerProfile.MasterMute] = 0,
            [SoncaBleMixerProfile.LiveMute] = 0,
            [SoncaBleMixerProfile.EchoEnable] = 0,
            [SoncaBleMixerProfile.ReverbEnable] = 0
        };
        private readonly Dictionary<ushort, ushort> state;
        public Bench() { state = new(initial); }
        public bool NoSignal, FailWrite, BadReadback, HoldCapture, AllowModel;
        public readonly List<C360ElectronicSetup> CapturedSetups = new();
        public int Stops;
        public bool BluetoothUnavailable, CaptureFailure;
        public bool Restored => initial.All(p => state[p.Key] == p.Value);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var copy = new Dictionary<ushort, ushort>(state);
            if (BadReadback && Writes.Count > 0) { copy[SoncaBleMixerProfile.EchoEnable] = 0; BadReadback = false; }
            return Task.FromResult<IReadOnlyDictionary<ushort, ushort>>(copy);
        }
        public Task WriteAsync(ushort register, ushort value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); state[register] = value; Writes.Add(new(register, value));
            if (FailWrite) { FailWrite = false; throw new IOException("Applied but ACK lost"); }
            return Task.CompletedTask;
        }
        public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        {
            Stages.Add(stage);
            CapturedSetups.Add(setup);
            Check(setup.LineOutCaptureId == "line-out" && (AllowModel || setup.PlaybackId(stage) == (stage == C360ElectronicStage.UsbA ? "usb" : "analog")), "Endpoint changed");
            Check(state[SoncaBleMixerProfile.MasterMute] == 0 && state[SoncaBleMixerProfile.LiveMute] == 0, "Capture muted");
            Check((state[SoncaBleMixerProfile.InputCapabilities] & 1) == 0, "FBX not disabled before the next test");
            if (stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift or C360ElectronicStage.MicDry)
            {
                Check(state[SoncaBleMixerProfile.MicVolume] == 4097, "MIC level was not restored before reordered MIC stage");
                int effects = stage == C360ElectronicStage.MicEffects ? 1 : 0;
                Check(state[SoncaBleMixerProfile.MicMute] == 0 && state[SoncaBleMixerProfile.MusicMute] == 1 && state[SoncaBleMixerProfile.EchoEnable] == effects && state[SoncaBleMixerProfile.ReverbEnable] == effects, "MIC stage isolation wrong");
            }
            else
                Check(state[SoncaBleMixerProfile.MicMute] == 1 && state[SoncaBleMixerProfile.MicVolume] == 0 && state[SoncaBleMixerProfile.MusicMute] == 0 && state[SoncaBleMixerProfile.EchoEnable] == 0 && state[SoncaBleMixerProfile.ReverbEnable] == 0 && state[SoncaBleMixerProfile.InputSource] == (stage == C360ElectronicStage.Bluetooth ? 0 : stage is C360ElectronicStage.LineIn or C360ElectronicStage.AcousticMic ? 4 : 1), "Music isolation wrong");
            Entered.TrySetResult();
            if (HoldCapture) await Task.Delay(Timeout.Infinite, token);
            if (CaptureFailure) throw new InvalidOperationException("fixture: control/acquisition failure");
            if (BluetoothUnavailable && stage == C360ElectronicStage.Bluetooth)
                throw new BluetoothPlaybackUnavailableException("fixture: Windows pairing failed; no audio endpoint");
            return new(stage, NoSignal ? "NO_SIGNAL" : "PASS", "fixture");
        }
        public void StopAudio() => Stops++;
    }
}
