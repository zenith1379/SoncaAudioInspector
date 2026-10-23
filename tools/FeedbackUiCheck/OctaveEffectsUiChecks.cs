using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class OctaveEffectsUiChecks
{
    public static void Run(Assembly assembly, string output, string fixture)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "data"));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
        var runnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
        var routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        using var engine = (IDisposable)Activator.CreateInstance(engineType)!;
        object runner = Activator.CreateInstance(runnerType, engine)!;
        var routing = (UserControl)Activator.CreateInstance(routingType)!;
        routingType.GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routingType.GetField("_testRunner", hidden)!.SetValue(routing, runner);
        routingType.GetField("_referenceRouteKey", hidden)!.SetValue(routing, "OCTAVE_EFFECTS_UI_CHECK");
        runnerType.GetProperty("UseLogSweepFrequencyResponse")!.SetValue(runner, true);
        if ((int)runnerType.GetField("LogSweepPointsPerOctave")!.GetRawConstantValue()! != 24) throw new Exception("Default comparison is not 1/24.");
        double[] centers = (double[])runnerType.GetMethod("GenerateLogSweepEvaluationFrequencies", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { 50.0, 18000.0, 24 })!;
        if (centers.Length != 206 || centers[0] != 50 || centers[^1] != 18000) throw new Exception("Comparison grid lost 50 or 18k endpoints.");
        var flat = centers.ToDictionary(f => f, _ => 0.0);
        object repeatability = runnerType.GetMethod("AssessFeqRepeatabilityInRange")!.Invoke(null,
            new object?[] { new List<Dictionary<double, double>> { flat, flat }, true, 3.0, null, 18000.0, 50.0 })!;
        if ((int)repeatability.GetType().GetProperty("CheckedPointCount")!.GetValue(repeatability)! != centers.Length)
            throw new Exception("Reference repeatability does not use the same 1/24 grid.");
        var raw = Enumerable.Range(0, 1915).ToDictionary(i => 20 * Math.Pow(2, i / 192.0), _ => -20.0); raw[1000] = -5;
        var smoothing = assembly.GetType("SoncaAudioInspector.ResponseSmoothing")!.GetMethod("Octave24")!;
        var comparison = (Dictionary<double, double>)smoothing.Invoke(null, new object[] { raw, centers })!;
        routingType.GetField("_freqs", hidden)!.SetValue(routing, comparison.Keys.ToList());
        routingType.GetField("_dbValues", hidden)!.SetValue(routing, comparison.Values.ToList());
        routingType.GetField("_rawResponse", hidden)!.SetValue(routing, raw);
        var jsonOptions = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        object result = JsonSerializer.Deserialize(File.ReadAllText(fixture), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, jsonOptions)!;
        runnerType.GetProperty("LastSweepResult")!.SetValue(runner, result);
        routingType.GetMethod("InitCharts", hidden)!.Invoke(routing, null);
        var plot = (FrameworkElement)routing.FindName("PlotFreqResponse");
        var menu = plot.ContextMenu ?? throw new Exception("Response plot has no right-click menu.");
        if (menu.Items.Count != 2) throw new Exception("Details/Impulse must only be opened by Ctrl+Shift+I.");
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        object[] snapshots = { Array.Empty<double>(), Array.Empty<double>() };
        routingType.GetMethod("GetSafeFrequencySnapshots", hidden)!.Invoke(routing, snapshots);
        if (((double[])snapshots[0]).Length <= centers.Length || !((double[])snapshots[1]).Contains(-5.0)) throw new Exception("No smoothing was reconstructed from the smooth curve.");
        if (((List<double>)routingType.GetField("_dbValues", hidden)!.GetValue(routing)!).Count != centers.Length) throw new Exception("Viewing raw data mutated comparison/verdict inputs.");
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        routingType.GetMethod("GetSafeFrequencySnapshots", hidden)!.Invoke(routing, snapshots);
        if (((double[])snapshots[0]).Length != centers.Length) throw new Exception("Returning to 1/24 did not restore comparison data.");
        Console.WriteLine("PASS 1/24 50–18k default, real pre-smoothing data, right-click actions and unchanged verdict inputs.");

        string path = (string)routingType.GetMethod("GetStandardDeviceFileName", hidden)!.Invoke(routing, null)!;
        routingType.GetMethod("SaveReferenceCurve", hidden)!.Invoke(routing, new object?[] { path, comparison.Keys.ToArray(), comparison.Values.ToArray(), 3.0, -20.0 });
        routingType.GetMethod("CheckAndLoadStandardDevice", hidden)!.Invoke(routing, null);
        var rawReference = (IReadOnlyDictionary<double, double>)routingType.GetField("_rawStandardCurve", hidden)!.GetValue(routing)!;
        if (rawReference.Count != raw.Count || rawReference[1000] != -5 || routingType.GetField("_standardImpulseEffects", hidden)!.GetValue(routing) == null)
            throw new Exception("Raw/golden-effect reference did not survive persistence and reload.");
        File.AppendAllText(path, "\n# changed outside app\n");
        routingType.GetMethod("LoadRawStandardCurve", hidden)!.Invoke(routing, new object[] { path, "RECEIVED_RMS_DBFS_V1", false });
        if (((IReadOnlyDictionary<double, double>)routingType.GetField("_rawStandardCurve", hidden)!.GetValue(routing)!).Count != 0)
            throw new Exception("Mismatched raw sidecar was accepted for another CSV.");
        string analysisId = (string)assembly.GetType("SoncaAudioInspector.ResponseSmoothing")!.GetField("AnalysisId")!.GetRawConstantValue()!;
        File.WriteAllText(path, File.ReadAllText(path).Replace("# ResponseAnalysis=" + analysisId, "# ResponseAnalysis=OLD48"));
        routingType.GetMethod("CheckAndLoadStandardDevice", hidden)!.Invoke(routing, null);
        if (routingType.GetField("_standardCurve", hidden)!.GetValue(routing) is Dictionary<double, double> old && old.Count > 0)
            throw new Exception("A 1/48 legacy line was silently used for 1/24 comparison.");
        Console.WriteLine("PASS raw/reference-effect restart readback, CSV hash pairing and incompatible smoothing rejection.");

        var testType = assembly.GetType("SoncaAudioInspector.TestConfig")!;
        object testConfig = JsonSerializer.Deserialize("""
            {"id":"CONFIG_SYNC","name":"Model reference config","Playback Out":"OUT","Recording In":"IN",
             "FrequencyResponseMethod":"LogSweep","LogSweepDurationSeconds":7.2,"Playback Level dBFS":-18,
             "MinimumInputSignalDbFs":-80,"TestEffect":true,"ImpulseEffects":{"ExpectEcho":true,"ExpectReverb":true}}
            """, testType)!;
        routingType.GetMethod("ApplyTestCaseConfig")!.Invoke(routing, new[] { testConfig });
        routingType.GetMethod("ConfigureProductionMeasurement", hidden)!.Invoke(routing, new[] { testConfig });
        if (Math.Abs((double)runnerType.GetProperty("LogSweepDurationSeconds")!.GetValue(runner)! - 7.2) > .0001)
            throw new Exception("Model duration ignored in manual/reference acquisition.");
        string configPath = (string)routingType.GetMethod("GetStandardDeviceFileName", hidden)!.Invoke(routing, null)!;
        routingType.GetMethod("SaveReferenceCurve", hidden)!.Invoke(routing, new object?[] { configPath, comparison.Keys.ToArray(), comparison.Values.ToArray(), 3.0, -20.0 });
        routingType.GetMethod("CheckAndLoadStandardDevice", hidden)!.Invoke(routing, null);
        if (routingType.GetField("_standardCurve", hidden)!.GetValue(routing) is not Dictionary<double,double> { Count: > 0 })
            throw new Exception("Same-config model reference not reloaded.");
        testType.GetProperty("MinimumInputSignalDbFs")!.SetValue(testConfig, (double?)-75);
        routingType.GetMethod("CheckAndLoadStandardDevice", hidden)!.Invoke(routing, null);
        if (routingType.GetField("_standardCurve", hidden)!.GetValue(routing) is Dictionary<double,double> { Count: > 0 })
            throw new Exception("Legacy prefix lookup bypassed changed model configuration.");
        Console.WriteLine("PASS ordinary model duration propagation and full-config CSV identity; old legacy-prefix reference rejected after config edit.");

        var warningBuilder = routingType.GetMethod("BuildSampleRateWarning", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (object?[] rates in new object?[][] { new object?[] { 48000, 44100, 44100, null }, new object?[] { 44100, 48000, 44100, null },
            new object?[] { 44100, 44100, 48000, null }, new object?[] { 44100, 44100, 44100, 48000 } })
        {
            string warning = (string)warningBuilder.Invoke(null, rates)!;
            if (!warning.Contains("48 kHz") || !warning.Contains("44.1 kHz")) throw new Exception("48 kHz was not flagged against the 44.1 kHz standard.");
        }
        if ((string)warningBuilder.Invoke(null, new object?[] { 44100, 44100, 44100, 44100 })! != "")
            throw new Exception("Fully aligned 44.1 kHz path falsely warned.");
        var updateWarning = routingType.GetMethod("UpdateSampleRateWarning", hidden)!;
        updateWarning.Invoke(routing, new object?[] { 48000 });
        var warningBorder = (FrameworkElement)routing.FindName("BorderSampleRateWarning");
        if (warningBorder.Visibility != Visibility.Visible) throw new Exception("Actual 48 kHz capture warning was hidden with the setup sidebar.");
        RenderElement(warningBorder, Path.Combine(output, "sample-rate-warning.png"), 1050, 100);
        updateWarning.Invoke(routing, new object?[] { 44100 });
        if (warningBorder.Visibility != Visibility.Collapsed) throw new Exception("Sample-rate warning did not clear after 44.1 kHz readback.");
        Console.WriteLine("PASS visible 44.1 kHz standard warning for app/output/input/actual capture at 48 kHz, including sidebar collapsed.");

        object rows = routingType.GetMethod("BuildMeasurementValues", hidden)!.Invoke(routing, new object[] { result, false })!;
        string all = string.Join("\n", ((System.Collections.IEnumerable)rows).Cast<object>().Select(r => r.ToString()));
        foreach (string expected in new[] { "Sample rate", "Clipping", "SNR", "Clock", "C50", "Echo/reverb trong bản thu", "THD+N" })
            if (!all.Contains(expected)) throw new Exception("Measurement details missing " + expected);
        var details = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementDetailsWindow")!)!;
        details.GetType().GetMethod("Update")!.Invoke(details, new object[] { "Synthetic echo 200 ms + reverb · same sweep", rows, comparison, result });
        var detailsTabs = ((DockPanel)details.Content).Children.OfType<TabControl>().Single();
        if (!((TabItem)detailsTabs.Items[0]).Header.ToString()!.Contains("Impulse")
            || ((ContentControl)((TabItem)detailsTabs.Items[0]).Content).Content.GetType().Name != "ImpulseAnalysisView")
            throw new Exception("Ctrl+Shift+I details do not open directly on the current Impulse graph.");
        Render(details, Path.Combine(output, "measurement-details.png"), 1020, 690);
        ImpulseGraphUiChecks.Check(assembly, result, output);
        details.Close();
        Console.WriteLine("PASS complete measurement table, same-sweep IR/ETC/decay window and rendered UI evidence.");
    }

    private static void Render(Window window, string path, int width, int height)
    {
        RenderElement((FrameworkElement)window.Content, path, width, height);
    }

    private static void RenderElement(FrameworkElement content, string path, int width, int height)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
}
