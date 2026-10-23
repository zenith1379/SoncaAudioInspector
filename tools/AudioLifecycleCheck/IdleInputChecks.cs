using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class IdleInputChecks
{
    // Fresh input-only captures: this mode never creates a render stream or sends BLE commands.
    public static async Task Run(string output, string configPath)
    {
        Directory.CreateDirectory(output);
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        using var enumerator = new MMDeviceEnumerator();
        using var input = enumerator.GetDevice(config["RecordingDeviceId"]!.GetValue<string>());
        using var engine = new AudioEngine { UseExclusivePlayback = false,
            RecordingChannel = config["LastRecordingChannel"]?.GetValue<int?>() };
        AudioEngine.flagSaveFile = false;
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
        var observations = new List<object>();
        var captures = new List<object>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task Observe()
        {
            for (int i = 0; i < 29; i++)
            {
                var meters = renderDevices.Select(device =>
                {
                    try { return new { device.FriendlyName, peak = (double?)device.AudioMeterInformation.MasterPeakValue, error = (string?)null }; }
                    catch (Exception ex) { return new { device.FriendlyName, peak = (double?)null, error = (string?)ex.Message }; }
                }).ToArray();
                observations.Add(new { utc = DateTime.UtcNow, meters, usage = AudioSessionDiagnostics.Scan() });
                await Task.Delay(250, cancellation.Token);
            }
        }
        try
        {
            Task observation = Observe();
            for (int pass = 1; pass <= 2; pass++)
            {
                var capture = await engine.CaptureSilenceAsync(input, null, 3, cancellation.Token);
                float[] samples = capture.DutSamples;
                if (samples.Length == 0) throw new Exception("Input produced no fresh samples.");
                int rate = capture.DutSampleRate;
                using (var wav = new WaveFileWriter(Path.Combine(output, $"idle-{pass}.wav"), WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)))
                    wav.WriteSamples(samples, 0, samples.Length);
                int window = rate / 4;
                double Db(IEnumerable<float> values) => 10 * Math.Log10(values.Select(v => (double)v * v).Average() + 1e-20);
                var row = new { pass, input = input.FriendlyName, inputId = input.ID, rate, recordingChannel = engine.RecordingChannel,
                    count = samples.Length, durationSeconds = samples.Length / (double)rate,
                    rmsDbFs = Db(samples), peak = samples.Max(v => Math.Abs(v)),
                    quarterSecondRmsDbFs = Enumerable.Range(0, samples.Length / window).Select(i => Db(samples.Skip(i * window).Take(window))).ToArray() };
                captures.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                await Task.Delay(300, cancellation.Token);
            }
            await observation;
            var report = new { utc = DateTime.UtcNow, playbackCreated = false, captures, observations };
            File.WriteAllText(Path.Combine(output, "idle-input.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Fresh input-only captures and Windows render/session observations saved; no playback or BLE control sent.");
        }
        finally
        {
            engine.StopAllAudio();
            foreach (var device in renderDevices) device.Dispose();
        }
    }
}
