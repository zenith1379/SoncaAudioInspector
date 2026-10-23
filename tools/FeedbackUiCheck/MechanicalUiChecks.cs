using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class MechanicalUiChecks
{
    private static Delegate Runner<TRequest, TResult>(Func<object, CancellationToken, Task<object>> run) =>
        new Func<TRequest, CancellationToken, Task<TResult>>(async (request, token) => (TResult)await run(request!, token));

    public static void Run(Assembly assembly, string fixtures, string output)
    {
        // Raised test events run outside Application.Run; install the same
        // dispatcher context as real WPF input so awaited handlers resume on UI.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "data"));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        Type resultType = assembly.GetType("SoncaAudioInspector.MechanicalTestResult")!;
        Type requestType = assembly.GetType("SoncaAudioInspector.MechanicalTestRequest")!;
        object Read(string file, Type type) => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(fixtures, file)), type, options)!;
        object impulse = Read("high-order-48000.json", assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!);
        object looseResult = Read("loose-particle.json", resultType), airResult = Read("air-leak.json", resultType);
        var window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementDetailsWindow")!)!;
        Type windowType = window.GetType();
        var rows = Array.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementValue")!, 0);
        windowType.GetMethod("Update")!.Invoke(window, new object[] { "Dữ liệu mô phỏng · kiểm tra giao diện, không phát âm thanh", rows, new Dictionary<double, double>(), impulse });
        var tabs = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
        TabItem Tab(string header) => tabs.Items.Cast<TabItem>().Single(t => t.Header.ToString()!.Contains(header));
        var loose = (UserControl)Tab("Hạt rác").Content; var air = (UserControl)Tab("Rò khí").Content;
        object Field(object target, string name) => target.GetType().GetField(name, hidden)!.GetValue(target)!;
        var loosePlay = (Button)Field(loose, "_play"); var airPlay = (Button)Field(air, "_play");
        bool Busy() => (bool)windowType.GetProperty("IsDiagnosticRunning")!.GetValue(window)!;
        void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        void PumpUntil(Func<bool> complete)
        {
            var frame = new DispatcherFrame(); var watch = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (complete() || watch.Elapsed.TotalSeconds > 3) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            Require(complete(), "Timed out waiting for asynchronous UI cancellation.");
        }
        void Capture(string file)
        {
            window.UpdateLayout(); Pump();
            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, file + ".png")); encoder.Save(stream);
        }
        int calls = 0, cancellations = 0; bool hold = false, fail = false;
        Func<object, CancellationToken, Task<object>> fake = async (request, token) =>
        {
            calls++;
            Require((double)requestType.GetProperty("FrequencyHz")!.GetValue(request)! == 40, "Default tone frequency changed.");
            Require((double)requestType.GetProperty("LevelDbFs")!.GetValue(request)! == -18, "Default signal level must start low.");
            if (fail) throw new IOException("Thiết bị đã ngắt kết nối.");
            if (hold)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancellations++; throw; }
            }
            return requestType.GetProperty("Kind")!.GetValue(request)!.ToString() == "LooseParticle" ? looseResult : airResult;
        };
        Delegate runner = (Delegate)typeof(MechanicalUiChecks).GetMethod(nameof(Runner), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(requestType, resultType).Invoke(null, new object[] { fake })!;
        windowType.GetProperty("DiagnosticRunner")!.SetValue(window, runner);
        window.Show(); window.UpdateLayout(); Pump();
        tabs.SelectedItem = Tab("H10"); Pump();
        var high = Tab("H10").Content;
        Require(((DataGrid)Field(high, "_table")).Items.Count == 26, "H10–H35 did not render all 26 orders.");
        Capture("high-order");
        tabs.SelectedItem = Tab("Hạt rác"); Pump(); tabs.SelectedItem = Tab("Rò khí"); Pump();
        Require(calls == 0, "Opening/selecting diagnostic tabs emitted a test automatically.");
        hold = true; tabs.SelectedItem = Tab("Hạt rác");
        loosePlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(Busy() && !loosePlay.IsEnabled && !airPlay.IsEnabled, "Tabs allowed concurrent acquisition.");
        object request = Activator.CreateInstance(requestType, Enum.ToObject(assembly.GetType("SoncaAudioInspector.MechanicalTestKind")!, 1), 40.0, -18.0, 2.0, 12.0)!;
        var duplicate = (Task)windowType.GetMethod("RunDiagnosticAsync", hidden)!.Invoke(window, new[] { request })!;
        Require(duplicate.IsFaulted && calls == 1, "Second diagnostic bypassed the shared acquisition guard.");
        _ = duplicate.Exception;
        ((Button)Field(loose, "_stop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        PumpUntil(() => !Busy());
        Require(!Busy() && cancellations == 1 && loosePlay.IsEnabled && airPlay.IsEnabled, "Stop did not cancel and release both tabs.");
        hold = false; loosePlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(((TextBlock)Field(loose, "_status")).Text.Contains("PHI CHU KỲ"), "Particle result was lost in UI.");
        Capture("loose-particle");
        Require(((TextBox)Field(loose, "_frequency")).Text == "40", "Tone frequency is not visible in its editor.");
        tabs.SelectedItem = Tab("Rò khí"); airPlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(((TextBlock)Field(air, "_status")).Text.Contains("ĐIỀU BIÊN"), "Air leak result was lost in UI.");
        Capture("air-leak");
        window.WindowState = WindowState.Normal; window.Width = 1000; window.Height = 700; Capture("air-leak-compact");
        fail = true; airPlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(!Busy() && airPlay.IsEnabled && ((TextBlock)Field(air, "_status")).Text == "CHƯA ĐO ĐƯỢC", "Device error left controls locked or showed a stale result.");
        fail = false; hold = true; loosePlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Require(ReferenceEquals(Field(window, "_result"), impulse), "Standalone test changed the saved sweep result.");
        // Exercise the real parent routing guard with no endpoints selected.
        // It must reject before background capture or tone emission is reached.
        var routing = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioRouting")!)!;
        using var engine = (IDisposable)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        routing.GetType().GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routing.GetType().GetField("_measurementDetailsWindow", hidden)!.SetValue(routing, window);
        var savedFrequencies = new List<double> { 80, 1000 };
        routing.GetType().GetField("_freqs", hidden)!.SetValue(routing, savedFrequencies);
        routing.GetType().GetMethod("CancelAndDiscardCurrentMeasurement")!.Invoke(routing, null);
        PumpUntil(() => !Busy());
        Require(cancellations == 2 && savedFrequencies.Count == 2, "Parent Stop did not cancel diagnostic or discarded the previous line.");
        loosePlay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        window.Close(); PumpUntil(() => !Busy()); Require(cancellations == 3, "Closing details failed to cancel its test.");
        routing.GetType().GetField("_routingWorkflowActive", hidden)!.SetValue(routing, true);
        var guarded = (Task)routing.GetType().GetMethod("RunMechanicalDiagnosticAsync", hidden)!.Invoke(routing,
            new[] { request, (object)CancellationToken.None })!;
        Require(guarded.IsFaulted && guarded.Exception!.GetBaseException().Message.Contains("đang đo"), "Parent routing workflow allowed another capture.");
        Console.WriteLine("PASS shown dark H10–H35/LP/ALD tabs, no automatic tone, low start level, shared busy guard, Stop/close cancellation, error recovery, unchanged sweep, compact/maximized layouts; injected runner emitted no audio.");
    }
}
