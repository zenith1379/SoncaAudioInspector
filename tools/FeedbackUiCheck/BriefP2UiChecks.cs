using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class BriefP2UiChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Hidden)?.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, Hidden)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
    private static object? Get(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);
    private static void Put(object owner, string name, object? value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var value in Tree(child)) yield return value;
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Tree(root).OfType<T>().Single(e => e.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output); var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }
        Type Type(string name) => assembly.GetType("SoncaAudioInspector." + name)!;
        object Read(string name, string json) => JsonSerializer.Deserialize(json, Type(name))!;
        var model = Read("ModelConfig", """
            {"model":"P2 offline","testItems":{"InOut":{"Devices":{"Input":{"OUT":"mock-out"},"Output":{"IN":"mock-in"}},
            "Tests":[{"id":"IO001","name":"Fixture hõm","Playback Out":"OUT","Recording In":"IN","DipWindowsEnabled":false,
            "DipWindows":[{"MinHz":56,"MaxHz":75}]}]}}}
            """);
        var editor = (Window)Activator.CreateInstance(Type("ModelConfigEditorWindow"), model)!;
        var routing = (FrameworkElement)Activator.CreateInstance(Type("AudioRouting"))!;
        var engine = (IDisposable)Activator.CreateInstance(Type("AudioEngine"))!;
        var runner = Activator.CreateInstance(Type("TestRunner"), engine)!;
        Set(routing, "_audioEngine", engine); Set(routing, "_testRunner", runner);
        try
        {
            var tabs = Tree(editor).OfType<TabItem>().ToArray();
            Check(tabs.Any(t => t.Header?.ToString() == "Biên & khoảng xét hõm"), "Editor contains response-limit tab");
            var tab = tabs.Single(t => t.Header?.ToString() == "Biên & khoảng xét hõm");
            var sections = (TabControl)LogicalTreeHelper.GetParent(tab); int index = sections.Items.IndexOf(tab);
            Check(((TabItem)sections.Items[index - 1]).Header.ToString() == "Bật/tắt kiểm tra", "Limit tab immediately follows toggles");
            sections.SelectedItem = tab;
            var master = Named<CheckBox>(editor, "DipWindowsEnabled"); var grid = Named<DataGrid>(editor, "DipWindowsGrid");
            var json = Named<TextBox>(editor, "ModelJson"); var warning = Named<TextBlock>(editor, "DipWarnings");
            Check(json.IsReadOnly && grid.Columns.Count == 7, "JSON read-only, grid exposes all seven fields/actions");
            Check(((TextBlock)Field(editor, "_responsePreviewNote")!).Text.Contains("minh họa quanh 0 dB"), "No data gives explicitly illustrative preview");
            Check(warning.Text.Contains("octave"), "Wide-window warning is visible");
            master.IsChecked = true;
            object Row() => grid.Items[0];
            Put(Row(), "Min", "[60, 70]"); Call(editor, "SynchronizeForm");
            Check(json.Text.Contains("\"MinHz\": 60") && json.Text.Contains("\"MaxHz\": 70"), "Quick pair input updates both numeric JSON bounds");
            Put(Row(), "Mode", "Loại khỏi xét"); Call(editor, "SynchronizeForm");
            Check(json.Text.Contains("\"Mode\": \"Exclude\""), "Friendly Exclude choice projects canonical JSON");
            Put(Row(), "Enabled", false); Call(editor, "SynchronizeForm");
            Check(json.Text.Contains("\"Enabled\": false"), "Per-row enabled switch persists");
            Click(Named<Button>(editor, "AddDipWindow"));
            Check(grid.Items.Count == 2, "Add action creates second window");
            grid.ApplyTemplate(); grid.Measure(new Size(900, 240)); grid.Arrange(new Rect(0, 0, 900, 240)); grid.UpdateLayout();
            var delete = Visuals(grid).OfType<Button>().Single(b => b.Content?.ToString() == "Xóa" && ReferenceEquals(b.DataContext, grid.Items[1]));
            Click(delete);
            Check(grid.Items.Count == 1, "Delete action removes the selected window");
            Put(Row(), "Enabled", true); Put(Row(), "Mode", "Bỏ giới hạn dưới"); Put(Row(), "Min", "56–75 Hz"); Call(editor, "SynchronizeForm");
            Check(Get(Get(Row(), "Window")!, "MinHz")!.Equals(56d) && Get(Get(Row(), "Window")!, "MaxHz")!.Equals(75d), "Quick dash range accepts Hz suffix");
            var test = Field(editor, "_editing")!;
            var reference = new Dictionary<double, double> { [50] = 0, [56] = 0, [60] = -10, [65] = -10, [70] = -10, [75] = 0, [80] = 0, [1000] = 0, [18000] = 0 };
            var row = Activator.CreateInstance(Type("AutoTestCaseItem"))!;
            Put(row, "Config", JsonSerializer.Deserialize(JsonSerializer.Serialize(test), Type("TestConfig"))!);
            Put(row, "Id", "IO001"); Put(row, "Name", "Fixture hõm"); Put(row, "StandardCurve", reference);
            Put(row, "ResponseFrequencies", reference.Keys.ToArray()); Put(row, "ResponseLevels", reference.Keys.Select(_ => 0d).ToArray());
            ((IList)Field(routing, "_autoTestCases")!).Add(row);
            ((TextBox)routing.FindName("TxtFreqTolerance")).Text = "3";
            var previewProperty = editor.GetType().GetProperty("ResponsePreviewProvider")!;
            var callback = Delegate.CreateDelegate(previewProperty.PropertyType, routing, routing.GetType().GetMethod("GetResponseLimitPreview", Hidden)!);
            var routeBefore = JsonSerializer.Serialize(new { Playback = ((ComboBox)routing.FindName("ComboPlayback")).SelectedIndex,
                Recording = ((ComboBox)routing.FindName("ComboRecording")).SelectedIndex, LoadedKey = Field(routing, "_loadedStandardDeviceKey") });
            previewProperty.SetValue(editor, callback);
            var data = Call(routing, "GetResponseLimitPreview", test)!;
            Check(!ReferenceEquals(reference, Get(data, "StandardCurve")) && ((IDictionary)Get(data, "StandardCurve")!).Count == reference.Count, "Preview clones captured data for matching configuration");
            Check(((TextBlock)Field(editor, "_responsePreviewNote")!).Text.Contains("đã chụp"), "Preview names captured data source");
            var routeAfter = JsonSerializer.Serialize(new { Playback = ((ComboBox)routing.FindName("ComboPlayback")).SelectedIndex,
                Recording = ((ComboBox)routing.FindName("ComboRecording")).SelectedIndex, LoadedKey = Field(routing, "_loadedStandardDeviceKey") });
            Check(routeBefore == routeAfter, "Preview callback leaves device selections and loaded reference key unchanged");
            dynamic preview = Field(editor, "_responseLimitPreview")!;
            object[] Plots() => ((IEnumerable)preview.Plot.GetPlottables()).Cast<object>().ToArray();
            bool Blue(object p)
            {
                if (p.GetType().Name != "HorizontalSpan") return false;
                dynamic color = ((dynamic)p).FillStyle.Color;
                return color.Red == 96 && color.Green == 165 && color.Blue == 250 && color.Alpha > 0;
            }
            double[][] Curve(object p) => ((IEnumerable)((dynamic)p).Data.GetScatterPoints()).Cast<object>()
                .Select(c => new[] { (double)((dynamic)c).X, (double)((dynamic)c).Y }).ToArray();
            object[] Orange() => Plots().Where(p => p.GetType().Name == "Scatter" && ((dynamic)p).Color.Red == 249 && ((dynamic)p).Color.Green == 115).ToArray();
            double blueAlpha = Plots().Count(Blue) == 1 ? Convert.ToDouble(((dynamic)Plots().Single(Blue)).FillStyle.Color.Alpha) : -1;
            File.WriteAllText(Path.Combine(output, "plot-metadata.txt"), $"Blue spans: {Plots().Count(Blue)}; alpha: {blueAlpha}");
            Check(Plots().Count(Blue) == 1 && (blueAlpha is >= .09 and <= .11 || blueAlpha is >= 25 and <= 26), "Exactly one blue span at alpha 0.10");
            Check(Orange().Length == 3 && Orange().Select(Curve).Where(c => c.All(p => p[1] < 0)).All(c => c.All(p => p[0] < Math.Log10(56)) || c.All(p => p[0] > Math.Log10(75))), "LowerOnly lower limit splits into two finite segments around the window");
            Check(Orange().Select(Curve).Any(c => c.Any(p => Math.Abs(p[0] - Math.Log10(65)) < 1e-8) && c.All(p => p[1] == 3)), "Upper bridge stays continuous at +3 dB over the reference dip");
            Put(Row(), "Mode", "Loại khỏi xét"); Call(editor, "SynchronizeForm");
            Check(Orange().Length == 4, "Exclude splits both bounds into separate scatters");
            preview.Plot.SavePng(Path.Combine(output, "editor-exclude.png"), 1000, 400);
            master.IsChecked = false;
            Check(Plots().Count(Blue) == 0 && Orange().Length == 2, "Master off removes blue fill and restores continuous limits");
            preview.Plot.SavePng(Path.Combine(output, "editor-disabled.png"), 1000, 400);
            master.IsChecked = true; Put(Row(), "Mode", "Bỏ giới hạn dưới"); Call(editor, "SynchronizeForm");
            preview.Plot.SavePng(Path.Combine(output, "editor-lower-only.png"), 1000, 400);
            Check(new FileInfo(Path.Combine(output, "editor-lower-only.png")).Length > 1000, "ScottPlot preview renders offline PNG");
            Set(routing, "_appliedTestConfig", test); Set(routing, "_standardCurve", reference);
            Call(routing, "InitCharts");
            Check(!(bool)Call(routing, "IsFrequencyPointOverThreeDb", 65d, -20d)!, "Main marker accepts lowered dip");
            Check((bool)Call(routing, "IsFrequencyPointOverThreeDb", 65d, 4d)!, "Main marker flags upper bridge failure");
            var settings = Type("ResponseLimitSettings").GetMethod("Capture")!.Invoke(null, [test, 3d])!;
            Put(row, "ResponseLimits", settings); Set(routing, "_reviewedAutoTestCase", row);
            Put(test, "DipWindowsEnabled", false); ((TextBox)routing.FindName("TxtFreqTolerance")).Text = "99";
            Check((bool)Call(routing, "IsFrequencyPointOverThreeDb", 65d, 4d)! && !(bool)Call(routing, "IsFrequencyPointOverThreeDb", 65d, -20d)!, "Review keeps captured tolerance and windows after live controls change");
            var stored = ((IEnumerable)Get(settings, "DipWindows")!).Cast<object>().Single();
            Put(Get(Row(), "Window")!, "MinHz", 100d);
            Check(Get(stored, "MinHz")!.Equals(56d), "Response snapshot owns independent window objects");
            Set(routing, "_reviewedAutoTestCase", null); Put(test, "DipWindowsEnabled", true); Put(Get(Row(), "Window")!, "MinHz", 56d);
            ((TextBox)routing.FindName("TxtFreqTolerance")).Text = "3";
            ((IList)Field(routing, "_freqs")!).Add(65d); ((IList)Field(routing, "_dbValues")!).Add(-20d);
            Call(routing, "CaptureAutoTestResultForReview", row);
            Check(Get(row, "ResponseLimits") != null, "Acoustic result capture stores limit settings");
            Put(test, "DipWindowsEnabled", false);
            Check(Get(Get(row, "ResponseLimits")!, "DipWindowsEnabled")!.Equals(true), "Captured settings survive later configuration changes");
            var c360Test = Read("TestConfig", """
                {"id":"IO001","name":"C360 snapshot","SpeakerStage":"LineIn","Playback Out":"OUT","Recording In":"IN",
                "DipWindowsEnabled":true,"DipWindows":[{"MinHz":56,"MaxHz":75}],"CriticalZones":[{"MinHz":1000,"MaxHz":1010}]}
                """);
            var io = Read("InOutConfig", "{\"Tests\":[" + JsonSerializer.Serialize(c360Test) + "]}");
            var setup = Read("C360ElectronicSetup", "{\"AnalogPlaybackId\":\"mock-out\",\"UsbPlaybackId\":\"mock-usb\",\"LineOutCaptureId\":\"mock-in\"}");
            object stage = Enum.Parse(Type("C360ElectronicStage"), "LineIn");
            var plan = Activator.CreateInstance(Type("C360ModelPlan"), io, setup, Array.CreateInstance(Type("C360ElectronicStage"), 0))!;
            var progress = Activator.CreateInstance(Type("C360ElectronicResult"), stage, "PASS", "ĐÃ CÓ LINE CHUẨN fixture", null)!;
            var savedReference = Activator.CreateInstance(Type("C360Reference"), "fixture", DateTimeOffset.UtcNow, reference, null, null, 2)!;
            Put(progress, "Reference", savedReference); Call(routing, "ApplyC360Progress", progress, plan);
            var c360Settings = Get(row, "ResponseLimits")!;
            Check(Get(c360Settings, "ToleranceDb")!.Equals(3d) && ((IEnumerable)Get(c360Settings, "CriticalZones")!).Cast<object>().Count() == 1
                && ((IEnumerable)Get(c360Settings, "DipWindows")!).Cast<object>().Count() == 1, "C360 progress captures tolerance, CRITICAL and dip windows");
            Check(((IDictionary)Get(row, "FrequencyLimits")!).Count == reference.Count, "C360 snapshot includes reference bounds for all target points");
            Call(routing, "ClearC360ModelResultsFor", new object?[] { null });
            Check(Get(row, "ResponseLimits") == null && Get(row, "ResponseAssessment") == null, "Clearing C360 results clears judgement snapshots");
            var noSigProgress = Activator.CreateInstance(Type("C360ElectronicResult"), stage, "NO_SIGNAL", "Không có tín hiệu đường vào", null)!;
            Call(routing, "ApplyC360Progress", noSigProgress, plan);
            Check(Get(row, "FreqStatus")!.ToString() == "CHƯA CÓ TÍN HIỆU", "F5 C360 UI preserves CHƯA CÓ TÍN HIỆU when reference is missing");
            var failProgress = Activator.CreateInstance(Type("C360ElectronicResult"), stage, "FAIL", "Lệch kênh Chưa có LINE CHUẨN khớp cấu hình; đáp tuyến chưa được so.", null)!;
            Call(routing, "ApplyC360Progress", failProgress, plan);
            Check(Get(row, "FreqStatus")!.ToString() != "CHƯA CÓ LINE CHUẨN — KHÔNG KẾT LUẬN" && Get(row, "Status")!.ToString() == "FAIL", "F5 C360 UI preserves FAIL when reference is missing");

            var steps = (IList)Get(runner, "Steps")!;
            Put(steps[0]!, "Status", "Pass"); Put(steps[0]!, "Details", "fixture");
            Put(runner, "MissingFrequencyLimits", true); Put(runner, "LastFeqInvalidReason", "");
            Call(routing, "SetFinalVerdict", false);
            Check(((Button)routing.FindName("BtnSaveStandard")).IsEnabled
                && ((TextBlock)routing.FindName("LblVerdict")).Text == "CHƯA CÓ LINE CHUẨN — KHÔNG KẾT LUẬN",
                "F1 manual verdict without reference keeps BtnSaveStandard enabled and shows CHƯA CÓ LINE CHUẨN");
            Put(runner, "MissingFrequencyLimits", false);

            var badConfig = Read("TestConfig", """{"id":"IO999","name":"Bad dip","DipWindowsEnabled":true,"DipWindows":[{"MinHz":75,"MaxHz":56}]}""");
            var badCase = Activator.CreateInstance(Type("AutoTestCaseItem"))!;
            Put(badCase, "Id", "IO999"); Put(badCase, "Name", "Bad dip"); Put(badCase, "Config", badConfig);
            bool precheckOk = (bool)Type("AudioRouting").GetMethod("ValidateAutoTestCaseConfiguration", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [badCase])!;
            Check(!precheckOk && Get(badCase, "Status")!.ToString() == "INVALID", "F9 Auto Test precheck catches invalid dip window config as INVALID");

            Click(Named<Button>(editor, "EditJson"));
            Check(!json.IsReadOnly && !((FrameworkElement)Field(editor, "_formLayout")!).IsEnabled, "JSON editing locks response-limit form");
            Check(!routing.IsLoaded && !editor.IsLoaded, "Offline tests never trigger view Loaded/device discovery");
            // Drain only queued form synchronization; no window is shown and no Save callback is installed.
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P2 WPF assertions; no audio/BLE/server."));
            Console.WriteLine($"PASS {checks.Count} P2 WPF assertions and preview PNG; no audio/BLE/server.");
        }
        finally
        {
            foreach (string name in new[] { "_frequencyChartRefreshTimer", "_meterUiTimer" })
                if (Field(routing, name) is DispatcherTimer timer) timer.Stop();
            engine.Dispose(); editor.Close();
        }
    }
}
