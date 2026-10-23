using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;

internal static class ImpulsePerformanceUiChecks
{
    public static void CheckDenseRendering(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        var sdk = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ScottPlot")
            ?? Assembly.Load("ScottPlot");
        dynamic plot = Activator.CreateInstance(sdk.GetType("ScottPlot.Plot")!)!;
        double[] times = Enumerable.Range(0, 100000).Select(i => i / 100000.0).ToArray();
        double[] values = Enumerable.Repeat(-60.0, times.Length).ToArray();
        object source = Activator.CreateInstance(sdk.GetType("ScottPlot.DataSources.SignalXYSourceDoubleArray")!, times, values)!;
        dynamic trace = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ImpulseSignalPlot")!, source)!;
        var colorType = sdk.GetType("ScottPlot.Color")!;
        object signal = trace;
        signal.GetType().GetProperty("Color")!.SetValue(signal,
            colorType.GetMethod("FromHex", new[] { typeof(string) })!.Invoke(null, new object[] { "#FF0000" }));
        trace.LineWidth = 1.6f; trace.LineStyle.AntiAlias = false;
        plot.PlottableList.Add(trace); plot.Axes.SetLimits(0.0, 1.0, -180.0, 20.0);
        using (dynamic image = plot.GetImage(600, 400)) File.WriteAllBytes(Path.Combine(output, "flat-trace.png"), (byte[])image.GetImageBytes());
        values[50000] = 0; values[60000] = -180;
        using (dynamic image = plot.GetImage(600, 400)) File.WriteAllBytes(Path.Combine(output, "extrema-trace.png"), (byte[])image.GetImageBytes());
        dynamic cached = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ImpulseSignalPlot")!, source, true)!;
        cached.Color = trace.Color; cached.LineWidth = trace.LineWidth; cached.LineStyle.AntiAlias = false;
        byte[] Render(dynamic item, int width)
        {
            plot.PlottableList.Clear(); plot.PlottableList.Add(item);
            using dynamic image = plot.GetImage(width, 400);
            return (byte[])image.GetImageBytes();
        }
        void Same(int width)
        {
            byte[] expected = Render(trace, width);
            if (!expected.SequenceEqual(Render(cached, width)) || !expected.SequenceEqual(Render(cached, width)))
                throw new Exception("Cached IR geometry differs from uncached extrema rendering.");
        }
        Same(600);
        plot.Axes.SetLimits(.2, .8, -150.0, 10.0); Same(600);
        Same(900);
        dynamic replacement = Activator.CreateInstance(sdk.GetType("ScottPlot.DataSources.SignalXYSourceDoubleArray")!, times,
            values.Select(v => v + 10).ToArray())!;
        cached.Data = replacement; trace.Data = replacement;
        Same(900);
        cached.Data.YOffset = trace.Data.YOffset = 5; Same(900);
        Console.WriteLine("PASS cached/uncached pixels agree on warm repaint, pan/zoom, resize, lazy source replacement and source offset.");
        // Same geometry and style in alternating runs, with enough samples to
        // expose reduction cost separately from WPF opening/JIT and image encoding.
        double[] largeTimes = Enumerable.Range(0, 1048576).Select(i => i / 1048576.0).ToArray();
        double[] largeValues = largeTimes.Select((t, i) => -60 + 20 * Math.Sin(i * .71)).ToArray();
        dynamic largeSource = Activator.CreateInstance(sdk.GetType("ScottPlot.DataSources.SignalXYSourceDoubleArray")!, largeTimes, largeValues)!;
        trace.Data = largeSource; cached.Data = largeSource;
        plot.Axes.SetLimits(0.0, 1.0, -180.0, 20.0);
        var normalMs = new List<double>(); var cachedMs = new List<double>();
        double Draw(dynamic item)
        {
            plot.PlottableList.Clear(); plot.PlottableList.Add(item);
            var watch = Stopwatch.StartNew();
            using (IDisposable image = plot.GetImage(1200, 650)) { }
            return watch.Elapsed.TotalMilliseconds;
        }
        Draw(trace); Draw(cached);
        for (int i = 0; i < 9; i++)
        {
            if (i % 2 == 0) { normalMs.Add(Draw(trace)); cachedMs.Add(Draw(cached)); }
            else { cachedMs.Add(Draw(cached)); normalMs.Add(Draw(trace)); }
        }
        var benchmark = new { samples = largeTimes.Length, uncachedMs = normalMs, cachedMs,
            uncachedMedianMs = normalMs.Order().ElementAt(4), cachedMedianMs = cachedMs.Order().ElementAt(4) };
        File.WriteAllText(Path.Combine(output, "dense-render-benchmark.json"), JsonSerializer.Serialize(benchmark, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"1M identical-style repaint median: uncached {benchmark.uncachedMedianMs:F3} ms; cached {benchmark.cachedMedianMs:F3} ms.");
        Console.WriteLine("Rendered constant signal and single-sample peak/notch for pixel-level extrema verification.");
    }

    public static void Run(Assembly assembly, object result, string output)
    {
        Directory.CreateDirectory(output);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var values = new Dictionary<string, object>();
        double Time(string name, Action action)
        {
            var watch = Stopwatch.StartNew(); action(); watch.Stop();
            values[name] = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"PERF {name}: {watch.Elapsed.TotalMilliseconds:F3} ms"); Console.Out.Flush();
            File.WriteAllText(Path.Combine(output, "timing.json"), JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            return watch.Elapsed.TotalMilliseconds;
        }
        static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        Window window = null!;
        long allocated = GC.GetTotalAllocatedBytes();
        Time("constructMs", () => window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ImpulseAnalysisWindow")!, result)!);
        var view = (UserControl)window.Content; var type = view.GetType();
        dynamic plot = type.GetField("_plot", hidden)!.GetValue(view)!;
        dynamic navigator = type.GetField("_navigator", hidden)!.GetValue(view)!;
        Time("firstPaintMs", () => { window.Show(); window.UpdateLayout(); Pump(); });
        values["openAllocatedBytes"] = GC.GetTotalAllocatedBytes() - allocated;
        Time("renderFullMs", () => { using var image = (IDisposable)plot.Plot.GetImage(1200, 650); });
        dynamic measured = result;
        double fullStart = -measured.ImpulsePlot.DirectIndex / (double)measured.ImpulsePlot.SampleRate;
        double fullEnd = (measured.ImpulsePlot.Samples.Length - 1 - measured.ImpulsePlot.DirectIndex) / (double)measured.ImpulsePlot.SampleRate;
        plot.Plot.Axes.SetLimits(fullStart, fullEnd, -180.0, 20.0);
        Time("fullRangeColdMs", () => { using var image = (IDisposable)plot.Plot.GetImage(1200, 650); });
        var repeats = new List<double>();
        for (int i = 0; i < 7; i++)
            repeats.Add(Time("fullRangeRepeat" + i + "Ms", () => { using var image = (IDisposable)plot.Plot.GetImage(1200, 650); }));
        values["fullRangeRepeatMedianMs"] = repeats.Order().ElementAt(repeats.Count / 2);
        Pump(); // Drain the range-change navigator refresh before measuring hover.
        var frames = new List<double>();
        int before = (int)navigator.Plot.RenderManager.RenderCount;
        int mainBefore = (int)plot.Plot.RenderManager.RenderCount;
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(20); // Separate real pointer updates beyond the 60 Hz throttle; outside the timed region.
            int cursor = i;
            frames.Add(Time("cursor" + i + "Ms", () =>
            {
                type.GetMethod("UpdateCursor", hidden)!.Invoke(view, new object[] { new Point(350 + cursor * 40, 220) }); Pump();
            }));
        }
        values["navigatorRendersDuringHover"] = (int)navigator.Plot.RenderManager.RenderCount - before;
        values["mainPlotRendersDuringHover"] = (int)plot.Plot.RenderManager.RenderCount - mainBefore;
        values["cursorMedianMs"] = frames.Order().ElementAt(frames.Count / 2);
        values["cursorWorstMs"] = frames.Max();
        // Also exercise the real WPF/ScottPlot mouse event path without moving
        // the user's physical pointer, to detect a renderer refresh underneath
        // the independent cursor overlay.
        var control = (Control)plot;
        var surface = (UIElement)control.Template.FindName("PART_SKElement", control)!;
        int routedBefore = (int)plot.Plot.RenderManager.RenderCount;
        Time("routedMouseMoveMs", () =>
        {
            surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent }); Pump();
        });
        values["rendersDuringRoutedMouseMove"] = (int)plot.Plot.RenderManager.RenderCount - routedBefore;
        if ((int)values["navigatorRendersDuringHover"] != 0 || (int)values["mainPlotRendersDuringHover"] != 0
            || (int)values["rendersDuringRoutedMouseMove"] != 0)
            throw new Exception("Pointer hover unexpectedly redraws an Impulse plot.");
        Time("zoomMs", () => { plot.Plot.Axes.SetLimitsX(-.02, .14); plot.Refresh(); Pump(); });
        Time("renderZoomMs", () => { using var image = (IDisposable)plot.Plot.GetImage(1200, 650); });
        var toggle = (CheckBox)type.GetField("_showWindowed", hidden)!.GetValue(view)!;
        Time("windowedOnMs", () => { toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); Pump(); });
        window.Close(); Pump();
        Time("reopenConstructMs", () => window = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ImpulseAnalysisWindow")!, result)!);
        Time("reopenPaintMs", () => { window.Show(); window.UpdateLayout(); Pump(); });
        window.Close(); Pump();
        values["dllSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        File.WriteAllText(Path.Combine(output, "timing.json"), JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS immutable Impulse opening, render, hover, zoom and reopening benchmark.");
    }
}
