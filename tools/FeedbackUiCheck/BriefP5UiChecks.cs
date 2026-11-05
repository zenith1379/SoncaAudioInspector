using System.Collections;
using System.Diagnostics;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class BriefP5UiChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var value in Tree(child)) yield return value;
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Tree(root).OfType<T>().Single(e => e.Name == name);
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Hidden)?.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
    private static void Put(object owner, string name, object? value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);
    private static Func<object, Task<bool>> _submit = _ => throw new InvalidOperationException();
    private static Task<bool> Submit<T>(T session) => _submit(session!);
    public static void Run(Assembly assembly, string output, string? screenshotDir)
    {
        Directory.CreateDirectory(output); screenshotDir ??= Path.Combine(output, "screenshots"); Directory.CreateDirectory(screenshotDir);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); File.WriteAllLines(Path.Combine(output, "progress.txt"), checks); }
        Type Type(string name) => assembly.GetType("SoncaAudioInspector." + name)!;
        object Read(string name, string json) => JsonSerializer.Deserialize(json, Type(name))!;
        var model = Read("ModelConfig", """
            {"model":"P5 offline", "testItems":{"InOut":{"Devices":{"Input":{"OUT":"mock-out"},"Output":{"IN":"mock-in"}},
            "Tests":[{"id":"IO001","name":"P5 policy", "enable":true,"PlaybackOut":"OUT","RecordingIn":"IN"}]}}}
            """);
        var editor = (Window)Activator.CreateInstance(Type("ModelConfigEditorWindow"), model)!;
        try
        {
            var tab = Tree(editor).OfType<TabItem>().Single(t => t.Header?.ToString() == "Biên & khoảng xét hõm");
            tab.IsSelected = true;
            var band = Named<CheckBox>(editor, "CheckBandAverage");
            var tol = Named<TextBox>(editor, "TxtBandAverageTolerance");
            var single = Named<CheckBox>(editor, "CheckSingleRunMargin");
            var margin = Named<TextBox>(editor, "TxtSingleRunMarginDb");
            var json = Named<TextBox>(editor, "ModelJson");
            Check(band.IsChecked == false && single.IsChecked == false && tol.Text == "" && margin.Text == ""
                && !json.Text.Contains("BandAverage") && !json.Text.Contains("SingleRunPass"), "Untouched policy form preserves clean JSON");
            Check(Tree(tab).OfType<TextBlock>().Any(t => t.Text.Contains("dễ lọt lỗi chập chờn (rè, tiếp xúc lỏng)") && t.Foreground == Brushes.Gold), "Visible gold intermittent-fault warning");
            band.IsChecked = true; single.IsChecked = true; Call(editor, "SynchronizeForm");
            Check(tol.Text == "1.5" && margin.Text == "1.0" && tol.IsEnabled && margin.IsEnabled, "Enabling initializes policy defaults 1.5 / 1.0");
            Check(json.Text.Contains("\"BandAverageEnabled\": true") && json.Text.Contains("\"SingleRunPassWhenMarginDbEnabled\": true"), "Checkboxes synchronize JSON projection");
            var error = (TextBlock)Field(editor, "_error")!;
            foreach (var (input, text, expected) in new[] { (tol, "0.09", "0.1"), (tol, "12.1", "12.0"), (tol, "NaN", "hữu hạn"),
                (margin, "0", "M"), (margin, "12.1", "M") })
            {
                input.Text = text; Call(editor, "SynchronizeForm");
                Check(error.Text.Contains(expected), "Immediate form validation " + input.Name + "=" + text);
                input.Text = input == tol ? "1.5" : "1.0"; Call(editor, "SynchronizeForm");
            }
            var parse = Type("ModelConfigEditorWindow").GetMethod("ParseModel", BindingFlags.Static | BindingFlags.Public)!;
            Check(parse.Invoke(null, ["P5 offline", json.Text]) != null && error.Foreground == Brushes.LightGray, "Valid new policy projection parses");
            band.IsChecked = false; single.IsChecked = false; Call(editor, "SynchronizeForm");
            Check(!tol.IsEnabled && !margin.IsEnabled && json.Text.Contains("\"BandAverageEnabled\": false") && json.Text.Contains("\"SingleRunPassWhenMarginDbEnabled\": false"), "Disabling explicitly preserves saved thresholds without enforcing options");
            band.IsChecked = true; single.IsChecked = true; Call(editor, "SynchronizeForm");
            // Only the editor is shown. No routing Loaded event or measurement workflow is invoked.
            editor.ShowActivated = false; editor.ShowInTaskbar = false;
            editor.Width = 1200; editor.Height = 1000; editor.Show(); editor.UpdateLayout(); Drain();
            var bitmap = new RenderTargetBitmap(1200, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(screenshotDir, "editor-p5-policy.png"))) png.Save(stream);
            var pixels = new byte[1200 * 1000 * 4]; bitmap.CopyPixels(pixels, 1200 * 4, 0);
            Check(pixels.Any(p => p != 0) && new FileInfo(Path.Combine(screenshotDir, "editor-p5-policy.png")).Length > 1000, "P5 policy screenshot contains rendered editor pixels (no measurement workflow)");
        }
        finally { editor.Close(); }

        var routing = (FrameworkElement)Activator.CreateInstance(Type("AudioRouting"))!;
        var engine = (IDisposable)Activator.CreateInstance(Type("AudioEngine"))!;
        var runner = Activator.CreateInstance(Type("TestRunner"), engine)!;
        Set(routing, "_audioEngine", engine); Set(routing, "_testRunner", runner);
        try
        {
            var row = Read("AutoTestCaseItem", """
                {"Id":"IO001","Name":"Replay P5","Status":"PASS","Config":{"id":"IO001","name":"Replay P5","PlaybackOut":"OUT","RecordingIn":"IN"},
                "ResponseFrequencies":[50,100,250,1000,4000,8000,18000],"ResponseLevels":[0,0.2,-0.1,0,0.2,-0.3,0],
                "StandardCurve":{"50":0,"100":0,"250":0,"1000":0,"4000":0,"8000":0,"18000":0},
                "FreqStatus":"PASS fixture","ThdStatus":"0.1% fixture"}
                """);
            string Snapshot() => JsonSerializer.Serialize(new[] { "Config", "ResponseFrequencies", "ResponseLevels", "UnsmoothedResponse",
                "StandardCurve", "ResponseAssessment", "AdditionalResults", "DistortionMeasurements", "Status" }
                .ToDictionary(name => name, name => row.GetType().GetProperty(name)!.GetValue(row)));
            string before = Snapshot();
            var times = new List<string>();
            foreach (bool lazy in new[] { false, true })
            {
                Set(routing, "_isExecutingAutoSuite", lazy);
                Call(routing, "DisplayAutoTestResult", row); // warm-up outside timing
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) Call(routing, "DisplayAutoTestResult", row);
                watch.Stop(); var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
                times.Add($"{(lazy ? "lazy" : "eager")} 1000 warmed UI replays: wall {watch.Elapsed.TotalMilliseconds:F3} ms; CPU {cpu.TotalMilliseconds:F3} ms");
                Call(routing, "SaveFullResponseGraphPng", Path.Combine(output, lazy ? "lazy.png" : "eager.png"));
            }
            Check(File.ReadAllBytes(Path.Combine(output, "lazy.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "eager.png"))), "Eager/lazy real QA PNG byte-identical (800x450)");
            string after = Snapshot();
            Check(before == after, "Eager/lazy UI keeps all snapshot measurements, assessment and additional results unchanged");
            Set(routing, "_isExecutingAutoSuite", false); Call(routing, "DisplayAutoTestResult", row);
            Check(!routing.IsLoaded, "Review reconstructs deferred auxiliary views without hardware discovery");
            File.WriteAllLines(Path.Combine(output, "ui-replay-timing.txt"), times);

            // Exercise actual Routing QA retry with a missing real PNG and an offline submit boundary.
            var sessionType = Type("CompletedMeasurementPendingQaSession");
            var typedCases = Array.CreateInstance(Type("AutoTestCaseItem"), 1); typedCases.SetValue(row, 0);
            string qaFolder = Path.GetFullPath(Path.Combine(output, "routing-qa-retry"));
            var session = sessionType.GetMethod("Create")!.Invoke(null, ["P5 offline", Call(routing, "AutoTestConfigurationKey"), "P5-serial",
                qaFolder, true, true, typedCases, Array.Empty<string>()])!;
            foreach (string path in (string[])sessionType.GetProperty("GraphPaths")!.GetValue(session)!)
                if (File.Exists(path)) File.Delete(path);
            Set(routing, "_pendingQaCompletionSession", session);
            var submitProperty = routing.GetType().GetProperty("QaCompletionSubmitOverride", Hidden)!;
            var submitDelegate = typeof(BriefP5UiChecks).GetMethod(nameof(Submit), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(sessionType).CreateDelegate(submitProperty.PropertyType);
            submitProperty.SetValue(routing, submitDelegate);
            int submissions = 0;
            _submit = _ => { submissions++; throw new IOException("fixture before SavePending"); };
            bool complete = ((Task<bool>)routing.GetType().GetMethod("RetryPendingQaCompletionAsync")!.Invoke(routing, null)!).GetAwaiter().GetResult();
            var graphs = (string[])sessionType.GetProperty("GraphPaths")!.GetValue(session)!;
            Check(!complete && Field(routing, "_pendingQaCompletionSession") != null && File.Exists(graphs[0])
                && ((TextBlock)routing.FindName("LblVerdict")).Text.Contains("QA chưa xong"), "D15 real Routing retry rebuilds missing QA PNG and retains separate completion state on pre-save error");
            var graphFrame = new PngBitmapDecoder(new Uri(graphs[0]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Check(graphFrame.PixelWidth == 800 && graphFrame.PixelHeight == 450 && submissions == 1, "D15 repaired QA image is actual 800x450 PNG, with no measurement invocation");
            Check(File.ReadAllBytes(graphs[0]).SequenceEqual(File.ReadAllBytes(Path.Combine(output, "eager.png"))), "D15 repaired PNG matches original QA PNG byte-for-byte");
            _submit = saved =>
            {
                submissions++;
                var stepType = Type("ServerEngine+AudioQaStepResult");
                var steps = Array.CreateInstance(stepType, 1); steps.SetValue(Activator.CreateInstance(stepType, "IO001", "PASS", "fixture"), 0);
                var pending = Activator.CreateInstance(Type("PendingAudioQaUpload"), "offline", "fixture", "P5-serial", true, steps,
                    graphs.Select(Path.GetFileName).ToArray(), true, "routing-P5", DateTimeOffset.UtcNow, false)!;
                return Task.FromResult(Type("LocalQaStorage").GetMethod("SavePending")!.Invoke(null, [pending, graphs, output]) != null);
            };
            complete = ((Task<bool>)routing.GetType().GetMethod("RetryPendingQaCompletionAsync")!.Invoke(routing, null)!).GetAwaiter().GetResult();
            Check(complete && submissions == 2 && Field(routing, "_pendingQaCompletionSession") == null
                && File.Exists(Path.Combine(qaFolder, ".pending-audio-qa.json")), "D15 real Routing retry queues durable QA and clears completion state without reacquisition");
            var interrupted = sessionType.GetMethod("Create")!.Invoke(null, ["P5 offline", Call(routing, "AutoTestConfigurationKey"), "P5-discard",
                qaFolder, true, true, typedCases, graphs])!;
            Set(routing, "_pendingQaCompletionSession", interrupted);
            var pendingSubmit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _submit = _ => pendingSubmit.Task;
            var pendingRetry = (Task<bool>)routing.GetType().GetMethod("RetryPendingQaCompletionAsync")!.Invoke(routing, null)!;
            Call(routing, "ClearPendingQaCompletion"); pendingSubmit.SetResult(false);
            pendingRetry.GetAwaiter().GetResult();
            Check(Field(routing, "_pendingQaCompletionSession") == null && !File.Exists(Path.Combine(qaFolder, "qa-completion-pending.json")),
                "D15 explicit discard during pending retry cannot resurrect memory session or durable completion marker");
            CheckC360Restore(assembly, routing, output, Check);
        }
        finally
        {
            foreach (string name in new[] { "_frequencyChartRefreshTimer", "_meterUiTimer" })
                if (Field(routing, name) is DispatcherTimer timer) timer.Stop();
            engine.Dispose();
        }
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P5 WPF assertions; offline only."));
        Console.WriteLine($"PASS {checks.Count} P5 WPF assertions, policy screenshot and eager/lazy QA PNG equality; no audio/BLE/HTTP.");
    }
    private static void CheckC360Restore(Assembly assembly, FrameworkElement routing, string output, Action<bool, string> check)
    {
        Type Type(string name) => assembly.GetType("SoncaAudioInspector." + name)!;
        object Read(string name, string json) => JsonSerializer.Deserialize(json, Type(name))!;
        var io = Read("InOutConfig", """
            {"BluetoothAutoRemove":false,"Devices":{"Input":{"OUT":"mock-out"},"Output":{"IN":"mock-in"}},
             "Tests":[{"id":"IO001","name":"P5 restore","SpeakerStage":"MicEffects","TestEffect":true,"FrequencyResponseMethod":"LogSweep",
              "Playback Out":"OUT","Recording In":"IN","MaximumRuns":1,"RequiredPasses":1,"enable":true}]}
            """);
        Put(io, "AutomationProfile", Type("C360ModelDefinition").GetField("Profile")!.GetRawConstantValue());
        var test = ((IList)io.GetType().GetProperty("Tests")!.GetValue(io)!)[0]!;
        var row = Activator.CreateInstance(Type("AutoTestCaseItem"))!;
        Put(row, "Id", "IO001"); Put(row, "Name", "P5 restore"); Put(row, "Config", test);
        var cases = (IList)Field(routing, "_autoTestCases")!; cases.Clear(); cases.Add(row);
        Set(routing, "_activeInOutConfig", io); Set(routing, "_reviewedAutoTestCase", null);
        var endpoints = Array.CreateInstance(Type("C360ModelEndpoint"), 2);
        endpoints.SetValue(Activator.CreateInstance(Type("C360ModelEndpoint"), "out", "mock-out", false), 0);
        endpoints.SetValue(Activator.CreateInstance(Type("C360ModelEndpoint"), "in", "mock-in", true), 1);
        var provider = routing.GetType().GetField("C360EndpointProvider", Hidden)!;
        provider.SetValue(routing, Expression.Lambda(provider.FieldType, Expression.Constant(endpoints, provider.FieldType.GenericTypeArguments[0])).Compile());
        var proxy = (P5RestoreBenchProxy)DispatchProxy.Create(Type("IC360ElectronicBench"), typeof(P5RestoreBenchProxy));
        proxy.Assembly = assembly; proxy.MissingUsb = false; proxy.FailRestore = true;
        var settings = Read("LogSweepSettings", """{"SampleRate":44100,"DurationSeconds":1.486077097505669,"Amplitude":0.1,"DecayAnalysisSeconds":3}""");
        var analyzer = Type("StandardAcousticMeasurement");
        var excitation = (float[])analyzer.GetMethod("GenerateLogSweep")!.Invoke(null, [settings])!;
        var capture = new float[4096 + excitation.Length + 44100 * 3];
        Array.Copy(excitation, 0, capture, 4096, excitation.Length);
        proxy.Measurement = analyzer.GetMethods().Single(m => m.Name == "AnalyzeLogSweep" && m.GetParameters().Length == 5)
            .Invoke(null, [excitation, capture, settings, null, -80d])!;
        var factory = routing.GetType().GetField("C360BenchFactory", Hidden)!;
        factory.SetValue(routing, Expression.Lambda(factory.FieldType, Expression.Constant(proxy, factory.FieldType.GenericTypeArguments[0])).Compile());
        Set(routing, "EnsureC360Connection", new Func<CancellationToken, Task<bool>>(_ => Task.FromResult(true)));
        var notices = new List<string>(); Set(routing, "SignalWarningPresenter", new Action<string, string>((message, _) => notices.Add(message)));
        ((CheckBox)routing.FindName("ChkSendToServer")).IsChecked = true;
        int submits = 0; _submit = _ => { submits++; return Task.FromResult(true); };
        string? previousData = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(routing.Dispatcher));
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.GetFullPath(Path.Combine(output, "isolated-c360-data")));
        try
        {
            void Run(bool reference = false)
            {
                var task = (Task)Call(routing, "RunC360ModelAutoTestAsync", reference, null)!;
                var timer = Stopwatch.StartNew();
                while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 30) { Drain(); Thread.Sleep(5); }
                if (!task.IsCompleted) throw new TimeoutException("P5 mocked C360 routing did not complete");
                task.GetAwaiter().GetResult();
            }
            Run();
            var session = Field(routing, "_c360AutoTestResumeSession");
            check(session != null && (bool)session.GetType().GetProperty("BleRestorePending")!.GetValue(session)!
                && ((IDictionary)session.GetType().GetProperty("Completed")!.GetValue(session)!).Count == 1
                && ((TextBlock)routing.FindName("LblVerdict")).Text == "CHƯA HOÀN TẤT" && submits == 0
                && ((Button)routing.FindName("BtnStartAutoTest")).Content.ToString() == "TIẾP TỤC ĐO TỰ ĐỘNG",
                "D14 real C360 Routing: restore failure after all stages PASS retains resume, CHƯA HOÀN TẤT and zero QA submissions; " + string.Join("; ", notices));
            int captures = proxy.Captures, writes = proxy.RestoreWrites; proxy.FailRestore = false;
            Run();
            check(proxy.Restored && proxy.Captures == captures && proxy.RestoreWrites > writes
                && Field(routing, "_c360AutoTestResumeSession") == null && submits == 1,
                $"D14 real C360 Routing: remaining=0 resume restores original baseline/readback, then finalizes and submits QA without another capture; restored={proxy.Restored}, captures={proxy.Captures}/{captures}, writes={proxy.RestoreWrites}/{writes}, submits={submits}; " + string.Join("; ", notices));
            // Acquire an offline LINE reference, then exercise the real low-signal warning/upload order.
            Put(test, "SpeakerStage", "LineIn"); Put(test, "TestEffect", false); Put(row, "Config", test);
            Put(proxy.Measurement, "PeakSample", .001); Put(proxy.Measurement, "SignalLevelDbFs", -60d);
            Run(reference: true);
            var events = new List<string>();
            _submit = _ => { events.Add("QA"); return Task.FromResult(true); };
            Set(routing, "SignalWarningPresenter", new Action<string, string>((message, title) =>
            { if (title == "Kiểm tra tín hiệu đo" && message.Contains("TÍN HIỆU ĐO THẤP")) events.Add("warning"); }));
            Run();
            check(row.GetType().GetProperty("Status")!.GetValue(row)?.ToString() == "PASS" && events.SequenceEqual(["QA", "warning"]),
                "Group A real C360 PASS with LowSignalWarning calls QA before displaying end-of-suite warning");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", previousData);
        }
    }
}

public class P5RestoreBenchProxy : C360ModelBenchProxy
{
    public bool FailRestore;
    public int RestoreWrites;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "WriteAsync" && Captures > 0)
        {
            RestoreWrites++;
            if (FailRestore && !((CancellationToken)args![2]!).CanBeCanceled && (ushort)args[0]! == 0x6400) throw new InvalidOperationException("P5 mock BLE restore failed");
        }
        return base.Invoke(method, args);
    }
}
