using System.Text.Json;
using System.IO;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class C360LevelChecks
{
    public static async Task Run(string output, string setupPath, string configPath)
    {
        Directory.CreateDirectory(output);
        var defaults = JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(setupPath))!;
        var checking = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!;
        C360ModelDefinition.EnsureSeed(checking);
        var model = checking.models.First(m => C360ModelDefinition.Matches(m.testItems.InOut));
        foreach (var test in model.testItems.InOut.Tests) test.Enable = test.SpeakerStage == "LineIn";
        var line = model.testItems.InOut.Tests.Single(t => t.IsEnabled);
        using var manager = new SoncaBleManager(); manager.LogMessage += Console.WriteLine;
        if (!await manager.EnsureConnectedAsync(defaults.BleControllerAddress, userInitiated: true)) throw new Exception("BLE unavailable");
        using var devices = new MMDeviceEnumerator(); var endpoints = new List<C360ModelEndpoint>();
        foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (d) endpoints.Add(new(d.ID, d.FriendlyName, d.DataFlow == DataFlow.Capture));
        var initialPlan = C360ModelPlan.Create(model.testItems.InOut, endpoints, defaults);
        using var playback = devices.GetDevice(initialPlan.Setup.LinePlaybackId!); using var input = devices.GetDevice(defaults.LineOutCaptureId);
        float beforeOutput = playback.AudioEndpointVolume.MasterVolumeLevelScalar, beforeInput = input.AudioEndpointVolume.MasterVolumeLevelScalar;
        using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
        var control = new C360AudioBench(engine, manager, output); var baseline = await control.ReadAsync(default);
        var rows = new List<(double Output, double Input, double Level, double Score, C360ElectronicResult Result)>();
        async Task Measure(double outPercent, double inPercent, string tag, double levelDbFs = -12)
        {
            line.WindowsPlaybackPercent = outPercent; line.WindowsRecordingPercent = inPercent;
            line.PlaybackLevelDbfs = levelDbFs;
            var plan = C360ModelPlan.Create(model.testItems.InOut, endpoints, defaults);
            var bench = new C360AudioBench(engine, manager, Path.Combine(output, tag));
            var result = (await new C360ElectronicSuite().RunAsync(plan.Bind(bench), plan.Setup, stages: plan.Stages)).Single();
            var m = result.Measurement;
            double score = double.PositiveInfinity;
            if (m != null && !m.IsClipped && m.SignalLevelDbFs >= -35 && m.EstimatedSnrDb >= 30 && result.Status is "PASS" or "FAIL")
            {
                var points = new[] { 80.0, 1000, 4000 }.Select(f => new TestRunner(engine).BuildSweepDistortionSummary(m, f)).ToArray();
                if (points.All(p => p.IsValid && double.IsFinite(p.ThdPercent))) score = points.Max(p => p.ThdPercent);
            }
            rows.Add((outPercent, inPercent, levelDbFs, score, result));
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows.Select(r => new { output = r.Output, input = r.Input, levelDbFs = r.Level, score = double.IsFinite(r.Score) ? (double?)r.Score : null, r.Result.Status, r.Result.Summary }), C360Reference.Json));
            Console.WriteLine($"LEVELS out={outPercent:F0}% in={inPercent:F0}% score={score:F4}: {result.Summary}");
        }
        try
        {
            foreach (double level in new[] { 25.0, 50, 75, 100 }) await Measure(level, beforeInput * 100, "output-" + level);
            var best = rows.OrderBy(r => r.Score).First();
            if (!double.IsFinite(best.Score)) best = rows.OrderByDescending(r => r.Result.Measurement?.EstimatedSnrDb ?? -180).First();
            foreach (double signal in new[] { -9.0, -6 })
            {
                if (rows.Last().Result.Measurement is { PeakSample: > .7 }) break;
                await Measure(100, beforeInput * 100, "signal-" + Math.Abs(signal), signal);
            }
            best = rows.OrderBy(r => r.Score).First();
            if (!double.IsFinite(best.Score)) best = rows.OrderByDescending(r => r.Result.Measurement?.EstimatedSnrDb ?? -180).First();
            foreach (double level in new[] { 50.0, 75, 100 }.Where(v => Math.Abs(v - beforeInput * 100) > 1)) await Measure(best.Output, level, "input-" + level, best.Level);
            best = rows.OrderBy(r => r.Score).First();
            if (!double.IsFinite(best.Score)) best = rows.OrderByDescending(r => r.Result.Measurement?.EstimatedSnrDb ?? -180).First();
            await Measure(best.Output, best.Input, "confirm", best.Level);
            var last = rows.Last();
            bool repeatable = double.IsFinite(last.Score) && Math.Abs(last.Score - best.Score) <= Math.Max(.1, best.Score * .25);
            File.WriteAllText(Path.Combine(output, "recommendation.json"), JsonSerializer.Serialize(new { verified = repeatable, WindowsPlaybackPercent = best.Output, WindowsRecordingPercent = best.Input,
                PlaybackLevelDbfs = best.Level,
                firstMaxThd = best.Score, confirmationMaxThd = double.IsFinite(last.Score) ? (double?)last.Score : null }, C360Reference.Json));
            Console.WriteLine("Level recommendation verified=" + repeatable);
        }
        finally
        {
            var restored = await control.ReadAsync(default);
            var changed = baseline.Where(p => restored.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key.ToString("X4")).ToArray();
            bool windowsRestored = Math.Abs(playback.AudioEndpointVolume.MasterVolumeLevelScalar - beforeOutput) < .01 && Math.Abs(input.AudioEndpointVolume.MasterVolumeLevelScalar - beforeInput) < .01;
            File.WriteAllText(Path.Combine(output, "restoration.json"), JsonSerializer.Serialize(new { registers = baseline.Count, changed, windowsRestored, beforeOutput, beforeInput }));
            if (changed.Length > 0 || !windowsRestored) throw new Exception("Level test restoration mismatch");
        }
    }
}
