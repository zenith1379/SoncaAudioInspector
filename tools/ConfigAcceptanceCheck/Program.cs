using System.Collections;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoncaAudioInspector;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    private static int _checks;
    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message); _checks++;
    }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception) { _checks++; return; } throw new Exception(message);
    }
    [STAThread]
    private static void Main(string[] args)
    {
        string directory = Path.GetFullPath(args[0]);
        SetDllDirectory(Path.Combine(directory, "runtimes", "win-x64", "native"));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        if (args.Length > 3 && args[2] == "--live-qa") { LiveQaUploadChecks.Run(Path.GetFullPath(args[3]), Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return; }
        if (args.Length > 3 && args[2] == "--verify-qa-receipt") { LiveQaUploadChecks.Run(Path.GetFullPath(args[3]), Path.GetFullPath(args[1]), true).GetAwaiter().GetResult(); return; }
        if (args.Length > 2 && args[2] == "--live-server") { LiveServerChecks.Run(Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return; }
        Run(directory, Path.GetFullPath(args[1]));
    }
    private static void Run(string directory, string output)
    {
        Directory.CreateDirectory(output);
        var runtime = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(Path.Combine(directory, "checking_config.json")))!;
        foreach (var model in runtime.models)
            Check(ModelConfigEditorWindow.ParseModel(model.model, JsonSerializer.Serialize(model)).testItems.InOut.Tests.Count == model.testItems.InOut.Tests.Count,
                "Runtime model failed new-config validation: " + model.model);
        var config = JsonSerializer.Deserialize<TestConfig>("""{"TestEffect":"True","TestAirLeak":true,"TestLooseParticle":"False","TestSoundLevel":"True"}""")!;
        Check(config.TestEffect && config.TestAirLeak && !config.TestLooseParticle && config.TestSoundLevel, "Boolean/string flags do not agree");
        Reject(() => JsonSerializer.Deserialize<TestConfig>("""{"TestEffect":"yes"}"""), "Invalid flag silently disabled test");
        Check(!new TestConfig().HasAdditionalTests, "Legacy config gained enabled tests");
        var localModel = new ModelConfig { model = "Local", AudioConfigUpdatedUtc = DateTimeOffset.UtcNow };
        localModel.testItems.InOut.Tests.Add(new TestConfig { id = "IO001", TestEffect = true });
        var firmwareModel = new ModelConfig { model = "Local" };
        firmwareModel.testItems.InOut.Tests.Add(new TestConfig { id = "IO001", TestEffect = false });
        var merge = typeof(SplashWindow).GetMethod("MergeDownloadedAudioConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!;
        string combined = (string)merge.Invoke(null, new object[] {
            JsonSerializer.Serialize(new CheckingConfig { models = new() { localModel } }),
            JsonSerializer.Serialize(new CheckingConfig { models = new() { firmwareModel } }) })!;
        Check(JsonSerializer.Deserialize<CheckingConfig>(combined)!.models[0].testItems.InOut.Tests[0].TestEffect, "Old firmware config overwrote newer local/shared audio criteria");
        config.ValidateAdditionalTests();
        Reject(() => new TestConfig { TestAirLeak = true, MechanicalFrequencyHz = 20 }.ValidateAdditionalTests(), "Short low-frequency tone accepted");
        Reject(() => new TestConfig { TestSoundLevel = true, SoundLevelToleranceDb = double.NaN }.ValidateAdditionalTests(), "NaN tolerance accepted");
        Reject(() => new TestConfig { TestEffect = true, RubBuzzTestFreq = 70 }.ValidateAdditionalTests(), "Effect/rub-buzz conflict accepted");
        var settings = new ImpulseEffectSettings();
        var echo = new ImpulseEffectResult(true, true, false, 150, new[] { new EchoTap(150, -20), new EchoTap(300, -30) }, -20, -60, .1, .5, .99, -90, 3, false, "ECHO", Array.Empty<string>());
        Check(ConfiguredAcousticTests.Effect(echo, settings, null).Passed, "Valid expected echo failed");
        Check(ConfiguredAcousticTests.Effect(echo with { EchoDetected = false }, settings, null).Status == "FAIL", "Missing effect passed");
        Check(ConfiguredAcousticTests.Effect(null, settings, null).Invalid, "Missing sweep passed");
        Check(ConfiguredAcousticTests.Effect(echo with { TailTruncated = true }, settings, null).Invalid, "Truncated effect passed");
        Check(ConfiguredAcousticTests.Effect(echo with { CapturedTailSeconds = .5 }, settings, null).Invalid, "Short effect passed");
        Check(ConfiguredAcousticTests.Effect(echo with { ImpulseNoiseDb = -30 }, settings, null).Invalid, "Noisy effect passed");
        Check(ConfiguredAcousticTests.Effect(echo with { EchoDelayMs = double.NaN }, settings, null).Invalid, "NaN echo passed");
        Check(ConfiguredAcousticTests.Effect(echo, settings with { ExpectedEchoDelayMs = 200 }, null).Status == "FAIL", "Delay mismatch passed");
        Check(ConfiguredAcousticTests.Effect(echo, settings with { ExpectReverb = true }, null).Status == "FAIL", "Missing reverb passed");
        Check(ConfiguredAcousticTests.Effect(echo with { EchoDetected = false }, settings with { ExpectEcho = false, ExpectReverb = false }, null).Passed, "Explicit dry expectation failed");
        var reference = ImpulseEffectReference.From(echo, settings)!;
        Check(ConfiguredAcousticTests.Effect(echo, settings, reference).Passed, "Same reference failed");
        Check(ConfiguredAcousticTests.Effect(echo, settings with { MinimumEchoLevelDb = -40 }, reference).Invalid, "Stale reference passed");
        var points = new[] { new DiagnosisResponsePoint(80, -30, -30, 3), new DiagnosisResponsePoint(1000, -20, -20, 3), new DiagnosisResponsePoint(4000, -40, -40, 3) };
        Check(ConfiguredAcousticTests.SoundLevel(points, true, true, false, double.NaN, double.NaN, 3).Passed, "Identical absolute levels failed");
        Check(ConfiguredAcousticTests.SoundLevel(points, true, true, true, -26, -20, 3).Status == "FAIL", "Normalized curve hid 6 dB loss");
        Check(ConfiguredAcousticTests.SoundLevel(points, true, true, true, double.NaN, -20, 3).Invalid, "Missing original level passed");
        Check(ConfiguredAcousticTests.SoundLevel(points, true, false, false, 0, 0, 3).Invalid, "Missing golden passed");
        Check(ConfiguredAcousticTests.SoundLevel(points, false, true, false, 0, 0, 3).Invalid, "Invalid capture passed");
        var mechanical = new MechanicalTestResult(MechanicalTestKind.LooseParticle, true, "CHƯA THẤY XUNG", "Clean", 40, -30, -80, -90, -70, 0, 0, Array.Empty<MechanicalEvent>(), Array.Empty<TimeDomainPoint>());
        Check(ConfiguredAcousticTests.Mechanical(mechanical).Passed, "Clean screening failed");
        Check(ConfiguredAcousticTests.Mechanical(mechanical with { Status = "CÓ XUNG PHI CHU KỲ" }).Status == "FAIL", "Loose particle evidence passed");
        Check(ConfiguredAcousticTests.Mechanical(mechanical with { Status = "CẦN ĐO LẠI" }).Invalid, "Ambiguous impacts passed");
        Check(ConfiguredAcousticTests.Mechanical(mechanical with { IsValid = false }).Invalid, "Invalid mechanical capture passed");
        Check(ConfiguredAcousticTests.Mechanical(mechanical with { Kind = MechanicalTestKind.AirLeak, Status = "CÓ NHIỄU ĐIỀU BIÊN" }).Status == "FAIL", "Air leak evidence passed");
        Check(ConfiguredAcousticTests.Mechanical(mechanical with { Status = "UNKNOWN" }).Invalid, "Unknown mechanical status passed");

        var app = new App(); app.InitializeComponent();
        typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { CheckEditor(output); CheckTheme(output); CheckTracking(); CheckBleTheme(output); CheckCalibration(output); }
        finally { app.Shutdown(); }
        File.WriteAllText(Path.Combine(output, "checks.txt"), $"PASS {_checks} config, acceptance and WPF checks. Synthetic/software only; no hardware tones or BLE writes.\n");
        Console.WriteLine($"PASS {_checks} config, acceptance and WPF checks");
    }
    private static T Private<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void CheckTracking()
    {
        var routing = (AudioRouting)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AudioRouting));
        var method = typeof(AudioRouting).GetMethod("RunTrackedConfiguredTestsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var disabled = new AutoTestCaseItem();
        ((Task)method.Invoke(routing, new object?[] { disabled, null, null, false })!).GetAwaiter().GetResult();
        Check(Private<Task?>(routing, "_activeMeasurementTask") == null && disabled.AdditionalResults.Count == 0, "Disabled extra tests left workflow busy");
        var invalid = new AutoTestCaseItem { Config = new TestConfig { TestEffect = true, TestAirLeak = true, TestLooseParticle = true } };
        ((Task)method.Invoke(routing, new object?[] { invalid, null, null, false })!).GetAwaiter().GetResult();
        Check(Private<Task?>(routing, "_activeMeasurementTask") == null && invalid.AdditionalResults.Count == 3 && invalid.AdditionalResults.All(r => r.Invalid), "Invalid capture did not block mechanical playback or release workflow");
    }
    private sealed class OfflineWriter : ISoncaMixerWriter
    {
        public bool IsConnected => false;
        public bool SupportsMixerControls => false;
        public IReadOnlyDictionary<ushort, ushort> MixerState => new Dictionary<ushort, ushort>();
        public Task<bool> SetOptionAsync(SoncaMixerOption option, bool enabled) => throw new Exception("UI check attempted BLE option write");
        public Task<bool> SendPacketAsync(byte[] data, string? description = null) => throw new Exception("UI check attempted BLE write");
    }
    private static void CheckBleTheme(string output)
    {
        var type = typeof(SoncaBleMixerWindow);
        var window = (Window)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(c => c.GetParameters().Length == 2).Invoke(new object[] { "UI TEST", new OfflineWriter() });
        // Detach device discovery and cleanup so this render cannot affect a live BLE session.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, type.GetMethod("SoncaBleMixerWindow_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        window.Closing -= (System.ComponentModel.CancelEventHandler)Delegate.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), window, type.GetMethod("SoncaBleMixerWindow_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!);
        ((FrameworkElement)window.FindName("PanelSettings")).Visibility = Visibility.Visible;
        var combo = (ComboBox)window.FindName("CmbDevices");
        combo.Items.Add(new ComboBoxItem { Content = "Sonca BLE — thiết bị minh họa", Foreground = Brushes.White }); combo.SelectedIndex = 0;
        window.Show(); window.UpdateLayout();
        try
        {
            var scan = (Button)window.FindName("BtnScan");
            Check(Visuals<Border>(scan).Any(b => b.Background is SolidColorBrush s && s.Color == Colors.Black), "BLE scan button did not receive black template");
            Check(Visuals<ContentPresenter>(scan).Any(c => TextBlock.GetForeground(c) is SolidColorBrush s && s.Color == Colors.White), "BLE scan button text is not white");
            combo.IsDropDownOpen = true; window.UpdateLayout();
            var item = (ComboBoxItem)combo.Items[0]; item.ApplyTemplate();
            Check(Visuals<Border>(item).Any(b => b.Background is SolidColorBrush s && s.Color == Colors.Black), "BLE dropdown did not receive black template");
            Render(window, Path.Combine(output, "ble-mixer-settings.png")); combo.IsDropDownOpen = false;
            var preset = ((Button)window.FindName("BtnPreset")).ContextMenu!; preset.PlacementTarget = window; preset.IsOpen = true; window.UpdateLayout();
            Check(Visuals<Border>(preset).Any(b => b.Background is SolidColorBrush s && s.Color == Colors.Black), "BLE preset menu did not receive black template");
            preset.IsOpen = false;
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    }
    private static object Invoke(object obj, string name) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, null)!;
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t;
            foreach (var nested in Visuals<T>(child)) yield return nested;
        }
    }
    private static void Render(Window window, string path)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void CheckEditor(string output)
    {
        var model = JsonSerializer.Deserialize<ModelConfig>("""{"model":"Demo","customModel":42,"testItems":{"InOut":{"Devices":{"Input":{"OUT1":"Speaker"},"Output":{"MIC2":"FastTrack"}},"Tests":[{"id":"IO001","name":"Loa trái","Playback Out":"OUT1","Recording In":"MIC2","Recording Channel":2,"customTest":"keep","TestEffect":"False"},{"id":"IO002","name":"Loa phải","Playback Out":"OUT1","Recording In":"MIC2"}]}}}""")!;
        var window = new ModelConfigEditorWindow(model); window.Show(); window.UpdateLayout();
        try
        {
            Render(window, Path.Combine(output, "config-routes.png"));
            var tabs = Private<TabControl>(window, "_tabs"); Check(tabs.Items.Count == 2, "Redundant devices tab remains"); tabs.SelectedIndex = 0; window.UpdateLayout();
            var sections = Visuals<TabControl>(window).First(t => t != tabs); sections.SelectedIndex = 1; window.UpdateLayout();
            var effect = Visuals<CheckBox>(window).Single(c => c.Content.ToString()!.StartsWith("TestEffect")); effect.IsChecked = true;
            var air = Visuals<CheckBox>(window).Single(c => c.Content.ToString()!.StartsWith("TestAirLeak")); air.IsChecked = true;
            Check(!model.testItems.InOut.Tests[0].TestEffect && !model.testItems.InOut.Tests[0].TestAirLeak, "Form mutated original before Save");
            Render(window, Path.Combine(output, "config-test-switches.png"));
            tabs.SelectedIndex = 1; window.UpdateLayout();
            var json = Private<TextBox>(window, "_json");
            Check(json.Text.Contains("\"TestEffect\": true") && json.Text.Contains("customTest"), "Form-to-JSON lost flags or custom field");
            json.Text = json.Text.Replace("\"TestLooseParticle\": false", "\"TestLooseParticle\": true");
            tabs.SelectedIndex = 0; window.UpdateLayout();
            var saved = (ModelConfig)Invoke(window, "ReadEditorModel");
            Check(saved.testItems.InOut.Tests[0].TestLooseParticle && saved.testItems.InOut.Tests[0].TestEffect, "JSON-to-form/save lost changes");
            Check(saved.Extra!["customModel"].GetInt32() == 42 && saved.testItems.InOut.Tests[0].Extra!["customTest"].GetString() == "keep", "Unknown fields lost on round trip");
            Check(saved.testItems.InOut.Tests[0].RecordingChannel == 2, "Channel changed on form save");
            sections = Visuals<TabControl>(window).First(t => t != tabs); sections.SelectedIndex = 2; window.UpdateLayout();
            var number = Visuals<TextBox>(window).Single(t => t.ToolTip?.ToString() == "Tần số tone (Hz)"); number.Text = "nonsense";
            Reject(() => Invoke(window, "ReadEditorModel"), "Bad numeric input silently kept old value");
            number.Text = "40,5";
            saved = (ModelConfig)Invoke(window, "ReadEditorModel");
            Check(saved.testItems.InOut.Tests[0].MechanicalFrequencyHz == 40.5, "Vietnamese decimal comma rejected");
            window.Width = 820; window.Height = 600; sections.SelectedIndex = 1; window.UpdateLayout();
            Render(window, Path.Combine(output, "config-minimum-size.png"));
            tabs.SelectedIndex = 1; json.Text = "{bad json"; tabs.SelectedIndex = 0;
            Check(tabs.SelectedIndex == 0 && json.Text == "{bad json", "Invalid JSON trapped navigation or was silently discarded");
            Reject(() => Invoke(window, "ReadEditorModel"), "Saving invalid pending JSON succeeded");
            json.Text = JsonSerializer.Serialize(saved, ModelConfigEditorWindow.JsonOptions); Invoke(window, "ApplyJson");
            for (int i = 0; i < 6; i++) { tabs.SelectedIndex = 1; tabs.SelectedIndex = 0; }
            Check(tabs.SelectedIndex == 0, "Repeated JSON navigation failed");
            int localSaves = 0, publishes = 0;
            window.SaveModel = _ => localSaves++;
            window.PublishModel = candidate => { publishes++; return Task.FromResult(candidate); };
            var publish = Visuals<Button>(window).Single(b => b.Name == "PublishModel");
            publish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(localSaves == 1 && publishes == 1 && publish.IsEnabled, "Publish button did not save, publish, and restore controls");
            window.PublishModel = _ => Task.FromException<ModelConfig>(new IOException("Offline test"));
            publish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(localSaves == 2 && publish.IsEnabled && Private<TextBlock>(window, "_error").Text.Contains("Offline test"), "Publish failure lost local save or froze controls");
        }
        finally { window.Close(); }
        Check(!model.testItems.InOut.Tests[0].TestEffect, "Cancel leaked draft edits");
        var fresh = new ModelConfigEditorWindow(null);
        Private<TextBox>(fresh, "_name").Text = "New model";
        try { Check(((ModelConfig)Invoke(fresh, "ReadEditorModel")).testItems.InOut.Tests.Count == 1, "New model defaults cannot be saved"); }
        finally { fresh.Close(); }
        var c360 = C360ModelDefinition.Create();
        var acousticWindow = new ModelConfigEditorWindow(c360);
        acousticWindow.Show(); acousticWindow.UpdateLayout();
        try
        {
            var list = Private<ListBox>(acousticWindow, "_testList");
            var draft = Private<ModelConfig>(acousticWindow, "_draft");
            list.SelectedItem = draft.testItems.InOut.Tests.Single(t => t.SpeakerStage == "AcousticMic");
            acousticWindow.UpdateLayout();
            Check(Visuals<Button>(acousticWindow).Any(b => b.Content?.ToString() == "Chọn file calibration…"), "Per-test calibration picker missing");
            Check(Visuals<CheckBox>(acousticWindow).Any(b => b.Content?.ToString()?.Contains("Đã đo chỉnh bằng mic thật") == true && b.IsChecked == false), "Provisional microphone setup is not visible");
            var savedDraft = (ModelConfig)Invoke(acousticWindow, "ReadEditorModel");
            Check(savedDraft.testItems.InOut.Tests.Single(t => t.SpeakerStage == "AcousticMic") is { AcousticSetupPending: true, IsEnabled: false }, "Saving provisional settings enabled the real-mic test");
            Render(acousticWindow, Path.Combine(output, "c360-acoustic-draft.png"));
        }
        finally { acousticWindow.Close(); }
    }
    private static void CheckCalibration(string output)
    {
        string data = Path.Combine(output, "calibration-data-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(data);
        string original = Path.Combine(data, "source.txt"); File.WriteAllText(original, "20 -2\n1000 0\n20000 2\n");
        var library = new MicrophoneCalibrationLibrary(data);
        var first = library.Import(original, "mic-a"); File.Delete(original);
        var reopened = new MicrophoneCalibrationLibrary(data);
        Check(reopened.Resolve("mic-a", "FastTrack")?.Points.Count == 3, "Calibration did not survive restart/deleted source file");
        Check(reopened.Selection("mic-b") == MicrophoneCalibrationLibrary.Auto, "Calibration leaked to another input");
        File.WriteAllText(original, "20 -3\n1000 0\n20000 3\n");
        var second = reopened.Import(original, "mic-a");
        Check(first.FileName != second.FileName && reopened.Files.Count == 2, "New calibration overwrote previous file");
        reopened.Import(original, "mic-b");
        Check(reopened.Files.Count == 2, "Duplicate content created another saved file");
        reopened.Select("mic-a", first.FileName);
        Check(new MicrophoneCalibrationLibrary(data).Selection("mic-a") == first.FileName, "Selecting old calibration was not persisted");
        reopened.Select("mic-b", MicrophoneCalibrationLibrary.None);
        Check(new MicrophoneCalibrationLibrary(data).Resolve("mic-b", "KT USB") == null, "Explicit no-calibration reverted to automatic");
        File.WriteAllText(original, "20 NaN\n1000 0\n");
        Reject(() => reopened.Import(original, "mic-a"), "Non-finite microphone correction accepted");
        string savedPath = Path.Combine(data, "microphone-calibrations", first.FileName);
        byte[] backup = File.ReadAllBytes(savedPath); File.WriteAllText(savedPath, "tampered");
        Reject(() => new MicrophoneCalibrationLibrary(data).Resolve("mic-a", "FastTrack"), "Corrupted saved calibration silently ignored");
        File.WriteAllBytes(savedPath, backup);
        string? previous = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", data);
        var routing = new AudioRouting();
        routing.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), routing, typeof(AudioRouting).GetMethod("AudioRouting_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        routing.Unloaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), routing, typeof(AudioRouting).GetMethod("AudioRouting_Unloaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        var window = new Window { Width = 1200, Height = 780, Content = routing };
        try
        {
            Invoke(routing, "RefreshMicrophoneCalibrationSelection"); window.Show(); window.UpdateLayout();
            var setup = (FrameworkElement)routing.FindName("PanelSetup");
            var calibration = (Button)routing.FindName("BtnImportMicrophoneCalibration");
            Check(setup.Visibility == Visibility.Collapsed && !calibration.IsVisible && routing.FindName("ComboMicrophoneCalibration") == null,
                "Calibration should have one file button inside input setup, without an external selector");
            setup.Visibility = Visibility.Visible;
            ((ColumnDefinition)routing.FindName("SetupColumn")).Width = new GridLength(450);
            window.UpdateLayout();
            Check(calibration.IsVisible && calibration.ContextMenu?.Items.Count == 1, "Input calibration button or clear selection command missing");
            Render(window, Path.Combine(output, "input-microphone-calibration.png"));
        }
        finally { window.Close(); Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", previous); }
    }
    private static void CheckTheme(string output)
    {
        // Exercise the same dictionary used by the BLE mixer without opening a BLE connection.
        var window = new Window { Width = 480, Height = 220, Background = Brushes.Black };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/ConfigTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(20) }; window.Content = panel;
        var button = new Button { Content = "Kết nối", Background = Brushes.Green, Foreground = Brushes.Red };
        var combo = new ComboBox { ItemsSource = new[] { "Loa Sonca", "Thiết bị kiểm tra" }, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(button); panel.Children.Add(combo); window.Show(); window.UpdateLayout(); combo.IsDropDownOpen = true; window.UpdateLayout();
        try
        {
            Check(Visuals<Border>(button).Any(b => b.Background is SolidColorBrush s && s.Color == Colors.Black), "Button local color overrides black template");
            Check(Visuals<ContentPresenter>(button).Any(c => TextBlock.GetForeground(c) is SolidColorBrush s && s.Color == Colors.White), "Button text is not white");
            Check(combo.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem item && item.Foreground is SolidColorBrush s && s.Color == Colors.White, "Dropdown item text is not white");
            Render(window, Path.Combine(output, "black-white-controls.png"));
        }
        finally { combo.IsDropDownOpen = false; window.Close(); }
    }
}
