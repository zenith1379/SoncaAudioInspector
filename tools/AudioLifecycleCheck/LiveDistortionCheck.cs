using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class LiveDistortionCheck
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static async Task RunEffects(string output, string configPath)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        using var enumerator = new MMDeviceEnumerator();
        bool usb = config["UseUsbPlayback"]!.GetValue<bool>();
        using var playback = enumerator.GetDevice(config[usb ? "UsbPlaybackDeviceId" : "BluetoothPlaybackDeviceId"]!.GetValue<string>());
        using var recording = enumerator.GetDevice(config["RecordingDeviceId"]!.GetValue<string>());
        if (playback.State != DeviceState.Active || recording.State != DeviceState.Active) throw new Exception("Selected endpoint is not active; no stimulus emitted.");
        int rate = config["PlaybackSampleRate"]!.GetValue<int>();
        using var engine = new AudioEngine { PlaybackSampleRate = rate, UseExclusivePlayback = false,
            PlaybackChannel = config["LastPlaybackChannel"]?.GetValue<int?>(), RecordingChannel = config["LastRecordingChannel"]?.GetValue<int?>() };
        AudioEngine.flagSaveFile = false;
        double volume = playback.AudioEndpointVolume.MasterVolumeLevelScalar;
        double level = config["PlaybackLevelDbfs"]!.GetValue<double>();
        var settings = new LogSweepSettings(rate, 18, Math.Min(22000, rate * .47), config["LogSweepDurationSeconds"]!.GetValue<double>(),
            Math.Pow(10, level / 20), PointsPerOctave: 48, DecayAnalysisSeconds: 3,
            DistortionEvaluationFrequenciesHz: new[] { 80.0, 1000.0, 4000.0 }, EffectsSettings: new());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var quiet = await engine.CaptureSilenceAsync(recording, null, 1.2, cancellation.Token);
            Save(Path.Combine(output, "quiet.wav"), quiet.DutSamples, quiet.DutSampleRate);
            double noise = 10 * Math.Log10(quiet.DutSamples.Select(v => (double)v * v).Average() + 1e-18);
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            Console.WriteLine($"Actual capture: {playback.FriendlyName} -> {recording.FriendlyName}; {rate} Hz; {level:F1} dBFS; Windows {volume:P1}; background {noise:F2} dBFS; tail {settings.DecayAnalysisSeconds:F1} s.");
            float[] capture = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0, settings.DurationSeconds + settings.DecayAnalysisSeconds,
                cancellationToken: cancellation.Token, customSweepSamples: sweep);
            Save(Path.Combine(output, "sweep-recorded.wav"), capture, engine.RecordingSampleRate);
            if (rate != engine.RecordingSampleRate) { settings = settings with { SampleRate = engine.RecordingSampleRate }; sweep = StandardAcousticMeasurement.GenerateLogSweep(settings); }
            Save(Path.Combine(output, "sweep-excitation.wav"), sweep, settings.SampleRate);
            File.WriteAllText(Path.Combine(output, "settings.json"), JsonSerializer.Serialize(settings, Options));
            File.WriteAllText(Path.Combine(output, "capture-context.json"), JsonSerializer.Serialize(new { noise, level, volume,
                playback = playback.ID, recording = recording.ID, playbackName = playback.FriendlyName, recordingName = recording.FriendlyName,
                engine.PlaybackChannel, engine.RecordingChannel, engine.RecordingSampleRate, capturedUtc = DateTime.UtcNow }, Options));
            ReanalyzeEffects(output);
        }
        finally { engine.StopAllAudio(); }
        if (Math.Abs(playback.AudioEndpointVolume.MasterVolumeLevelScalar - volume) > 1e-6) throw new Exception("Playback endpoint level changed during check.");
    }

    public static void ReanalyzeEffects(string directory)
    {
        var settings = JsonSerializer.Deserialize<LogSweepSettings>(File.ReadAllText(Path.Combine(directory, "settings.json")), Options)!;
        double noise = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "capture-context.json")))!["noise"]!.GetValue<double>();
        var result = StandardAcousticMeasurement.AnalyzeLogSweep(Read(Path.Combine(directory, "sweep-excitation.wav")),
            StandardAcousticMeasurement.ApplySubsonicConditioning(Read(Path.Combine(directory, "sweep-recorded.wav")), settings.SampleRate), settings, null, noise);
        var saved = result with { AlignedTransferFunction = Array.Empty<Complex>(), HarmonicTransferFunctions = Array.Empty<Complex[]>() };
        File.WriteAllText(Path.Combine(directory, "effects-result.json"), JsonSerializer.Serialize(saved, Options));
        Console.WriteLine(JsonSerializer.Serialize(new { result.Validity, result.SignalLevelDbFs, result.NoiseFloorDbFs, result.EstimatedSnrDb,
            result.DirectArrivalMs, result.EstimatedClockDriftPpm, result.ImpulseEffects }, Options));
    }
    public static async Task Run(string output, string configPath)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        using var enumerator = new MMDeviceEnumerator();
        bool usb = config["UseUsbPlayback"]!.GetValue<bool>();
        using var playback = enumerator.GetDevice(config[usb ? "UsbPlaybackDeviceId" : "BluetoothPlaybackDeviceId"]!.GetValue<string>());
        using var recording = enumerator.GetDevice(config["RecordingDeviceId"]!.GetValue<string>());
        if (playback.State != DeviceState.Active || recording.State != DeviceState.Active) throw new Exception("Selected endpoint is not active; no stimulus emitted.");
        int rate = config["PlaybackSampleRate"]!.GetValue<int>();
        using var engine = new AudioEngine { PlaybackSampleRate = rate, UseExclusivePlayback = false,
            PlaybackChannel = config["LastPlaybackChannel"]?.GetValue<int?>(), RecordingChannel = config["LastRecordingChannel"]?.GetValue<int?>() };
        AudioEngine.flagSaveFile = false;
        double volume = playback.AudioEndpointVolume.MasterVolumeLevelScalar;
        double level = config["PlaybackLevelDbfs"]!.GetValue<double>();
        double amplitude = Math.Pow(10, level / 20);
        Console.WriteLine(JsonSerializer.Serialize(new { playback = playback.FriendlyName, recording = recording.FriendlyName,
            rate, engine.PlaybackChannel, engine.RecordingChannel, level, windowsVolume = volume, captureMix = recording.AudioClient.MixFormat.ToString() }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var quiet = await engine.CaptureSilenceAsync(recording, null, 1.2, cancellation.Token);
            Save(Path.Combine(output, "quiet.wav"), quiet.DutSamples, quiet.DutSampleRate);
            double noise = 10 * Math.Log10(quiet.DutSamples.Select(v => (double)v * v).Average() + 1e-18);
            Console.WriteLine($"Background: {noise:F2} dBFS.");
            var tone = new float[rate * 2];
            for (int i = 0; i < tone.Length; i++)
            {
                double fade = Math.Min(1, Math.Min(i, tone.Length - 1 - i) / (rate * .01));
                tone[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * i / rate) * fade);
            }
            float[] toneCapture = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0, 2,
                cancellationToken: cancellation.Token, customSweepSamples: tone);
            Save(Path.Combine(output, "tone-1k.wav"), toneCapture, engine.RecordingSampleRate);
            int toneRate = engine.RecordingSampleRate;
            int start = Math.Max(0, toneCapture.Length / 2 - toneRate / 2);
            var quality = AdvancedAudioMeasurement.AnalyzeTone(toneCapture.Skip(start).Take(toneRate).ToArray(), toneRate, 1000, 9);
            File.WriteAllText(Path.Combine(output, "tone-quality.json"), JsonSerializer.Serialize(quality, Options));
            Console.WriteLine($"Tone 1k: THD={quality.ThdPercent:F6}%, THD+N={quality.ThdNPercent:F6}%, signal={quality.SignalLevelDbFs:F2} dBFS, peak={quality.PeakSample:F6}, SNR={quality.SnrDb:F2} dB, valid={quality.IsValid}.");
            if (!quality.IsValid || quality.PeakSample >= .995 || quality.SignalLevelDbFs < -75 || quality.SnrDb < 10)
                throw new Exception("Tone not valid above background; check source/routing before sweep. No distortion verdict.");
            var settings = new LogSweepSettings(rate, 18, Math.Min(22000, rate * .47), config["LogSweepDurationSeconds"]!.GetValue<double>(), amplitude,
                PointsPerOctave: 48, DistortionEvaluationFrequenciesHz: new[] { 80.0, 1000.0, 4000.0 },
                ApplyDistortionCalibration: config["ApplyDistortionCalibration"]?.GetValue<bool>() ?? false,
                UseHarmonicFrequencyAsDistortionReference: config["UseHarmonicFrequencyAsDistortionReference"]?.GetValue<bool>() ?? false);
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            float[] capture = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0, sweep.Length / (double)rate + 3,
                cancellationToken: cancellation.Token, customSweepSamples: sweep);
            Save(Path.Combine(output, "sweep-recorded.wav"), capture, engine.RecordingSampleRate);
            if (rate != engine.RecordingSampleRate) { settings = settings with { SampleRate = engine.RecordingSampleRate }; sweep = StandardAcousticMeasurement.GenerateLogSweep(settings); }
            Save(Path.Combine(output, "sweep-excitation.wav"), sweep, settings.SampleRate);
            File.WriteAllText(Path.Combine(output, "settings.json"), JsonSerializer.Serialize(settings, Options));
            File.WriteAllText(Path.Combine(output, "capture-context.json"), JsonSerializer.Serialize(new { noise, level, volume, playback = playback.ID, recording = recording.ID }, Options));
            Reanalyze(output);
        }
        finally { engine.StopAllAudio(); }
        if (Math.Abs(playback.AudioEndpointVolume.MasterVolumeLevelScalar - volume) > 1e-6) throw new Exception("Playback endpoint level changed during check.");
    }
    public static void Reanalyze(string directory)
    {
        var settings = JsonSerializer.Deserialize<LogSweepSettings>(File.ReadAllText(Path.Combine(directory, "settings.json")), Options)!;
        float[] sweep = Read(Path.Combine(directory, "sweep-excitation.wav"));
        float[] capture = Read(Path.Combine(directory, "sweep-recorded.wav"));
        double noise = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "capture-context.json")))!["noise"]!.GetValue<double>();
        var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, StandardAcousticMeasurement.ApplySubsonicConditioning(capture, settings.SampleRate), settings, null, noise);
        File.WriteAllText(Path.Combine(directory, "sweep-points.json"), JsonSerializer.Serialize(result.SweepHarmonics, Options));
        var point = result.SweepHarmonics.FirstOrDefault(p => p.FundamentalFrequencyHz == 1000);
        string summary = JsonSerializer.Serialize(new { point, result.Validity, result.SignalLevelDbFs, result.NoiseFloorDbFs, result.EstimatedSnrDb, result.DirectArrivalMs, result.EstimatedClockDriftPpm }, Options);
        File.WriteAllText(Path.Combine(directory, "sweep-summary.json"), summary);
        Console.WriteLine(summary);
        var type = typeof(StandardAcousticMeasurement).Assembly.GetType("SoncaAudioInspector.SweepFourier")!;
        object fft = Activator.CreateInstance(type, new object[] { true })!;
        for (int order = 1; order <= 9; order++)
        {
            Complex[] spectrum = result.HarmonicTransferFunctions[order];
            if (spectrum.Length == 0) continue;
            var ir = spectrum.ToArray(); type.GetMethod("Inverse")!.Invoke(fft, new object[] { ir });
            double log = Math.Log(settings.EndFrequencyHz / settings.StartFrequencyHz);
            double offset(int n) => settings.DurationSeconds * Math.Log(n) / log * settings.SampleRate;
            int left = Math.Clamp((int)Math.Round(.05 * settings.SampleRate), 8, Math.Max(8, (int)Math.Floor((offset(order + 1) - offset(order)) * .45)));
            int right = Math.Clamp((int)Math.Round(settings.ImpulseWindowRightMs / 1000 * settings.SampleRate), 8,
                Math.Max(8, order == 1 ? (int)Math.Round(settings.ImpulseWindowRightMs / 1000 * settings.SampleRate) : (int)Math.Floor((offset(order) - offset(order - 1)) * .45)));
            double[] samples = ir.Take(left + right + 1).Select(v => v.Real).ToArray();
            File.WriteAllText(Path.Combine(directory, $"harmonic-ir-{order}.json"), JsonSerializer.Serialize(new { left, samples }, Options));
        }
    }
    private static void Save(string path, float[] values, int rate) { using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)); writer.WriteSamples(values, 0, values.Length); }
    private static float[] Read(string path) { using var reader = new WaveFileReader(path); var list = new List<float>(); float[]? frame; while ((frame = reader.ReadNextSampleFrame()) != null) list.Add(frame[0]); return list.ToArray(); }
}
