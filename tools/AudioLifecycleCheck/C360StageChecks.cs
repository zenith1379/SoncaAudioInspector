using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class C360StageChecks
{
    public static async Task Run(string output, string configFile, string setupFile, string id, bool verifyReverb = false, bool reverbOff = false, string? referenceDirectory = null, bool findReference = true)
    {
        Directory.CreateDirectory(output);
        var model = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configFile))!.models.Single(m => C360ModelDefinition.Matches(m.testItems?.InOut));
        if (!model.testItems.InOut.Tests.Any(t => t.id == id)) throw new Exception("Unknown test ID");
        foreach (var test in model.testItems.InOut.Tests) test.Enable = test.id == id;
        if (reverbOff) foreach (var test in model.testItems.InOut.Tests.Where(t => t.SpeakerStage == "MicEffects"))
            test.ImpulseEffects = (test.ImpulseEffects ?? new()) with { ExpectReverb = false };
        var saved = JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(setupFile))!;
        using var manager = new SoncaBleManager(); manager.LogMessage += Console.WriteLine;
        if (!await manager.EnsureConnectedAsync(saved.BleControllerAddress, userInitiated: true)) throw new Exception("BLE unavailable; no stimulus emitted.");
        using var enumeration = new MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in enumeration.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (device)
            endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        File.WriteAllText(Path.Combine(output, "endpoints-before-source.json"), JsonSerializer.Serialize(endpoints, C360Reference.Json));
        var plan = C360ModelPlan.Create(model.testItems.InOut, endpoints, saved);
        using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
        var bench = new C360AudioBench(engine, manager, output) { VerifyReverbAlways = verifyReverb };
        var baseline = await bench.ReadAsync(default);
        try
        {
            IC360ElectronicBench acquisition = bench;
            if (referenceDirectory != null)
            {
                using var input = enumeration.GetDevice(plan.Setup.LineOutCaptureId);
                using var client = input.AudioClient;
                string mixerKey = string.Join(";", new ushort[] { 0x6102, 0x6103, 0x6400, 0x6402, 0x6403, 0x6467, 0x6254 }
                    .Select(reg => $"{reg:X4}:{baseline.GetValueOrDefault(reg)}")) + ";CAPTURE_RATE:" + client.MixFormat.SampleRate;
                acquisition = new C360ReferenceBench(bench, plan, model.model, referenceDirectory, mixerKey, findReference, 3,
                    run => new C360AudioBench(engine, manager, Path.Combine(output, "reference-run-" + run)));
            }
            var results = await new C360ElectronicSuite().RunAsync(plan.Bind(acquisition), plan.Setup, stages: plan.Stages);
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { id, results = results.Select(r => new { r.StageName, r.Status, r.Summary }) }, C360Reference.Json));
            Console.WriteLine(results.Single().Summary);
        }
        finally
        {
            var restored = await bench.ReadAsync(default);
            var changed = baseline.Where(p => restored.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key.ToString("X4")).ToArray();
            File.WriteAllText(Path.Combine(output, "restoration.json"), JsonSerializer.Serialize(new { registers = baseline.Count, changed }));
            if (changed.Length > 0) throw new Exception("DSP restoration incomplete");
            Console.WriteLine("PASS DSP restoration: " + baseline.Count);
        }
    }
}
