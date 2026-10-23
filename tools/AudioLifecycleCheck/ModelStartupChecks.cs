using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class ModelStartupChecks
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void AudioSessions()
    {
        var windowsMeter = new ExternalAudioSession("FastTrack", "thu", "SystemSettings", 10, true);
        var windowsTone = new ExternalAudioSession("FastTrack", "phát", "SystemSettings", 10, true);
        var recorder = new ExternalAudioSession("FastTrack", "thu", "REW", 20, false);
        var secondInstance = new ExternalAudioSession("FastTrack", "thu", "SoncaAudioInspector", 30, false);
        var quietSystemSound = new ExternalAudioSession("FastTrack", "phát", "System", 0, false, true);
        var audibleSystemSound = quietSystemSound with { SignalDetected = true };
        var report = new AudioUsageReport(DateTime.Now,
            new[] { windowsMeter, windowsTone, recorder, secondInstance, quietSystemSound, audibleSystemSound }, Array.Empty<string>());
        Require(!windowsMeter.RequiresAttention && report.Details.Contains("Windows Settings"),
            "Windows input meter became a blocking warning or disappeared from diagnostics.");
        Require(windowsTone.RequiresAttention && recorder.RequiresAttention && secondInstance.RequiresAttention,
            "A real playback/recording app or a second Sonca instance was hidden.");
        Require(report.AttentionSessions.Count == 4 && !report.WarningDetails.Contains("kiểm tra mức thu"),
            "Informational sessions leaked into the warning.");
        Require(!quietSystemSound.RequiresAttention && audibleSystemSound.RequiresAttention,
            "System sound policy did not distinguish a silent session from playback data.");
        Require(!windowsMeter.Description.Contains("có dữ liệu phát"), "Microphone noise was described as another app emitting sound.");
        Require(AudioSessionDiagnostics.IsDeviceInUse(new System.Runtime.InteropServices.COMException("Busy", unchecked((int)0x8889000A))),
            "Actual exclusive-use errors were suppressed.");

        // Read-only live scans: an empty scope must inspect no endpoints, and a
        // one-endpoint scope must never include sessions from unrelated outputs.
        var empty = AudioSessionDiagnostics.Scan(new HashSet<string>());
        Require(empty.Sessions.Count == 0 && empty.UnreadableDevices.Count == 0, "Empty model scope scanned unrelated endpoints.");
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToList();
        try
        {
            foreach (var device in devices)
            {
                var scoped = AudioSessionDiagnostics.Scan(new HashSet<string> { device.ID });
                Require(scoped.Sessions.All(s => s.DeviceId == device.ID && s.ProcessId != Environment.ProcessId),
                    "Model diagnostics included another endpoint or its own process.");
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "audio-session-scope-and-warning-policy-pass", activeEndpoints = devices.Count }));
    }

    public static void Run(string testDirectory, string dependencyDirectory)
    {
        Directory.CreateDirectory(testDirectory);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", testDirectory);
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(dependencyDirectory, name.Name + ".dll");
            return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var config = new CheckingConfig
        {
            models = new List<ModelConfig>
            {
                new() { model = "MI SAM", testItems = new TestItems { InOut = new InOutConfig
                {
                    Devices = new DevicesConfig
                    {
                        Input = new Dictionary<string, string> { ["Test out"] = "__missing_model_playback__" },
                        Output = new Dictionary<string, string> { ["Test in"] = "__missing_model_capture__" }
                    }
                } } }
            }
        };
        AtomicFile.WriteAllText(Path.Combine(testDirectory, "checking_config.json"), JsonSerializer.Serialize(config));
        AtomicFile.WriteAllText(Path.Combine(testDirectory, "routing_value.json"), JsonSerializer.Serialize(new AppConfig { HeadroomEnabled = false }));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                window = new MainWindow();
                var combo = (ComboBox)window.FindName("ComboModels");
                var routing = (AudioRouting)typeof(MainWindow).GetField("_audioRoutingView", Instance)!.GetValue(window)!;
                object? Field(string name) => typeof(MainWindow).GetField(name, Instance)!.GetValue(window);
                var autoTest = (Button)routing.FindName("BtnStartAutoTest");
                Require(combo.Items.Count == 2 && combo.Items.Contains("C360") && combo.SelectedIndex == -1 && !autoTest.IsEnabled,
                    "Startup selected a model or enabled Auto Test before operator selection.");
                Require(Field("_pendingDeviceConfig") == null && Field("_selectedModelDeviceConfig") == null
                    && Field("_deviceConnectionWindow") == null && !(bool)Field("_audioUsageChecked")!,
                    "Startup queued a model popup or inspected competing sessions.");
                string? manualOut = routing.SelectedPlaybackDevice?.ID;
                string? manualIn = routing.SelectedRecordingDevice?.ID;
                combo.SelectedIndex = 0;
                Require(Field("_pendingDeviceConfig") is InOutConfig && Field("_selectedModelDeviceName")?.ToString() == "MI SAM",
                    "Explicit model selection did not validate and queue the missing-device popup.");
                string missing = Field("_pendingDeviceMissing")?.ToString() ?? "";
                Require(missing.Contains("__missing_model_playback__") && missing.Contains("__missing_model_capture__"),
                    "Model popup does not identify both configured I/O endpoints.");
                Require(routing.SelectedPlaybackDevice?.ID == manualOut && routing.SelectedRecordingDevice?.ID == manualIn,
                    "Model validation replaced the operator's manual route.");
                combo.SelectedIndex = -1;
                Require(Field("_pendingDeviceConfig") == null && Field("_selectedModelDeviceConfig") == null && !autoTest.IsEnabled,
                    "Clearing model selection left a pending popup or an active Auto Test model.");
                typeof(MainWindow).GetMethod("ShowDeviceConnectionWindow", Instance)!
                    .Invoke(window, new object?[] { "MI SAM", config.models[0].testItems.InOut, "stale check" });
                Require(Field("_pendingDeviceConfig") == null, "A stale check queued a popup after the model was cleared.");
                combo.SelectedIndex = 0;
                Require(Field("_pendingDeviceConfig") is InOutConfig, "Reselecting a model no longer validates devices.");
                combo.SelectedIndex = -1;

                using var enumerator = new MMDeviceEnumerator();
                var playback = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                try
                {
                    // Analog 1/2 is a physical-connector alias, including after a
                    // USB port change. Scope must follow the route resolver rather
                    // than scanning every output with a generic FastTrack name.
                    var alias = new InOutConfig { Devices = new DevicesConfig
                    {
                        Input = new Dictionary<string, string> { ["Analog 1/2"] = "Analog Connector (old USB instance)" }
                    } };
                    var expected = (MMDevice?)typeof(AudioRouting).GetMethod("ResolvePlaybackDevice", Instance)!
                        .Invoke(routing, new object[] { "Analog 1/2", alias.Devices.Input["Analog 1/2"], playback, true });
                    var ids = routing.GetModelDeviceIds(alias);
                    Require(ids.SetEquals(expected == null ? Array.Empty<string>() : new[] { expected.ID }),
                        "Audio-session scope ignored the recovered physical 1/2 connector or included another output.");
                }
                finally { foreach (var device in playback) device.Dispose(); }
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "model-startup-selection-popup-guard-pass", manualRoutePreserved = true, missingPlaybackAndCapture = true }));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (window != null) typeof(MainWindow).GetMethod("OnClosed", Instance)!.Invoke(window, new object[] { EventArgs.Empty });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("Model startup check failed.", failure);
    }
}
