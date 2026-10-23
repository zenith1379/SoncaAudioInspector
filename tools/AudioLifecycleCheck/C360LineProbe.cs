using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal sealed class C360LineProbe(IC360ElectronicBench inner, AudioEngine engine, string directory) : IC360ElectronicBench
{
    public string DeviceIdentity => inner.DeviceIdentity;
    public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
    public Task WriteAsync(ushort register, ushort value, CancellationToken token) => inner.WriteAsync(register, value, token);
    public void StopAudio() => inner.StopAudio();
    public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        if (stage == C360ElectronicStage.MicEffects) return new(stage, "PASS", "Diagnostic only: MIC capture skipped; not an effect result.");
        
        using var devices = new MMDeviceEnumerator(); using var input = devices.GetDevice(setup.LineOutCaptureId);
        var rows = new List<object>();
        engine.PlaybackSampleRate = setup.SampleRate; engine.RecordingGain = 1; engine.RecordingChannel = setup.CaptureChannel;
        engine.PlaybackChannel = null; engine.UseExclusivePlayback = false;
        foreach (var output in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) using (output)
        {
            if (output.FriendlyName.Contains("HDMI") || output.FriendlyName.Contains("MSI")) continue;
            var tone = Enumerable.Range(0, setup.SampleRate).Select(i => (float)(Math.Pow(10, -24 / 20.0) * Math.Sin(2 * Math.PI * 1000 * i / setup.SampleRate))).ToArray();
            var samples = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 1000, 1.4, cancellationToken: token, customSweepSamples: tone);
            var quality = AdvancedAudioMeasurement.AnalyzeTone(samples, engine.RecordingSampleRate, 1000, 9);
            rows.Add(new { output.FriendlyName, output.ID, quality.IsValid, quality.FundamentalFrequencyHz, quality.SignalLevelDbFs, quality.ThdPercent, quality.SnrDb });
            Console.WriteLine(JsonSerializer.Serialize(rows.Last(), new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            using var wav = new WaveFileWriter(Path.Combine(directory, stage + "-probe-" + rows.Count + ".wav"), WaveFormat.CreateIeeeFloatWaveFormat(engine.RecordingSampleRate, 1));
            wav.WriteSamples(samples, 0, samples.Length);
        }
        File.WriteAllText(Path.Combine(directory, stage + "-output-probes.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        return new(stage, "PASS", "Diagnostic only: see per-endpoint probe file; not a measurement verdict.");
    }
}
