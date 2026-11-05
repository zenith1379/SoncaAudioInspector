using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class BriefP4UiChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Tree(child)) yield return descendant;
    }
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Tree(root).OfType<T>().Single(e => e.Name == name);
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public static void Run(Assembly assembly, string output, string? screenshotDir = null)
    {
        Directory.CreateDirectory(output);
        screenshotDir ??= Path.Combine(output, "screenshots");
        Directory.CreateDirectory(screenshotDir);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }
        var modelType = assembly.GetType("SoncaAudioInspector.ModelConfig")!;
        var editorType = assembly.GetType("SoncaAudioInspector.ModelConfigEditorWindow")!;
        var model = JsonSerializer.Deserialize("""
            {"model":"P4 offline editor", "testItems":{"InOut":{
              "Devices":{"Input":{"OUT":"mock-out"},"Output":{"IN":"mock-in"}},
              "Tests":[{"id":"IO001","name":"P4 tone steps","PlaybackOut":"OUT","RecordingIn":"IN","enable":true}]
            }}}
            """, modelType)!;
        Window? editor = null;
        try
        {
            editor = (Window)Activator.CreateInstance(editorType, model)!;
            editor.ShowActivated = false; editor.ShowInTaskbar = false;
            editor.Show(); editor.UpdateLayout(); Drain();
            var json = Named<TextBox>(editor, "ModelJson");
            var enableTab = Tree(editor).OfType<TabItem>().Single(t => t.Header?.ToString() == "Bật/tắt kiểm tra");
            var rubTab = Tree(editor).OfType<TabItem>().Single(t => t.Header?.ToString() == "Rè (Rub & Buzz)");
            var check = Named<CheckBox>(enableTab, "CheckTestRubBuzz");
            Check(check.IsChecked == false && !JsonDocument.Parse(json.Text).RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Tests")[0].TryGetProperty("TestRubBuzz", out _), "Untouched editor preserves absent nullable RubBuzz JSON");
            foreach (string name in new[] { "TxtRubBuzzFrequencies", "TxtRubBuzzLevel", "TxtRubBuzzStepSeconds", "TxtRubBuzzPeakLimit", "TxtRubBuzzRmsLimit" })
                Check(Named<TextBox>(rubTab, name) != null, "RubBuzz tab field " + name);
            Check(Tree(rubTab).OfType<TextBlock>().Any(t => t.Text.Contains("tweeter") && t.Text.Contains("tụ bảo vệ")), "Tweeter/protection capacitor note visible in tab");
            Check(Named<TextBox>(rubTab, "TxtRubBuzzFrequencies").Text == "50, 70, 100, 150", "Default woofer frequencies displayed");
            enableTab.IsSelected = true; check.IsChecked = true; Drain();
            Check(JsonDocument.Parse(json.Text).RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Tests")[0].GetProperty("TestRubBuzz").GetBoolean(), "Checkbox updates TestRubBuzz true JSON projection");
            var rms = Named<TextBox>(rubTab, "TxtRubBuzzRmsLimit");
            Check(rms.Text == "1.5", "Enabling initializes RMS limit to 1.5 percent");
            rubTab.IsSelected = true; Drain();
            var peak = Named<TextBox>(rubTab, "TxtRubBuzzPeakLimit");
            peak.Text = ""; rms.Text = ""; Drain();
            var error = (TextBlock)editorType.GetField("_error", Hidden)!.GetValue(editor)!;
            Check(error.Text.Contains("nhập ít nhất một giới hạn"), "Both empty limits show immediate form error");
            var parse = editorType.GetMethod("ParseModel", BindingFlags.Public | BindingFlags.Static)!;
            bool rejected = false;
            try { parse.Invoke(null, ["P4 offline editor", json.Text]); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException?.Message.Contains("nhập ít nhất một giới hạn") == true; }
            Check(rejected, "ParseModel rejects JSON with both limits empty");
            peak.Text = "-24"; rms.Text = "1.5"; Drain();
            Check(parse.Invoke(null, ["P4 offline editor", json.Text]) != null, "Restored peak and RMS parse successfully");
            Check(!error.Text.Contains("nhập ít nhất một giới hạn"), "Restored limits clear error");
            Named<TextBox>(rubTab, "TxtRubBuzzFrequencies").Text = "30, 50"; Drain();
            Check(error.Text.Contains("24 chu kỳ"), "Form validates stable cycles immediately");
            Named<TextBox>(rubTab, "TxtRubBuzzFrequencies").Text = "50, 70, 100, 150"; Drain();
            Check(parse.Invoke(null, ["P4 offline editor", json.Text]) != null, "Restored default frequencies parse successfully");
            editor.Width = 1200; editor.Height = 900;
            editor.UpdateLayout(); Drain();
            int width = (int)Math.Ceiling(editor.ActualWidth), height = (int)Math.Ceiling(editor.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(editor);
            string path = Path.Combine(screenshotDir, "editor-rubbuzz-tab.png");
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) png.Save(stream);
            Check(File.Exists(path) && new FileInfo(path).Length > 1000, "Rendered editor-rubbuzz-tab.png");
        }
        finally { editor?.Close(); }
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P4 WPF assertions; no audio/BLE/server."));
        Console.WriteLine($"PASS {checks.Count} P4 WPF assertions and RubBuzz tab PNG; no audio/BLE/server.");
    }
}
