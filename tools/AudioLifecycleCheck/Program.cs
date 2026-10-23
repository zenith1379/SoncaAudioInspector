using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

// Endpoint settings are read-only. Explicit acoustic test modes emit test tones.
// Run baseline and fixed assemblies in separate processes with the same arguments.
string Option(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "="))?.Split('=', 2)[1] ?? fallback;
int cycles = int.Parse(Option("--cycles", "0"));
if (args.Any(a => a.StartsWith("--reverb-replay="))) { ReverbReplayChecks.Run(Option("--reverb-replay", ""), Option("--output", "")); return; }
if (args.Any(a => a.StartsWith("--c360-stage-live="))) { await C360StageChecks.Run(Option("--c360-stage-live", ""), Option("--model-config", ""), Option("--c360-setup", ""), Option("--test-id", "IO004"), args.Contains("--verify-reverb"), args.Contains("--reverb-off"), args.Any(a => a.StartsWith("--c360-reference-dir=")) ? Option("--c360-reference-dir", "") : null, !args.Contains("--compare-reference")); return; }
if (args.Any(a => a.StartsWith("--c360-usb-route="))) { await C360UsbRouteChecks.Run(Option("--c360-usb-route", ""), Option("--model-config", ""), Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16)); return; }
if (args.Any(a => a.StartsWith("--c360-levels="))) { await C360LevelChecks.Run(Option("--c360-levels", ""), Option("--c360-setup", ""), Option("--model-config", "")); return; }
int continuousSeconds = int.Parse(Option("--continuous-seconds", "0"));
bool verifyFixes = args.Contains("--verify-fixes");
if (args.Contains("--frequency-shift-logic")) { FrequencyShiftChecks.Logic(); return; }
if (args.Any(a => a.StartsWith("--fbx-replay="))) { FrequencyShiftChecks.Replay(Option("--fbx-replay", ""), Option("--output", "")); return; }
if (args.Any(a => a.StartsWith("--c360-effects-live="))) { await FrequencyShiftChecks.Live(Option("--c360-effects-live", ""), Option("--c360-setup", ""), "MicEffects", args.Contains("--dry-effects"), args.Contains("--find-effects-reference")); return; }
if (args.Any(a => a.StartsWith("--c360-fbx-live="))) { await FrequencyShiftChecks.Live(Option("--c360-fbx-live", ""), Option("--c360-setup", "")); return; }
if (args.Any(a => a.StartsWith("--ble-option-check="))) { await BleInputSourceChecks.Run(Option("--ble-option-check", ""), false, knownAddress: Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16), exerciseOptions: true); return; }
if (args.Any(a => a.StartsWith("--qa-graph-storage="))) { QaGraphStorageChecks.Run(Option("--qa-graph-storage", "")); return; }
if (args.Any(a => a.StartsWith("--frequency-shift-audit="))) { FrequencyShiftChecks.Audit(Option("--frequency-shift-audit", "")); return; }
if (args.Contains("--c360-sequence-logic")) { await C360ElectronicChecks.Run(); C360AcousticChecks.Run(); return; }
if (args.Any(a => a.StartsWith("--c360-electronic-check="))) { await BleInputSourceChecks.Run(Option("--c360-electronic-check", ""), false, knownAddress: Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16), c360SetupPath: Option("--c360-setup", ""), probeC360Line: args.Contains("--probe-c360-line"), exerciseOptions: args.Contains("--exercise-options")); return; }
if (args.Any(a => a.StartsWith("--select-measurement-input="))) { await BleInputSourceChecks.Run(Option("--select-measurement-input", ""), false, knownAddress: Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16), leaveSource: byte.Parse(Option("--source", "4"))); return; }
if (args.Any(a => a.StartsWith("--live-route-audit="))) { await LiveRouteAudit.Run(Option("--live-route-audit", ""), Option("--routing-config", ""), Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16)); return; }
if (args.Any(a => a.StartsWith("--live-noise-isolation="))) { await BleInputSourceChecks.Run(Option("--live-noise-isolation", ""), false, knownAddress: Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16), noiseConfigPath: Option("--routing-config", "")); return; }
if (args.Any(a => a.StartsWith("--line-distortion-audit="))) { await BleInputSourceChecks.Run(Option("--line-distortion-audit", ""), false, knownAddress: Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16), distortionConfigPath: Option("--routing-config", "")); return; }
if (args.Any(a => a.StartsWith("--ble-jack-status="))) { await BleJackStatusChecks.Run(Option("--ble-jack-status", ""), Convert.ToUInt64(Option("--ble-address", "423E3F752ECC"), 16), int.Parse(Option("--watch-seconds", "90"))); return; }
if (args.Any(a => a.StartsWith("--windows-bt-state="))) { await BleAutoInputPathChecks.WindowsState(Option("--windows-bt-state", "")); return; }
if (args.Any(a => a.StartsWith("--ble-auto-paths="))) { await BleAutoInputPathChecks.Run(Option("--ble-auto-paths", ""), args.Contains("--connect-auto-peer")); return; }
if (args.Any(a => a.StartsWith("--ble-source-availability="))) { await BleInputSourceChecks.Run(Option("--ble-source-availability", ""), false, knownAddress: args.Any(a => a.StartsWith("--ble-address=")) ? Convert.ToUInt64(Option("--ble-address", ""), 16) : null, probeAvailability: true); return; }
if (args.Any(a => a.StartsWith("--ble-source-hold="))) { await BleInputSourceChecks.Run(Option("--ble-source-hold", ""), false, holdSeconds: int.Parse(Option("--hold-seconds", "20")), holdSource: byte.Parse(Option("--source", "4")), knownAddress: args.Any(a => a.StartsWith("--ble-address=")) ? Convert.ToUInt64(Option("--ble-address", ""), 16) : null, deferSourceRead: args.Contains("--defer-source-read")); return; }
if (args.Any(a => a.StartsWith("--idle-input-check="))) { await IdleInputChecks.Run(Option("--idle-input-check", ""), Option("--routing-config", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector", "routing_value.json"))); return; }
if (args.Any(a => a.StartsWith("--ble-port-probe="))) { await BleInputSourceChecks.Run(Option("--ble-port-probe", ""), false, Option("--routing-config", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector", "routing_value.json"))); return; }
if (args.Any(a => a.StartsWith("--ble-input-source-check="))) { await BleInputSourceChecks.Run(Option("--ble-input-source-check", ""), args.Contains("--exercise-source")); return; }
if (args.Any(a => a.StartsWith("--live-effects-check="))) { await LiveDistortionCheck.RunEffects(Option("--live-effects-check", ""), Option("--routing-config", "")); return; }
if (args.Any(a => a.StartsWith("--reanalyze-effects="))) { LiveDistortionCheck.ReanalyzeEffects(Option("--reanalyze-effects", "")); return; }
if (args.Any(a => a.StartsWith("--live-distortion-check="))) { await LiveDistortionCheck.Run(Option("--live-distortion-check", ""), Option("--routing-config", "")); return; }
if (args.Any(a => a.StartsWith("--reanalyze-distortion="))) { LiveDistortionCheck.Reanalyze(Option("--reanalyze-distortion", "")); return; }
if (args.Contains("--sweep-cancellation")) { await SweepCancellationChecks.Run(verifyFixes); return; }
if (args.Contains("--sample-rate-snapshot")) { await SampleRateSnapshot.RunAsync(); return; }
if (args.Contains("--ble-snapshot")) { await BleConnectionChecks.SnapshotAsync(); return; }
if (args.Contains("--ble-logic")) { BleConnectionChecks.Logic(); return; }
if (args.Contains("--ble-connect-check")) { await BleConnectionChecks.ConnectControlAsync(); return; }
if (args.Contains("--ble-volume-observe")) { await BleVolumeObservation.RunAsync(int.Parse(Option("--watch-seconds", "45")), args.Contains("--passive")); return; }
if (args.Contains("--windows-volume-snapshot")) { BleVolumeObservation.WindowsSnapshot(); return; }
if (args.Contains("--ble-control-check")) { await BleConnectionChecks.ConnectControlAsync(exerciseControls: true); return; }
if (args.Contains("--manual-line-check")) { await ManualInputCheck.Line(Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory))); return; }
if (args.Contains("--manual-input-check")) { await ManualInputCheck.Run(Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory))); return; }
if (args.Contains("--sync-standard-rates")) { ImpulseLoopbackCheck.SyncStandardRates(); return; }
if (args.Any(a => a.StartsWith("--restore-capture-rate="))) { ImpulseLoopbackCheck.RestoreCaptureRate(int.Parse(Option("--restore-capture-rate", "48000"))); return; }
if (args.Contains("--impulse-loopback")) { await ImpulseLoopbackCheck.RunAsync(Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory)), int.Parse(Option("--playback-rate", "48000")), args.Contains("--align-capture-rate")); return; }
if (args.Contains("--model-startup-check")) { ModelStartupChecks.Run(Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory)), Path.GetFullPath(Option("--dependency-directory", AppContext.BaseDirectory))); return; }
if (args.Contains("--audio-session-check")) { ModelStartupChecks.AudioSessions(); return; }
if (args.Contains("--fasttrack-logic")) { FastTrackReconnectChecks.Logic(); return; }
if (args.Contains("--fasttrack-snapshot")) { FastTrackReconnectChecks.Hardware(apply: false); return; }
if (args.Contains("--fasttrack-apply")) { FastTrackReconnectChecks.Hardware(apply: true); return; }
if (args.Contains("--fasttrack-ui")) { FastTrackReconnectChecks.Ui(Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory)), Path.GetFullPath(Option("--dependency-directory", AppContext.BaseDirectory))); return; }
if (args.Contains("--long-run-check")) { await LongRunChecks.RunAsync(Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory))); return; }
if (args.Contains("--sweep-benchmark")) { SweepPerformanceChecks.Run(); return; }
if (args.Contains("--sweep-equivalence")) { SweepPerformanceChecks.Run(repeats: 1); return; }
if (args.Any(a => a.StartsWith("--native-sweep-comparison="))) { SweepNumericalComparison.Run(Option("--native-sweep-comparison", "")); return; }
if (args.Contains("--production-sweep-benchmark")) { SweepPerformanceChecks.RunProduction(int.Parse(Option("--sweep-length", "524288")), int.Parse(Option("--runs", "3")), args.Contains("--silent-capture")); return; }
if (args.Contains("--clock-equivalence")) { SweepPerformanceChecks.CheckClockCorrection(); return; }
if (args.Contains("--startup-check")) { await StartupWorkflowChecks.RunAsync(); return; }
if (args.Contains("--login-owner-guard"))
{
    var shouldDefer = typeof(MainWindow).GetMethod("ShouldDeferDeviceConnectionWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
    Require((bool)shouldDefer.Invoke(null, new object[] { false, false })!, "Unshown MainWindow did not defer its owned device dialog.");
    Require((bool)shouldDefer.Invoke(null, new object[] { true, false })!, "MainWindow without a presentation source did not defer its owned dialog.");
    Require(!(bool)shouldDefer.Invoke(null, new object[] { true, true })!, "Shown MainWindow unnecessarily deferred its owned dialog.");
    Write(new { stage = "login-owner-lifecycle-guard-pass" });
    return;
}
if (args.Contains("--audio-usage")) { Write(AudioSessionDiagnostics.Scan()); return; }
if (args.Contains("--observe-scope-route")) { await ScopeRouteObservation.Run(); return; }
if (args.Contains("--wav-player-check")) { await ScopeRouteObservation.Run(playWav: true); return; }
if (args.Contains("--hold-silent-output"))
{
    using var audioEnumerator = new MMDeviceEnumerator();
    var endpoints = audioEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    try
    {
        var realtek = endpoints.Single(d => d.FriendlyName.Contains("Realtek"));
        using var silentOutput = new WasapiOut(realtek, AudioClientShareMode.Shared, false, 100);
        silentOutput.Init(new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true });
        silentOutput.Play();
        Write(new { stage = "silent-session-active", processId = Environment.ProcessId });
        await Task.Delay(20000);
        silentOutput.Stop();
    }
    finally { foreach (var endpoint in endpoints) endpoint.Dispose(); }
    return;
}
if (args.Contains("--isolate")) { await IndependentAudioProbe.Run(args); return; }
if (args.Contains("--integrity-check"))
{
    double gain = Math.Pow(10, -10.508596 / 20.0);
    float[] ReadChannel(string path)
    {
        using var reader = new WaveFileReader(path);
        var result = new List<float>(); float[]? frame;
        while ((frame = reader.ReadNextSampleFrame()) != null) result.Add(frame[1]);
        return result.ToArray();
    }
    Require(CaptureDataIntegrity.HasRepeatedPcm16Bytes(ReadChannel(".artifacts/audio-lifecycle/input-output-comparison/01-playing.wav"), gain), "Missed real byte-corrupted capture.");
    Require(CaptureDataIntegrity.HasRepeatedPcm16Bytes(ReadChannel(".artifacts/audio-lifecycle/independent-mid/01-playing.wav"), gain), "Missed quieter corrupted capture.");
    Require(CaptureDataIntegrity.HasRepeatedPcm16Bytes(ReadChannel(".artifacts/audio-lifecycle/replug-byte-test/01-playing.wav").Concat(ReadChannel(".artifacts/audio-lifecycle/input-output-comparison/01-playing.wav")).ToArray(), gain), "Missed corruption beginning during capture.");
    Require(!CaptureDataIntegrity.HasRepeatedPcm16Bytes(ReadChannel(".artifacts/audio-lifecycle/replug-byte-test/01-playing.wav"), gain), "Rejected real healthy capture.");
    Require(!CaptureDataIntegrity.HasRepeatedPcm16Bytes(ReadChannel(".artifacts/audio-lifecycle/independent-low/01-playing.wav"), gain), "Misclassified near-silence as byte corruption.");
    Require(!CaptureDataIntegrity.HasRepeatedPcm16Bytes(new float[44100], gain), "Misclassified silence.");
    Write(new { stage = "real-wav-byte-integrity-check-pass" });
    return;
}
if (args.Contains("--logic-only"))
{
    ReferenceSelectionChecks();
    FreshWindowChecks();
    ResponseLevelChecks();
    SweepAmplitudeChecks();
    ScopeWaveformChecks();
    ScopeWavChecks();
    return;
}
if (args.Contains("--ui-guards"))
{
    string dependencyDirectory = Path.GetFullPath(Option("--dependency-directory", AppContext.BaseDirectory));
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        string path = Path.Combine(dependencyDirectory, name.Name + ".dll");
        return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
    };
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new App(); app.InitializeComponent();
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            var login = new LoginWindow();
            var pendingLogin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int loginCalls = 0;
            Func<string, string, IProgress<string>?, Task<bool>> fakeLogin = (_, _, _) => { loginCalls++; return pendingLogin.Task; };
            typeof(LoginWindow).GetField("Authenticate", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(login, fakeLogin);
            ((System.Windows.Controls.TextBox)login.FindName("TxtUsername")).Text = "unit-test";
            ((System.Windows.Controls.PasswordBox)login.FindName("TxtPassword")).Password = "not-a-real-password";
            var clickLogin = typeof(LoginWindow).GetMethod("BtnLogin_Click", BindingFlags.NonPublic | BindingFlags.Instance)!;
            clickLogin.Invoke(login, new object[] { login, new System.Windows.RoutedEventArgs() });
            clickLogin.Invoke(login, new object[] { login, new System.Windows.RoutedEventArgs() });
            var loginButton = (System.Windows.Controls.Button)login.FindName("BtnLogin");
            Require(loginCalls == 1 && !loginButton.IsEnabled, "Fast login clicks sent overlapping requests.");
            pendingLogin.SetResult(false);
            var frame = new System.Windows.Threading.DispatcherFrame();
            var loginTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            var loginWait = Stopwatch.StartNew();
            loginTimer.Tick += (_, _) => { if (loginButton.IsEnabled || loginWait.Elapsed.TotalSeconds > 3) frame.Continue = false; };
            loginTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); loginTimer.Stop();
            Require(loginCalls == 1 && loginButton.IsEnabled, "Login did not await completion or restore UI after response.");
            using var guardEngine = new AudioEngine();
            var routing = new AudioRouting();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(AudioRouting).GetField("_audioEngine", flags)!.SetValue(routing, guardEngine);
            var guardRunner = new TestRunner(guardEngine);
            typeof(AudioRouting).GetField("_testRunner", flags)!.SetValue(routing, guardRunner);
            string graphDirectory = Path.GetFullPath(Option("--test-directory", AppContext.BaseDirectory));
            Directory.CreateDirectory(graphDirectory);
            string graphPath = Path.Combine(graphDirectory, "server-graph-contract.png");
            object plotControl = routing.FindName("PlotFreqResponse");
            object plot = plotControl.GetType().GetProperty("Plot")!.GetValue(plotControl)!;
            typeof(AudioRouting).GetMethod("SaveServerGraphPng", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new[] { plot, graphPath });
            using (var graphStream = File.OpenRead(graphPath))
            {
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(graphStream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Require(decoder.Frames[0].PixelWidth == 800 && decoder.Frames[0].PixelHeight == 450,
                    "PNG upload dimensions changed.");
            }
            Require(!Directory.EnumerateFiles(graphDirectory, ".fullscreen-*.png").Any(), "PNG render left a temporary disk file.");
            Console.WriteLine("Server graph: WPF decoded 800x450 PNG; no fullscreen temporary file.");
            foreach (double level in new[] { -60.0, -20.0, -1.0, -0.5, 0.0 })
            {
                routing.PlaybackLevelDbfs = level;
                var generated = StandardAcousticMeasurement.GenerateLogSweep(new LogSweepSettings(
                    44100, 18, 20000, .5, guardRunner.PlaybackAmplitude));
                Require(generated.All(sample => float.IsFinite(sample) && Math.Abs(sample) <= 1),
                    "A level allowed by Audio Routing cannot produce a valid sweep.");
                Require(guardRunner.PlaybackLevelDbFs == level, "UI silently changed the requested excitation level.");
            }
            if (!args.Contains("--skip-scope-endpoints"))
            {
            using (var scopeEnumerator = new MMDeviceEnumerator())
            using (var scopeOutput = scopeEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            using (var scopeInput = scopeEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
            {
                var scope = new RoutingScopeWindow(guardEngine, scopeOutput, scopeInput, 0, 0);
                var clickSine = typeof(RoutingScopeWindow).GetMethod("BtnSineCheck_Click", flags)!;
                var status = (System.Windows.Controls.TextBlock)scope.FindName("TxtSineActionStatus");
                var capture = (FreshCaptureWindow)typeof(RoutingScopeWindow).GetField("_captureWindow", flags)!.GetValue(scope)!;
                Require(!(bool)typeof(RoutingScopeWindow).GetField("_sineRunning", flags)!.GetValue(scope)!, "Opening scope started WAV output.");
                var weak = Enumerable.Range(0, 4096).Select(i => (float)(.0016 * Math.Sin(2 * Math.PI * i / 48))).ToArray();
                var copy = weak.ToArray();
                typeof(RoutingScopeWindow).GetMethod("FitScopeToSamples", flags)!.Invoke(scope, new object[] { weak });
                Require((int)typeof(RoutingScopeWindow).GetField("_voltsPerDivisionIndex", flags)!.GetValue(scope)! == 2,
                    "Weak sine remains invisible at the default vertical scale.");
                Require(weak.SequenceEqual(copy), "Plot fit altered actual capture levels.");
                var refreshScope = typeof(RoutingScopeWindow).GetMethod("RefreshScope", flags)!;
                typeof(RoutingScopeWindow).GetField("_sineRunning", flags)!.SetValue(scope, true);
                refreshScope.Invoke(scope, null);
                Require(!(bool)typeof(RoutingScopeWindow).GetField("_sineRunning", flags)!.GetValue(scope)!
                    && status.Text.Contains("Đã dừng phát sine"), "Stopped renderer was still shown as playing.");
                capture.Reset(0);
                capture.Append(Enumerable.Range(0, 4096).Select(i => (float)(.00002 * Math.Sin(i * .73))).ToArray(), 48000);
                refreshScope.Invoke(scope, null);
                Require(((System.Windows.Controls.TextBlock)scope.FindName("TxtFrequency")).Text == "-- Hz",
                    "Background noise was displayed as a measured frequency.");
                capture.Reset(0);
                capture.Append(weak, 48000);
                typeof(RoutingScopeWindow).GetField("_lastSineAnalysisUtc", flags)!.SetValue(scope, DateTime.MinValue);
                refreshScope.Invoke(scope, null);
                Require(((System.Windows.Controls.TextBlock)scope.FindName("TxtFrequency")).Text.Contains("1000"),
                    "Scope cannot analyze external WAV unless its own player is running.");
                Require(!guardEngine.IsContinuousPlaybackActive, "Scope used the measurement engine's output.");
                int rateBefore = guardEngine.PlaybackSampleRate;
                bool exclusiveBefore = guardEngine.UseExclusivePlayback;
                double levelBefore = guardRunner.PlaybackLevelDbFs;
                typeof(RoutingScopeWindow).GetField("_previousRecordingChannel", flags)!.SetValue(scope, (int?)1);
                typeof(RoutingScopeWindow).GetField("_scopeSettingsApplied", flags)!.SetValue(scope, true);
                guardEngine.PlaybackChannel = 1; guardEngine.RecordingChannel = 0;
                scope.Close();
                Require(guardEngine.PlaybackChannel == 1 && guardEngine.RecordingChannel == 1
                    && guardEngine.PlaybackSampleRate == rateBefore && guardEngine.UseExclusivePlayback == exclusiveBefore
                    && guardRunner.PlaybackLevelDbFs == levelBefore, "Closing scope changed the next measurement settings.");
            }
            }
            else Console.WriteLine("Scope endpoint checks skipped explicitly; this host has no default capture endpoint.");
            string standardKey = (string)typeof(AudioRouting).GetMethod("GetStandardDeviceKey", flags)!.Invoke(routing, null)!;
            Require(standardKey.Contains(TestRunner.FrequencyResponseLevelBasis), "Old normalized standards can collide with dBFS standards.");
            typeof(AudioRouting).GetMethod("RenderLocalMeasurementDiagnosis", flags)!.Invoke(routing, null);
            var diagnosis = (System.Windows.Controls.TextBlock)routing.FindName("TxtFailureDiagnosis");
            Require(diagnosis.Visibility == System.Windows.Visibility.Visible && diagnosis.Text.Contains("chưa đủ tin cậy"),
                "Empty acquisition did not show local invalid-data diagnosis.");
            typeof(AudioRouting).GetMethod("ClearLocalMeasurementDiagnosis", flags)!.Invoke(routing, null);
            Require(diagnosis.Visibility == System.Windows.Visibility.Collapsed && diagnosis.Text.Length == 0, "Old diagnosis was not cleared.");
            var begin = typeof(AudioRouting).GetMethod("TryBeginRoutingWorkflow", flags)!;
            using var workflow = (IDisposable)begin.Invoke(routing, null)!;
            Require(routing.IsTestingBusy, "Workflow is not marked busy.");
            var headroomEnabled = typeof(AudioRouting).GetField("_routingHeadroomEnabled", flags)!;
            var toggleHeadroom = typeof(AudioRouting).GetMethod("BtnToggleHeadroom_Click", flags)!;
            bool initialHeadroom = (bool)headroomEnabled.GetValue(routing)!;
            toggleHeadroom.Invoke(routing, new object[] { routing, new System.Windows.RoutedEventArgs() });
            Require((bool)headroomEnabled.GetValue(routing)! == initialHeadroom, "Headroom setting changed during measurement.");
            foreach (string name in new[] { "ComboPlayback", "ComboRecording", "ComboRoutingRecordingChannel", "ComboPlaybackSampleRate", "BtnToggleHeadroom" })
                Require(!((System.Windows.UIElement)routing.FindName(name)).IsEnabled, "Measurement configuration is editable during a workflow: " + name);
            var guardedDevice = (System.Windows.Controls.ComboBox)routing.FindName("ComboPlayback");
            guardedDevice.Items.Add("synthetic selection");
            guardedDevice.SelectedIndex = 0;
            Require(guardedDevice.SelectedItem == null, "Programmatic endpoint change bypassed the workflow guard.");
            guardedDevice.Items.Remove("synthetic selection");
            Require(begin.Invoke(routing, null) == null, "Overlapping UI workflow accepted.");
            typeof(AudioRouting).GetField("_isExecutingAutoSuite", flags)!.SetValue(routing, false);
            Require(routing.IsTestingBusy, "Cancel cleared busy before workflow cleanup.");
            foreach (string handler in new[] { "BtnStart_Click", "BtnStartAutoTest_Click", "BtnSaveStandardReference_Click", "BtnNoiseTest_Click", "BtnOpenRoutingScope_Click" })
                typeof(AudioRouting).GetMethod(handler, flags)!.Invoke(routing, new object[] { routing, new System.Windows.RoutedEventArgs() });
            Require(typeof(AudioRouting).GetField("_activeMeasurementTask", flags)!.GetValue(routing) == null,
                "A guarded UI handler started a measurement.");
            Require(typeof(AudioRouting).GetField("_routingScopeWindow", flags)!.GetValue(routing) == null,
                "Scope opened during an active workflow.");
            workflow.Dispose();
            Require(((System.Windows.UIElement)routing.FindName("BtnToggleHeadroom")).IsEnabled,
                "Workflow did not restore configuration controls.");
            toggleHeadroom.Invoke(routing, new object[] { routing, new System.Windows.RoutedEventArgs() });
            string configPath = (string)typeof(AudioRouting).GetMethod("GetRoutingConfigPath", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            Require((bool)headroomEnabled.GetValue(routing)! != initialHeadroom
                && AtomicFile.ReadJson<AppConfig>(configPath)!.HeadroomEnabled != initialHeadroom,
                "Pause/continue state was not applied and persisted.");
            toggleHeadroom.Invoke(routing, new object[] { routing, new System.Windows.RoutedEventArgs() });
            using var nextWorkflow = (IDisposable)begin.Invoke(routing, null)!;
            Require(nextWorkflow != null, "UI workflow did not release after cleanup.");
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start(); thread.Join();
    if (failure != null) { Console.Error.WriteLine(failure); Environment.ExitCode = 1; return; }
    Write(new { stage = "ui-workflow-guards-pass" });
    return;
}
using var engine = new AudioEngine { UseExclusivePlayback = false };
using var enumerator = new MMDeviceEnumerator();
var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToList();
try
{
    foreach (var device in devices) Snapshot("initial", device);
    if (cycles == 0 && continuousSeconds == 0 && !args.Contains("--reference-three") && !args.Contains("--stop-loopback") && !args.Contains("--probe-channels") && !args.Contains("--scope-sweep")) return;
    var input = devices.Single(d => d.DataFlow == DataFlow.Capture && d.FriendlyName.Contains(Option("--input", "FastTrack Pro"), StringComparison.OrdinalIgnoreCase));
    var output = devices.Single(d => d.DataFlow == DataFlow.Render && d.FriendlyName.Contains(Option("--output", "Analog Connector 1/2"), StringComparison.OrdinalIgnoreCase));
    string beforeInput = Settings(input), beforeOutput = Settings(output);
    engine.PlaybackSampleRate = ReadFormat(input).SampleRate;
    engine.RecordingChannel = int.Parse(Option("--channel", "1"));
    engine.PlaybackChannel = int.Parse(Option("--playback-channel", "0"));
    using var captureKeepAlive = args.Contains("--keep-capture-open") ? new SharedCaptureSession() : null;
    captureKeepAlive?.Ensure(input, false);
    if (captureKeepAlive != null) Write(new { stage = "bounded-shared-capture-started", note = "Playback still stops between tests; capture released at end of this process." });
    if (args.Contains("--scope-sweep"))
    {
        var live = new List<float>(); object liveLock = new(); int rate = 44100;
        void ReceiveScope(float[] data, int sr)
        {
            lock (liveLock)
            {
                rate = sr; live.AddRange(data);
                if (live.Count > sr * 2) live.RemoveRange(0, live.Count - sr * 2);
            }
        }
        float[] ReadLive() { lock (liveLock) return live.TakeLast(rate).ToArray(); }
        double Floor(float[] data) => 10 * Math.Log10(Math.Max(1e-20, data.Select(x => x * (double)x).DefaultIfEmpty().Average()));
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            engine.StopAllAudio();
            lock (liveLock) live.Clear();
            engine.StartContinuousCapture(input, ReceiveScope);
            Require(engine.CaptureSession.IsActive, "Scope-first capture did not retain FastTrack session.");
            await Task.Delay(1500);
            float[] background = ReadLive();
            double beforeDbFs = Floor(background);
            Write(new { stage = "scope-background", cycle, beforeDbFs });
            Require(background.Length > 10000 && beforeDbFs < -70, "Input is already noisy; no stimulus emitted.");
            engine.StartContinuousPlayback(output, SignalType.Sine, 1000, .05, engine.PlaybackChannel);
            await Task.Delay(1800);
            float[] toneCapture = ReadLive();
            var quality = AdvancedAudioMeasurement.AnalyzeTone(toneCapture, rate, 1000, 9);
            engine.StopContinuousPlayback();
            string captureDirectory = Option("--capture-directory", "");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                using var wav = new WaveFileWriter(Path.Combine(captureDirectory, $"sine-{cycle}.wav"), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
                wav.WriteSamples(toneCapture, 0, toneCapture.Length);
            }
            Require(!engine.IsAnyPlaybackActive, "Scope stop left playback active.");
            await Task.Delay(1500);
            double afterDbFs = Floor(ReadLive());
            engine.StopContinuousCapture();
            Require(engine.CaptureSession.IsActive && !engine.IsContinuousCaptureActive, "Scope close released the shared device session or leaked its callback.");
            Write(new { stage = "scope-cycle", cycle, quality.FundamentalFrequencyHz, quality.ThdPercent, quality.IsValid,
                quality.SignalLevelDbFs, quality.PeakSample, quality.ClippedSamplePercent, afterDbFs });
            Require(quality.IsValid && quality.ThdPercent < 1 && afterDbFs < -70, "Scope cycle acquired invalid signal/noise.");
        }
        var manualRunner = new TestRunner(engine)
        {
            UseLogSweepFrequencyResponse = true, AutoTestOneKilohertzOnly = false,
            LogSweepVerificationRuns = 1, RequireLogSweepPhaseAlignment = false,
            LogSweepDurationSeconds = 131072.0 / engine.PlaybackSampleRate,
            PlaybackLevelDbFs = -20, MinimumInputSignalDbFs = -70,
            EnableNoiseDiagnostics = true, AmbientNoiseMaxRetries = 0
        };
        int toneSpectra = 0, sweepCaptures = 0;
        manualRunner.OnThdSpectrumReady += (_, _, _, _, _) => toneSpectra++;
        manualRunner.OnMeasurementCaptured += (_, _, _, _, _) => sweepCaptures++;
        manualRunner.OnLogMessage += (source, message) => Write(new { source, message });
        await manualRunner.RunTestAsync(output, input);
        Write(new { stage = "manual-sweep-result", toneSpectra, sweepCaptures, manualRunner.ThdAcquisitionInvalid,
            points = manualRunner.LastToneQualities.Select(p => new { frequency = p.Key, p.Value.ThdPercent, harmonicOrders = p.Value.Harmonics.Select(h => h.Order).ToArray() }),
            audioStopped = !engine.IsAnyPlaybackActive });
        Require(toneSpectra == 0 && sweepCaptures == 1 && manualRunner.LastToneQualities.Count == 3
            && !manualRunner.ThdAcquisitionInvalid, "Manual sweep triggered separate tone THD or produced invalid results.");
        Require(!engine.IsAnyPlaybackActive && Settings(input) == beforeInput && Settings(output) == beforeOutput, "Playback/settings changed at workflow boundary.");
        engine.Dispose();
        Require(!engine.CaptureSession.IsActive, "Engine shutdown retained shared capture.");
        return;
    }
    if (args.Contains("--probe-channels"))
    {
        foreach (MMDevice probeOutput in devices.Where(d => d.DataFlow == DataFlow.Render && d.FriendlyName.Contains("FastTrack Pro")))
        for (int outputChannel = 0; outputChannel < 2; outputChannel++)
        {
            var recordedChannels = new[] { new List<float>(), new List<float>() };
            using var capture = new WasapiCapture(input, false, 100);
            capture.DataAvailable += (_, e) =>
            {
                for (int c = 0; c < Math.Min(2, capture.WaveFormat.Channels); c++)
                    recordedChannels[c].AddRange(CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, capture.WaveFormat, 1, c));
            };
            capture.StartRecording();
            engine.StartContinuousPlayback(probeOutput, SignalType.Sine, 1000, .1, outputChannel);
            await Task.Delay(1500);
            engine.StopAllAudio(); capture.StopRecording(); capture.Dispose();
            for (int c = 0; c < 2; c++)
            {
                float[] window = recordedChannels[c].TakeLast(capture.WaveFormat.SampleRate).ToArray();
                double rms = Math.Sqrt(window.Select(x => x * (double)x).DefaultIfEmpty().Average());
                Write(new { stage = "channel-probe", output = probeOutput.FriendlyName, outputChannel = outputChannel + 1, inputChannel = c + 1,
                    samples = window.Length, rmsDbFs = 20 * Math.Log10(Math.Max(rms, 1e-20)), peak = window.Select(x => Math.Abs((double)x)).DefaultIfEmpty().Max() });
            }
        }
        return;
    }
    if (args.Contains("--stop-loopback"))
    {
        var captured = new List<float>();
        object captureLock = new();
        using var loopback = new WasapiLoopbackCapture(output);
        loopback.DataAvailable += (_, e) =>
        {
            float[] block = CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, loopback.WaveFormat, 1, engine.PlaybackChannel);
            lock (captureLock) captured.AddRange(block);
        };
        loopback.StartRecording();
        engine.StartContinuousPlayback(output, SignalType.Sine, 1000, .05);
        await Task.Delay(1000);
        double playingPeak;
        lock (captureLock) playingPeak = captured.Select(x => Math.Abs((double)x)).DefaultIfEmpty().Max();
        Require(playingPeak > .001, "Loopback did not observe the test tone before StopAllAudio.");
        engine.StopAllAudio();
        Require(!engine.IsAnyPlaybackActive && !engine.IsContinuousCaptureActive, "Audio still active after StopAllAudio.");
        lock (captureLock) captured.Clear();
        // A separate silent shared stream keeps WASAPI loopback delivering samples
        // after the app has stopped; otherwise silence may produce no packets.
        using var silenceOut = new WasapiOut(output, AudioClientShareMode.Shared, false, 100);
        silenceOut.Init(new ContinuousSignalProvider(engine.PlaybackSampleRate, 2) { Volume = 0 });
        silenceOut.Play();
        await Task.Delay(800);
        loopback.StopRecording(); loopback.Dispose();
        silenceOut.Stop();
        float[] tail;
        lock (captureLock) tail = captured.TakeLast(loopback.WaveFormat.SampleRate / 4).ToArray();
        double residualPeak = tail.Select(x => Math.Abs((double)x)).DefaultIfEmpty(double.NaN).Max();
        Require(tail.Length > 0 && residualPeak < .0001, $"Unexpected signal after stop: {residualPeak}.");
        Write(new { stage = "stop-loopback-pass", playingPeak, residualPeak, samples = tail.Length, settingsUnchanged = Settings(input) == beforeInput && Settings(output) == beforeOutput });
        return;
    }
    if (args.Contains("--reference-three"))
    {
        for (int batch = 1; batch <= int.Parse(Option("--reference-batches", "1")); batch++)
        {
        Write(new { stage = "reference-batch-start", batch });
        var runner = new TestRunner(engine)
        {
            CaptureSession = captureKeepAlive,
            UseLogSweepFrequencyResponse = true,
            LogSweepDurationSeconds = 131072.0 / engine.PlaybackSampleRate,
            LogSweepVerificationRuns = 1,
            PlaybackLevelDbFs = -20,
            PlaybackFrequencyScale = 1,
            RequireLogSweepPhaseAlignment = false,
            MinimumInputSignalDbFs = -70,
            EvaluationMaxFrequencyHz = 20000,
            EnableNoiseDiagnostics = true,
            AmbientNoiseMaxRetries = 0,
            ReuseSuiteNoiseFloor = false,
            FreqResponseToleranceDb = 3
        };
        var curves = new List<Dictionary<double, double>>();
        Dictionary<double, double> curve = new();
        runner.OnFrequencyResponsePoint += (frequency, db) => curve[frequency] = db;
        runner.OnLogMessage += (source, message) => Write(new { source, message });
        for (int run = 1; run <= 3; run++)
        {
            curve = new();
            await runner.RunTestAsync(output, input, referenceAcquisition: true);
            Require(!engine.IsAnyPlaybackActive, "Old test is still playing at the reference boundary.");
            Write(new { stage = "reference", run, valid = runner.ReferenceAcquisitionValid, reason = runner.LastFeqInvalidReason,
                points = curve.Count, signalDbFs = runner.LastSignalLevelDbFs, peak = runner.LastFeqPeakSample, audioStopped = !engine.IsAnyPlaybackActive });
            if (!runner.ReferenceAcquisitionValid) return;
            curves.Add(curve);
        }
        ReferenceCurvePair pair = ReferenceCurveSelector.SelectClosestPair(curves);
        Write(new { stage = "reference-selected", pair.FirstRun, pair.SecondRun, pair.MaximumDifferenceDb, pair.RmsDifferenceDb, points = pair.AverageCurve.Count });
        Require(pair.MaximumDifferenceDb <= 3, "No pair is stable enough to save.");
        runner.StandardCurve = pair.AverageCurve;
        runner.AutoTestOneKilohertzOnly = true;
        await runner.RunTestAsync(output, input);
        Write(new { stage = "auto-after-reference", runner.HasComparedToStandard, runner.LastFrequencyMaxDeviationDb,
            runner.LastMeasuredThdPercent, runner.ThdAcquisitionInvalid, audioStopped = !engine.IsAnyPlaybackActive });
        Require(!engine.IsAnyPlaybackActive, "Auto Test left a playback source running.");
        Require(Settings(input) == beforeInput && Settings(output) == beforeOutput, "Reference/Auto Test changed endpoint settings.");
        }
        captureKeepAlive?.Dispose();
        Require(captureKeepAlive?.IsActive != true, "Capture session remained open after workflow disposal.");
        if (captureKeepAlive == null)
        {
            Require(engine.CaptureSession.IsActive, "Engine did not preserve FastTrack input between workflows.");
            engine.Dispose();
            Require(!engine.CaptureSession.IsActive, "Engine shutdown retained capture resources.");
        }
        Write(new { stage = "capture-session-released", audioStopped = !engine.IsAnyPlaybackActive });
        return;
    }
    bool duplex = args.Contains("--silent-duplex");
    long callbacks = 0, samples = 0;
    double sumSquares = 0, peak = 0;
    object sampleLock = new();
    void Receive(float[] block, int rate)
    {
        lock (sampleLock)
        {
            callbacks++;
            samples += block.Length;
            foreach (float sample in block) { sumSquares += sample * (double)sample; peak = Math.Max(peak, Math.Abs(sample)); }
        }
    }
    void Start()
    {
        engine.StartContinuousCapture(input, Receive);
        if (duplex) engine.StartContinuousPlayback(output, SignalType.Sine, 1000, 0);
    }
    void Stop()
    {
        engine.StopContinuousPlayback();
        engine.StopContinuousCapture();
    }
    Start();
    await Task.Delay(600);
    Stop();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    int handlesBefore = process.HandleCount;
    for (int cycle = 1; cycle <= cycles; cycle++)
    {
        Start();
        await Task.Delay(300);
        Stop();
        long stoppedCallbacks;
        lock (sampleLock) stoppedCallbacks = callbacks;
        await Task.Delay(30);
        lock (sampleLock) Require(callbacks == stoppedCallbacks, "Capture callback continued after Stop returned.");
        Require(!engine.IsContinuousCaptureActive && !engine.IsContinuousPlaybackActive, "Stream still active after Stop.");
        if (cycle % 10 == 0) { process.Refresh(); Write(new { stage = "cycles", cycle, handles = process.HandleCount, threads = process.Threads.Count }); }
    }
    process.Refresh();
    int handleDelta = process.HandleCount - handlesBefore;
    Write(new { stage = "resource-result", cycles, duplex, handlesBefore, handlesAfter = process.HandleCount, handleDelta });
    // Thread/COM runtime handles also await GC. Report both counts; a raw peak
    // alone is not evidence of a permanent leak or of an ADC fault.
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    process.Refresh();
    int settledHandleDelta = process.HandleCount - handlesBefore;
    Write(new { stage = "resource-after-gc", handles = process.HandleCount, settledHandleDelta });
    if (continuousSeconds > 0)
    {
        Start();
        for (int second = 0; second < continuousSeconds; second++)
        {
            await Task.Delay(1000);
            if ((second + 1) % 10 == 0)
            {
                process.Refresh();
                lock (sampleLock) Write(new { stage = "continuous", seconds = second + 1, handles = process.HandleCount, samples, rmsDbFs = 10 * Math.Log10(Math.Max(sumSquares / Math.Max(samples, 1), 1e-20)), peak });
            }
        }
        Stop();
    }
    if (verifyFixes)
    {
        var guardedRunner = new TestRunner(engine);
        Task guardedRun = guardedRunner.RunTestAsync(null!, null!);
        try { await guardedRunner.RunTestAsync(null!, null!); throw new Exception("Overlapping TestRunner runs accepted."); }
        catch (InvalidOperationException) { }
        await guardedRun;
        Write(new { stage = "runner-overlap-rejected" });
        Require(settledHandleDelta < 16, $"Handles still retained after GC: {settledHandleDelta}.");
        Task<AudioEngine.DualCaptureResult> active = engine.CaptureSilenceAsync(input, null, .8);
        try { await engine.CaptureSilenceAsync(input, null, .25); throw new Exception("Overlapping measurements were accepted."); }
        catch (InvalidOperationException) { Write(new { stage = "overlap-rejected" }); }
        try { Start(); throw new Exception("Continuous capture interrupted a measurement."); }
        catch (InvalidOperationException) { Write(new { stage = "continuous-overlap-rejected" }); }
        Require((await active).DutSamples.Length > 0, "No samples from capture.");
        using (var cancellation = new CancellationTokenSource(100))
        {
            try { await engine.CaptureSilenceAsync(input, null, 5, cancellation.Token); throw new Exception("Cancellation not observed."); }
            catch (OperationCanceledException) { }
        }
        Require((await engine.CaptureSilenceAsync(input, null, .3)).DutSamples.Length > 0, "Capture did not recover after cancellation.");
        for (int run = 0; run < 3; run++)
        {
            float[] recording = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 1000, .3,
                customSweepSamples: new float[engine.PlaybackSampleRate]);
            Require(recording.Length > 0, "No samples from finite silent duplex measurement.");
        }
        using (var cancellation = new CancellationTokenSource(350))
        {
            try
            {
                await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 1000, 5,
                    cancellationToken: cancellation.Token, customSweepSamples: new float[engine.PlaybackSampleRate]);
                throw new Exception("Duplex cancellation not observed.");
            }
            catch (OperationCanceledException) { }
        }
        try { await engine.PlayFileAndRecordAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav"), output, input, .1); throw new Exception("Missing file accepted."); }
        catch (FileNotFoundException) { }
        Require(typeof(AudioEngine).GetField("_wasapiCapture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine) == null, "Capture leaked after file-open failure.");
        // Valid source with an invalid selected channel fails after capture allocation.
        string testWav = Path.Combine(Path.GetTempPath(), "sonca-lifecycle-" + Guid.NewGuid() + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(testWav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1))) writer.WriteSamples(new float[4410], 0, 4410);
            engine.RecordingChannel = 999;
            try { await engine.PlayFileAndRecordAsync(testWav, output, input, .1); throw new Exception("Invalid channel accepted."); }
            catch (ArgumentOutOfRangeException) { }
            Require(typeof(AudioEngine).GetField("_wasapiCapture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine) == null, "Capture leaked after setup failure.");
        }
        finally { File.Delete(testWav); engine.RecordingChannel = 1; }
        Start(); await Task.Delay(350); Stop(); Stop();
        Write(new { stage = "failure-cancellation-recovery-pass" });
    }
    Require(Settings(input) == beforeInput && Settings(output) == beforeOutput, "Endpoint settings changed during test.");
    Snapshot("final", input); Snapshot("final", output);
    lock (sampleLock) Write(new { stage = "complete", callbacks, samples, rmsDbFs = 10 * Math.Log10(Math.Max(sumSquares / Math.Max(samples, 1), 1e-20)), peak });
}
finally { foreach (var device in devices) device.Dispose(); }

static WaveFormat ReadFormat(MMDevice device) { using var client = device.AudioClient; return client.MixFormat; }
static string Settings(MMDevice device) => JsonSerializer.Serialize(new { device.ID, format = ReadFormat(device).ToString(), volume = device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute, channels = Enumerable.Range(0, device.AudioEndpointVolume.Channels.Count).Select(c => device.AudioEndpointVolume.Channels[c].VolumeLevelScalar).ToArray() });
static void Snapshot(string stage, MMDevice device) => Write(new { stage, device.FriendlyName, settings = JsonSerializer.Deserialize<object>(Settings(device)) });
static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static void ResponseLevelChecks()
{
    using var silentEngine = new AudioEngine();
    var runner = new TestRunner(silentEngine) { PlaybackLevelDbFs = -20, PlaybackFrequencyScale = 1 };
    var response = new[] { new FrequencyResponsePoint(100, -6, 6, 0, 0),
        new FrequencyResponsePoint(1000, -12, 0, 0, 0), new FrequencyResponsePoint(10000, -18, -6, 0, 0) };
    var method = typeof(TestRunner).GetMethod("InterpolateScaledLogSweepResponse", BindingFlags.NonPublic | BindingFlags.Instance)!;
    double[] frequencies = { 100, 1000, 10000, 20000 };
    var original = (Dictionary<double, double>)method.Invoke(runner, new object[] { response, frequencies })!;
    Require(Math.Abs(original[1000] - (-32 - 10 * Math.Log10(2))) < 1e-8, "1 kHz was normalized or RMS conversion is wrong.");
    Require(original[20000] == original[10000], "Upper frequency endpoint incorrectly wraps to first point.");
    runner.PlaybackLevelDbFs -= 6;
    var quieter = (Dictionary<double, double>)method.Invoke(runner, new object[] { response, frequencies })!;
    Require(frequencies.All(f => Math.Abs(quieter[f] - original[f] + 6) < 1e-8), "Playback level loss disappeared from received dBFS.");
    Write(new { stage = "response-absolute-rms-level-check-pass" });
}

static void SweepAmplitudeChecks()
{
    foreach (double level in new[] { -60.0, -20.0, -1.0, -0.5, 0.0 })
    {
        double amplitude = Math.Pow(10, level / 20);
        var samples = StandardAcousticMeasurement.GenerateLogSweep(new LogSweepSettings(48000, 18, 22000, .5, amplitude));
        double peak = samples.Max(s => Math.Abs((double)s));
        Require(samples.All(float.IsFinite) && peak <= 1 && Math.Abs(peak / amplitude - 1) < .001,
            "Sweep either rejects UI level, clips, or silently attenuates it.");
    }
    foreach (double amplitude in new[] { 0.0, -1.0, 1.0001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        try
        {
            StandardAcousticMeasurement.GenerateLogSweep(new LogSweepSettings(48000, 18, 22000, .5, amplitude));
            throw new Exception("Invalid amplitude accepted.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Require(ex.ParamName == "Amplitude", "Wrong parameter rejected.");
            Require(AudioMeasurementErrors.Describe(ex).StartsWith("Biên độ phát không hợp lệ"), "Amplitude error was not translated.");
        }
    }
    Require(AudioMeasurementErrors.Describe(new TargetInvocationException(new ArgumentOutOfRangeException("Amplitude"))).StartsWith("Biên độ"), "Wrapped error was not translated.");
    Require(!AudioMeasurementErrors.Describe(new Exception("Unknown driver failure")).Contains("Unknown driver"), "Raw English driver message leaked to UI.");
    Write(new { stage = "sweep-amplitude-and-vietnamese-errors-pass" });
}

static void ScopeWaveformChecks()
{
    foreach (int rate in new[] { 44100, 48000 })
    foreach (double frequency in new[] { 80.0, 1000.0, 4000.0 })
    foreach (double phase in new[] { .1, 1.2, 3.4, 5.6 })
    {
        var input = Enumerable.Range(0, 4096).Select(i => (float)(.02 + .1 * Math.Sin(2 * Math.PI * frequency * i / rate + phase))).ToArray();
        var original = input.ToArray();
        var trace = ScopeWaveform.Create(input, rate, 5000 / frequency, true);
        Require(trace.Triggered, "Clean sine did not trigger.");
        int zero = Array.FindIndex(trace.TimeMs, x => x >= 0);
        Require(zero == 1 && trace.Samples[zero] > trace.Samples[zero - 1], "Scope is not aligned to a rising crossing.");
        double fraction = -trace.TimeMs[0] / (trace.TimeMs[1] - trace.TimeMs[0]);
        double crossing = trace.Samples[0] + fraction * (trace.Samples[1] - trace.Samples[0]);
        Require(Math.Abs(crossing - input.Average(s => (double)s)) < 1e-7, "Trigger phase varies with packet boundary.");
        Require(input.SequenceEqual(original), "Scope mutated raw analysis samples.");
        int source = Array.IndexOf(input, (float)trace.Samples[0]);
        Require(source >= 0 && trace.Samples.Select((sample, i) => sample == input[source + i]).All(equal => equal), "Scope synthesized or smoothed the sine.");
    }
    var distorted = Enumerable.Range(0, 4096).Select(i => (float)Math.Clamp(.7 * Math.Sin(2 * Math.PI * i / 48), -.4, .4)).ToArray();
    var distortedTrace = ScopeWaveform.Create(distorted, 48000, 5, true);
    Require(distortedTrace.Samples.Count(s => s == .4f) > 40, "Scope hid clipped peaks.");
    Require(!ScopeWaveform.Create(new float[4096], 48000, 5, true).Triggered, "Silence faked a sine trigger.");
    string wav = ".artifacts/auto-retry-cleanup/bluetooth-captures/sine-1.wav";
    if (File.Exists(wav))
    {
        using var reader = new AudioFileReader(wav);
        var data = new float[reader.WaveFormat.SampleRate];
        int count = reader.Read(data, 0, data.Length);
        int checkedFrames = 0;
        for (int offset = 0; offset + 4096 <= count; offset += 1379)
        {
            var frame = data.Skip(offset).Take(4096).ToArray();
            var trace = ScopeWaveform.Create(frame, reader.WaveFormat.SampleRate, 5, true);
            Require(trace.Triggered, "Previously captured Bluetooth sine did not trigger.");
            checkedFrames++;
        }
        Write(new { stage = "saved-bluetooth-wav-scope-check", checkedFrames });
    }
    Write(new { stage = "scope-phase-and-raw-samples-pass" });
}

static void ScopeWavChecks()
{
    string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "sine-1000Hz-minus12dBFS-RMS.wav");
    using var reader = new WaveFileReader(path);
    Require(reader.WaveFormat.SampleRate == 44100 && reader.WaveFormat.BitsPerSample == 24 && reader.WaveFormat.Channels == 1,
        "Scope WAV format changed.");
    var bytes = new byte[(int)reader.Length]; reader.ReadExactly(bytes);
    var samples = CaptureSampleDecoder.Decode(bytes, bytes.Length, reader.WaveFormat, 1, 0);
    double rms = Math.Sqrt(samples.Average(s => (double)s * s));
    Require(Math.Abs(20 * Math.Log10(rms) + 12) < .0001, "Scope WAV level is not -12 dBFS RMS.");
    var tone = AdvancedAudioMeasurement.AnalyzeTone(samples.Take(16384).ToArray(), 44100, 1000, 9);
    Require(tone.IsValid && Math.Abs(tone.FundamentalFrequencyHz - 1000) < .1 && tone.ThdPercent < .001,
        "Scope WAV is not a clean 1 kHz tone.");
    using var repeating = new RepeatingWaveFile(path);
    repeating.Position = repeating.Length - 300;
    byte[] boundary = new byte[600];
    Require(repeating.Read(boundary, 0, boundary.Length) == boundary.Length
        && boundary.Take(300).SequenceEqual(bytes.TakeLast(300))
        && boundary.Skip(300).SequenceEqual(bytes.Take(300)), "WAV repeat inserts padding or skips samples.");
    using var player = new ScopeWavPlayer();
    player.Stop(); player.Stop();
    Require(!player.IsPlaying, "Empty/stopped WAV player remains active.");
    Write(new { stage = "scope-wav-level-tone-and-repeat-pass", thdPercent = tone.ThdPercent });
}

static void FreshWindowChecks()
{
    DateTime now = DateTime.UtcNow;
    var window = new FreshCaptureWindow(16384, () => now);
    Require(!window.TryRead(4096, out _, out _), "Uninitialized scope accepted as fresh input.");
    window.Append(Enumerable.Repeat(.5f, 16800).Concat(Enumerable.Repeat(.1f, 4095)).ToArray(), 48000);
    Require(!window.TryRead(4096, out _, out _), "Partial analysis window accepted.");
    window.Append(new[] { .1f }, 48000);
    Require(window.TryRead(4096, out var samples, out int rate) && samples.All(s => s == .1f) && rate == 48000,
        "Settling samples entered the analysis.");
    window.Reset();
    Require(!window.TryRead(4096, out _, out _), "Previous tone survived reset.");
    window.Append(Enumerable.Repeat(.5f, 16800).Concat(Enumerable.Repeat(.2f, 4096)).ToArray(), 48000);
    Require(window.TryRead(4096, out samples, out _) && samples.All(s => s == .2f), "Stimulus change mixed old/new samples.");
    now = now.AddMilliseconds(501);
    Require(!window.TryRead(4096, out _, out _), "Stopped input reused an old PASS window.");
    window.Append(new[] { .3f }, 48000);
    Require(!window.TryRead(4096, out _, out _), "One packet after a dropout restored stale samples.");
    window.Append(new float[20000], 44100);
    Require(window.TryRead(4096, out samples, out rate) && rate == 44100 && samples.All(s => s == 0),
        "Sample-rate change reused data from the old clock.");
    Write(new { stage = "fresh-capture-window-checks-pass" });
}

static void ReferenceSelectionChecks()
{
    Dictionary<double, double> Curve(double offset) => new() { [20] = offset, [1000] = offset + 1, [20000] = offset - 2 };
    foreach (int outlier in new[] { 0, 1, 2 })
    {
        var curves = new[] { Curve(0), Curve(.1), Curve(.2) };
        curves[outlier] = Curve(8);
        ReferenceCurvePair selected = ReferenceCurveSelector.SelectClosestPair(curves);
        Require(selected.FirstRun != outlier + 1 && selected.SecondRun != outlier + 1, "Selected an outlier.");
        foreach (double f in curves[0].Keys)
            Require(Math.Abs(selected.AverageCurve[f] - (curves[selected.FirstRun - 1][f] + curves[selected.SecondRun - 1][f]) / 2) < 1e-12, "Wrong average.");
    }
    var mismatched = new[] { Curve(0), Curve(.1), Curve(.2) }; mismatched[1].Remove(1000);
    try { ReferenceCurveSelector.SelectClosestPair(mismatched); throw new Exception("Missing point accepted."); } catch (ArgumentException) { }
    var invalid = new[] { Curve(0), Curve(.1) }; invalid[1][1000] = double.NaN;
    try { ReferenceCurveSelector.SelectClosestPair(invalid); throw new Exception("NaN accepted."); } catch (ArgumentException) { }
    ReferenceCurvePair gainChange = ReferenceCurveSelector.SelectClosestPair(new[] { Curve(0), Curve(6) });
    Require(gainChange.MaximumDifferenceDb == 6, "Gain offset was hidden by normalization.");
    var edgeOnlyDifference = new[]
    {
        new Dictionary<double, double> { [20] = 0, [50] = 0, [1000] = 0, [18000] = 0, [20000] = 0 },
        new Dictionary<double, double> { [20] = 12, [50] = 0.2, [1000] = 0.2, [18000] = 0.2, [20000] = -12 }
    };
    ReferenceCurvePair evaluated = ReferenceCurveSelector.SelectClosestPair(edgeOnlyDifference,
        evaluationMinHz: 50, evaluationMaxHz: 18000);
    Require(Math.Abs(evaluated.MaximumDifferenceDb - 0.2) < 1e-10
        && evaluated.AverageCurve.ContainsKey(20) && evaluated.AverageCurve.ContainsKey(20000),
        "Out-of-band edge points changed the reference verdict or were discarded from the saved curve.");
    // Whole-pair choice: no pointwise cherry-picking to manufacture a flat line.
    var crossing = new[] { new Dictionary<double, double> { [20] = 0, [1000] = 9, [20000] = 0 },
        new Dictionary<double, double> { [20] = 0, [1000] = 0, [20000] = 9 },
        new Dictionary<double, double> { [20] = 9, [1000] = 0, [20000] = 0 } };
    Require(ReferenceCurveSelector.SelectClosestPair(crossing).MaximumDifferenceDb == 9, "Pair was mixed point by point.");
    Write(new { stage = "reference-selection-logic-pass" });
}
