using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class RewUiChecks
{
    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "data"));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
        var runnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
        var routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        object engine = Activator.CreateInstance(engineType)!;
        object runner = Activator.CreateInstance(runnerType, engine)!;
        var routing = (UserControl)Activator.CreateInstance(routingType)!;
        routingType.GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routingType.GetField("_testRunner", hidden)!.SetValue(routing, runner);
        routingType.GetField("_isConfigLoaded", hidden)!.SetValue(routing, true);
        if (routing.FindName("ChkNormalizeOneKilohertz") != null)
            throw new Exception("Fresh response is normalized; absolute dBFS must be the default.");
        var rateCombo = (ComboBox)routing.FindName("ComboPlaybackSampleRate");
        if (!rateCombo.Items.OfType<ComboBoxItem>().Any(i => i.Tag?.ToString() == "48000")) throw new Exception("48 kHz cannot be selected for REW comparison.");
        foreach (int rate in new[] { 44100, 48000 })
        foreach (int length in new[] { 65536, 131072, 524288, 1048576 })
        {
            rateCombo.SelectedItem = rateCombo.Items.OfType<ComboBoxItem>().First(i => i.Tag?.ToString() == rate.ToString());
            routingType.GetMethod("SelectLogSweepDuration", hidden)!.Invoke(routing, new object[] { length / (double)rate });
            routingType.GetMethod("ConfigureSelectedSweep", hidden)!.Invoke(routing, null);
            routingType.GetMethod("ConfigureProductionMeasurement", hidden)!.Invoke(routing, new object?[] { null });
            double actual = (double)runnerType.GetProperty("LogSweepDurationSeconds")!.GetValue(runner)!;
            if (Math.Round(actual * rate) != length || (int)engineType.GetProperty("PlaybackSampleRate")!.GetValue(engine)! != rate)
                throw new Exception($"Selected {length}/{rate} was overwritten by acquisition configuration.");
            var selected = (ComboBoxItem)((ComboBox)routing.FindName("ComboSweepLength")).SelectedItem;
            if (!selected.Content.ToString()!.Contains((length / (double)rate).ToString("0.00"))) throw new Exception("Length label is inaccurate.");
            ((CheckBox)routing.FindName("ChkDistortionCalibration")).IsChecked = true;
            ((CheckBox)routing.FindName("ChkDistortionHarmonicReference")).IsChecked = true;
            routingType.GetMethod("SaveConfig", hidden)!.Invoke(routing, null);
            string legacyConfigPath = (string)routingType.GetMethod("GetRoutingConfigPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(legacyConfigPath))!;
            legacy["NormalizeFrequencyResponseToOneKilohertz"] = true;
            File.WriteAllText(legacyConfigPath, legacy.ToJsonString());
            using var reloadedEngine = (IDisposable)Activator.CreateInstance(engineType)!;
            object reloadedRunner = Activator.CreateInstance(runnerType, reloadedEngine)!;
            var reloaded = (UserControl)Activator.CreateInstance(routingType)!;
            routingType.GetField("_audioEngine", hidden)!.SetValue(reloaded, reloadedEngine);
            routingType.GetField("_testRunner", hidden)!.SetValue(reloaded, reloadedRunner);
            routingType.GetMethod("LoadConfig", hidden)!.Invoke(reloaded, null);
            double restored = (double)runnerType.GetProperty("LogSweepDurationSeconds")!.GetValue(reloadedRunner)!;
            if (Math.Round(restored * rate) != length || reloaded.FindName("ChkNormalizeOneKilohertz") != null
                || (bool)runnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.GetValue(reloadedRunner)!
                || !(bool)runnerType.GetProperty("ApplyDistortionCalibration")!.GetValue(reloadedRunner)!
                || !(bool)runnerType.GetProperty("UseHarmonicFrequencyAsDistortionReference")!.GetValue(reloadedRunner)!)
                throw new Exception($"Restart did not restore {length}/{rate}, absolute level and distortion options.");
        }
        Console.WriteLine("PASS 64K/128K/512K/1M at 44.1/48 kHz: acquisition, labels, persistence and restart; independent distortion settings.");
        routingType.GetField("_standardOneKilohertzLevelDbFs", hidden)!.SetValue(routing, -20.0);
        routingType.GetMethod("UpdateOneKilohertzLevel", hidden)!.Invoke(routing, new object[] { -26.020599913 });
        string level = ((TextBlock)routing.FindName("TxtOneKilohertzLevel")).Text;
        if (!level.Contains("-6.0 dB") || !level.Contains("-26.0 dBFS")) throw new Exception("Absolute dB deviation is hidden: " + level);
        Console.WriteLine("PASS raw response displays the real -6.02 dB offset from its reference.");
        ((IDisposable)engine).Dispose();

        var mainType = assembly.GetType("SoncaAudioInspector.MainWindow")!;
        var shortcut = mainType.GetMethod("TryHandleModelShortcut", BindingFlags.Static | BindingFlags.NonPublic)!;
        var calls = new List<bool>();
        var action = new Action<bool>(calls.Add);
        var modifiers = ModifierKeys.Control | ModifierKeys.Shift;
        shortcut.Invoke(null, new object[] { Key.M, modifiers, false, action });
        shortcut.Invoke(null, new object[] { Key.N, modifiers, false, action });
        shortcut.Invoke(null, new object[] { Key.N, modifiers, true, action });
        shortcut.Invoke(null, new object[] { Key.N, ModifierKeys.Control, false, action });
        if (!calls.SequenceEqual(new[] { false, true })) throw new Exception("Model shortcuts or key-repeat guard failed.");
        var editorType = assembly.GetType("SoncaAudioInspector.ModelConfigEditorWindow")!;
        var parse = editorType.GetMethod("ParseModel")!;
        const string json = """
            {"model":"original","vendorExtra":123,"testItems":{"InOut":{"Description 2":"keep me","Devices":{"Input":{"out":"Speaker"},"Output":{"in":"Mic"}},"Tests":[{"id":"IO1","Playback Out":"out","Recording In":"in","Bass THD Frequency":100,"Frequency Response Method":"LogSweep","customLimit":42}]}}}
            """;
        object model = parse.Invoke(null, new object[] { "MODEL MỚI", json })!;
        string serialized = JsonSerializer.Serialize(model, model.GetType());
        using var readback = JsonDocument.Parse(serialized);
        if (readback.RootElement.GetProperty("model").GetString() != "MODEL MỚI" || readback.RootElement.GetProperty("vendorExtra").GetInt32() != 123
            || readback.RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Description 2").GetString() != "keep me"
            || readback.RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Tests")[0].GetProperty("customLimit").GetInt32() != 42)
            throw new Exception("Model edits lost unknown configuration fields.");
        var savedTest = readback.RootElement.GetProperty("testItems").GetProperty("InOut").GetProperty("Tests")[0];
        if (savedTest.GetProperty("BassThdFrequency").GetDouble() != 100 || savedTest.GetProperty("FrequencyResponseMethod").GetString() != "LogSweep")
            throw new Exception("Legacy model field names are displayed but ignored by measurement configuration.");
        foreach (string invalid in new[] { json.Replace("\"out\":\"Speaker\"", "\"other\":\"Speaker\""), json.Replace("\"id\":\"IO1\"", "\"id\":\"\"") })
        {
            bool rejected = false;
            try { parse.Invoke(null, new object[] { "Invalid", invalid }); }
            catch (TargetInvocationException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid model route/id was accepted.");
        }
        var atomic = assembly.GetType("SoncaAudioInspector.AtomicFile")!.GetMethod("WriteAllText")!;
        string configPath = Path.Combine(output, "checking_config.json");
        string saved = "{\"models\":[" + serialized + "," + serialized.Replace("MODEL MỚI", "SECOND MODEL") + "]}";
        atomic.Invoke(null, new object[] { configPath, "{\"models\":[]}", true });
        atomic.Invoke(null, new object[] { configPath, saved, true });
        object fullConfig = JsonSerializer.Deserialize(File.ReadAllText(configPath), assembly.GetType("SoncaAudioInspector.CheckingConfig")!)!;
        string configReadback = JsonSerializer.Serialize(fullConfig, fullConfig.GetType());
        if (JsonDocument.Parse(configReadback).RootElement.GetProperty("models").GetArrayLength() != 2 || !File.Exists(configPath + ".bak"))
            throw new Exception("Saving model configuration lost models or failed to back up.");
        var editor = (Window)Activator.CreateInstance(editorType, new object?[] { model })!;
        var content = (FrameworkElement)editor.Content;
        content.Measure(new Size(860, 660)); content.Arrange(new Rect(0, 0, 860, 660)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(860, 660, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "model-editor.png"))) png.Save(stream);
        editor.Close();
        Console.WriteLine("PASS Ctrl+Shift+M/N, repeat guard, model validation, unknown fields, multi-model config, atomic backup and editor render.");
    }
}
