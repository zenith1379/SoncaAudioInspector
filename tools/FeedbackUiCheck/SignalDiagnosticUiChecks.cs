using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class SignalDiagnosticUiChecks
{
    private static Func<TRequest, CancellationToken, Task<TResult>> Runner<TRequest, TResult>(Func<object, CancellationToken, Task<object>> run)
        => async (request, token) => (TResult)await run(request!, token);

    public static void Run(Assembly assembly, string output, string fixtures)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        object Read(string file, string type) => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(fixtures, file + ".json")), assembly.GetType("SoncaAudioInspector." + type)!, options)!;
        object impulse = Read("polarity-sweep", "StandardAcousticResult");
        object imdResult = Read("imd-48000", "SignalDiagnosticResult"), compResult = Read("compression-48000", "SignalDiagnosticResult");
        object polarity = Read("polarity", "PolarityDiagnosticResult");
        var window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementDetailsWindow")!)!;
        Type type = window.GetType();
        var rows = Array.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementValue")!, 0);
        type.GetMethod("Update")!.Invoke(window, new[] { "Fixture mô phỏng · không phát âm thanh", rows, new Dictionary<double, double>(), impulse });
        type.GetMethod("UpdatePolarity")!.Invoke(window, new[] { polarity });
        var tabs = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
        TabItem Tab(string header) => tabs.Items.Cast<TabItem>().Single(t => t.Header.ToString()!.Contains(header));
        object Field(object target, string name) => target.GetType().GetField(name, hidden)!.GetValue(target)!;
        var imd = (UserControl)Tab("IMD").Content; var comp = (UserControl)Tab("Compression").Content;
        var loose = (UserControl)Tab("Hạt rác").Content; var air = (UserControl)Tab("Rò khí").Content;
        var plays = new[] { imd, comp, loose, air }.Select(v => (Button)Field(v, "_play")).ToArray();
        bool Busy() => (bool)type.GetProperty("IsDiagnosticRunning")!.GetValue(window)!;
        void Check(bool yes, string message) { if (!yes) throw new Exception(message); }
        void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        void Until(Func<bool> done)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < 3) { Pump(); Thread.Sleep(10); }
            Check(done(), "Diagnostic cancellation timed out.");
        }
        void Capture(string file)
        {
            window.UpdateLayout(); Pump(); var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, file + ".png")); encoder.Save(stream);
        }
        int calls = 0, cancellations = 0; bool hold = false, fail = false;
        Type requestType = assembly.GetType("SoncaAudioInspector.SignalDiagnosticRequest")!;
        Func<object, CancellationToken, Task<object>> fake = async (request, token) =>
        {
            calls++;
            Check((double)requestType.GetProperty("LevelDbFs")!.GetValue(request)! == -24, "Default signal must start low.");
            if (fail) throw new IOException("Thiết bị đã ngắt kết nối.");
            if (hold)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancellations++; throw; }
            }
            return requestType.GetProperty("Kind")!.GetValue(request)!.ToString() == "Imd" ? imdResult : compResult;
        };
        var runner = (Delegate)typeof(SignalDiagnosticUiChecks).GetMethod(nameof(Runner), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(requestType, assembly.GetType("SoncaAudioInspector.SignalDiagnosticResult")!).Invoke(null, new object[] { fake })!;
        type.GetProperty("SignalDiagnosticRunner")!.SetValue(window, runner);
        window.Show(); window.UpdateLayout(); Pump();
        Check(!tabs.Items.Cast<TabItem>().Any(t => t.Header.ToString()!.Contains("Đáp tuyến") || t.Header.ToString()!.Contains("THD / H2")), "Old response/THD tabs remain in Impulse-Line.");
        foreach (string header in new[] { "Cực tính", "IMD", "Compression", "Hạt rác", "Rò khí" }) { tabs.SelectedItem = Tab(header); Pump(); }
        Check(calls == 0, "Selecting tabs emitted a diagnostic automatically.");
        tabs.SelectedItem = Tab("Cực tính"); Capture("polarity");
        tabs.SelectedItem = Tab("IMD"); hold = true;
        plays[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(Busy() && plays.All(p => !p.IsEnabled), "IMD did not lock all four acquisition tabs.");
        ((Button)Field(comp, "_stop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
        Check(cancellations == 1 && plays.All(p => p.IsEnabled), "Cross-tab Stop failed to cancel and release controls.");
        hold = false; plays[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(((TextBlock)Field(imd, "_status")).Text.Contains("IMD VƯỢT"), "IMD result was not displayed."); Capture("imd");
        tabs.SelectedItem = Tab("Compression"); plays[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(((TextBlock)Field(comp, "_status")).Text.Contains("CÓ NÉN"), "Compression result was not displayed."); Capture("compression");
        window.WindowState = WindowState.Normal; window.Width = 1000; window.Height = 700; Capture("compression-compact");
        int before = calls; ((TextBox)Field(comp, "_increase")).Text = "100";
        plays[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(calls == before && !Busy() && ((TextBlock)Field(comp, "_status")).Text == "CHƯA ĐO ĐƯỢC", "Invalid level recipe reached capture.");
        ((TextBox)Field(comp, "_increase")).Text = "6";
        fail = true; plays[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(!Busy() && plays[1].IsEnabled && ((TextBlock)Field(comp, "_status")).Text == "CHƯA ĐO ĐƯỢC", "Device error locked controls or retained a result.");
        fail = false; hold = true; plays[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(ReferenceEquals(Field(window, "_result"), impulse), "Standalone diagnostic replaced the sweep.");
        var routing = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioRouting")!)!;
        using var engine = (IDisposable)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        routing.GetType().GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routing.GetType().GetField("_measurementDetailsWindow", hidden)!.SetValue(routing, window);
        var frequencies = new List<double> { 80, 1000 };
        routing.GetType().GetField("_freqs", hidden)!.SetValue(routing, frequencies);
        routing.GetType().GetMethod("CancelAndDiscardCurrentMeasurement")!.Invoke(routing, null); Until(() => !Busy());
        Check(cancellations == 2 && frequencies.Count == 2, "Parent Stop discarded line data or failed to cancel.");
        plays[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); window.Close(); Until(() => !Busy());
        Check(cancellations == 3, "Closing details did not cancel acquisition.");
        routing.GetType().GetField("_routingWorkflowActive", hidden)!.SetValue(routing, true);
        object guardedRequest = Activator.CreateInstance(requestType, Enum.ToObject(assembly.GetType("SoncaAudioInspector.SignalDiagnosticKind")!, 0),
            1000.0, 60.0, 7000.0, -24.0, 6.0, 2.0, 1.0)!;
        var guarded = (Task)routing.GetType().GetMethod("RunSignalDiagnosticAsync", hidden)!.Invoke(routing, new[] { guardedRequest, (object)CancellationToken.None })!;
        Check(guarded.IsFaulted && guarded.Exception!.GetBaseException().Message.Contains("đang đo"), "Routing lock allowed another signal test.");
        string csvPath = Path.Combine(output, "reference.csv"); File.WriteAllText(csvPath, "Frequency,Target\n1000,-20\n");
        string analysisId = (string)assembly.GetType("SoncaAudioInspector.ResponseSmoothing")!.GetField("AnalysisId")!.GetRawConstantValue()!;
        void WriteReference(bool withPolarity)
        {
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(csvPath)));
            var data = new Dictionary<string, object> { ["analysisId"] = analysisId, ["levelBasis"] = "fixture", ["csvSha256"] = sha,
                ["rawPoints"] = new[] { new { frequencyHz = 1000, levelDb = -20 } } };
            if (withPolarity) data["polarityReference"] = new { Polarity = "NORMAL", ContextKey = "fixture-route", SampleRate = 48000 };
            File.WriteAllText(Path.ChangeExtension(csvPath, ".json"), JsonSerializer.Serialize(data));
        }
        void LoadReference() => routing.GetType().GetMethod("LoadRawStandardCurve", hidden)!.Invoke(routing, new object[] { csvPath, "fixture", false });
        WriteReference(true); LoadReference();
        Check(Field(routing, "_standardPolarityReference") != null, "Polarity reference was not loaded from a matching hashed sidecar.");
        File.AppendAllText(csvPath, "2000,-20\n"); LoadReference();
        Check(routing.GetType().GetField("_standardPolarityReference", hidden)!.GetValue(routing) == null, "Changed CSV reused stale polarity metadata.");
        WriteReference(false); LoadReference();
        Check(routing.GetType().GetField("_standardPolarityReference", hidden)!.GetValue(routing) == null, "Legacy line invented a polarity reference.");
        Console.WriteLine("PASS shown polarity/IMD/compression tabs, removed response/THD tabs, no auto playback, low-level defaults, shared four-tab guard, Stop/parent Stop/close cancellation, invalid recipe, device error, preserved sweep, compact layout; injected runner emitted no audio.");
        Console.WriteLine("PASS hash-bound polarity sidecar load, mismatched CSV invalidation and legacy line without a reference.");
    }
}
