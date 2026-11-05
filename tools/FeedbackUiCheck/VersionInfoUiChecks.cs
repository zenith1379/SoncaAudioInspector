using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class VersionInfoUiChecks
{
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Tree(child)) yield return item;
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public static void Run(Assembly assembly, string output)
    {
        if (Directory.Exists(output)) throw new IOException("U1 UI output must be new.");
        Directory.CreateDirectory(output);
        string? oldDisable = Environment.GetEnvironmentVariable("SONCA_UPDATE_DISABLE_AUTO_CHECK");
        string? oldFeed = Environment.GetEnvironmentVariable("SONCA_UPDATE_FEED");
        Environment.SetEnvironmentVariable("SONCA_UPDATE_DISABLE_AUTO_CHECK", "1");
        string fixture = Path.Combine(output, "local-feed"); Directory.CreateDirectory(fixture);
        Environment.SetEnvironmentVariable("SONCA_UPDATE_FEED", new Uri(fixture + Path.DirectorySeparatorChar).AbsoluteUri);
        string json = Path.Combine(fixture, "version-info.json");
        void WriteFixture() => File.WriteAllText(json, JsonSerializer.Serialize(new { version = "1.0.0", releasedUtc = "2026-11-05T03:00:00Z",
            features = new[] { "Cập nhật theo file thay đổi", "Giữ nguyên dữ liệu của trạm", "Lịch sử phiên bản offline fixture" } }));
        WriteFixture();
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            checks.Add("PASS " + message); File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
        }
        Window? window = null;
        try
        {
            window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementDetailsWindow")!)!;
            window.WindowState = WindowState.Normal; window.Width = 1280; window.Height = 900;
            window.ShowActivated = false; window.ShowInTaskbar = false;
            var view = window.GetType().GetProperty("VersionInfo")!.GetValue(window)!;
            var viewType = view.GetType(); viewType.GetMethod("LoadFromDirectory")!.Invoke(view, [fixture]);
            var stateType = assembly.GetType("SoncaAudioInspector.Update.UpdateCheckState")!;
            var state = Activator.CreateInstance(stateType)!;
            int requests = 0;
            stateType.GetProperty("CheckRequested")!.SetValue(state, (Func<Window, Task>)(owner =>
            {
                requests++; stateType.GetMethod("SetStatus")!.Invoke(state, ["Đang dùng bản mới nhất"]); return Task.CompletedTask;
            }));
            viewType.GetMethod("Bind")!.Invoke(view, [state]);
            window.Show(); Drain();
            var tabs = Tree(window).OfType<TabControl>().First();
            var tab = tabs.Items.OfType<TabItem>().Last();
            Check(tab.Header?.ToString() == "Phiên bản · v1.0" && ReferenceEquals(tab.Content, view), "version tab is last and uses actual AppVersion.Display");
            tab.IsSelected = true; window.UpdateLayout(); Drain();
            TextBlock Text(string name) => Tree((DependencyObject)view).OfType<TextBlock>().Single(t => t.Name == name);
            Check(Text("RunningVersion").Text == "v1.0 (1.0.0)", "running version shown as v1.0 (1.0.0)");
            string build = File.GetLastWriteTime(assembly.Location).ToString("dd/MM/yyyy HH:mm:ss");
            Check(Text("VersionBuildDate").Text.Contains(build), "assembly write time displayed in machine local time");
            Check(Text("VersionFeatures").Text.Contains("Giữ nguyên dữ liệu của trạm"), "features read from fake version-info.json");
            Check(Text("VersionFeed").Text.StartsWith("file:", StringComparison.Ordinal) && Text("VersionUpdateStatus").Text == "Chưa kiểm", "offline local feed and initial unchecked state visible");
            Check(requests == 0, "opening/selecting version tab does not request updates");
            foreach (string status in new[] { "Đang kiểm", "Đang dùng bản mới nhất", "Có bản v1.0.1", "Lỗi: offline fixture" })
            {
                stateType.GetMethod("SetStatus")!.Invoke(state, [status]); Drain();
                Check(Text("VersionUpdateStatus").Text == status, "shared update state displays " + status);
            }
            Tree((DependencyObject)view).OfType<Button>().Single(b => b.Name == "CheckVersionUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Drain();
            Check(requests == 1 && Text("VersionUpdateStatus").Text == "Đang dùng bản mới nhất", "check button invokes bound shared callback exactly once; local stub, no HTTP");
            var start = (System.Diagnostics.ProcessStartInfo)viewType.GetMethod("HistoryStartInfo")!.Invoke(null, null)!;
            Check(start.UseShellExecute && start.FileName == "https://inventory.acnos.store/update", "history browser plan uses correct URL and UseShellExecute; browser not launched");
            File.Delete(json); viewType.GetMethod("LoadFromDirectory")!.Invoke(view, [fixture]);
            Check(Text("VersionFeatures").Text == "Bản phát triển — chưa có ghi chú phát hành", "missing version-info gives development fallback");
            File.WriteAllText(json, "{broken"); viewType.GetMethod("LoadFromDirectory")!.Invoke(view, [fixture]);
            Check(Text("VersionFeatures").Text.Contains("JSON không hợp lệ"), "malformed version-info gives short error without crash");
            WriteFixture(); viewType.GetMethod("LoadFromDirectory")!.Invoke(view, [fixture]);
            stateType.GetMethod("SetStatus")!.Invoke(state, ["Chưa kiểm"]); Drain(); window.UpdateLayout();
            int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "version-tab.png"))) encoder.Save(stream);
            byte[] pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            Check(pixels.Distinct().Count() > 16 && tab.IsSelected, "selected real version tab rendered to nonblank PNG");
            Console.WriteLine($"PASS U1 version tab: {checks.Count} WPF assertions; real MeasurementDetailsWindow, fake release info, no MainWindow constructor/audio/BLE/HTTP/browser.");
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("SONCA_UPDATE_DISABLE_AUTO_CHECK", oldDisable);
            Environment.SetEnvironmentVariable("SONCA_UPDATE_FEED", oldFeed);
        }
    }
}
