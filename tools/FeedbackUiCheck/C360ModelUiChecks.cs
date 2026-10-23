using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class C360ModelUiChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Assembly assembly, string fixture, string output, bool live, bool reference = false, string? modelConfig = null, string? testId = null, bool runButton = false)
    {
        Directory.CreateDirectory(output);
        string data = Path.Combine(output, "data"); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", data);
        File.WriteAllText(Path.Combine(data, "checking_config.json"), "{\"models\":[]}");
        File.WriteAllText(Path.Combine(data, "routing_value.json"), "{\"HeadroomEnabled\":false}");
        string sharedSequence=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SoncaAudioInspector","autotest_sequence.json");
        string? sequenceBefore=File.Exists(sharedSequence) ? File.ReadAllText(sharedSequence) : null;
        // A harness must never change the default Windows endpoint format while constructing MainWindow.
        assembly.GetType("SoncaAudioInspector.AudioSampleRateSetup")!.GetField("SuppressDeviceWrites",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,true);
        if (live)
        {
            string saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector", "c360-electronic.json");
            if (File.Exists(saved)) File.Copy(saved, Path.Combine(data, "c360-electronic.json"), true);
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        // WinRT discovery objects in the test host must be born in an MTA,
        // matching the production connection worker rather than a nested STA render frame.
        var ble = Task.Run(() => assembly.GetType("SoncaAudioInspector.SoncaBleManager")!.GetProperty("Instance")!.GetValue(null)!).GetAwaiter().GetResult();
        if (live) ble.GetType().GetEvent("LogMessage")!.AddEventHandler(ble, new Action<string>(message =>
        {
            lock (ble) File.AppendAllText(Path.Combine(output, "ble-timing.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }));
        if (!live) ble.GetType().GetMethod("SuppressAutomaticConnection")!.Invoke(ble, null);
        var main = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MainWindow")!)!;
        Application.Current.MainWindow = main;
        var routing = (FrameworkElement)main.GetType().GetField("_audioRoutingView", Hidden)!.GetValue(main)!;
        var type = routing.GetType();
        object Field(string name) => type.GetField(name, Hidden)!.GetValue(routing)!;
        void Set(string name, object? value) => type.GetField(name, Hidden)!.SetValue(routing, value);
        void Call(string name) => type.GetMethod(name, Hidden | BindingFlags.Public)!.Invoke(routing, null);
        var combo = (ComboBox)main.FindName("ComboModels");
        Check(combo.Items.Cast<string>().SequenceEqual(new[] { "C360" }) && combo.SelectedIndex == -1, "Seed model missing/selected automatically");
        Check(routing.FindName("BtnC360Electronic") == null, "Separate C360 button still present");
        var checking = main.GetType().GetField("_checkingConfig", Hidden)!.GetValue(main)!;
        var models = (System.Collections.IList)checking.GetType().GetProperty("models")!.GetValue(checking)!;
        object model = models[0]!;
        if (live && modelConfig != null)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(modelConfig));
            var candidate = json.RootElement.GetProperty("models").EnumerateArray().First(m => m.TryGetProperty("testItems", out var tests)
                && tests.TryGetProperty("InOut", out var io) && io.TryGetProperty("AutomationProfile", out var profile) && profile.GetString() == "C360Electronic");
            model = JsonSerializer.Deserialize(candidate.GetRawText(), assembly.GetType("SoncaAudioInspector.ModelConfig")!)!; models[0] = model;
        }
        var editor = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ModelConfigEditorWindow")!, new[] { model })!;
        string checkedModelName = live ? model.GetType().GetProperty("model")!.GetValue(model)!.ToString()! : "C360 tên có thể sửa";
        ((TextBox)editor.GetType().GetField("_name", Hidden)!.GetValue(editor)!).Text = checkedModelName;
        model = editor.GetType().GetMethod("ReadEditorModel", Hidden)!.Invoke(editor, null)!;
        models[0] = model;
        if (!live) SetMockEndpoints(assembly, routing, model);
        main.GetType().GetMethod("SaveCheckingConfig", Hidden)!.Invoke(main, null);
        main.GetType().GetMethod("LoadCheckingConfig", Hidden)!.Invoke(main, null);
        Check(combo.Items.Count == 1 && combo.Items[0]!.ToString() == checkedModelName, "Rename/reload duplicated or lost electronic profile");
        editor.Close();
        combo.SelectedIndex = 0;
        var rows = (System.Collections.IList)Field("_autoTestCases");
        Check(rows.Count == 4, "Normal Auto Test list missing four C360 tests");
        var auto = (Button)routing.FindName("BtnStartAutoTest");
        Check(auto.IsEnabled, "Normal Auto Test disabled");
        ((CheckBox)routing.FindName("ChkSendToServer")).IsChecked = false;
        var warnings = new List<string>(); Set("SignalWarningPresenter", new Action<string,string>((message, _) => warnings.Add(message)));
        bool Busy() => (bool)type.GetProperty("IsTestingBusy")!.GetValue(routing)!;
        object? singleRow = testId == null ? null : rows.Cast<object>().Single(r =>
        {
            var config = r.GetType().GetProperty("Config")!.GetValue(r)!;
            return config.GetType().GetProperty("id")!.GetValue(config)?.ToString() == testId;
        });
        if (live)
        {
            string sessionRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail data");
            var sessionsBefore = Directory.Exists(sessionRoot) ? Directory.GetFiles(sessionRoot, "summary.json", SearchOption.AllDirectories).ToHashSet() : new HashSet<string>();
            var liveWatch = System.Diagnostics.Stopwatch.StartNew();
            if (singleRow != null)
            {
                var test = singleRow.GetType().GetProperty("Config")!.GetValue(singleRow)!;
                if (runButton)
                {
                    type.GetMethod("ApplyTestCaseConfig")!.Invoke(routing, new[] { test });
                    ((Button)routing.FindName("BtnStart")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else _ = (Task)type.GetMethod("RunC360ModelAutoTestAsync", Hidden)!.Invoke(routing, new object?[] { reference, test })!;
            }
            else (reference ? (Button)routing.FindName("BtnSaveStandardReference") : auto).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !Busy(), 240);
            var sessionFiles = Directory.GetFiles(sessionRoot, "summary.json", SearchOption.AllDirectories)
                .Where(p => !sessionsBefore.Contains(p) && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "control-readbacks.jsonl"))).ToArray();
            Check(sessionFiles.Length == 1, "Expected exactly one completed GUI measurement session");
            File.WriteAllText(Path.Combine(output, "measurement-session.txt"), Path.GetDirectoryName(sessionFiles.Single()));
            Check(rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() != "RUNNING"), "Live run left measuring state");
            var measuredRows = singleRow == null ? rows.Cast<object>().ToArray() : new[] { singleRow };
            Check(measuredRows.All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() is "PASS" or "FAIL" or "INVALID"), "Live run did not finish requested stage attempts");
            Check(measuredRows.Count(r => r.GetType().GetProperty("ImpulseResult")!.GetValue(r) != null
                || r.GetType().GetProperty("FrequencyShift")!.GetValue(r) != null
                || reference && ((System.Collections.IDictionary)r.GetType().GetProperty("StandardCurve")!.GetValue(r)!).Count >= 3) >= (singleRow == null ? 2 : 1), "Requested captures/reference data missing");
            File.WriteAllText(Path.Combine(output, "first-run-timing.json"), JsonSerializer.Serialize(new { seconds = liveWatch.Elapsed.TotalSeconds, reference }));
            if (reference && singleRow == null && measuredRows.All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "PASS"))
            {
                int wavBefore = Directory.GetFiles(sessionRoot, "*.wav", SearchOption.AllDirectories).Length;
                liveWatch.Restart();
                ((Button)routing.FindName("BtnSaveStandardReference")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy(), 60);
                Check(Directory.GetFiles(sessionRoot, "*.wav", SearchOption.AllDirectories).Length == wavBefore, "Cached LINE standard unexpectedly played/recorded audio");
                Check(rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "PASS"), "Cached reference lost PASS state");
                Check(!rows[1]!.GetType().GetProperty("ThdStatus")!.GetValue(rows[1])!.ToString()!.Contains("CHƯA XÁC ĐỊNH"), "Cached frequency-shift values missing");
                File.WriteAllText(Path.Combine(output, "reference-reuse.json"), JsonSerializer.Serialize(new { seconds = liveWatch.Elapsed.TotalSeconds, additionalWavFiles = 0, passed = true }));
            }
            Console.WriteLine("PASS live model " + (reference ? "reference" : "Auto Test") + " finished stage attempts; measured verdicts are recorded separately; no server upload.");
        }
        else
        {
            var proxy = (C360ModelBenchProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.IC360ElectronicBench")!, typeof(C360ModelBenchProxy));
            proxy.Assembly = assembly;
            proxy.Measurement = JsonSerializer.Deserialize(File.ReadAllText(fixture), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!,
                new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })!;
            var factory = type.GetField("C360BenchFactory", Hidden)!;
            factory.SetValue(routing, Expression.Lambda(factory.FieldType, Expression.Constant(proxy, factory.FieldType.GenericTypeArguments[0])).Compile());
            Set("EnsureC360Connection", new Func<CancellationToken,Task<bool>>(_ => Task.FromResult(true)));
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == 4 && proxy.Restored, "Normal button did not dispatch/restore C360 suite: captures="+proxy.Captures+"; "+string.Join("; ",warnings));
            Check(rows.Cast<object>().Select(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString()).SequenceEqual(new[] { "PASS", "PASS", "PASS", "INVALID" }), "Stage results missing from standard list");
            Check(warnings.Any(w => w.Contains("USB A")), "USB A missing signal warning absent");
            Call("ClearC360ModelResults");
            proxy.HoldCapture = proxy.HoldCleanup = true;
            var pending = (Task)type.GetMethod("RunC360ModelAutoTestAsync", Hidden)!.Invoke(routing, new object?[] { false, null })!;
            Until(() => proxy.Captures == 5);
            Check(((Button)main.FindName("BtnDiscardMeasurement")).Visibility == Visibility.Visible, "Stop is hidden while normal Auto Test runs");
            Call("CancelAndDiscardCurrentMeasurement"); Until(() => proxy.Restoring);
            Check(Busy() && !pending.IsCompleted, "Cancellation released UI before speaker restoration");
            proxy.Cleanup.SetResult(); Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Check(!Busy() && proxy.Restored && rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "WAITING"), "Cancellation left stale result/busy state");
            proxy.HoldCapture = proxy.HoldCleanup = proxy.Restoring = false; proxy.MissingUsb = false;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Restored && rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "PASS"), "Rerun after cancellation failed");
            proxy.InvalidShift = true;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            var invalidValues = (System.Collections.IEnumerable)rows[1]!.GetType().GetProperty("MeasurementValues")!.GetValue(rows[1])!;
            Check(!invalidValues.Cast<object>().Any(v => v.GetType().GetProperties().Any(p => p.GetValue(v)?.ToString()?.Contains("NaN") == true)), "Invalid shift displays NaN measurements");
            proxy.InvalidShift = false;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            var levelValues = (System.Collections.IEnumerable)rows[1]!.GetType().GetProperty("MeasurementValues")!.GetValue(rows[1])!;
            Check(levelValues.Cast<object>().Count(v => v.GetType().GetProperties().Any(p => p.GetValue(v)?.ToString()?.StartsWith("Mức tone BẬT") == true)) == 3,
                "Separate per-carrier FBX level changes missing from measurement details");
            int beforeReference = proxy.Captures;
            var referenceButton = (Button)routing.FindName("BtnSaveStandardReference");
            referenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == beforeReference + 8 && proxy.Restored, "Reference did not use two same-config captures per stage and BLE restoration");
            Check(Directory.GetFiles(Path.Combine(data, "save standards"), "c360-*.json").Length == 4, "Four stage references not saved");
            beforeReference = proxy.Captures;
            referenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == beforeReference, "Matching references were unnecessarily remeasured");
            Check(!rows[1]!.GetType().GetProperty("ThdStatus")!.GetValue(rows[1])!.ToString()!.Contains("CHƯA XÁC ĐỊNH"), "Cached FBX reference discarded its measured shift");
            var activeConfig = Field("_activeInOutConfig");
            var activeTests = (System.Collections.IList)activeConfig.GetType().GetProperty("Tests")!.GetValue(activeConfig)!;
            var lineConfig = activeTests[2]!;
            lineConfig.GetType().GetProperty("LogSweepDurationSeconds")!.SetValue(lineConfig, 7.2);
            type.GetMethod("ApplyTestCaseConfig")!.Invoke(routing, new[] { lineConfig });
            type.GetMethod("ConfigureProductionMeasurement", Hidden)!.Invoke(routing, new[] { lineConfig });
            var runner = Field("_testRunner");
            Check(Math.Abs((double)runner.GetType().GetProperty("LogSweepDurationSeconds")!.GetValue(runner)! - 7.2) < .0001,
                "Manual/reference runner ignored edited model duration");
            referenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == beforeReference + 2, "Config edit did not invalidate exactly the affected stage reference");
            beforeReference = proxy.Captures;
            ((Button)routing.FindName("BtnStart")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == beforeReference + 1 && proxy.Restored, "Manual LINE did not use selected model stage and routed backend");
            var shownReference = (System.Collections.IDictionary)rows[2]!.GetType().GetProperty("StandardCurve")!.GetValue(rows[2])!;
            Check(shownReference.Count >= 3, "Compared C360 reference missing from reviewed graph");
            Console.WriteLine("PASS shared reference/Auto Test/manual BLE pipeline, two-run reference readback, zero-capture reuse, per-stage invalidation after config edit and model duration propagation. Mock audio only.");
            Console.WriteLine("PASS normal model dropdown/seed/rename/save/reload; no separate C360 button; normal Auto Test dispatch and four results; USB A missing signal popup; cancellation waits for restore; discarded rows cleared; rerun PASS. Mock capture only.");
        }
        var status = rows.Cast<object>().Select(r => new { name = r.GetType().GetProperty("Name")!.GetValue(r), status = r.GetType().GetProperty("Status")!.GetValue(r), summary = r.GetType().GetProperty("ThdStatus")!.GetValue(r) }).ToArray();
        bool sharedSequenceUnchanged=sequenceBefore==(File.Exists(sharedSequence) ? File.ReadAllText(sharedSequence) : null);
        if(!live)
        {
            Check(sharedSequenceUnchanged && File.Exists(Path.Combine(data,"autotest_sequence.json")),"Mock test changed the shared sequence or did not use its isolated sequence");
            Console.WriteLine("PASS offline isolation: mock endpoint inventory/capture rate, Windows format writes suppressed, shared sequence unchanged and test sequence stored under artifact.");
        }
        File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new { live, status, warnings, windowsFormatWritesSuppressed=true, sharedSequenceUnchanged }, new JsonSerializerOptions { WriteIndented = true }));
        type.GetMethod("DisplayAutoTestResult", Hidden)!.Invoke(routing, new[] { singleRow ?? rows[1] });
        var content = (FrameworkElement)main.Content;
        main.Content = null;
        var renderHost = new Window { Content = content, Width = 1920, Height = 1080, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, Background = Brushes.Black };
        renderHost.Show();
        var renderWait = System.Diagnostics.Stopwatch.StartNew();
        Until(() => renderWait.ElapsedMilliseconds > 150);
        content.Measure(new Size(1920,1080)); content.Arrange(new Rect(0,0,1920,1080)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "model-autotest.png"))) encoder.Save(file);
        renderHost.Close();
        // The window is intentionally never shown: no login/shared-config load or live meters.
        main.GetType().GetMethod("OnClosed", Hidden)!.Invoke(main, new object[] { EventArgs.Empty });
        if (live) ble.GetType().GetMethod("Disconnect")!.Invoke(ble, null);
    }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static void SetMockEndpoints(Assembly assembly, FrameworkElement routing, object model)
    {
        var endpoint = assembly.GetType("SoncaAudioInspector.C360ModelEndpoint")!;
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(endpoint))!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(model,model.GetType()));
        var devices=json.RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Devices");
        foreach(string direction in new[]{"Input","Output"})
        foreach(string name in devices.GetProperty(direction).EnumerateObject().Select(p=>p.Value.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase))
            list.Add(Activator.CreateInstance(endpoint,"mock-endpoint-"+list.Count,name,direction=="Output"));
        var provider=routing.GetType().GetField("C360EndpointProvider",Hidden)!;
        provider.SetValue(routing,Expression.Lambda(provider.FieldType,Expression.Constant(list,provider.FieldType.GenericTypeArguments[0])).Compile());
    }
    private static void Until(Func<bool> done, int seconds = 30)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && watch.Elapsed.TotalSeconds < seconds)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(5);
        }
        Check(done(), "UI test timed out");
    }
}

public class C360ModelBenchProxy : DispatchProxy
{
    public Assembly Assembly = null!;
    public object Measurement = null!;
    public int Captures;
    public bool MissingUsb = true, HoldCapture, HoldCleanup, Restoring, InvalidShift;
    public readonly TaskCompletionSource Cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<ushort, ushort> Initial = new() { [0x6254]=72,[0x6242]=4,[0x6400]=4097,[0x6447]=1,[0x6448]=0,[0x6449]=0,[0x6468]=0,[0x6404]=0,[0x640A]=0 };
    private Dictionary<ushort,ushort>? _state;
    private Dictionary<ushort,ushort> State => _state ??= new(Initial);
    public bool Restored => Initial.All(p => State[p.Key] == p.Value);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_DeviceIdentity": return "fixture-C360";
            case "StopAudio": return null;
            case "ReadAsync": return Task.FromResult<IReadOnlyDictionary<ushort,ushort>>(new Dictionary<ushort,ushort>(State));
            case "WriteAsync":
                State[(ushort)args![0]!] = (ushort)args[1]!;
                if (HoldCleanup && !((CancellationToken)args[2]!).CanBeCanceled) { Restoring = true; return Cleanup.Task; }
                return Task.CompletedTask;
            case "CaptureAsync":
                Captures++;
                return typeof(C360ModelBenchProxy).GetMethod(nameof(Capture), BindingFlags.Instance|BindingFlags.NonPublic)!
                    .MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(this, new[] { args![0], args[2] });
            default: throw new NotSupportedException(method.Name);
        }
    }
    private async Task<T> Capture<T>(object stage, object boxedToken)
    {
        var token = (CancellationToken)boxedToken;
        if (HoldCapture) await Task.Delay(Timeout.Infinite, token);
        await Task.Delay(15, token);
        bool missing = MissingUsb && stage.ToString() == "UsbA";
        bool shift = stage.ToString() == "MicFrequencyShift";
        var result = (T)Activator.CreateInstance(typeof(T), stage, missing ? "NO_SIGNAL" : "PASS", missing ? "USB A: chưa có tín hiệu phát" : "fixture: đã đo", missing || shift ? null : Measurement)!;
        if (shift && InvalidShift)
        {
            result = (T)Activator.CreateInstance(typeof(T), stage, "INVALID", "Không đủ tone", null)!;
            var invalid = Assembly.GetType("SoncaAudioInspector.FrequencyShiftResult")!.GetMethod("Invalid")!.Invoke(null, new object?[] { "Không đủ tone", null, null });
            typeof(T).GetProperty("FrequencyShift")!.SetValue(result, invalid); return result;
        }
        if (shift)
        {
            var points = new[] { 700, 1700, 3700 }.Select(f => new { NominalHz = f, MeasuredHz = f - 3.0, StabilityHz = .01, LevelDbFs = -25, NarrowBandSnrDb = 50 }).ToArray();
            var on = points.Select((p,i) => new { p.NominalHz, p.MeasuredHz, p.StabilityHz, LevelDbFs = p.LevelDbFs - (i == 2 ? 25 : 12), p.NarrowBandSnrDb }).ToArray();
            object observed = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { IsValid = true, Detected = true, ShiftHz = 0, UncertaintyHz = .15,
                RelativeClockPpm = 0, FitResidualHz = 0, Summary = "fixture di tần -3 Hz; mức tone giảm khi bật FBX", Off = points, On = on, FbxChangeDetected = false,
                SampleRate = 44100, OffShiftHz = -3, OnShiftHz = -3, OffClockPpm = 0, OnClockPpm = 0 }), Assembly.GetType("SoncaAudioInspector.FrequencyShiftResult")!)!;
            typeof(T).GetProperty("FrequencyShift")!.SetValue(result, observed);
        }
        return result;
    }
}
