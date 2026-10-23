using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed partial class C360AudioBench
{
    private async Task<ReverbDecayResult> VerifyReverbAsync(C360ElectronicSetup setup, MMDevice playback, MMDevice recording, CancellationToken token)
    {
        var original = await ReadAsync(token);
        ushort echo = original[SoncaBleMixerProfile.EchoEnable], reverb = original[SoncaBleMixerProfile.ReverbEnable];
        const double preRoll = .35;
        ReverbDecayResult? previous = null;
        double tailSeconds = Math.Max(2, setup.TestConfiguration?.ImpulseEffects?.CaptureTailSeconds ?? 3);
        try
        {
            async Task<float[]> Capture(bool enabled, float[] signal, double duration, string folder)
            {
                await WriteAsync(SoncaBleMixerProfile.EchoEnable, 0, token);
                await WriteAsync(SoncaBleMixerProfile.ReverbEnable, enabled ? reverb : (ushort)0, token);
                var state = await ReadAsync(token);
                if (state[SoncaBleMixerProfile.EchoEnable] != 0 || state[SoncaBleMixerProfile.ReverbEnable] != (enabled ? reverb : 0))
                    throw new IOException("Chưa xác nhận được trạng thái echo/reverb của mẫu đối chứng.");
                await Task.Delay(300, token);
                var captured = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0,
                    duration,
                    cancellationToken: token, customSweepSamples: signal);
                Directory.CreateDirectory(folder);
                Save(Path.Combine(folder, enabled ? "reverb-wet.wav" : "reverb-dry.wav"), captured, engine.RecordingSampleRate);
                return captured;
            }
            var result = await ReverbDecayDiagnostics.VerifyAsync(setup.LevelDbFs, async (attempt, level, ct) =>
            {
                string folder = attempt == 1 ? directory : Path.Combine(directory, "reverb-retry");
                var bands = previous is { FailureCode: "LOW_TAIL_QUALITY", Bands.Count: 2 }
                    ? ReverbDecayDiagnostics.PlanBandRetry(previous, level, setup.SampleRate, preRoll, tailSeconds) : null;
                float[] signal = bands == null ? ReverbDecayDiagnostics.Generate(setup.SampleRate, level, preRoll)
                    : ReverbDecayDiagnostics.GenerateBandSequence(setup.SampleRate, bands);
                double duration = signal.Length / (double)setup.SampleRate + tailSeconds;
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(folder);
                Save(Path.Combine(folder, "reverb-excitation.wav"), signal, setup.SampleRate);
                File.WriteAllText(Path.Combine(folder, "reverb-probe.json"), JsonSerializer.Serialize(new
                    { sampleRate = setup.SampleRate, levelDbFs = level, preRollSeconds = preRoll, durationSeconds = duration, bands }, Json));
                var dry = await Capture(false, signal, duration, folder); int dryRate = engine.RecordingSampleRate;
                var wet = await Capture(true, signal, duration, folder);
                var measured = (dryRate == engine.RecordingSampleRate ? ReverbDecayDiagnostics.Analyze(dry, wet, dryRate, preRoll, bands)
                    : new ReverbDecayResult(false, false, "Sample rate đổi giữa hai mẫu reverb; cần đo lại.", Array.Empty<ReverbDecayBand>()) { FailureCode = "RATE_CHANGED" })
                    with { Attempts = attempt, StimulusLevelDbFs = level };
                File.WriteAllText(Path.Combine(folder, "reverb-attempt.json"), JsonSerializer.Serialize(measured, Json));
                previous = measured;
                return measured;
            }, token);
            File.WriteAllText(Path.Combine(directory, "reverb-verification.json"), JsonSerializer.Serialize(result, Json));
            return result;
        }
        finally
        {
            StopAudio();
            await WriteAsync(SoncaBleMixerProfile.ReverbEnable, reverb, CancellationToken.None);
            await WriteAsync(SoncaBleMixerProfile.EchoEnable, echo, CancellationToken.None);
            var state = await ReadAsync(CancellationToken.None);
            if (state[SoncaBleMixerProfile.EchoEnable] != echo || state[SoncaBleMixerProfile.ReverbEnable] != reverb)
                throw new IOException("Chưa khôi phục đủ echo/reverb sau mẫu đối chứng.");
        }
    }
}
