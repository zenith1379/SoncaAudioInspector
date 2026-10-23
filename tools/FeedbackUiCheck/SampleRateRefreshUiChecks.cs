using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class SampleRateRefreshUiChecks
{
    public static void Run(Assembly assembly, string output)
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
        var rateCombo = (ComboBox)routing.FindName("ComboPlaybackSampleRate");
        rateCombo.SelectedItem = rateCombo.Items.OfType<ComboBoxItem>().Single(i => i.Tag?.ToString() == "44100");
        routingType.GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routingType.GetField("_testRunner", hidden)!.SetValue(routing, runner);
        ((Button)routing.FindName("BtnStart")).IsEnabled = true;
        var observe = routingType.GetMethod("ObserveEndpointSampleRates", hidden)!;
        var refresh = routingType.GetMethod("RefreshCaptureFormatIfIdle", hidden)!;
        var actual = routingType.GetField("_lastObservedCaptureRate", hidden)!;
        var pending = routingType.GetField("_captureFormatRefreshPending", hidden)!;
        var syncFailure = routingType.GetField("_sampleRateSyncFailure", hidden)!;
        var activeTask = routingType.GetField("_activeMeasurementTask", hidden)!;
        var warningBuilder = routingType.GetMethod("BuildSampleRateWarning", BindingFlags.Static | BindingFlags.NonPublic)!;
        void Observe(string? outputId, int? outputRate, string? inputId, int? inputRate) =>
            observe.Invoke(routing, new object?[] { outputId, outputRate, inputId, inputRate });
        string Warning(int? outRate, int? inRate) => (string)warningBuilder.Invoke(null,
            new object?[] { 44100, outRate, inRate, actual.GetValue(routing) })!;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var sharedWarning = routingType.GetMethod("BuildSharedFormatWarning", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(((string)sharedWarning.Invoke(null, new object?[] { 44100, 48000 })!).Contains("Default Format 44.1 kHz")
            && (string)sharedWarning.Invoke(null, new object?[] { 44100, 44100 })! == "",
            "Processed Shared format was confused with the configured Default Format.");

        // Keep the old measurement immutable while Sound changes the current route.
        object oldSweep = JsonSerializer.Deserialize("{\"SampleRate\":48000}", assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!)!;
        runnerType.GetProperty("LastSweepResult")!.SetValue(runner, oldSweep);
        Observe("out", 48000, "in", 48000);
        actual.SetValue(routing, 48000);
        syncFailure.SetValue(routing, "Previous driver sync failed");
        Require(Warning(48000, 48000).Contains("48 kHz"), "Real 48 kHz path was not warned.");
        Observe("out", 44100, "in", 44100);
        Require(actual.GetValue(routing) == null && Warning(44100, 44100) == "", "Sound change retained a stale capture warning.");
        Require((string)syncFailure.GetValue(routing)! == "", "Sound change retained a stale sync failure.");
        Require(ReferenceEquals(oldSweep, runnerType.GetProperty("LastSweepResult")!.GetValue(runner))
            && (int)oldSweep.GetType().GetProperty("SampleRate")!.GetValue(oldSweep)! == 48000,
            "Refreshing Windows settings relabeled the previous 48 kHz recording.");
        Require((bool)pending.GetValue(routing)!, "Format change did not schedule capture-session refresh.");

        var running = new TaskCompletionSource();
        activeTask.SetValue(routing, running.Task);
        refresh.Invoke(routing, null);
        Require((bool)pending.GetValue(routing)!, "Format refresh interrupted an active measurement.");
        activeTask.SetValue(routing, null);
        refresh.Invoke(routing, null);
        Require(!(bool)pending.GetValue(routing)!, "Idle capture-session refresh was not completed.");

        actual.SetValue(routing, 48000);
        Observe("out", 44100, "in", 44100);
        Require(Warning(44100, 44100).Contains("lượt vừa thu 48 kHz"), "Actual new 48 kHz capture was hidden by aligned endpoint settings.");
        Observe("out", null, "in", null);
        Require((int?)actual.GetValue(routing) == 48000, "Transient endpoint read failure cleared an actual capture warning.");
        Observe("out", 48000, "in", 48000);
        Require(actual.GetValue(routing) == null && Warning(48000, 48000).Contains("48 kHz"), "Reverse 44.1 to 48 kHz change was missed.");
        actual.SetValue(routing, 48000);
        Observe("out2", 48000, "in2", 48000);
        Require(actual.GetValue(routing) == null, "Changing devices retained another route's capture rate.");
        Console.WriteLine("PASS 48 -> 44.1 -> 48 kHz, same endpoint IDs, route changes, transient reads, stale failure clearing and immutable previous measurement.");
        Console.WriteLine("PASS capture refresh waits for measurement completion; new actual 48 kHz capture remains warned.");

        // Exercise the actual WPF warning display without changing any Windows settings.
        routingType.GetField("_observedEndpointRates", hidden)!.SetValue(routing, null);
        syncFailure.SetValue(routing, "");
        var updateWarning = routingType.GetMethod("UpdateSampleRateWarning", hidden)!;
        updateWarning.Invoke(routing, new object?[] { 48000 });
        var border = (FrameworkElement)routing.FindName("BorderSampleRateWarning");
        Require(border.Visibility == Visibility.Visible, "Actual capture warning was not visible.");
        border.Measure(new Size(1100, 110)); border.Arrange(new Rect(0, 0, 1100, 110)); border.UpdateLayout();
        var image = new RenderTargetBitmap(1100, 110, 96, 96, PixelFormats.Pbgra32); image.Render(border);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(output, "actual-capture-warning.png"))) png.Save(file);
        updateWarning.Invoke(routing, new object?[] { 44100 });
        Require(border.Visibility == Visibility.Collapsed, "Warning did not clear with actual 44.1 kHz capture.");
        Console.WriteLine("PASS WPF warning visible at actual 48 kHz and cleared at actual 44.1 kHz; no endpoint writes.");

        var endpoints = new List<IDisposable>();
        try
        {
            routingType.GetField("_isAutoSelectingDevices", hidden)!.SetValue(routing, true);
            var itemType = assembly.GetType("SoncaAudioInspector.DeviceItem")!;
            foreach (var pair in new[] { ("ComboPlayback", "GetPlaybackDevices"), ("ComboRecording", "GetRecordingDevices") })
            {
                var combo = (ComboBox)routing.FindName(pair.Item1);
                foreach (object device in (System.Collections.IEnumerable)engineType.GetMethod(pair.Item2)!.Invoke(engine, null)!)
                {
                    endpoints.Add((IDisposable)device);
                    string name = (string)device.GetType().GetProperty("FriendlyName")!.GetValue(device)!;
                    combo.Items.Add(Activator.CreateInstance(itemType, device, name));
                }
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            }
            engineType.GetProperty("RecordingChannel")!.SetValue(engine, 0);
            engineType.GetProperty("PlaybackChannel")!.SetValue(engine, 0);
            routingType.GetMethod("RefreshSelectedDeviceFormats", hidden)!.Invoke(routing, null);
            foreach (var pair in new[] { ("SelectedPlaybackDevice", "TxtPlaybackFormat"), ("SelectedRecordingDevice", "TxtRecordingFormat") })
            {
                object? device = routingType.GetProperty(pair.Item1)!.GetValue(routing);
                if (device == null) continue;
                int rate = (int)routingType.GetMethod("ReadEndpointSampleRate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { device })!;
                string expected = (rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz";
                string displayed = ((TextBlock)routing.FindName(pair.Item2)).Text;
                Require(displayed.StartsWith("Default Format: " + expected) && displayed.Contains("Shared ")
                    && displayed.Contains("32-bit Float"), "Device and processing format labels are missing or misleading.");
                Console.WriteLine("READBACK " + displayed);
            }
            var inputLabel = (TextBlock)routing.FindName("TxtRecordingFormat");
            if (((ComboBox)routing.FindName("ComboRecording")).SelectedItem != null)
            {
                inputLabel.Measure(new Size(365, double.PositiveInfinity));
                inputLabel.Arrange(new Rect(0, 0, 365, inputLabel.DesiredSize.Height)); inputLabel.UpdateLayout();
                var bitmap = new RenderTargetBitmap(365, (int)Math.Ceiling(inputLabel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(inputLabel); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "device-and-shared-format.png")); encoder.Save(file);
            }
            Require((int?)engineType.GetProperty("RecordingChannel")!.GetValue(engine) == 0
                && (int?)engineType.GetProperty("PlaybackChannel")!.GetValue(engine) == 0,
                "Periodic format refresh changed the selected audio channels.");
            Console.WriteLine("PASS periodic WPF format labels match live Windows readback and preserve channel selection; no settings writes.");
        }
        finally { foreach (var device in endpoints) device.Dispose(); }
    }
}
