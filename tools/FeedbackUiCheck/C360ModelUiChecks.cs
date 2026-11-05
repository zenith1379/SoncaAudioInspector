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
    public static void Run(Assembly assembly, string fixture, string output, bool live, bool reference = false, string? modelConfig = null, string? testId = null, bool runButton = false,
        string? bleAddress = null, string? referenceDirectory = null, bool allowNonstandardRate = false)
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
            if (bleAddress != null)
            {
                string setupPath=Path.Combine(data,"c360-electronic.json");
                var setup=File.Exists(setupPath) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(setupPath))!.AsObject() : new System.Text.Json.Nodes.JsonObject();
                setup["BleControllerAddress"]=Convert.ToUInt64(bleAddress,16);
                File.WriteAllText(setupPath,setup.ToJsonString());
            }
            if(referenceDirectory!=null)
            {
                string standards=Path.Combine(data,"save standards");Directory.CreateDirectory(standards);
                foreach(string file in Directory.GetFiles(referenceDirectory,"c360-*.json"))File.Copy(file,Path.Combine(standards,Path.GetFileName(file)));
            }
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
        if (live && modelConfig != null)
        {
            // MainWindow can replace its model list while reloading portable
            // settings. Physical qualification must use the supplied snapshot,
            // not the seed model retained by an earlier list/editor reference.
            using var document = JsonDocument.Parse(File.ReadAllText(modelConfig));
            var expected = document.RootElement.GetProperty("models").EnumerateArray().Single(m =>
                m.TryGetProperty("testItems", out var tests) && tests.TryGetProperty("InOut", out var io)
                && io.TryGetProperty("AutomationProfile", out var profile) && profile.GetString() == "C360Electronic");
            var ioJson = expected.GetProperty("testItems").GetProperty("InOut");
            var ioType = assembly.GetType("SoncaAudioInspector.InOutConfig")!;
            object frozen = JsonSerializer.Deserialize(ioJson.GetRawText(), ioType)!;
            var apply = type.GetMethod("ApplyModelDevices", new[] { ioType, typeof(string).MakeByRefType() })!;
            object?[] arguments = { frozen, null };
            Check((bool)apply.Invoke(routing, arguments)!, "Configured live endpoints unavailable: " + arguments[1]);
            var active = type.GetField("_activeInOutConfig", Hidden)!.GetValue(routing)!;
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(frozen,ioType)),
                System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(active,ioType))),
                "Live measurement config differs from the supplied snapshot");
            File.WriteAllText(Path.Combine(output,"effective-model.json"),JsonSerializer.Serialize(active,ioType,new JsonSerializerOptions{WriteIndented=true}));
        }
        var rows = (System.Collections.IList)Field("_autoTestCases");
        C360ModelBenchProxy proxy = null!;
        Check(rows.Count == (live ? rows.Count : 6), "Normal Auto Test list missing four C360 tests");
        var auto = (Button)routing.FindName("BtnStartAutoTest");
        Check(auto.IsEnabled, "Normal Auto Test disabled");
        ((CheckBox)routing.FindName("ChkSendToServer")).IsChecked = false;
        var warnings = new List<string>(); Set("SignalWarningPresenter", new Action<string,string>((message, title) => { warnings.Add(message); File.AppendAllText(Path.Combine(output, "warnings.log"), title + ": " + message + Environment.NewLine); }));
        if(live && allowNonstandardRate)
        {
            var presenter=type.GetField("SampleRateQuestionPresenter",Hidden)!;
            var rates=Expression.Parameter(presenter.FieldType.GenericTypeArguments[0]);
            Func<object,bool> approved=request=>{File.AppendAllText(Path.Combine(output,"approved-sample-rates.jsonl"),JsonSerializer.Serialize(request,request.GetType())+Environment.NewLine);return true;};
            presenter.SetValue(routing,Expression.Lambda(presenter.FieldType,Expression.Invoke(Expression.Constant(approved),Expression.Convert(rates,typeof(object))),rates).Compile());
        }
        bool Busy() => (bool)type.GetProperty("IsTestingBusy")!.GetValue(routing)!;
        object? singleRow = testId == null ? null : rows.Cast<object>().Single(r =>
        {
            var config = r.GetType().GetProperty("Config")!.GetValue(r)!;
            return config.GetType().GetProperty("id")!.GetValue(config)?.ToString() == testId;
        });
        if (live)
        {
            main.Show(); // Windows basic pairing needs a real WPF app window.
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
            Until(() => !Busy(), 600);
            var sessionFiles = Directory.GetFiles(sessionRoot, "summary.json", SearchOption.AllDirectories)
                .Where(p => !sessionsBefore.Contains(p) && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "control-readbacks.jsonl"))).ToArray();
            File.WriteAllText(Path.Combine(output, "completion-state.json"), JsonSerializer.Serialize(new { sessions = sessionFiles, verdict = ((TextBlock)routing.FindName("LblVerdict")).Text, warnings,
                rows = rows.Cast<object>().Select(r => new { name = r.GetType().GetProperty("Name")!.GetValue(r), status = r.GetType().GetProperty("Status")!.GetValue(r), details = r.GetType().GetProperty("DistortionText")!.GetValue(r) }) }, new JsonSerializerOptions { WriteIndented = true }));
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
            if(!reference)C360BleAfterAutoTestChecks.Run(assembly,ble,output,Path.Combine(Path.GetDirectoryName(sessionFiles.Single())!,"bluetooth-cleanup.json"));
        }
        else
        {
            proxy = (C360ModelBenchProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.IC360ElectronicBench")!, typeof(C360ModelBenchProxy));
            File.WriteAllText(Path.Combine(data, "audio-bench-preferences.json"), "{\"MaximumRuns\":3,\"RequiredPasses\":2,\"BluetoothAutoRemove\":true}");
            var io=Field("_activeInOutConfig");io.GetType().GetProperty("BluetoothAutoRemove")!.SetValue(io,true);
            int cleanups = 0;
            Set("CompletedBluetoothCleanup", new Func<Task>(() => { Check(proxy.Restored, "Bluetooth cleanup ran before DSP restoration"); cleanups++; return Task.CompletedTask; }));
            proxy.Assembly = assembly;
            proxy.Measurement = JsonSerializer.Deserialize(File.ReadAllText(fixture), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!,
                new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals })!;
            var factory = type.GetField("C360BenchFactory", Hidden)!;
            factory.SetValue(routing, Expression.Lambda(factory.FieldType, Expression.Constant(proxy, factory.FieldType.GenericTypeArguments[0])).Compile());
            Set("EnsureC360Connection", new Func<CancellationToken,Task<bool>>(_ => Task.FromResult(true)));
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(cleanups == 1 && auto.Content.ToString() == "TIẾP TỤC ĐO TỰ ĐỘNG", "Fully attempted Auto Test with INVALID skipped Bluetooth cleanup or lost resumable results");
            Check(proxy.Captures == 12 && proxy.Restored, "Normal button did not dispatch/restore C360 suite: captures="+proxy.Captures+"; "+string.Join("; ",warnings));
            Check(rows.Cast<object>().Select(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString()).SequenceEqual(new[] { "PASS", "PASS", "PASS", "PASS", "INVALID", "PASS" }), "Stage results missing from standard list");
            Check(warnings.Any(w => w.Contains("USB A")), "USB A missing signal warning absent");
            Call("ClearC360ModelResults");
            proxy.HoldCapture = proxy.HoldCleanup = true;
            var pending = (Task)type.GetMethod("RunC360ModelAutoTestAsync", Hidden)!.Invoke(routing, new object?[] { false, null })!;
            Until(() => proxy.Captures == 13);
            Check(((Button)main.FindName("BtnDiscardMeasurement")).Visibility == Visibility.Visible, "Stop is hidden while normal Auto Test runs");
            Call("CancelAndDiscardCurrentMeasurement"); Until(() => proxy.Restoring);
            Check(Busy() && !pending.IsCompleted, "Cancellation released UI before speaker restoration");
            proxy.Cleanup.SetResult(); Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Check(!Busy() && proxy.Restored && rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "WAITING"), "Cancellation left stale result/busy state");
            Check(cleanups == 1 && auto.Content.ToString() == "BẮT ĐẦU ĐO TỰ ĐỘNG", "Discarded suite retained its continuation or removed Bluetooth pairing");
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
            var mixedConfig=Field("_activeInOutConfig");
            var mixedTests=(System.Collections.IList)mixedConfig.GetType().GetProperty("Tests")!.GetValue(mixedConfig)!;
            var firstMixed=mixedTests[0]!;
            firstMixed.GetType().GetProperty("MaximumRuns")!.SetValue(firstMixed,1);firstMixed.GetType().GetProperty("RequiredPasses")!.SetValue(firstMixed,1);
            int mixedBefore=proxy.Captures;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>!Busy());
            Check(proxy.Captures==mixedBefore+11,"Per-test 1/1 policy was ignored by GUI suite; expected one capture plus five 2/3 stages");
            firstMixed.GetType().GetProperty("MaximumRuns")!.SetValue(firstMixed,3);firstMixed.GetType().GetProperty("RequiredPasses")!.SetValue(firstMixed,2);
            int beforeDisabledCleanup=cleanups;
            io.GetType().GetProperty("BluetoothAutoRemove")!.SetValue(io,false);
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>!Busy());
            Check(cleanups==beforeDisabledCleanup,"Legacy global Bluetooth flag overrode the model opt-out");
            io.GetType().GetProperty("BluetoothAutoRemove")!.SetValue(io,true);
            int completedCleanups = cleanups;
            int beforeReference = proxy.Captures;
            var referenceButton = (Button)routing.FindName("BtnSaveStandardReference");
            proxy.UnavailableBluetooth = true;
            referenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            string partialVerdict = ((TextBlock)routing.FindName("LblVerdict")).Text;
            var partialStatuses = rows.Cast<object>().Select(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString()).ToArray();
            Check(proxy.Captures == beforeReference + 11 && proxy.Restored && partialVerdict.StartsWith("LINE CHUẨN CHƯA ĐỦ")
                && partialStatuses.SequenceEqual(new[] { "PASS", "PASS", "PASS", "PASS", "PASS", "INVALID" }),
                "Unavailable BT left the reference session unfinished or erased earlier results");
            Check(Directory.GetFiles(Path.Combine(data, "save standards"), "c360-*.json").Length == 5,
                "Unavailable BT saved an unqualified reference or lost completed references");
            File.WriteAllText(Path.Combine(output,"partial-reference-state.json"),JsonSerializer.Serialize(new { verdict=partialVerdict, statuses=partialStatuses,
                savedReferences=5, bleStateRestored=proxy.Restored, offline=true },new JsonSerializerOptions{WriteIndented=true}));
            proxy.UnavailableBluetooth = false;
            beforeReference = proxy.Captures;
            referenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == beforeReference + 2 && proxy.Restored, "Retry did not reuse five completed references and acquire only BT");
            Check(Directory.GetFiles(Path.Combine(data, "save standards"), "c360-*.json").Length == 6, "Four stage references not saved");
            beforeReference = proxy.Captures;
            referenceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == beforeReference, "Matching references were unnecessarily remeasured");
            Check(!rows[1]!.GetType().GetProperty("ThdStatus")!.GetValue(rows[1])!.ToString()!.Contains("CHƯA XÁC ĐỊNH"), "Cached FBX reference discarded its measured shift");
            var activeConfig = Field("_activeInOutConfig");
            var activeTests = (System.Collections.IList)activeConfig.GetType().GetProperty("Tests")!.GetValue(activeConfig)!;
            var lineConfig = activeTests.Cast<object>().Single(t => t.GetType().GetProperty("SpeakerStage")!.GetValue(t)?.ToString() == "LineIn");
            lineConfig.GetType().GetProperty("SweepLengthSamples")!.SetValue(lineConfig, null);
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
            var shownReference = (System.Collections.IDictionary)rows[3]!.GetType().GetProperty("StandardCurve")!.GetValue(rows[3])!;
            Check(shownReference.Count >= 3, "Compared C360 reference missing from reviewed graph");
            Check(cleanups == completedCleanups, "Reference/manual measurement removed Bluetooth pairing");
            Set("CompletedBluetoothCleanup", new Func<Task>(() => throw new InvalidOperationException("unpair fixture failure")));
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(auto.IsEnabled && warnings.Any(w => w.Contains("unpair fixture failure")), "Bluetooth cleanup failure stranded Auto Test or was hidden");
            Check(rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() != "RUNNING"), "Cleanup error left a running row");
            Console.WriteLine("PASS shared reference/Auto Test/manual BLE pipeline, two-run reference readback, zero-capture reuse, per-stage invalidation after config edit and model duration propagation. Mock audio only.");
            Console.WriteLine("PASS normal model dropdown/seed/rename/save/reload; no separate C360 button; normal Auto Test dispatch and six results with 3/2 voting; USB A missing signal popup; cancellation waits for restore; discarded rows cleared; rerun PASS. Mock capture only.");
        }
        var status = rows.Cast<object>().Select(r => new { name = r.GetType().GetProperty("Name")!.GetValue(r), status = r.GetType().GetProperty("Status")!.GetValue(r), summary = r.GetType().GetProperty("ThdStatus")!.GetValue(r) }).ToArray();
        bool sharedSequenceUnchanged=sequenceBefore==(File.Exists(sharedSequence) ? File.ReadAllText(sharedSequence) : null);
        if(!live)
        {
            Check(sharedSequenceUnchanged && File.Exists(Path.Combine(data,"autotest_sequence.json")),"Mock test changed the shared sequence or did not use its isolated sequence");
            Console.WriteLine("PASS offline isolation: mock endpoint inventory/capture rate, Windows format writes suppressed, shared sequence unchanged and test sequence stored under artifact.");
        }
        File.WriteAllText(Path.Combine(output, "ui-results.json"), JsonSerializer.Serialize(new { live, status, warnings, windowsFormatWritesSuppressed=true, sharedSequenceUnchanged }, new JsonSerializerOptions { WriteIndented = true }));
        Set("_allowInlineEffectImpulse",true); // Same context change as selecting a saved Auto Test row.
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
        if (!live)
        {
            var effectRow = rows[0]!;
            effectRow.GetType().GetProperty("ImpulseResult")!.SetValue(effectRow, JsonSerializer.Deserialize(File.ReadAllText(fixture), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            type.GetMethod("DisplayAutoTestResult", Hidden)!.Invoke(routing, new[] { effectRow });
            var impulseHost = (ContentControl)routing.FindName("ImpulseEffectHost");
            Check(impulseHost.Visibility == Visibility.Visible && impulseHost.Content.GetType().Name == "ImpulseAnalysisView", "IO001 does not share the Ctrl+Shift+I impulse view");
            content.UpdateLayout();
            var irBitmap = new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); irBitmap.Render(content);
            var irEncoder = new PngBitmapEncoder(); irEncoder.Frames.Add(BitmapFrame.Create(irBitmap));
            using (var file = File.Create(Path.Combine(output, "io001-impulse.png"))) irEncoder.Save(file);
            Set("_frequencyViewAdjustedByUser", true);
            type.GetMethod("DisplayAutoTestResult", Hidden)!.Invoke(routing, new[] { rows[1] });
            Check(impulseHost.Visibility == Visibility.Collapsed && ((FrameworkElement)routing.FindName("PlotFreqResponse")).Visibility == Visibility.Visible, "IO002 hidden behind impulse");
            type.GetMethod("DisplayAutoTestResult", Hidden)!.Invoke(routing, new[] { rows[3] });
            Check(impulseHost.Visibility == Visibility.Collapsed, "Impulse overlay covers LINE graph");
            dynamic responsePlot = Field("PlotFreqResponse");
            var responseLimits = responsePlot.Plot.Axes.GetLimits();
            Check(Math.Abs((double)responseLimits.Left - Math.Log10(20)) < .01 && Math.Abs((double)responseLimits.Right - Math.Log10(20000)) < .01,
                "Review graph is cropped to its evaluation range");
            Check(Math.Abs((double)responsePlot.Plot.Axes.Left.TickGenerator.Interval - 1) < .001, "LINE response did not use 1 dB spacing");
            var responseValues = (double[])rows[3]!.GetType().GetProperty("ResponseLevels")!.GetValue(rows[3])!;
            Check(responseValues.Count(v => v >= (double)responseLimits.Bottom && v <= (double)responseLimits.Top) > responseValues.Length / 2,
                "Response graph kept IO002 Hz viewport and hid its dBFS data after zoom");
            foreach (int index in new[] { 1, 2, 3, 4, 5 })
            {
                type.GetMethod("DisplayAutoTestResult", Hidden)!.Invoke(routing, new[] { rows[index] });
                if (index != 1) Check(Math.Abs((double)responsePlot.Plot.Axes.Left.TickGenerator.Interval - 1) < .001, "Response did not use 1 dB spacing across MIC/LINE/USB/BT");
                content.UpdateLayout();
                var capture = new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); capture.Render(content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture));
                string id = rows[index]!.GetType().GetProperty("Id")!.GetValue(rows[index])!.ToString()!;
                using var pngFile = File.Create(Path.Combine(output, "review-" + id + ".png")); png.Save(pngFile);
            }
            var activePlan=type.GetMethod("BuildC360Plan",Hidden)!.Invoke(routing,new[]{Field("_activeInOutConfig")})!;
            var progressType=assembly.GetType("SoncaAudioInspector.C360ElectronicResult")!;
            var stageType=assembly.GetType("SoncaAudioInspector.C360ElectronicStage")!;
            var nextStage=Enum.Parse(stageType,"MicFrequencyShift");
            type.GetMethod("DisplayAutoTestResult",Hidden)!.Invoke(routing,new[]{rows[0]});
            string previousDetails=((TextBox)routing.FindName("TxtSweepDistortionMetrics")).Text;
            string previousVerdict=((TextBlock)routing.FindName("LblVerdict")).Text;
            var running=Activator.CreateInstance(progressType,nextStage,"RUNNING","next test",null)!;
            type.GetMethod("ApplyC360Progress",Hidden)!.Invoke(routing,new[]{running,activePlan});
            Check(ReferenceEquals(Field("_reviewedAutoTestCase"),rows[0])
                && ((TextBox)routing.FindName("TxtSweepDistortionMetrics")).Text==previousDetails
                && ((TextBlock)routing.FindName("LblVerdict")).Text==previousVerdict,"Starting the next stage replaced the completed preview");
            content.UpdateLayout();
            var items=(ItemsControl)routing.FindName("ListAutoTestCases");
            var presenter=(ContentPresenter)items.ItemContainerGenerator.ContainerFromItem(rows[1]);
            var activeBorder=(Border)items.ItemTemplate.FindName("AutoTestCaseRow",presenter);
            Until(()=>activeBorder.BorderBrush.ToString()=="#FFFFD54F");
            Check(activeBorder.Background.ToString()=="#FF30270E"&&activeBorder.BorderThickness.Left==2,
                "The active IO row is not visibly yellow while the previous result is held");
            var heldImage=new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32);heldImage.Render(content);
            var heldEncoder=new PngBitmapEncoder();heldEncoder.Frames.Add(BitmapFrame.Create(heldImage));
            using(var file=File.Create(Path.Combine(output,"io001-result-io002-running.png")))heldEncoder.Save(file);
            var done=Activator.CreateInstance(progressType,nextStage,"PASS","completed next test",null)!;
            progressType.GetProperty("FrequencyShift")!.SetValue(done,rows[1]!.GetType().GetProperty("FrequencyShift")!.GetValue(rows[1]));
            type.GetMethod("ApplyC360Progress",Hidden)!.Invoke(routing,new[]{done,activePlan});
            Check(ReferenceEquals(Field("_reviewedAutoTestCase"),rows[1]),"Completed next stage did not replace the preview");
            Until(()=>activeBorder.BorderThickness.Left==0);
            for(int i=0;i<rows.Count;i++)
                Check(type.GetMethod("BuildAutoTestId",Hidden)!.Invoke(routing,new object[]{i})?.ToString()==rows[i]!.GetType().GetProperty("Id")!.GetValue(rows[i])?.ToString(),
                    "Server step code differs from the configured IO code");
            var engine=Field("_audioEngine");
            Check(engine.GetType().GetField("MeasurementSampleRateGuard",Hidden)!.GetValue(engine)!=null,"The application did not install the pre-playback rate confirmation");
            Console.WriteLine("PASS completed preview retained until the next terminal result; actual configured IO codes used for server steps; pre-playback rate guard installed.");
            var emptyRow = Activator.CreateInstance(rows[5]!.GetType())!;
            emptyRow.GetType().GetProperty("Config")!.SetValue(emptyRow, rows[5]!.GetType().GetProperty("Config")!.GetValue(rows[5]));
            type.GetMethod("DisplayAutoTestResult", Hidden)!.Invoke(routing, new[] { emptyRow });
            var emptyArgs = new object?[] { null, null };
            type.GetMethod("GetSafeFrequencySnapshots", Hidden)!.Invoke(routing, emptyArgs);
            Check(((double[])emptyArgs[0]!).Length == 0, "Empty stage borrowed another stage's curve");
            Console.WriteLine("PASS chart transitions IO001 -> IO002 -> response after zoom; per-stage graph snapshots; empty results do not borrow a prior curve.");
            type.GetMethod("ShowMeasurementDetails",Hidden)!.Invoke(routing,new object[]{false});
            var details = (Window)Field("_measurementDetailsWindow");
            details.GetType().GetMethod("SelectImpedance")!.Invoke(details,null);
            Check(details.GetType().GetProperty("Impedance")!.GetValue(details)?.GetType().Name == "ImpedanceView", "Impedance not embedded in Ctrl+Shift+I");
            details.Close();

            // Interrupt after two complete stages, without using the explicit discard button.
            Call("CancelAndDiscardCurrentMeasurement");
            int effectCaptures = proxy.CapturesByStage.GetValueOrDefault("MicEffects");
            proxy.HoldStage = "MicDry";
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => proxy.LastStage == "MicDry");
            string heldSerial = ((TextBlock)routing.FindName("TxtProductSerial")).Text;
            string heldFolder = Field("_autoTestSessionFolder").ToString()!;
            string graph = Path.Combine(heldFolder, "FRA-IO001.png");
            Check(File.Exists(graph), "Completed IO001 graph was not saved before interruption");
            byte[] firstGraph = File.ReadAllBytes(graph);
            Call("InterruptMeasurementForEndpointLoss"); Until(() => !Busy());
            Check(auto.Content.ToString() == "TIẾP TỤC ĐO TỰ ĐỘNG" && auto.Background.ToString() == "#FFFF8C00"
                && rows[0]!.GetType().GetProperty("Status")!.GetValue(rows[0])?.ToString() == "PASS"
                && rows[1]!.GetType().GetProperty("Status")!.GetValue(rows[1])?.ToString() == "PASS" && proxy.Restored,
                "Endpoint loss discarded completed stages, lost the orange resume button or skipped DSP restoration");
            var resumeImage = new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); resumeImage.Render(content);
            var resumeEncoder = new PngBitmapEncoder(); resumeEncoder.Frames.Add(BitmapFrame.Create(resumeImage));
            using(var imageFile=File.Create(Path.Combine(output,"interrupted-resume-button.png"))) resumeEncoder.Save(imageFile);
            proxy.HoldStage = null;
            proxy.Identity = "another-board";
            int blockedCaptures = proxy.Captures;
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.Captures == blockedCaptures && warnings.Last().Contains("Loa BLE đã đổi"), "A different board reused saved results");
            proxy.Identity = "fixture-C360";
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(proxy.CapturesByStage["MicEffects"] == effectCaptures + 2
                && File.ReadAllBytes(graph).SequenceEqual(firstGraph)
                && ((TextBlock)routing.FindName("TxtProductSerial")).Text == heldSerial
                && rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "PASS")
                && auto.Content.ToString() == "BẮT ĐẦU ĐO TỰ ĐỘNG", "Continuation reran IO001, overwrote its graph, changed product identity or lost merged results");
            // The manual LINE button must not leave the Auto Test effect impulse over its response plot.
            var effectConfig = rows[0]!.GetType().GetProperty("Config")!.GetValue(rows[0])!;
            type.GetMethod("ApplyTestCaseConfig")!.Invoke(routing,new[]{effectConfig});
            ((Button)routing.FindName("BtnStart")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(((FrameworkElement)routing.FindName("ImpulseEffectHost")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)routing.FindName("PlotFreqResponse")).Visibility == Visibility.Visible,
                "Manual LINE at MIC IN showed the effect impulse instead of the frequency response");
            Call("CancelAndDiscardCurrentMeasurement");
            proxy.HoldStage = "MicEffects";
            auto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => proxy.LastStage == "MicEffects");
            Call("InterruptMeasurementForEndpointLoss"); Until(() => !Busy());
            Check(auto.Content.ToString() == "TIẾP TỤC ĐO TỰ ĐỘNG", "Interruption during the first stage lost the continuation");
            Call("CancelAndDiscardCurrentMeasurement");
            Check(auto.Content.ToString() == "BẮT ĐẦU ĐO TỰ ĐỘNG" && rows.Cast<object>().All(r => r.GetType().GetProperty("Status")!.GetValue(r)?.ToString() == "WAITING"),
                "Explicit discard did not clear the entire interrupted session");
            proxy.HoldStage = null;
            Console.WriteLine("PASS interruption/resume: orange button, preserved IO001/IO002 and image/serial, changed-board guard, only unfinished stages rerun; explicit discard restarts; manual MIC IN LINE keeps response graph.");
        }
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
    public bool MissingUsb = true, HoldCapture, HoldCleanup, Restoring, InvalidShift, UnavailableBluetooth;
    public string? HoldStage, LastStage;
    public string Identity = "fixture-C360";
    public readonly Dictionary<string,int> CapturesByStage = new();
    public readonly TaskCompletionSource Cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<ushort, ushort> Initial = new() { [0x6102]=8160,[0x6103]=4064,[0x6254]=72,[0x6242]=4,[0x6400]=4097,[0x6447]=1,[0x6448]=0,[0x6449]=0,[0x6468]=0,[0x6404]=0,[0x640A]=0 };
    private Dictionary<ushort,ushort>? _state;
    private Dictionary<ushort,ushort> State => _state ??= new(Initial);
    public bool Restored => Initial.All(p => State[p.Key] == p.Value);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_DeviceIdentity": return Identity;
            case "StopAudio": return null;
            case "ReadAsync": return Task.FromResult<IReadOnlyDictionary<ushort,ushort>>(new Dictionary<ushort,ushort>(State));
            case "WriteAsync":
                State[(ushort)args![0]!] = (ushort)args[1]!;
                if (HoldCleanup && !((CancellationToken)args[2]!).CanBeCanceled) { Restoring = true; return Cleanup.Task; }
                return Task.CompletedTask;
            case "CaptureAsync":
                Captures++;
                LastStage = args![0]!.ToString();
                CapturesByStage[LastStage!] = CapturesByStage.GetValueOrDefault(LastStage!) + 1;
                return typeof(C360ModelBenchProxy).GetMethod(nameof(Capture), BindingFlags.Instance|BindingFlags.NonPublic)!
                    .MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(this, new[] { args![0], args[2] });
            default: throw new NotSupportedException(method.Name);
        }
    }
    private async Task<T> Capture<T>(object stage, object boxedToken)
    {
        var token = (CancellationToken)boxedToken;
        if (HoldCapture || HoldStage == stage.ToString()) await Task.Delay(Timeout.Infinite, token);
        await Task.Delay(15, token);
        if (UnavailableBluetooth && stage.ToString() == "Bluetooth")
            throw (Exception)Activator.CreateInstance(Assembly.GetType("SoncaAudioInspector.BluetoothPlaybackUnavailableException")!, "fixture: Windows pairing failed")!;
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
