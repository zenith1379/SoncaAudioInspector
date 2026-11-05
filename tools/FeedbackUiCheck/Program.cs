using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string directory = Path.GetFullPath(args[0]);
        if (args.Contains("--brief-phase=P6"))
        {
            // P6 adds no app UI. Decode the ALC artifact before any App startup.
            try
            {
                string output = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--output="))?.Split('=', 2)[1] ?? Path.Combine(directory, "p6-ui-checks"));
                string source = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--p6-output="))?.Split('=', 2)[1] ?? Path.Combine(directory, "..", "..", "checks", "alc-P6"));
                string png = Path.Combine(source, "io001-gate-vs-io003-comparison.png");
                using var stream = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.Single();
                if (frame.PixelWidth < 900 || frame.PixelHeight < 500 || stream.Length == 0) throw new InvalidDataException("P6 PNG dimensions/size invalid.");
                var pixels = new byte[frame.PixelHeight * frame.PixelWidth * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
                if (pixels.Distinct().Count() < 16) throw new InvalidDataException("P6 PNG is blank.");
                foreach (string name in new[] { "io001-gate-vs-io003-summary.csv", "io001-gate-vs-io003-curves.csv", "qualification-template.vi.md", "checks.txt" })
                    if (!File.Exists(Path.Combine(source, name)) || new FileInfo(Path.Combine(source, name)).Length == 0) throw new InvalidDataException("P6 output missing: " + name);
                string artifactRoot = source;
                while (!File.Exists(Path.Combine(artifactRoot, "artifact.json")) && Path.GetDirectoryName(artifactRoot) is string parent) artifactRoot = parent;
                if (!File.Exists(Path.Combine(artifactRoot, "artifact.json")) || !output.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("P6 UI output must be within the same artifact.");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "P6-ui-check.txt"), $"PASS P6: decoded nonblank {frame.PixelWidth}x{frame.PixelHeight} PNG and required ALC outputs. No App/UI behavior changed.\r\n");
                Console.WriteLine($"PASS P6 UI artifact check: {frame.PixelWidth}x{frame.PixelHeight}; no App instance created.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--brief-phase=P1") || args.Contains("--brief-phase=P2") || args.Contains("--brief-phase=P3") || args.Contains("--brief-phase=P4") || args.Contains("--brief-phase=P5"))
            System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(directory, "runtimes", "win-x64", "native", "libSkiaSharp.dll"));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "SoncaAudioInspector.dll"));
        var app = (Application)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.App")!)!;
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        var startupUri = app.StartupUri;
        // A UI check may pump rendering but must not launch the app's splash/login
        // workflow or turn a dispatcher error into a modal warning that hides failure.
        typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (args.Contains("--brief-phase=P1"))
        {
            string output = args.FirstOrDefault(a => a.StartsWith("--output="))?.Split('=', 2)[1]
                ?? (args.Length > 2 ? args[2] : Path.Combine(directory, "p1-ui-checks"));
            BriefP1UiChecks.Run(assembly, Path.GetFullPath(output)); app.Shutdown(); return;
        }
        if (args.Contains("--brief-phase=P2"))
        {
            string output = args.FirstOrDefault(a => a.StartsWith("--output="))?.Split('=', 2)[1] ?? Path.Combine(directory, "p2-ui-checks");
            BriefP2UiChecks.Run(assembly, Path.GetFullPath(output)); app.Shutdown(); return;
        }
        if (args.Contains("--brief-phase=P3"))
        {
            string output = args.FirstOrDefault(a => a.StartsWith("--output="))?.Split('=', 2)[1] ?? Path.Combine(directory, "p3-ui-checks");
            string? screenshotDir = args.FirstOrDefault(a => a.StartsWith("--screenshot-dir="))?.Split('=', 2)[1];
            BriefP3UiChecks.Run(assembly, Path.GetFullPath(output), screenshotDir != null ? Path.GetFullPath(screenshotDir) : null);
            app.Shutdown(); return;
        }
        if (args.Contains("--brief-phase=P4"))
        {
            string output = args.FirstOrDefault(a => a.StartsWith("--output="))?.Split('=', 2)[1] ?? Path.Combine(directory, "p4-ui-checks");
            string? screenshotDir = args.FirstOrDefault(a => a.StartsWith("--screenshot-dir="))?.Split('=', 2)[1];
            BriefP4UiChecks.Run(assembly, Path.GetFullPath(output), screenshotDir != null ? Path.GetFullPath(screenshotDir) : null);
            app.Shutdown(); return;
        }
        if (args.Contains("--brief-phase=P5"))
        {
            string output = args.FirstOrDefault(a => a.StartsWith("--output="))?.Split('=', 2)[1] ?? Path.Combine(directory, "p5-ui-checks");
            string? screenshotDir = args.FirstOrDefault(a => a.StartsWith("--screenshot-dir="))?.Split('=', 2)[1];
            try { BriefP5UiChecks.Run(assembly, Path.GetFullPath(output), screenshotDir != null ? Path.GetFullPath(screenshotDir) : null); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            app.Shutdown(); return;
        }
        if (args.Any(a => a.StartsWith("--brief-phase="))) throw new ArgumentException("Chỉ hỗ trợ --brief-phase=P1, P2, P3, P4, P5 hoặc P6.");
        if (args.Length > 1 && args[1] == "--source-layout-ui")
        { SourceLayoutUiChecks.Run(assembly, startupUri, Path.GetFullPath(args[2])); app.Shutdown(); return; }
        if(args.Length>1 && args[1]=="--ble-bluetooth-audio-live")
        {BleBluetoothAudioLiveChecks.Run(assembly,Path.GetFullPath(args[2]),Convert.ToUInt64(args[3],16));app.Shutdown();return;}
        if(args.Length>1 && args[1]=="--ble-bluetooth-audio-ui")
        {BleBluetoothAudioUiChecks.Run(assembly,Path.GetFullPath(args[2]));app.Shutdown();return;}
        if(args.Length>1 && args[1]=="--ble-model-names-ui")
        {BleModelNamesUiChecks.Run(assembly,Path.GetFullPath(args[2]));app.Shutdown();return;}
        if(args.Length>1 && args[1]=="--response-db-labels")
        {ResponseDbLabelsChecks.Run(assembly,Path.GetFullPath(args[2]));app.Shutdown();return;}
        if (args.Length > 1 && args[1] == "--impedance-ui")
        { ImpedanceUiChecks.Run(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return; }
        if (args.Length > 1 && args[1] == "--c360-model-ui")
        {
            try { C360ModelUiChecks.Run(assembly, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), args.Contains("--live"), args.Contains("--reference"), args.FirstOrDefault(a => a.StartsWith("--model-config="))?.Split('=',2)[1], args.FirstOrDefault(a => a.StartsWith("--test-id="))?.Split('=',2)[1], args.Contains("--run-button"), args.FirstOrDefault(a=>a.StartsWith("--ble-address="))?.Split('=',2)[1], args.FirstOrDefault(a=>a.StartsWith("--reference-dir="))?.Split('=',2)[1],args.Contains("--allow-nonstandard-rate")); }
            finally
            {
                var manager = assembly.GetType("SoncaAudioInspector.SoncaBleManager")!.GetProperty("Instance")!.GetValue(null)!;
                manager.GetType().GetMethod("Disconnect")!.Invoke(manager, null);
                app.Shutdown();
            }
            return;
        }
        if (args.Length > 1 && args[1] == "--ble-options-ui")
        {
            BleOptionsUiChecks.Run(assembly); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--measurement-cancellation-ui")
        {
            MeasurementCancellationUiChecks.Run(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--signal-diagnostics-ui")
        {
            SignalDiagnosticUiChecks.Run(assembly, Path.GetFullPath(args[2]), Path.GetFullPath(args[3])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--mechanical-ui")
        {
            MechanicalUiChecks.Run(assembly, Path.GetFullPath(args[2]), Path.GetFullPath(args[3])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--measurement-diagnosis-ui")
        {
            MeasurementDiagnosisUiChecks.Run(assembly, Path.GetFullPath(args[2]), Path.GetFullPath(args[3])); app.Shutdown(); return;
        }
        app.Dispatcher.UnhandledExceptionFilter += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            e.RequestCatch = false;
        };
        if (args.Length > 1 && args[1] == "--sample-rate-refresh-ui")
        {
            SampleRateRefreshUiChecks.Run(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--dense-render-check")
        {
            ImpulsePerformanceUiChecks.CheckDenseRendering(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && (args[1] == "--impulse-view-only" || args[1] == "--impulse-performance-only"))
        {
            var options = new System.Text.Json.JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
            object result = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(args[3]), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, options)!;
            if (args[1] == "--impulse-performance-only") ImpulsePerformanceUiChecks.Run(assembly, result, Path.GetFullPath(args[2]));
            else ImpulseGraphUiChecks.Check(assembly, result, Path.GetFullPath(args[2]));
            app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--ble-startup-performance")
        {
            BleMixerUiChecks.Performance(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--ble-mixer-ui")
        {
            BleMixerUiChecks.Run(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--ble-mixer-live-ui")
        {
            BleMixerUiChecks.Live(assembly, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--manual-line-ui")
        {
            ManualLineUiChecks.Run(assembly, directory, Path.GetFullPath(args[2])); app.Shutdown(); return;
        }
        if (args.Length > 1 && args[1] == "--octave-effects-ui")
        {
            OctaveEffectsUiChecks.Run(assembly, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--rew-ui-only")
        {
            RewUiChecks.Run(assembly, Path.GetFullPath(args[2]));
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--model-toggle-only")
        {
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var mainType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
            var window = (Window)Activator.CreateInstance(mainType)!;
            app.MainWindow = window;
            var models = (ComboBox)window.FindName("ComboModels");
            if (models.SelectedItem == null)
                throw new Exception("Startup model was not available for the toggle check.");
            models.SelectedIndex = -1;
            if (models.SelectedItem != null)
                throw new Exception("Clicking the selected model cannot clear the selection.");
            var toggleRouting = (UserControl)((ContentControl)window.FindName("MainContentArea")).Content;
            if (((Button)toggleRouting.FindName("BtnStartAutoTest")).IsEnabled)
                throw new Exception("Auto Test remains enabled after clearing Model.");
            if (mainType.GetField("_selectedModelDeviceConfig", privateInstance)!.GetValue(window) != null)
                throw new Exception("Device monitoring remains active with an empty Model.");
            var caseType = assembly.GetType("SoncaAudioInspector.AutoTestCaseItem")!;
            foreach (string property in new[] { "ResponseFrequencies", "ResponseLevels", "DistortionMeasurements", "ResponseLevelUnit" })
                if (caseType.GetProperty(property) == null)
                    throw new Exception($"Auto Test result preview is missing {property}.");
            if (caseType.GetProperty("DistortionPreviewPath") != null)
                throw new Exception("Auto Test still stores a distortion image path.");
            Console.WriteLine("PASS clearing Model disables Auto Test/device monitoring and completed cases carry inline response/distortion data without distortion images.");
            Environment.Exit(0);
        }
        if (args.Length > 1 && args[1] == "--auto-resume-only")
        {
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var mainType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
            var window = (Window)Activator.CreateInstance(mainType)!;
            app.MainWindow = window;
            var resumeRouting = (UserControl)((ContentControl)window.FindName("MainContentArea")).Content;
            var resumeRoutingType = resumeRouting.GetType();
            ((TextBox)window.FindName("TxtSerialNumber")).Text = "RESUME_CHECK";
            ((TextBox)resumeRouting.FindName("TxtFreqTolerance")).Text = "3.0";
            var cases = (System.Collections.IList)resumeRoutingType.GetField("_autoTestCases", privateInstance)!.GetValue(resumeRouting)!;
            cases.Clear();
            var caseType = assembly.GetType("SoncaAudioInspector.AutoTestCaseItem")!;
            Array caseArray = Array.CreateInstance(caseType, 3);
            for (int i = 0; i < 3; i++)
            {
                object test = Activator.CreateInstance(caseType)!;
                caseType.GetProperty("Status")!.SetValue(test, i == 0 ? "PASS" : "WAITING");
                cases.Add(test);
                caseArray.SetValue(test, i);
            }
            string folder = Path.Combine(Path.GetTempPath(), "sonca-auto-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string passGraph = Path.Combine(folder, "FRA-AC001.png");
            string failedGraph = Path.Combine(folder, "FRA-AC002.png");
            try
            {
                File.WriteAllBytes(passGraph, new byte[] { 1 });
                File.WriteAllBytes(failedGraph, new byte[] { 2 });
                var paths = (List<string>)resumeRoutingType.GetField("_autoTestGraphPaths", privateInstance)!.GetValue(resumeRouting)!;
                paths.Add(passGraph);
                paths.Add(failedGraph);
                var resumeType = resumeRoutingType.GetNestedType("AutoTestResumeSession", BindingFlags.NonPublic)!;
                object session = resumeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single(ctor => ctor.GetParameters().Length == 5)
                    .Invoke(new object[] { "MI SAM", "RESUME_CHECK", folder, caseArray, 3.0 });
                resumeRoutingType.GetField("_autoTestResumeSession", privateInstance)!.SetValue(resumeRouting, session);
                resumeRoutingType.GetField("_autoTestSessionFolder", privateInstance)!.SetValue(resumeRouting, folder);
                var preserve = resumeRoutingType.GetMethod("PreservePassedAutoTestsForResume", privateInstance)!;
                var index = resumeRoutingType.GetMethod("GetAutoTestResumeIndex", privateInstance)!;
                if (!(bool)preserve.Invoke(resumeRouting, null)! || paths.Count != 1 || paths[0] != passGraph
                    || (int)index.Invoke(resumeRouting, new object[] { "MI SAM" })! != 1)
                    throw new Exception("Auto Test did not retain one PASS and resume at the interrupted case.");
                if ((int)index.Invoke(resumeRouting, new object[] { "OTHER MODEL" })! != 0)
                    throw new Exception("Auto Test reused a PASS after changing model.");
                File.Delete(passGraph);
                if ((int)index.Invoke(resumeRouting, new object[] { "MI SAM" })! != 0)
                    throw new Exception("Auto Test skipped a PASS whose graph evidence is missing.");
            }
            finally
            {
                if (File.Exists(passGraph)) File.Delete(passGraph);
                if (File.Exists(failedGraph)) File.Delete(failedGraph);
                Directory.Delete(folder);
            }
            Console.WriteLine("PASS Auto Test resumes at the interrupted case and retains only completed PASS evidence.");
            Environment.Exit(0);
        }
        if (args.Length > 1 && args[1] == "--audio-routing-only")
        {
            const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
            var audioOnlyWindowType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
            var window = (Window)Activator.CreateInstance(audioOnlyWindowType)!;
            app.MainWindow = window;
            if (window.Title != "Sonca Audio Routing")
                throw new Exception("Main window is not branded as the Audio Routing build.");
            if (window.FindName("BtnTabStandardMeasurement") != null || window.FindName("BtnTabVisualAI") != null)
                throw new Exception("A removed module tab is still present in the main window.");
            if (window.FindName("BtnScanQr") is not Button qrButton || qrButton.Visibility != Visibility.Collapsed)
                throw new Exception("The legacy QR entry point is exposed.");
            if (audioOnlyWindowType.GetField("_visualAIView", instanceFields) != null)
                throw new Exception("Visual AI is still compiled into the Audio Routing shell.");
            if (audioOnlyWindowType.GetField("_standardMeasurementView", instanceFields)!.GetValue(window) != null
                || audioOnlyWindowType.GetField("_qrScanView", instanceFields)!.GetValue(window) != null)
                throw new Exception("A removed module was initialized by the Audio Routing build.");
            var content = ((ContentControl)window.FindName("MainContentArea")).Content as FrameworkElement;
            if (content?.GetType().FullName != "SoncaAudioInspector.AudioRouting")
                throw new Exception("Main content is not Audio Routing.");
            if (content.FindName("BtnStartAutoTest") is not Button)
                throw new Exception("Auto Test was removed from Audio Routing.");
            Console.WriteLine("PASS only Audio Routing is initialized and Auto Test remains available.");
            Environment.Exit(0);
        }
        if (args.Length > 1 && args[1] == "--model-config-only")
        {
            var singleModelWindowType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
            using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "checking_config.json")));
            var models = config.RootElement.GetProperty("models");
            if (models.GetArrayLength() != 1 || models[0].GetProperty("model").GetString() != "MI SAM")
                throw new Exception("Packaged configuration does not contain exactly one MI SAM model.");
            var isSupported = singleModelWindowType.GetMethod("IsSupportedModel", BindingFlags.Static | BindingFlags.NonPublic)!;
            if (!(bool)isSupported.Invoke(null, new object?[] { "MI SAM" })!
                || !(bool)isSupported.Invoke(null, new object?[] { "D'AURIS 500" })!
                || !(bool)isSupported.Invoke(null, new object?[] { "CARRY ON" })!)
                throw new Exception("Configured models cannot enter the model selector.");
            Console.WriteLine("PASS packaged default MI SAM is preserved and additional configured models are supported.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--upload-identity-only")
        {
            const BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
            var uploadRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
            var productType = assembly.GetType("SoncaAudioInspector.ProductInfo")!;
            var serverType = assembly.GetType("SoncaAudioInspector.ServerEngine")!;
            object oldProduct = Activator.CreateInstance(productType)!;
            productType.GetProperty("Id")!.SetValue(oldProduct, "DB-ID-OLD");
            productType.GetProperty("ProductCode")!.SetValue(oldProduct, "MISAM_280926_001");
            productType.GetProperty("SerialNumber")!.SetValue(oldProduct, "SERIAL-OLD");
            var matches = uploadRoutingType.GetMethod("ProductMatchesUploadIdentity", privateStatic)!;
            if (!(bool)matches.Invoke(null, new[] { oldProduct, "misam_280926_001" })!
                || (bool)matches.Invoke(null, new[] { oldProduct, "MISAM_280926_002" })!)
                throw new Exception("Upload product identity matching accepted a stale product or rejected the current one.");
            var uploadView = Activator.CreateInstance(uploadRoutingType)!;
            uploadRoutingType.GetMethod("SetCurrentProduct")!.Invoke(uploadView, new[] { oldProduct });
            if (!ReferenceEquals(serverType.GetProperty("CurrentProduct")!.GetValue(null), oldProduct))
                throw new Exception("Audio Routing did not synchronize the selected server product.");
            uploadRoutingType.GetMethod("SetCurrentProduct")!.Invoke(uploadView, new object?[] { null });
            if (serverType.GetProperty("CurrentProduct")!.GetValue(null) != null)
                throw new Exception("Audio Routing did not clear the stale server product.");
            Console.WriteLine("PASS upload identity rejects a stale product and clearing the Audio Routing product clears ServerEngine.CurrentProduct.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--normalization-only")
        {
            string isolatedDataDirectory = Path.Combine(directory, "test-data", "sonca-reference-check-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", isolatedDataDirectory);
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var normalizationRunnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
            var normalize = normalizationRunnerType.GetMethod("NormalizeAtOneKilohertz", BindingFlags.Static | BindingFlags.NonPublic)!;
            object[] normalizeArgs = { new Dictionary<double, double> { [500] = -26, [2000] = -14 }, 0.0 };
            var normalized = (Dictionary<double, double>)normalize.Invoke(null, normalizeArgs)!;
            if (Math.Abs((double)normalizeArgs[1] - (-20.0)) > 0.001
                || Math.Abs(normalized[500] + 6.0) > 0.001 || Math.Abs(normalized[2000] - 6.0) > 0.001)
                throw new Exception("1 kHz interpolation/normalization failed");

            var normalizationEngine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
            var normalizationRunner = Activator.CreateInstance(normalizationRunnerType, normalizationEngine)!;
            normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(normalizationRunner, true);
            var normalizationRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
            UserControl CreateRouting(object audioEngine, object testRunner)
            {
                var routing = (UserControl)Activator.CreateInstance(normalizationRoutingType)!;
                normalizationRoutingType.GetField("_audioEngine", privateInstance)!.SetValue(routing, audioEngine);
                normalizationRoutingType.GetField("_testRunner", privateInstance)!.SetValue(routing, testRunner);
                normalizationRoutingType.GetField("_referenceRouteKey", privateInstance)!.SetValue(routing, "NORMALIZATION_RESTART_TO_CHECK");
                return routing;
            }
            var first = CreateRouting(normalizationEngine, normalizationRunner);
            if (first.FindName("ChkNormalizeOneKilohertz") != null) throw new Exception("Normalization option was not removed.");
            normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(normalizationRunner, false);
            var parseNumber = normalizationRoutingType.GetMethod("ParseDoubleSafe", privateInstance)!;
            if ((double)parseNumber.Invoke(first, new object[] { "3,0", 0.0 })! != 3.0
                || (double)parseNumber.Invoke(first, new object[] { "0,50", 0.0 })! != 0.5)
                throw new Exception("Vietnamese decimal comma was interpreted as a thousands separator.");
            var repairTolerance = normalizationRoutingType.GetMethod("RepairStoredTolerance", BindingFlags.Static | BindingFlags.NonPublic)!;
            if ((double)repairTolerance.Invoke(null, new object[] { 300.0, 3.0, 12.0, 10.0 })! != 3.0
                || (double)repairTolerance.Invoke(null, new object[] { 5000.0, 0.5, 5.0, 100.0 })! != 0.5)
                throw new Exception("Corrupted saved dB/THD limits were not repaired.");
            if (Math.Abs((double)normalizationRoutingType.GetProperty("PlaybackLevelDbfs")!.GetValue(first)!) > 0.001
                || ((CheckBox)first.FindName("ChkSendToServer")).IsChecked != true)
                throw new Exception("Fresh Audio Routing does not default to 0 dBFS and server upload enabled.");
            string path = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(first, null)!;
            string configPath = (string)normalizationRoutingType.GetMethod("GetRoutingConfigPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            byte[]? previousConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            string? rawReferencePath = null;
            byte[]? previousRawReference = null;
            string? legacyCandidatePath = null;
            byte[]? previousLegacyCandidate = null;
            string? autoReferencePath = null;
            byte[]? previousAutoReference = null;
            string? mismatchedReferencePath = null;
            byte[]? previousMismatchedReference = null;
            string? legacyTolerancePath = null;
            string? repairedTolerancePath = null;
            try
            {
                normalizationRoutingType.GetMethod("SaveConfig", privateInstance)!.Invoke(first, null);
                var oldConfig = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!;
                oldConfig["LogSweepDurationSeconds"] = 65536.0 / 44100.0;
                oldConfig["NormalizeFrequencyResponseToOneKilohertz"] = true;
                File.WriteAllText(configPath, oldConfig.ToJsonString());
                var reloadedEngine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
                var reloadedRunner = Activator.CreateInstance(normalizationRunnerType, reloadedEngine)!;
                var configReload = CreateRouting(reloadedEngine, reloadedRunner);
                normalizationRoutingType.GetMethod("LoadConfig", privateInstance)!.Invoke(configReload, null);
                if (Math.Abs((double)normalizationRoutingType.GetProperty("PlaybackLevelDbfs")!.GetValue(configReload)!) > 0.001
                    || ((CheckBox)configReload.FindName("ChkSendToServer")).IsChecked != true)
                    throw new Exception("Saved 0 dBFS or server upload default was not restored.");
                if (configReload.FindName("ChkNormalizeOneKilohertz") != null
                    || (bool)normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.GetValue(reloadedRunner)!)
                    throw new Exception("Legacy 1 kHz normalization was not disabled");
                path = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(configReload, null)!;
                normalizationRoutingType.GetMethod("SaveReferenceCurve", privateInstance)!.Invoke(configReload,
                    new object?[] { path, new[] { 500.0, 1000.0, 2000.0 }, new[] { -6.0, 0.0, 6.0 }, 3.0, -20.0 });
                string jsonPath = Path.ChangeExtension(path, ".json");
                using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath)))
                {
                    if (json.RootElement.GetProperty("points").GetArrayLength() != 3
                        || json.RootElement.GetProperty("sweepSamples").GetInt32() != 65536)
                        throw new Exception("Portable JSON reference was not exported with the 64K curve.");
                }
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(configReload, null);
                var immediateCurve = (Dictionary<double, double>?)normalizationRoutingType.GetField("_standardCurve", privateInstance)!.GetValue(configReload);
                if (immediateCurve?.Count != 3 || immediateCurve[1000] != 0.0)
                    throw new Exception("A newly saved 64K reference was not loaded in the same measurement session.");
                var canReuseCalibration = normalizationRoutingType.GetMethod("CanReuseCalibrationReference", privateInstance)!;
                if (!(bool)canReuseCalibration.Invoke(configReload, new object[] { false })!
                    || (bool)canReuseCalibration.Invoke(configReload, new object[] { true })!)
                    throw new Exception("Calibration did not reuse a saved line or tried to skip a previously failed route.");
                normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.SetValue(reloadedRunner, 131072.0 / 44100.0);
                if ((bool)canReuseCalibration.Invoke(configReload, new object[] { false })!)
                    throw new Exception("Calibration reused a 64K line for a 128K sweep.");
                normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.SetValue(reloadedRunner, 65536.0 / 44100.0);
                var secondEngine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
                var secondRunner = Activator.CreateInstance(normalizationRunnerType, secondEngine)!;
                var second = CreateRouting(secondEngine, secondRunner);
                normalizationRoutingType.GetMethod("LoadConfig", privateInstance)!.Invoke(second, null);
                if (Math.Round((double)normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.GetValue(secondRunner)! * 44100.0) != 65536)
                    throw new Exception("Restarted view did not restore the 64K sweep length.");
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(second, null);
                var loaded = (Dictionary<double, double>?)normalizationRoutingType.GetField("_standardCurve", privateInstance)!.GetValue(second);
                var referenceLevel = (double?)normalizationRoutingType.GetField("_standardOneKilohertzLevelDbFs", privateInstance)!.GetValue(second);
                if (loaded?.Count != 3 || loaded[1000] != 0 || referenceLevel != -20.0)
                    throw new Exception($"Restarted view did not load normalized line and absolute 1 kHz level: path={path}, reloadPath={normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(second, null)}, count={loaded?.Count}, level={referenceLevel}");
                normalizationRoutingType.GetMethod("UpdateOneKilohertzLevel", privateInstance)!.Invoke(second, new object[] { -30.0 });
                if (!((TextBlock)second.FindName("TxtOneKilohertzLevel")).Text.Contains("-10.0 dB"))
                    throw new Exception("Absolute 1 kHz level difference is not visible after restart");
                normalizationRoutingType.GetMethod("UpdateOneKilohertzToneLevel", privateInstance)!.Invoke(second, new object[] { -5.0, 0.998 });
                if (!((TextBlock)second.FindName("TxtOneKilohertzToneLevel")).Text.Contains("CLIPPING"))
                    throw new Exception("Clipped 1 kHz tone level is not visible");
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(secondRunner, false);
                string rawPath = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(second, null)!;
                if (path == rawPath) throw new Exception("Raw and normalized modes share a reference file");
                rawReferencePath = rawPath;
                previousRawReference = File.Exists(rawPath) ? File.ReadAllBytes(rawPath) : null;
                normalizationRoutingType.GetMethod("SaveReferenceCurve", privateInstance)!.Invoke(second,
                    new object?[] { rawPath, new[] { 500.0, 1000.0, 2000.0 }, new[] { -26.0, -20.0, -14.0 }, 3.0, -20.0 });
                File.Delete(path);
                var fallbackEngine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
                var fallbackRunner = Activator.CreateInstance(normalizationRunnerType, fallbackEngine)!;
                var fallbackView = CreateRouting(fallbackEngine, fallbackRunner);
                normalizationRoutingType.GetMethod("LoadConfig", privateInstance)!.Invoke(fallbackView, null);
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(fallbackView, null);
                var converted = (Dictionary<double, double>?)normalizationRoutingType.GetField("_standardCurve", privateInstance)!.GetValue(fallbackView);
                if (converted?.Count != 3 || converted[500] != -6.0 || converted[1000] != 0.0 || converted[2000] != 6.0)
                    throw new Exception("Saved dBFS reference was not reused in normalized mode");
                File.Delete(rawReferencePath);
                var caseType = assembly.GetType("SoncaAudioInspector.AutoTestCaseItem")!;
                var testCase = Activator.CreateInstance(caseType)!;
                caseType.GetProperty("Id")!.SetValue(testCase, "NORMALIZATION");
                caseType.GetProperty("Name")!.SetValue(testCase, "Saved reference");
                var config = caseType.GetProperty("Config")!.GetValue(testCase)!;
                config.GetType().GetProperty("id")!.SetValue(config, "NORMALIZATION");
                config.GetType().GetProperty("PlaybackOut")!.SetValue(config, "RESTART");
                config.GetType().GetProperty("RecordingIn")!.SetValue(config, "CHECK");
                config.GetType().GetProperty("FrequencyResponseMethod")!.SetValue(config, "LogSweep");
                ((System.Collections.IList)normalizationRoutingType.GetField("_autoTestCases", privateInstance)!.GetValue(fallbackView)!).Add(testCase);
                normalizationRoutingType.GetMethod("ApplyTestCaseConfig")!.Invoke(fallbackView, new[] { config });
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, false);
                autoReferencePath = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(fallbackView, null)!;
                previousAutoReference = File.Exists(autoReferencePath) ? File.ReadAllBytes(autoReferencePath) : null;
                normalizationRoutingType.GetMethod("SaveReferenceCurve", privateInstance)!.Invoke(fallbackView,
                    new object?[] { autoReferencePath, new[] { 500.0, 1000.0, 2000.0 }, new[] { -26.0, -20.0, -14.0 }, 3.0, -20.0 });
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, true);
                fallbackEngine.GetType().GetProperty("PlaybackSampleRate")!.SetValue(fallbackEngine, 48000);
                var missingAt48k = (List<string>)normalizationRoutingType.GetMethod("GetMissingStandardReferenceTests", privateInstance)!.Invoke(fallbackView, null)!;
                fallbackEngine.GetType().GetProperty("PlaybackSampleRate")!.SetValue(fallbackEngine, 44100);
                var missingAt44k = (List<string>)normalizationRoutingType.GetMethod("GetMissingStandardReferenceTests", privateInstance)!.Invoke(fallbackView, null)!;
                if (missingAt48k.Count == 0 || missingAt44k.Count != 0)
                    throw new Exception($"Auto Test reference gate did not match the acquisition sample rate: 48k={string.Join("; ", missingAt48k)}, 44k={string.Join("; ", missingAt44k)}, file={autoReferencePath}");

                File.Delete(autoReferencePath);
                normalizationRoutingType.GetMethod("ApplyTestCaseConfig")!.Invoke(fallbackView, new[] { config });
                normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.SetValue(fallbackRunner, 131072.0 / 44100.0);
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, false);
                mismatchedReferencePath = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(fallbackView, null)!;
                previousMismatchedReference = File.Exists(mismatchedReferencePath) ? File.ReadAllBytes(mismatchedReferencePath) : null;
                normalizationRoutingType.GetMethod("SaveReferenceCurve", privateInstance)!.Invoke(fallbackView,
                    new object?[] { mismatchedReferencePath, new[] { 500.0, 1000.0, 2000.0 }, new[] { -26.0, -20.0, -14.0 }, 3.0, -20.0 });
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, true);
                normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.SetValue(fallbackRunner, 65536.0 / 44100.0);
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(fallbackView, null);
                var fastCurve = (Dictionary<double, double>?)normalizationRoutingType.GetField("_standardCurve", privateInstance)!.GetValue(fallbackView);
                if (fastCurve != null)
                    throw new Exception($"A 64K measurement incorrectly reused a 128K reference: file={mismatchedReferencePath}");
                normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.SetValue(fallbackRunner, 131072.0 / 44100.0);
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(fallbackView, null);
                var matchingCurve = (Dictionary<double, double>?)normalizationRoutingType.GetField("_standardCurve", privateInstance)!.GetValue(fallbackView);
                if (matchingCurve?.Count != 3 || matchingCurve[1000] != 0.0)
                    throw new Exception("A 128K measurement did not load its matching 128K reference.");
                File.Delete(mismatchedReferencePath);
                normalizationRunnerType.GetProperty("LogSweepDurationSeconds")!.SetValue(fallbackRunner, 65536.0 / 44100.0);
                string legacyKey = (string)normalizationRoutingType.GetMethod("GetStandardDeviceKeyForBasis", privateInstance)!
                    .Invoke(fallbackView, new object?[] { "RECEIVED_RMS_DBFS_V1", true, null })!;
                string legacyPrefix = legacyKey.Length > 72 ? legacyKey[..72].TrimEnd('_', '.', ' ') : legacyKey;
                string standardsDirectory = (string)normalizationRoutingType.GetMethod("GetStandardsDirectory", privateInstance)!.Invoke(fallbackView, null)!;
                legacyCandidatePath = Path.Combine(standardsDirectory, "standard_" + legacyPrefix + "_0123456789ABCDEF0123.csv");
                previousLegacyCandidate = File.Exists(legacyCandidatePath) ? File.ReadAllBytes(legacyCandidatePath) : null;
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, false);
                normalizationRoutingType.GetMethod("SaveReferenceCurve", privateInstance)!.Invoke(fallbackView,
                    new object?[] { legacyCandidatePath, new[] { 500.0, 1000.0, 2000.0 }, new[] { -26.0, -20.0, -14.0 }, 3.0, -20.0 });
                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, true);
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(fallbackView, null);
                var legacyCurve = (Dictionary<double, double>?)normalizationRoutingType.GetField("_standardCurve", privateInstance)!.GetValue(fallbackView);
                if (legacyCurve?.Count != 3 || legacyCurve[1000] != 0.0)
                    throw new Exception("An existing reference from a changed Bluetooth endpoint was not reused");

                normalizationRunnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(fallbackRunner, false);
                string inflatedKey = (string)normalizationRoutingType.GetMethod("GetStandardDeviceKeyForBasis", privateInstance)!
                    .Invoke(fallbackView, new object?[] { "RECEIVED_RMS_DBFS_V1", false, 300.0 })!;
                legacyTolerancePath = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileNameForKey", privateInstance)!
                    .Invoke(fallbackView, new object[] { inflatedKey })!;
                normalizationRoutingType.GetMethod("SaveReferenceCurve", privateInstance)!.Invoke(fallbackView,
                    new object?[] { legacyTolerancePath, new[] { 500.0, 1000.0, 2000.0 }, new[] { -26.0, -20.0, -14.0 }, 300.0, -20.0 });
                normalizationRoutingType.GetMethod("CheckAndLoadStandardDevice", privateInstance)!.Invoke(fallbackView, null);
                repairedTolerancePath = (string)normalizationRoutingType.GetMethod("GetStandardDeviceFileName", privateInstance)!.Invoke(fallbackView, null)!;
                var loadedLimits = (System.Collections.IDictionary?)normalizationRoutingType.GetField("_frequencyLimits", privateInstance)!.GetValue(fallbackView);
                object? middleLimit = loadedLimits?[1000.0];
                if (!File.Exists(repairedTolerancePath) || middleLimit == null
                    || Math.Abs((double)middleLimit.GetType().GetProperty("LowerDb")!.GetValue(middleLimit)! - (-23.0)) > 0.001)
                    throw new Exception("Inflated 300 dB reference was not repaired to the 3 dB limit.");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (rawReferencePath != null)
                {
                    if (previousRawReference == null) { if (File.Exists(rawReferencePath)) File.Delete(rawReferencePath); }
                    else File.WriteAllBytes(rawReferencePath, previousRawReference);
                }
                if (legacyCandidatePath != null)
                {
                    if (previousLegacyCandidate == null) { if (File.Exists(legacyCandidatePath)) File.Delete(legacyCandidatePath); }
                    else File.WriteAllBytes(legacyCandidatePath, previousLegacyCandidate);
                }
                if (autoReferencePath != null)
                {
                    if (previousAutoReference == null) { if (File.Exists(autoReferencePath)) File.Delete(autoReferencePath); }
                    else File.WriteAllBytes(autoReferencePath, previousAutoReference);
                }
                if (mismatchedReferencePath != null)
                {
                    if (previousMismatchedReference == null) { if (File.Exists(mismatchedReferencePath)) File.Delete(mismatchedReferencePath); }
                    else File.WriteAllBytes(mismatchedReferencePath, previousMismatchedReference);
                }
                if (legacyTolerancePath != null && File.Exists(legacyTolerancePath)) File.Delete(legacyTolerancePath);
                if (repairedTolerancePath != null && File.Exists(repairedTolerancePath)) File.Delete(repairedTolerancePath);
                if (previousConfig == null) { if (File.Exists(configPath)) File.Delete(configPath); }
                else File.WriteAllBytes(configPath, previousConfig);
            }
            Console.WriteLine("PASS decimal-comma repair, JSON export, restart normalization reload, 300 dB reference repair, strict sweep-length matching, Bluetooth reference reuse, and Auto Test reference gate");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--sweep-length-only")
        {
            const BindingFlags sweepFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
            object sweepEngine = Activator.CreateInstance(engineType)!;
            object sweepRunner = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.TestRunner")!, sweepEngine)!;
            var sweepRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
            var sweepRouting = (UserControl)Activator.CreateInstance(sweepRoutingType)!;
            sweepRoutingType.GetField("_audioEngine", sweepFlags)!.SetValue(sweepRouting, sweepEngine);
            sweepRoutingType.GetField("_testRunner", sweepFlags)!.SetValue(sweepRouting, sweepRunner);
            var sweepLengthCombo = (ComboBox)sweepRouting.FindName("ComboSweepLength");
            if ((sweepLengthCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "65536")
                throw new Exception("64K is no longer the default sweep length.");
            var frequencyGrid = (double[])sweepRunner.GetType().GetMethod("GenerateLogSweepEvaluationFrequencies", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { 50.0, 18000.0, 24 })!;
            if (frequencyGrid.Length != 206 || frequencyGrid[0] != 50.0 || frequencyGrid[^1] != 18000.0)
                throw new Exception("LogSweep comparison no longer uses the 206 point, 1/24 octave grid from 50–18k.");
            foreach (int length in new[] { 65536, 131072 })
            {
                double duration = length / 44100.0;
                sweepRoutingType.GetMethod("SelectLogSweepDuration", sweepFlags)!.Invoke(sweepRouting, new object[] { duration });
                double selected = (double)sweepRoutingType.GetMethod("GetSelectedLogSweepDuration", sweepFlags)!.Invoke(sweepRouting, null)!;
                var combo = (ComboBox)sweepRouting.FindName("ComboSweepLength");
                if ((combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() != length.ToString()
                    || Math.Round(selected * 44100) != length)
                    throw new Exception($"Sweep length {length} did not select an exact sample count.");
                if (!((ComboBoxItem)combo.SelectedItem).Content!.ToString()!.Contains(length == 65536 ? "1.49 s" : "2.97 s"))
                    throw new Exception("44.1 kHz sweep length label is inaccurate.");
                object config = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.TestConfig")!)!;
                config.GetType().GetProperty("LogSweepDurationSeconds")!.SetValue(config, 8.0);
                sweepRoutingType.GetMethod("ConfigureProductionMeasurement", sweepFlags)!.Invoke(sweepRouting, new[] { config });
                double applied = (double)sweepRunner.GetType().GetProperty("LogSweepDurationSeconds")!.GetValue(sweepRunner)!;
                if (Math.Round(applied * 44100) != length)
                    throw new Exception($"Model config overrode sweep length {length}.");
            }
            var rateCombo = (ComboBox)sweepRouting.FindName("ComboPlaybackSampleRate");
            var fortyEightKilohertz = new ComboBoxItem { Content = "48 kHz", Tag = "48000" };
            rateCombo.Items.Add(fortyEightKilohertz);
            rateCombo.SelectedItem = fortyEightKilohertz;
            if (!((ComboBoxItem)sweepLengthCombo.SelectedItem).Content!.ToString()!.Contains("2.73 s"))
                throw new Exception("48 kHz 128k sweep label is inaccurate.");
            Console.WriteLine("PASS default 64K sweep, exact 64K/128K selection, accurate 44.1/48 kHz labels, and model configuration.");
            ((IDisposable)sweepEngine).Dispose();
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--channel-routing-only")
        {
            var engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
            object routeEngine = Activator.CreateInstance(engineType)!;
            if ((int)engineType.GetProperty("PlaybackSampleRate")!.GetValue(routeEngine)! != 44100)
                throw new Exception("AudioEngine does not default to 44.1 kHz.");

            var routeProviderType = assembly.GetType("SoncaAudioInspector.SignalSampleProvider")!;
            var channelRoutingType = assembly.GetType("SoncaAudioInspector.ChannelRoutingSampleProvider")!;
            var routeSignalType = assembly.GetType("SoncaAudioInspector.SignalType")!;
            object sine = Enum.Parse(routeSignalType, "Sine");
            foreach (int targetChannel in new[] { 0, 1 })
            {
                object mono = Activator.CreateInstance(routeProviderType, new object?[]
                {
                    44100, sine, 1000.0, 1.0, null, null, null, 1.0, null, 1.0
                })!;
                object routed = Activator.CreateInstance(channelRoutingType, new object?[] { mono, targetChannel })!;
                float[] buffer = new float[256];
                int read = (int)channelRoutingType.GetMethod("Read")!.Invoke(routed, new object[] { buffer, 0, buffer.Length })!;
                if (read != buffer.Length) throw new Exception($"Channel router returned {read}/{buffer.Length} samples.");
                double left = 0.0, right = 0.0;
                for (int i = 0; i < read; i += 2)
                {
                    left += Math.Abs(buffer[i]);
                    right += Math.Abs(buffer[i + 1]);
                }
                if (targetChannel == 0 && (left <= 0.1 || right != 0.0))
                    throw new Exception($"Left routing leaked or was silent: L={left:F6}, R={right:F6}");
                if (targetChannel == 1 && (right <= 0.1 || left != 0.0))
                    throw new Exception($"Right routing leaked or was silent: L={left:F6}, R={right:F6}");
            }
            Console.WriteLine("PASS 44.1 kHz default; auto-test channel router isolates Left and Right.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--config-path-only")
        {
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
            var configMainWindowType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
            string activePath = (string)configMainWindowType.GetMethod("GetCheckingConfigReadPath", staticFlags)!.Invoke(null, null)!;
            string expectedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoncaAudioInspector", "checking_config.json");
            if (File.Exists(expectedPath) && !string.Equals(activePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Wrong active config: {activePath}");
            Console.WriteLine($"PASS active checking config: {activePath}");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--routing-preserve-only")
        {
            const BindingFlags preserveFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
            object preserveEngine = Activator.CreateInstance(engineType)!;
            object preserveRunner = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.TestRunner")!, preserveEngine)!;
            var preserveRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
            var preserveRouting = (UserControl)Activator.CreateInstance(preserveRoutingType)!;
            preserveRoutingType.GetField("_audioEngine", preserveFlags)!.SetValue(preserveRouting, preserveEngine);
            preserveRoutingType.GetField("_testRunner", preserveFlags)!.SetValue(preserveRouting, preserveRunner);
            engineType.GetProperty("PlaybackChannel")!.SetValue(preserveEngine, 1);
            engineType.GetProperty("RecordingChannel")!.SetValue(preserveEngine, 1);
            preserveRoutingType.GetMethod("ConfigureProductionMeasurement", preserveFlags)!.Invoke(preserveRouting, new object?[] { null });
            if ((int?)engineType.GetProperty("PlaybackChannel")!.GetValue(preserveEngine) != 1
                || (int?)engineType.GetProperty("RecordingChannel")!.GetValue(preserveEngine) != 1)
                throw new Exception("Manual run reset the selected channel.");
            var setupColumn = (ColumnDefinition)preserveRouting.FindName("SetupColumn");
            preserveRoutingType.GetMethod("SetSetupVisibility")!.Invoke(preserveRouting, new object[] { true });
            preserveRoutingType.GetMethod("ExpandThdChart")!.Invoke(preserveRouting, null);
            if (setupColumn.Width.Value != 0 || setupColumn.MinWidth != 0)
                throw new Exception("THD fullscreen leaves the setup column at its minimum width.");
            preserveRoutingType.GetMethod("RestoreChartsLayout")!.Invoke(preserveRouting, null);
            if (setupColumn.MinWidth != 280)
                throw new Exception("Exiting fullscreen did not restore the setup column.");
            Console.WriteLine("PASS manual L/R selection persists; THD fullscreen uses the entire left area.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--thd-badge-only")
        {
            const BindingFlags badgeFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var badgeEngine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
            var badgeRunnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
            var badgeRunner = Activator.CreateInstance(badgeRunnerType, badgeEngine)!;
            var badgeRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
            var badgeRouting = (UserControl)Activator.CreateInstance(badgeRoutingType)!;
            badgeRoutingType.GetField("_testRunner", badgeFlags)!.SetValue(badgeRouting, badgeRunner);
            var qualityType = assembly.GetType("SoncaAudioInspector.ToneQualityMetrics")!;
            float[] sine = Enumerable.Range(0, 44100)
                .Select(index => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * index / 44100)))
                .ToArray();
            object quality = assembly.GetType("SoncaAudioInspector.AdvancedAudioMeasurement")!
                .GetMethod("AnalyzeTone")!.Invoke(null, new object[] { sine, 44100, 1000.0, 9 })!;
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(double), qualityType);
            var qualities = (System.Collections.IDictionary)Activator.CreateInstance(dictionaryType)!;
            qualities.Add(1000.0, quality);
            badgeRunnerType.GetProperty("LastToneQualities")!.SetValue(badgeRunner, qualities);
            badgeRoutingType.GetMethod("RefreshThdVerdictAfterMeasurement", badgeFlags)!.Invoke(badgeRouting, null);
            string badgeText = ((TextBlock)badgeRouting.FindName("TxtThdVerdictBadge")).Text;
            if (!badgeText.Contains("%") || badgeText.Contains("CHƯA ĐO"))
                throw new Exception($"Completed tone THD still shows '{badgeText}'.");
            string statusText = ((TextBlock)badgeRouting.FindName("TxtThdStatus")).Text;
            if (!statusText.Contains("1000 Hz") || !statusText.Contains("THD") || !statusText.Contains("%"))
                throw new Exception($"Completed tone THD is missing from the main status: {statusText}.");
            qualities.Clear();
            badgeRunnerType.GetProperty("SilentInputDetected")!.SetValue(badgeRunner, true);
            badgeRoutingType.GetMethod("RefreshThdVerdictAfterMeasurement", badgeFlags)!.Invoke(badgeRouting, null);
            string noSignalBadge = ((TextBlock)badgeRouting.FindName("TxtThdVerdictBadge")).Text;
            if (noSignalBadge != "KHÔNG ĐỦ TÍN HIỆU")
                throw new Exception($"Silent EXCL capture has an ambiguous badge: {noSignalBadge}.");
            Console.WriteLine($"PASS completed EXCL-style tone updates Distortion badge: {badgeText}.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && (args[1] == "--advanced-upgrade-only" || args[1] == "--ir-window-settings-only"))
        {
            var measurementEngine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
            var measurementView = (UserControl)Activator.CreateInstance(
                assembly.GetType("SoncaAudioInspector.StandardMeasurementWindow")!, measurementEngine)!;
            foreach (string required in args[1] == "--advanced-upgrade-only"
                ? new[] { "TxtIrLeftMs", "TxtIrRightMs", "HarmonicsTab", "PlotHarmonics", "TransientTab", "PlotTransient" }
                : new[] { "TxtIrLeftMs", "TxtIrRightMs", "ChkFdw", "TxtFdwCycles" })
                if (measurementView.FindName(required) == null) throw new Exception($"Missing upgraded advanced control: {required}");
            if (measurementView.FindName("BtnExport") != null || measurementView.FindName("BtnGolden") != null)
                throw new Exception("Advanced report/golden controls remain");
            var left = (TextBox)measurementView.FindName("TxtIrLeftMs");
            var right = (TextBox)measurementView.FindName("TxtIrRightMs");
            var fdw = (CheckBox)measurementView.FindName("ChkFdw");
            var cycles = (TextBox)measurementView.FindName("TxtFdwCycles");
            if (left.Text != "125" || right.Text != "500" || fdw.IsChecked != false || cycles.IsEnabled)
                throw new Exception("Advanced sweep defaults differ from Tukey 125/500 ms, FDW off.");
            fdw.IsChecked = true;
            cycles.GetBindingExpression(UIElement.IsEnabledProperty)!.UpdateTarget();
            if (!cycles.IsEnabled) throw new Exception("Explicit FDW control did not enable its cycle count.");
            measurementView.GetType().GetMethod("ReleaseDeviceItems")!.Invoke(measurementView, null);
            ((IDisposable)measurementEngine).Dispose();
            if (args[1] == "--advanced-upgrade-only")
                Console.WriteLine("PASS advanced UI has gate controls, ESS harmonics and transient plots without report/golden controls.");
            Console.WriteLine("PASS advanced sweep uses 125/500 ms by default, with FDW off and an explicit opt-in cycle control.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--thd-layout-only")
        {
            VerifyThreePanelThdLayout(assembly, directory);
            Console.WriteLine("PASS THD uses three equal panels, stays Shared-only, and has no distortion image export.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--server-graph-only")
        {
            VerifyServerGraphContract(assembly, directory);
            Console.WriteLine("PASS full-screen graph render is downsampled to the 800x450 server contract.");
            app.Shutdown();
            return;
        }
        if (args.Length > 1 && args[1] == "--umik-selection-only")
        {
            VerifySelectedMeasurementMicrophoneWins(assembly);
            Console.WriteLine("PASS selected UMIK-1 remains authoritative over the legacy KT USB keyword.");
            app.Shutdown();
            return;
        }
        var engine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        if ((int?)engine.GetType().GetProperty("RecordingChannel")!.GetValue(engine) != null)
            throw new Exception("Audio Routing still defaults to a fixed capture channel");

        var runnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
        if (args.Length > 1 && args[1] == "--auto-feq-range-only")
        {
            object rangeRunner = Activator.CreateInstance(runnerType, engine)!;
            if ((double)runnerType.GetProperty("EvaluationMaxFrequencyHz")!.GetValue(rangeRunner)! != 18000.0)
                throw new Exception("Manual FEQ no longer evaluates through 18 kHz.");
            runnerType.GetProperty("EvaluationMaxFrequencyHz")!.SetValue(rangeRunner, 18000.0);
            if ((double)runnerType.GetProperty("EvaluationMaxFrequencyHz")!.GetValue(rangeRunner)! != 18000.0)
                throw new Exception("Auto FEQ evaluation limit was not applied.");
            var assess = runnerType.GetMethod("AssessFeqRepeatabilityInRange")!;
            var curve = new Dictionary<double, double> { [50] = 0, [1000] = 0, [15000] = 0, [18000] = 0 };
            object result15 = assess.Invoke(null, new object?[] { new List<Dictionary<double, double>> { curve, curve }, true, 3.0, null, 15000.0, 50.0 })!;
            object result18 = assess.Invoke(null, new object?[] { new List<Dictionary<double, double>> { curve, curve }, true, 3.0, null, 18000.0, 50.0 })!;
            int count15 = (int)result15.GetType().GetProperty("CheckedPointCount")!.GetValue(result15)!;
            int count18 = (int)result18.GetType().GetProperty("CheckedPointCount")!.GetValue(result18)!;
            if (count18 <= count15) throw new Exception("18 kHz FEQ evaluation did not include more points.");
            const BindingFlags rangePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
            var rangeRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
            var rangeRouting = (UserControl)Activator.CreateInstance(rangeRoutingType)!;
            rangeRoutingType.GetField("_audioEngine", rangePrivate)!.SetValue(rangeRouting, engine);
            rangeRoutingType.GetField("_testRunner", rangePrivate)!.SetValue(rangeRouting, rangeRunner);
            Type limitType = assembly.GetType("SoncaAudioInspector.FrequencyLimitPoint")!;
            Type limitDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(double), limitType);
            var limits = (System.Collections.IDictionary)Activator.CreateInstance(limitDictionaryType)!;
            limits.Add(50.0, Activator.CreateInstance(limitType, 50.0, 0.0, -5.0, 5.0, false)!);
            limits.Add(60.0, Activator.CreateInstance(limitType, 60.0, 0.0, -5.0, 5.0, false)!);
            rangeRoutingType.GetField("_frequencyLimits", rangePrivate)!.SetValue(rangeRouting, limits);
            MethodInfo isRed = rangeRoutingType.GetMethod("IsFrequencyPointOverThreeDb", rangePrivate)!;
            if ((bool)isRed.Invoke(rangeRouting, new object[] { 40.0, 20.0 })!
                || (bool)isRed.Invoke(rangeRouting, new object[] { 19000.0, 20.0 })!
                || (bool)isRed.Invoke(rangeRouting, new object[] { 55.0, 4.0 })!
                || !(bool)isRed.Invoke(rangeRouting, new object[] { 55.0, 6.0 })!)
                throw new Exception("Graph point colors do not follow the 50 Hz–18 kHz evaluation range and configured limits.");
            rangeRoutingType.GetField("_leaveAudioReleasedAfterAutoTest", rangePrivate)!.SetValue(rangeRouting, true);
            rangeRoutingType.GetMethod("ComboDevice_SelectionChanged", rangePrivate)!.Invoke(rangeRouting,
                new object[] { rangeRouting.FindName("ComboPlayback")!,
                    new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                        Array.Empty<object>(), Array.Empty<object>()) });
            if ((bool)rangeRoutingType.GetField("_leaveAudioReleasedAfterAutoTest", rangePrivate)!.GetValue(rangeRouting)!)
                throw new Exception("Selecting an audio endpoint did not re-enable the Headroom monitor.");
            Console.WriteLine($"PASS Auto Test judges and colors only 50 Hz–18 kHz ({count18} points), follows configured limits, and device selection re-enables Headroom.");
            app.Shutdown();
            return;
        }
        const int stableToneRate = 48000;
        const double stableToneFrequency = 80.0;
        float[] stableToneCapture = new float[stableToneRate * 2];
        for (int sample = 0; sample < stableToneCapture.Length; sample++)
        {
            int segment = sample / (stableToneCapture.Length / 4);
            double time = (double)sample / stableToneRate;
            double harmonicAmplitude = segment == 0 ? 0.04 : 0.001;
            stableToneCapture[sample] = (float)(0.2 * Math.Sin(2 * Math.PI * stableToneFrequency * time)
                + harmonicAmplitude * Math.Sin(4 * Math.PI * stableToneFrequency * time));
        }
        object stableTone = runnerType.GetMethod("AnalyzeStableToneCapture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { stableToneCapture, stableToneRate, stableToneFrequency })!;
        double stableToneThd = (double)stableTone.GetType().GetProperty("ThdPercent")!.GetValue(stableTone)!;
        if (stableToneThd < 0.4 || stableToneThd > 0.6)
            throw new Exception($"One disturbed THD segment biased the single-capture median: {stableToneThd:F3}%");

        float[] burstNoiseCapture = new float[stableToneRate * 2];
        var burstRandom = new Random(20260922);
        for (int sample = 0; sample < burstNoiseCapture.Length; sample++)
        {
            double time = (double)sample / stableToneRate;
            int segment = sample / (burstNoiseCapture.Length / 8);
            double noise = segment is 2 or 3 ? (burstRandom.NextDouble() * 2.0 - 1.0) * 0.12 : 0.0;
            burstNoiseCapture[sample] = (float)(0.2 * Math.Sin(2 * Math.PI * stableToneFrequency * time)
                + 0.001 * Math.Sin(4 * Math.PI * stableToneFrequency * time)
                + noise);
        }
        object burstStableTone = runnerType.GetMethod("AnalyzeStableToneCapture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { burstNoiseCapture, stableToneRate, stableToneFrequency })!;
        double burstStableToneThd = (double)burstStableTone.GetType().GetProperty("ThdPercent")!.GetValue(burstStableTone)!;
        if (burstStableToneThd < 0.4 || burstStableToneThd > 0.6)
            throw new Exception($"Intermittent capture noise biased THD: {burstStableToneThd:F3}%");
        float[] clippedCapture = (float[])stableToneCapture.Clone();
        clippedCapture[stableToneRate] = 1.0f;
        object clippedStableTone = runnerType.GetMethod("AnalyzeStableToneCapture", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { clippedCapture, stableToneRate, stableToneFrequency })!;
        double clippedPeak = (double)clippedStableTone.GetType().GetProperty("PeakSample")!.GetValue(clippedStableTone)!;
        if (clippedPeak < 0.995)
            throw new Exception("Stable-segment filtering hid a clipped capture sample.");
        if (args.Length > 1 && args[1] == "--thd-burst-only")
        {
            Console.WriteLine($"PASS intermittent capture noise rejected; THD={burstStableToneThd:F3}%.");
            app.Shutdown();
            return;
        }
        var bands = ((Array)runnerType.GetProperty("TestFrequencyBands")!.GetValue(null)!).Cast<object>()
            .Select(item => (double[])item.GetType().GetField("Item2")!.GetValue(item)!)
            .Select(frequencies => frequencies.Append(1100.0).Distinct().OrderBy(f => f).ToArray())
            .ToArray();
        var providerType = assembly.GetType("SoncaAudioInspector.SignalSampleProvider")!;
        double scale = (double)providerType.GetMethod("CalculateCommonMultitoneSampleScale")!
            .Invoke(null, new object[] { 48000, bands })!;
        var signalType = assembly.GetType("SoncaAudioInspector.SignalType")!;
        object multitone = Enum.Parse(signalType, "Multitone");
        double maximumGeneratedPeak = 0.0;
        foreach (double[] frequencies in bands)
        {
            object provider = Activator.CreateInstance(providerType, new object?[]
            {
                48000, multitone, 1100.0, 1.0, null, frequencies, null, scale, null, 1.0
            })!;
            float[] generated = new float[48000 * 6];
            providerType.GetMethod("Read")!.Invoke(provider, new object[] { generated, 0, generated.Length });
            double peak = generated.Max(sample => Math.Abs((double)sample));
            maximumGeneratedPeak = Math.Max(maximumGeneratedPeak, peak);
            if (peak > 0.781)
                throw new Exception($"Six-second multitone peak is not safely bounded: {peak:F6}");
        }
        if (maximumGeneratedPeak < 0.77)
            throw new Exception($"Multitone peak target is unexpectedly low: {maximumGeneratedPeak:F6}");
        var view = (UserControl)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.StandardMeasurementWindow")!, engine)!;
        var tabs = (TabControl)view.FindName("MeasurementTabs");
        tabs.SelectedItem = view.FindName("FeedbackTab");
        var start = (Button)view.FindName("BtnFeedbackStart");
        if (view.FindName("BtnFeedbackOn") != null || view.FindName("BtnFeedbackOff") != null) throw new Exception("Old two-step controls remain");
        var stop = (Button)view.FindName("BtnFeedbackStop");
        var export = (Button)view.FindName("BtnFeedbackExport");
        if (view.FindName("BtnExport") != null || view.FindName("BtnGolden") != null)
            throw new Exception("Advanced report/golden controls remain");
        if (!start.IsEnabled || stop.IsEnabled || export.IsEnabled) throw new Exception("Initial button state");
        var setBusy = view.GetType().GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setBusy.Invoke(view, new object?[] { true, null });
        if (start.IsEnabled || ((Button)view.FindName("BtnSweep")).IsEnabled || ((Button)view.FindName("BtnRefreshDevices")).IsEnabled)
            throw new Exception("Busy state did not lock controls");
        setBusy.Invoke(view, new object?[] { false, null });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var runner = Activator.CreateInstance(runnerType, engine)!;
        double[] testFrequencies = (double[])runnerType.GetProperty("TestFrequencies")!.GetValue(null)!;
        var stableCurve = testFrequencies.ToDictionary(frequency => frequency, _ => 0.0);
        var oneLocalVariation = new Dictionary<double, double>(stableCurve);
        oneLocalVariation[testFrequencies.First(frequency => frequency >= 50 && frequency < 250)] = 4.0;
        var assessRepeatability = runnerType.GetMethod("AssessFeqRepeatability")!;
        var assessInRange = runnerType.GetMethod("AssessFeqRepeatabilityInRange")!;
        object reference15k = assessInRange.Invoke(null, new object?[]
        {
            new List<Dictionary<double, double>> { stableCurve, stableCurve }, true, 3.0, null, 15000.0
        })!;
        object reference18k = assessInRange.Invoke(null, new object?[]
        {
            new List<Dictionary<double, double>> { stableCurve, stableCurve }, true, 3.0, null, 18000.0
        })!;
        int points15k = (int)reference15k.GetType().GetProperty("CheckedPointCount")!.GetValue(reference15k)!;
        int points18k = (int)reference18k.GetType().GetProperty("CheckedPointCount")!.GetValue(reference18k)!;
        if (points18k <= points15k)
            throw new Exception("Auto Test did not extend FEQ evaluation to 18 kHz.");
        object acceptedVariation = assessRepeatability.Invoke(null, new object?[]
        {
            new List<Dictionary<double, double>> { stableCurve, oneLocalVariation, stableCurve }, false, 3.0, null
        })!;
        if ((bool)acceptedVariation.GetType().GetProperty("IsInvalid")!.GetValue(acceptedVariation)!)
            throw new Exception("One local non-critical variation invalidated the whole reference");
        var unstableBassCurve = new Dictionary<double, double>(stableCurve);
        foreach (double frequency in testFrequencies.Where(frequency => frequency >= 50 && frequency < 250).Take(4))
            unstableBassCurve[frequency] = 4.0;
        object rejectedVariation = assessRepeatability.Invoke(null, new object?[]
        {
            new List<Dictionary<double, double>> { stableCurve, unstableBassCurve, stableCurve }, false, 3.0, null
        })!;
        if (!(bool)rejectedVariation.GetType().GetProperty("IsInvalid")!.GetValue(rejectedVariation)!)
            throw new Exception("Unstable Bass ratio above 10% was accepted");
        string repeatabilityDescription = (string)runnerType.GetMethod("DescribeFeqRepeatability")!
            .Invoke(null, new object[] { rejectedVariation, 12 })!;
        if (!repeatabilityDescription.Contains("min") || !repeatabilityDescription.Contains("max") || !repeatabilityDescription.Contains("lượt"))
            throw new Exception("Repeatability error does not expose per-run evidence");
        float[] sourceSweep = Enumerable.Range(0, 48000).Select(index => (float)Math.Sin(index * 0.01)).ToArray();
        var addPreroll = runnerType.GetMethod("AddLogSweepPreroll", BindingFlags.Static | BindingFlags.NonPublic)!;
        float[] sweepWithPreroll = (float[])addPreroll.Invoke(null, new object[] { sourceSweep, 48000 })!;
        if (sweepWithPreroll.Length != sourceSweep.Length + 24000
            || sweepWithPreroll[1000] != sourceSweep[1000]
            || sweepWithPreroll[25000] != sourceSweep[1000]
            || Math.Abs(sweepWithPreroll[23999]) > 1e-5)
            throw new Exception("Log-sweep 0.5-second duplicated preroll is incorrect");
        var checkFeqClipping = runnerType.GetMethod("CheckFeqInputClipping", flags)!;
        bool nearOverload = (bool)checkFeqClipping.Invoke(runner, new object[] { new float[] { 0.0f, 0.9609f, -0.94f }, "regression" })!;
        if (nearOverload) throw new Exception("Near-overload FEQ peak was incorrectly classified as clipping");
        bool atRail = (bool)checkFeqClipping.Invoke(runner, new object[] { new float[] { 0.0f, 0.995f, -0.2f }, "regression" })!;
        if (!atRail) throw new Exception("Full-scale FEQ sample was not classified as clipping");
        var routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        var routing = (UserControl)Activator.CreateInstance(routingType)!;
        if (routing.FindName("ComboRecordingChannel") != null || view.FindName("ComboMeasurementChannel") != null)
            throw new Exception("Unwanted DUT channel selector remains");
        routingType.GetField("_audioEngine", flags)!.SetValue(routing, engine);
        routingType.GetField("_testRunner", flags)!.SetValue(routing, runner);
        var routingEngine = engine;
        var testConfigType = assembly.GetType("SoncaAudioInspector.TestConfig")!;
        var mainWindowType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
        string activeCheckingConfigPath = (string)mainWindowType.GetMethod(
            "GetCheckingConfigReadPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        string expectedUserConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoncaAudioInspector", "checking_config.json");
        if (File.Exists(expectedUserConfigPath)
            && !string.Equals(activeCheckingConfigPath, expectedUserConfigPath, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Debug build did not prefer the current LocalAppData checking config");
        var input2Config = Activator.CreateInstance(testConfigType)!;
        testConfigType.GetProperty("RecordingChannel")!.SetValue(input2Config, 2);
        testConfigType.GetProperty("PlaybackFrequencyScale")!.SetValue(input2Config, 0.91875);
        testConfigType.GetProperty("BassThdSampleScale")!.SetValue(input2Config, 1.0);
        testConfigType.GetProperty("BassThdFrequency")!.SetValue(input2Config, 80.0);
        testConfigType.GetProperty("MidThdFrequency")!.SetValue(input2Config, 1000.0);
        testConfigType.GetProperty("TrebleThdFrequency")!.SetValue(input2Config, 4000.0);
        testConfigType.GetProperty("LogSweepRuns")!.SetValue(input2Config, 2);
        testConfigType.GetProperty("RequireLogSweepPhaseAlignment")!.SetValue(input2Config, false);
        testConfigType.GetProperty("MinimumInputSignalDbFs")!.SetValue(input2Config, -40.0);
        routingType.GetField("_isExecutingAutoSuite", flags)!.SetValue(routing, true);
        routingType.GetMethod("ConfigureProductionMeasurement", flags)!.Invoke(routing, new[] { input2Config });
        routingType.GetField("_isExecutingAutoSuite", flags)!.SetValue(routing, false);
        if ((int?)routingEngine.GetType().GetProperty("RecordingChannel")!.GetValue(routingEngine) != 1)
            throw new Exception("One-based Recording Channel 2 was not mapped to Fast Track input index 1");
        if (Math.Abs((double)runnerType.GetProperty("PlaybackFrequencyScale")!.GetValue(runner)! - 0.91875) > 1E-9)
            throw new Exception("Route playback frequency scale was not applied");
        if (Math.Abs((double)runnerType.GetProperty("BassDistortionSampleScale")!.GetValue(runner)! - 1.0) > 1E-9)
            throw new Exception("Route bass THD sample scale was not applied");
        if ((double)runnerType.GetProperty("BassThdFrequencyHz")!.GetValue(runner)! != 80.0
            || (double)runnerType.GetProperty("MidThdFrequencyHz")!.GetValue(runner)! != 1000.0
            || (double)runnerType.GetProperty("TrebleThdFrequencyHz")!.GetValue(runner)! != 4000.0)
            throw new Exception("Route-specific THD frequencies were not applied");
        if ((int)runnerType.GetProperty("LogSweepVerificationRuns")!.GetValue(runner)! != 2)
            throw new Exception("Route-specific log-sweep repeatability count was not applied");
        if ((bool)runnerType.GetProperty("RequireLogSweepPhaseAlignment")!.GetValue(runner)!)
            throw new Exception("Electronic route still requires acoustic log-sweep phase alignment");
        if ((double)runnerType.GetProperty("MinimumInputSignalDbFs")!.GetValue(runner)! != -40.0)
            throw new Exception("Route minimum input signal threshold was not applied");
        testConfigType.GetProperty("FrequencyResponseMethod")!.SetValue(input2Config, "LogSweep");
        routingType.GetMethod("ApplyTestCaseConfig")!.Invoke(routing, new[] { input2Config });
        if (!(bool)runnerType.GetProperty("UseLogSweepFrequencyResponse")!.GetValue(runner)!)
            throw new Exception("Clock-compensated route did not honor the requested log sweep");
        if (!ReferenceEquals(routingType.GetField("_appliedTestConfig", flags)!.GetValue(routing), input2Config))
            throw new Exception("Manual measurement did not retain the selected route configuration");
        var inOutConfigType = assembly.GetType("SoncaAudioInspector.InOutConfig")!;
        var emptyModelConfig = Activator.CreateInstance(inOutConfigType)!;
        object?[] applyModelArgs = { emptyModelConfig, null };
        bool modelDevicesValid = (bool)routingType.GetMethod("ApplyModelDevices", new[]
        {
            inOutConfigType,
            typeof(string).MakeByRefType()
        })!.Invoke(routing, applyModelArgs)!;
        if (!modelDevicesValid
            || routingType.GetField("_appliedTestConfig", flags)!.GetValue(routing) != null
            || (string)routingType.GetField("_referenceRouteKey", flags)!.GetValue(routing)! != "manual")
            throw new Exception("Selecting a model still overrides the manual measurement route");
        routingEngine.GetType().GetProperty("PlaybackChannel")!.SetValue(routingEngine, 1);
        routingType.GetMethod("ConfigureProductionMeasurement", flags)!.Invoke(routing, new object?[] { null });
        if ((int?)routingEngine.GetType().GetProperty("RecordingChannel")!.GetValue(routingEngine) != 1
            || (int?)routingEngine.GetType().GetProperty("PlaybackChannel")!.GetValue(routingEngine) != 1)
            throw new Exception("Manual measurement reset the user's selected playback or recording channel");
        if ((double)runnerType.GetProperty("PlaybackFrequencyScale")!.GetValue(runner)! != 1.0)
            throw new Exception("Unconfigured route did not restore the default playback frequency scale");
        if (!(bool)runnerType.GetProperty("UseCombinedMultitoneFrequencyResponse")!.GetValue(runner)!)
            throw new Exception("Normal Multitone did not default to one combined capture");
        if (args.Length > 1 && args[1] == "--recording-channel-only")
        {
            Console.WriteLine("PASS model selection preserves manual routing; standard THD targets and route compensation are configurable.");
            app.Shutdown();
            return;
        }
        var liveFrequencies = (List<double>)routingType.GetField("_freqs", flags)!.GetValue(routing)!;
        var liveValues = (List<double>)routingType.GetField("_dbValues", flags)!.GetValue(routing)!;
        liveFrequencies.AddRange(new[] { 100.0, 1000.0, 8000.0 });
        liveValues.AddRange(new[] { -2.0, 0.0, 1.0 });
        liveFrequencies.Clear(); liveValues.Clear();
        routingType.GetField("_referenceRouteKey", flags)!.SetValue(routing, "AC001_FIRST_ROUTE");
        string keyBefore = (string)routingType.GetMethod("GetStandardDeviceKey", flags)!.Invoke(routing, null)!;
        ((TextBox)routing.FindName("TxtThdLimit")).Text = "7.5";
        string keyAfter = (string)routingType.GetMethod("GetStandardDeviceKey", flags)!.Invoke(routing, null)!;
        if (keyBefore != keyAfter) throw new Exception("THD limit changes frequency reference identity");
        routingType.GetField("_referenceRouteKey", flags)!.SetValue(routing, "AC002_SECOND_ROUTE");
        string ac002Key = (string)routingType.GetMethod("GetStandardDeviceKey", flags)!.Invoke(routing, null)!;
        if (keyBefore == ac002Key)
            throw new Exception("AC001 and AC002 share a reference file");
        if (!ac002Key.Contains("MULTITONE_ALL_P1100_T10A8_CHMIX_FS1000000", StringComparison.Ordinal))
            throw new Exception("Reference key does not isolate the pilot/channel algorithm version");
        var logSweepSwitch = (CheckBox)routing.FindName("ChkUseLogSweep");
        logSweepSwitch.IsChecked = true;
        runnerType.GetProperty("UseLogSweepFrequencyResponse")!.SetValue(runner, true);
        string logSweepKey = (string)routingType.GetMethod("GetStandardDeviceKey", flags)!.Invoke(routing, null)!;
        if (logSweepKey == ac002Key || !logSweepKey.Contains("LOGSWEEP_VECAVG_FDLY_G18-22K_EDGE13_HP10_PPO48_D8_PR5_CHMIX_FS1000000", StringComparison.Ordinal))
            throw new Exception("Multitone and logarithmic sweep share a reference identity");
        if (routing.FindName("BorderFreqHover") is not Border || routing.FindName("TxtFreqHover") is not TextBlock)
            throw new Exception("Frequency point hover panel is missing");
        liveFrequencies.AddRange(new[] { 100.0, 1000.0, 1001.0 });
        liveValues.AddRange(new[] { -2.0, 0.0, 0.05 });
        routing.Width = 1200;
        routing.Height = 800;
        routing.Measure(new Size(1200, 800));
        routing.Arrange(new Rect(0, 0, 1200, 800));
        routing.UpdateLayout();
        routingType.GetMethod("UpdateFrequencyHover", flags)!.Invoke(routing, new object[] { new Point(400, 200) });
        if (((Border)routing.FindName("BorderFreqHover")).Visibility != Visibility.Visible
            || !((TextBlock)routing.FindName("TxtFreqHover")).Text.Contains("Hz"))
            throw new Exception("Frequency hover did not show one nearest point");
        routingType.GetMethod("ExpandFreqChart")!.Invoke(routing, null);
        using var inputSource = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("FeedbackUiCheckKeyboard")
            { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        var escape = new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice, inputSource, 0, System.Windows.Input.Key.Escape)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        routingType.GetMethod("OnPreviewKeyDown", flags)!.Invoke(routing, new object[] { escape });
        if ((bool)routingType.GetProperty("IsFreqExpanded")!.GetValue(routing)!)
            throw new Exception("Escape did not exit frequency fullscreen mode");
        logSweepSwitch.IsChecked = false;
        object testConfig = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.TestConfig")!)!;
        testConfig.GetType().GetProperty("FrequencyResponseMethod")!.SetValue(testConfig, "LogSweep");
        routingType.GetMethod("ApplyTestCaseConfig")!.Invoke(routing, new[] { testConfig });
        if (logSweepSwitch.IsChecked != true || !(bool)runnerType.GetProperty("UseLogSweepFrequencyResponse")!.GetValue(runner)!)
            throw new Exception("Test configuration did not select LogSweep");
        var saveReference = routingType.GetMethod("SaveReferenceCurve", flags)!;
		var buildStandardPath = routingType.GetMethod("BuildLengthSafeStandardFilePath", BindingFlags.Static | BindingFlags.NonPublic)!;
		string longIdentity = "AC001_Line_In_3.5mm_TO_Measurement_Mic_LOGSWEEP_VECAVG_FDLY_G18-22K_EDGE13_HP10_PPO48_D8_PR5_CH1_CAL_99-00192.txt_[USB_Wired]_Speakers_(6- USB_Audio_Device)_IN_[USB_Wired]_Microphone_(KT_USB_Audio)_V_100_G_130_T_3.0";
		string compactReference = (string)buildStandardPath.Invoke(null, new object[] { directory, longIdentity })!;
		string compactReferenceAgain = (string)buildStandardPath.Invoke(null, new object[] { directory, longIdentity })!;
		string otherMicReference = (string)buildStandardPath.Invoke(null, new object[] { directory, longIdentity.Replace("KT_USB_Audio", "DEMO_USB_MIC") })!;
		if (compactReference.Length > 210 || compactReference != compactReferenceAgain || compactReference == otherMicReference)
			throw new Exception("Length-safe standard filename is not short, deterministic, or mic-specific");
		saveReference.Invoke(routing, new object?[] { compactReference, new[] { 100.0, 1000.0, 8000.0 }, new[] { -2.0, 0.0, 1.0 }, 3.0, null });
		if (!File.Exists(compactReference)) throw new Exception("Long standard identity was not saved through compact path");
        string firstReference = Path.Combine(directory, "test-reference-AC001.csv");
        saveReference.Invoke(routing, new object?[] { firstReference, new[] { 100.0, 1000.0, 8000.0 }, new[] { -2.0, 0.0, 1.0 }, 3.0, null });
        if (!File.Exists(firstReference) || File.ReadAllLines(firstReference).Length < 5) throw new Exception("AC001 reference not saved immediately");
        try
        {
            saveReference.Invoke(routing, new object?[] { Path.Combine(directory, "test-reference-AC002.csv"), new[] { 100.0 }, new[] { double.NaN }, 3.0, null });
            throw new Exception("Invalid second reference saved");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        if (!File.Exists(firstReference)) throw new Exception("AC002 failure erased AC001 reference");
        using (var advancedCancellation = new System.Threading.CancellationTokenSource())
        {
            view.GetType().GetField("_advancedCancellation", flags)!.SetValue(view, advancedCancellation);
            setBusy.Invoke(view, new object?[] { true, null });
            var advancedStop = (Button)view.FindName("BtnStopAdvanced");
            if (!advancedStop.IsEnabled) throw new Exception("Advanced stop disabled");
            advancedStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!advancedCancellation.IsCancellationRequested) throw new Exception("Advanced stop failed");
            view.GetType().GetField("_advancedCancellation", flags)!.SetValue(view, null);
            setBusy.Invoke(view, new object?[] { false, null });
        }
        var missing = runnerType.GetMethod("FindMissingFrequencyLimits", flags)!;
        if (((double[])missing.Invoke(runner, null)!).Length == 0) throw new Exception("Missing limits accepted");
        runnerType.GetProperty("StandardCurve")!.SetValue(runner,
            new Dictionary<double, double> { [20] = 0, [20000] = 0 });
        if (((double[])missing.Invoke(runner, null)!).Length != 0) throw new Exception("Covered limits rejected");
        runnerType.GetProperty("StandardCurve")!.SetValue(runner,
            new Dictionary<double, double> { [1000] = 0 });
        if (((double[])missing.Invoke(runner, null)!).Length == 0) throw new Exception("Partial limits accepted");
        if (assembly.GetType("SoncaAudioInspector.AudioRouting")!.GetMethod("EnsureRealisticSampleGraphPlots", flags) != null)
            throw new Exception("Synthetic QA graph exporter remains");
        var checkContext = view.GetType().GetMethod("CheckAcousticContext", flags)!;
        checkContext.Invoke(view, null);
        engine.GetType().GetProperty("PlaybackVolume")!.SetValue(engine, 0.123);
        checkContext.Invoke(view, null);
        view.GetType().GetMethod("SetCurrentContext")!.Invoke(view, new object[] { "TEST-MODEL", "1" });
        view.GetType().GetField("_feedbackReport", flags)!.SetValue(view, "{}");
        view.GetType().GetMethod("UpdateFeedbackButtons", flags)!.Invoke(view, null);
        if (!start.IsEnabled || !export.IsEnabled) throw new Exception("Completed measurement state");
        view.GetType().GetMethod("CompleteFeedbackAttempt", flags)!.Invoke(view, null);
        if (!start.IsEnabled || !export.IsEnabled) throw new Exception("Completion lost report");
        ((TextBox)view.FindName("FeedbackExpected")).Text = "5";
        ((CheckBox)view.FindName("FeedbackLimitsConfirmed")).IsChecked = true;
        view.GetType().GetMethod("SetCurrentContext")!.Invoke(view, new object[] { "D500", "UI-TEST" });
        if (export.IsEnabled) throw new Exception("Product change retained stale baseline/report");
        if (((TextBox)view.FindName("FeedbackExpected")).Text != "" || ((CheckBox)view.FindName("FeedbackLimitsConfirmed")).IsChecked == true)
            throw new Exception("Model change retained previous limits");
        using (var cancellation = new System.Threading.CancellationTokenSource())
        {
            view.GetType().GetField("_feedbackCancellation", flags)!.SetValue(view, cancellation);
            setBusy.Invoke(view, new object?[] { true, null });
            if (!stop.IsEnabled) throw new Exception("Stop unavailable while running");
            tabs.SelectedIndex = 2;
            if (!ReferenceEquals(tabs.SelectedItem, view.FindName("FeedbackTab"))) throw new Exception("Running measurement hid stop button");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!cancellation.IsCancellationRequested) throw new Exception("Stop did not request cancellation");
            view.GetType().GetField("_feedbackCancellation", flags)!.SetValue(view, null);
            setBusy.Invoke(view, new object?[] { false, null });
        }
        ((TextBox)view.FindName("FeedbackExpected")).Text = "5";
        ((CheckBox)view.FindName("FeedbackLimitsConfirmed")).IsChecked = true;
        ((TextBox)view.FindName("FeedbackTolerance")).Text = "0.6";
        if (((CheckBox)view.FindName("FeedbackLimitsConfirmed")).IsChecked == true) throw new Exception("Changed limits still approved");
        if (!((TextBlock)view.FindName("FeedbackStatus")).Text.Contains("CHƯA ĐO")) throw new Exception("Settings invalidation");
        tabs.SelectedItem = view.FindName("FeedbackTab");
        foreach (var size in new[] { new Size(1200, 900), new Size(1000, 760) })
        {
            view.Width = size.Width;
            view.Height = size.Height;
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
            if (start.ActualWidth < 70 || start.ActualHeight < 20) throw new Exception("Feedback controls did not render");
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"feedback-ui-{size.Width:0}.png"));
            encoder.Save(file);
        }

        VerifyServerGraphContract(assembly, directory);

        ((IDisposable)engine).Dispose();
        Console.WriteLine("PASS WPF construction, single-step measurement, model/limit invalidation, busy locks, stop visibility/cancellation, responsive renders, and 800x450 server graph contract.");
        app.Shutdown();
    }

    private static void VerifyThreePanelThdLayout(Assembly assembly, string directory)
    {
        var routing = (UserControl)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioRouting")!)!;
        if (routing.FindName("SliderRecordingGain") != null)
            throw new Exception("Software recording gain is still visible");
        if (routing.FindName("SliderPlaybackVolume") != null)
            throw new Exception("Windows playback volume must be read-only");
        if (routing.FindName("ComboPlaybackSampleRate") is not ComboBox sampleRates
            || sampleRates.Items.Count != 1
            || (sampleRates.Items[0] as ComboBoxItem)?.Tag?.ToString() != "44100")
            throw new Exception("Playback sample rate is not fixed to 44.1 kHz");
        Type engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
        object routingEngine = Activator.CreateInstance(engineType)!;
        PropertyInfo playbackSampleRate = engineType.GetProperty("PlaybackSampleRate")!;
        playbackSampleRate.SetValue(routingEngine, 44100);
        if ((int)playbackSampleRate.GetValue(routingEngine)! != 44100)
            throw new Exception("44.1 kHz playback selection is not applied");
        playbackSampleRate.SetValue(routingEngine, 48000);
        PropertyInfo sharedOnly = engineType.GetProperty("UseExclusivePlayback")!;
        sharedOnly.SetValue(routingEngine, true);
        if ((bool)sharedOnly.GetValue(routingEngine)!)
            throw new Exception("Audio engine can still switch to Exclusive mode");
        var playbackDevices = ((System.Collections.IEnumerable)engineType.GetMethod("GetPlaybackDevices")!
            .Invoke(routingEngine, null)!).Cast<object>().ToArray();
        if (playbackDevices.Length > 0)
        {
            MethodInfo readWindowsVolume = engineType.GetMethod("TryGetWindowsPlaybackVolume")!;
            object playbackDevice = playbackDevices[0];
            double currentWindowsVolume = (double)(readWindowsVolume.Invoke(routingEngine, new[] { playbackDevice })
                ?? throw new Exception("Cannot read the active Windows playback volume"));
            if (engineType.GetMethod("ApplyWindowsPlaybackVolume") != null
                || engineType.GetMethod("TrySetWindowsPlaybackVolume") != null)
                throw new Exception("Audio engine still exposes a Windows playback-volume write path");
            Console.WriteLine($"PASS read-only Windows endpoint volume on '{playbackDevice.GetType().GetProperty("FriendlyName")?.GetValue(playbackDevice)}' at {currentWindowsVolume:P0}.");
            foreach (IDisposable device in playbackDevices.OfType<IDisposable>()) device.Dispose();
        }
        object routingRunner = Activator.CreateInstance(
            assembly.GetType("SoncaAudioInspector.TestRunner")!, routingEngine)!;
        routing.GetType().GetField("_audioEngine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(routing, routingEngine);
        routing.GetType().GetField("_testRunner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(routing, routingRunner);
        ((TextBox)routing.FindName("TxtLogSweepDuration")).Text = "17";
        ((TextBox)routing.FindName("TxtMultitoneDuration")).Text = "19";
        Type testConfigType = assembly.GetType("SoncaAudioInspector.TestConfig")!;
        object testConfig = Activator.CreateInstance(testConfigType)!;
        testConfigType.GetProperty("LogSweepDurationSeconds")!.SetValue(testConfig, 8.0);
        testConfigType.GetProperty("MultitoneDurationSeconds")!.SetValue(testConfig, 10.0);
        routing.GetType().GetMethod("ConfigureProductionMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(routing, new[] { testConfig });
        if ((double)routingRunner.GetType().GetProperty("LogSweepDurationSeconds")!.GetValue(routingRunner)! != 17.0
            || (double)routingRunner.GetType().GetProperty("MultitoneDurationSeconds")!.GetValue(routingRunner)! != 19.0)
            throw new Exception("Per-test JSON still overwrites the durations entered in the UI");
        var plots = new[] { "PlotThdFft", "PlotThdFft1k", "PlotThdFft4k" }
            .Select(name => (FrameworkElement?)routing.FindName(name) ?? throw new Exception($"Missing {name}"))
            .ToArray();
        if (plots.Select(Grid.GetColumn).Distinct().Count() != 3)
            throw new Exception("THD plots do not occupy three separate columns");
        if (plots[0].Parent is not Grid grid || grid.ColumnDefinitions.Count != 3
            || grid.ColumnDefinitions.Any(column => column.Width.GridUnitType != GridUnitType.Star)
            || grid.ColumnDefinitions.Select(column => column.Width.Value).Distinct().Count() != 1)
            throw new Exception("THD plot columns are not equally sized");

		routing.GetType().GetMethod("ExpandThdChart")!.Invoke(routing, null);
		if (plots.Any(plot => Grid.GetColumn(plot) != 0)
			|| !plots.Select(Grid.GetRow).SequenceEqual(new[] { 0, 1, 2 })
			|| grid.ColumnDefinitions[1].Width.Value != 0
			|| grid.ColumnDefinitions[2].Width.Value != 0)
			throw new Exception("Fullscreen THD plots are not stacked vertically at full width");
		routing.GetType().GetMethod("RestoreChartsLayout")!.Invoke(routing, null);
		if (plots.Select(Grid.GetColumn).Distinct().Count() != 3 || plots.Any(plot => Grid.GetRow(plot) != 0))
			throw new Exception("THD plots did not restore to the horizontal layout");

        MethodInfo update = routing.GetType().GetMethod("UpdateThdFftChart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo formatValue = routing.GetType().GetMethod("FormatThdValueLabel", BindingFlags.Static | BindingFlags.NonPublic)!;
        if (!string.Equals(formatValue.Invoke(null, new object[] { 0.125, false, false }) as string,
                "THD: 0.125%", StringComparison.Ordinal)
            || !string.Equals(formatValue.Invoke(null, new object[] { 0.125, true, false }) as string,
                "THD: 0.125%", StringComparison.Ordinal))
            throw new Exception("Finite THD labels must remain white/neutral without invalid wording");
        double[] frequencies = { 0, 80, 160, 1000, 4000, 8000 };
        double[] magnitudes = { 0.001, 0.1, 0.01, 0.02, 0.005, 0.002 };
        update.Invoke(routing, new object[] { 80.0, frequencies, magnitudes, 0.2, true });
        update.Invoke(routing, new object[] { 1000.0, frequencies, magnitudes, 0.1, false });
        update.Invoke(routing, new object[] { 4000.0, frequencies, magnitudes, 0.05, true });

        if (routing.GetType().GetMethod("SaveThdGraphsPng", BindingFlags.Instance | BindingFlags.NonPublic) != null)
            throw new Exception("Distortion image export still exists");
    }

    private static void VerifyServerGraphContract(Assembly assembly, string directory)
    {
        var audioRoutingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        var audioRouting = (UserControl)Activator.CreateInstance(audioRoutingType)!;
        object frequencyPlotControl = audioRouting.FindName("PlotFreqResponse")!;
        object plot = frequencyPlotControl.GetType().GetProperty("Plot")!.GetValue(frequencyPlotControl)!;
        string serverGraphPath = Path.Combine(directory, "server-graph-contract-test.png");
        audioRoutingType.GetMethod("SaveServerGraphPng", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new[] { plot, serverGraphPath });
        using (var graphStream = File.OpenRead(serverGraphPath))
        {
            var decoder = new PngBitmapDecoder(
                graphStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            if (frame.PixelWidth != 800 || frame.PixelHeight != 450)
                throw new Exception($"Server graph contract changed: {frame.PixelWidth}x{frame.PixelHeight}");
        }
        File.Delete(serverGraphPath);
        if (Directory.EnumerateFiles(directory, ".fullscreen-*.png").Any())
            throw new Exception("Fullscreen graph temporary file was not cleaned up");
    }

    private static void VerifySelectedMeasurementMicrophoneWins(Assembly assembly)
    {
        object engine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        object recordings = engine.GetType().GetMethod("GetRecordingDevices")!.Invoke(engine, null)!;
        object? umik = ((System.Collections.IEnumerable)recordings).Cast<object>()
            .FirstOrDefault(device => ((string)device.GetType().GetProperty("FriendlyName")!.GetValue(device)!)
                .Contains("UMIK-1", StringComparison.OrdinalIgnoreCase));
        if (umik == null) throw new Exception("UMIK-1 endpoint is not connected");

        Type routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        var routing = (UserControl)Activator.CreateInstance(routingType)!;
        var combo = (ComboBox)routing.FindName("ComboRecording");
        Type deviceItemType = assembly.GetType("SoncaAudioInspector.DeviceItem")!;
        foreach (object device in (System.Collections.IEnumerable)recordings)
        {
            string friendlyName = (string)device.GetType().GetProperty("FriendlyName")!.GetValue(device)!;
            object item = Activator.CreateInstance(deviceItemType, device, "[TEST] " + friendlyName)!;
            combo.Items.Add(item);
            if (ReferenceEquals(device, umik)) combo.SelectedItem = item;
        }

        object? resolved = routingType.GetMethod("ResolveRecordingDevice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(routing, new[] { "Measurement Mic", "KT USB", recordings });
        string selectedId = (string)umik.GetType().GetProperty("ID")!.GetValue(umik)!;
        string? resolvedId = (string?)resolved?.GetType().GetProperty("ID")!.GetValue(resolved);
        if (!string.Equals(selectedId, resolvedId, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Legacy KT USB mapping replaced the selected UMIK-1 endpoint");
        ((IDisposable)engine).Dispose();
    }
}
