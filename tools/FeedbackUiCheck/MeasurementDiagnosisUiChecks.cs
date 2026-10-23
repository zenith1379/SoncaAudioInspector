using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

internal static class MeasurementDiagnosisUiChecks
{
    public static void Run(Assembly assembly, string fixture, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "data"));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Type runnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
        Type routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        using var engine = (IDisposable)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        object runner = Activator.CreateInstance(runnerType, engine)!;
        var routing = (UserControl)Activator.CreateInstance(routingType)!;
        routingType.GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routingType.GetField("_testRunner", hidden)!.SetValue(routing, runner);
        runnerType.GetProperty("NormalizeFrequencyResponseToOneKilohertz")!.SetValue(runner, true);
        runnerType.GetProperty("LastOneKilohertzLevelDbFs")!.SetValue(runner, -36.0);
        routingType.GetField("_standardOneKilohertzLevelDbFs", hidden)!.SetValue(routing, -30.0);
        var frequencies = new List<double> { 100, 1000, 10000 };
        routingType.GetField("_freqs", hidden)!.SetValue(routing, frequencies);
        routingType.GetField("_dbValues", hidden)!.SetValue(routing, new List<double> { 0, 0, 0 });
        routingType.GetField("_standardCurve", hidden)!.SetValue(routing, frequencies.ToDictionary(f => f, _ => 0.0));
        routingType.GetField("_loadedStandardDeviceKey", hidden)!.SetValue(routing,
            routingType.GetMethod("GetStandardDeviceKey", hidden)!.Invoke(routing, null));
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var root = JsonNode.Parse(File.ReadAllText(fixture))!;
        root["Validity"] = "DIAGNOSTIC"; root["IsClipped"] = false; root["NoiseFloorDbFs"] = -100;
        var effect = root["ImpulseEffects"]!;
        effect["IsValid"] = true; effect["TailTruncated"] = false;
        effect["CapturedTailSeconds"] = 3; effect["ImpulseNoiseDb"] = -100;
        var text = (TextBlock)routing.FindName("TxtFailureDiagnosis");
        string Update()
        {
            object result = JsonSerializer.Deserialize(root.ToJsonString(), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, options)!;
            runnerType.GetProperty("LastSweepResult")!.SetValue(runner, result);
            routingType.GetMethod("RenderLocalMeasurementDiagnosis", hidden)!.Invoke(routing, null);
            return text.Text;
        }
        var messages = new List<string>();
        foreach (var state in new[] { (false, false, "Chưa phát hiện echo/reverb"), (true, false, "Có echo trong bản thu"),
            (false, true, "Có reverb trong bản thu"), (true, true, "Có reverb trong bản thu") })
        {
            effect["EchoDetected"] = state.Item1; effect["ReverbDetected"] = state.Item2;
            string message = Update();
            if (!message.Contains(state.Item3) || !message.Contains("yếu hơn line chuẩn khoảng 6.0 dB"))
                throw new Exception("UI lost effect state or normalized absolute level: " + message);
            if (!message.StartsWith("Tổng quan bản thu:") || message.Contains("tham khảo") || message.Contains("chưa phán đoán") || message.Length > 500)
                throw new Exception("Summary did not become concise and factual: " + message);
            messages.Add(message);
        }
        effect["CapturedTailSeconds"] = .5;
        if (!Update().Contains("Có echo trong bản thu") || !Update().Contains("Có reverb trong bản thu"))
            throw new Exception("Positive evidence disappeared when absence classification was unavailable.");
        effect["EchoDetected"] = false; effect["ReverbDetected"] = false;
        if (!Update().Contains("chưa xác định") || Update().Contains("Chưa phát hiện")) throw new Exception("Short-tail absence was classified by UI.");
        effect["EchoDetected"] = true; effect["ReverbDetected"] = true;
        root["NoiseFloorDbFs"] = -49;
        string noisy = Update();
        if (!noisy.Contains("Có echo trong bản thu") || !noisy.Contains("Có reverb trong bản thu"))
            throw new Exception("Background warning hid measured effects.");
        messages.Add(noisy);
        root["IsClipped"] = true;
        if (Update().Contains("Loa yếu hơn") || Update().Contains("Có echo trong bản thu")) throw new Exception("Clipped acquisition produced a volume/effect diagnosis.");
        var append = routingType.GetMethod("AppendLog", hidden)!;
        append.Invoke(routing, new object[] { "Thời gian kết quả", "Kết quả sẵn sàng sau 1.23s." });
        if (!((TextBlock)routing.FindName("TxtMeasurementTiming")).Text.Contains("1.23s")
            || !File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "measurement-timing.jsonl")).Contains("1.23s"))
            throw new Exception("Result timing is not visible and persisted.");
        File.WriteAllText(Path.Combine(output, "feedback.txt"), string.Join("\n\n", messages));
        Console.WriteLine("PASS local UI: four observed effect states, concise summary, positive effects survive noisy/short classification limits, clipping guard, normalized -6 dB, timing/readback; no audio emitted.");
    }
}
