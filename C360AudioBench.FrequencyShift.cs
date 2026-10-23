using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed partial class C360AudioBench
{
    private async Task<C360ElectronicResult> CaptureFrequencyShiftAsync(C360ElectronicSetup setup, CancellationToken token)
    {
        var settings = setup.TestConfiguration?.FrequencyShift ?? new FrequencyShiftSettings(); settings.Validate();
        var state = await ReadAsync(token);
        if (!state.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort flags) || !SoncaBleMixerProfile.SupportsOption(flags, SoncaMixerOption.MicFbx))
            return new(C360ElectronicStage.MicFrequencyShift, "INVALID", "Chưa đọc được tùy chọn Mic FBX từ loa.");
        bool initial = (flags & SoncaBleMixerProfile.OptionMask(SoncaMixerOption.MicFbx)) != 0;
        string identity = DeviceIdentity;
        var previous = (engine.PlaybackSampleRate, engine.PlaybackChannel, engine.RecordingChannel, engine.UseExclusivePlayback, engine.RecordingGain);
        async Task SetFbx(bool enabled, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (DeviceIdentity != identity || !await ble.SetOptionAsync(SoncaMixerOption.MicFbx, enabled))
                throw new InvalidOperationException("Chưa xác nhận đổi Mic FBX từ loa.");
            var read = await ReadAsync(cancellation);
            if (((read[SoncaBleMixerProfile.InputCapabilities] & 1) != 0) != enabled) throw new InvalidOperationException("Mic FBX đọc lại chưa khớp.");
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var output = enumerator.GetDevice(setup.AnalogPlaybackId);
            using var input = enumerator.GetDevice(setup.LineOutCaptureId);
            if (output.State != DeviceState.Active || input.State != DeviceState.Active || AudioEngine.IsWindowsMonoAudioEnabled())
                throw new InvalidOperationException("Kiểm tra thiết bị MIC / LINE OUT và tắt Windows Mono audio.");
            engine.PlaybackSampleRate = setup.SampleRate; engine.PlaybackChannel = setup.AnalogPlaybackChannel;
            engine.RecordingChannel = setup.CaptureChannel; engine.RecordingGain = 1; engine.UseExclusivePlayback = false;
            float[] stimulus = FrequencyShiftDiagnostics.Generate(setup.SampleRate, setup.LevelDbFs, settings);
            int rate = 0;
            var attempts = new List<FrequencyShiftCaptureAttempt>();
            Task<FrequencyShiftCaptureAnalysis> Capture(bool enabled)
                => FrequencyShiftDiagnostics.CaptureQualifiedAsync(async (attempt, cancellation) =>
            {
                await SetFbx(enabled, cancellation);
                // WASAPI startup can return less data than the requested wall time.
                // Qualify actual sample count before emitting the stimulus.
                var quiet = await engine.CaptureSilenceAsync(input, null, .8, token);
                if (quiet.DutSamples.Length < quiet.DutSampleRate / 2)
                    quiet = await engine.CaptureSilenceAsync(input, null, 1.0, token);
                if (quiet.DutSamples.Length < quiet.DutSampleRate / 2)
                    throw new IOException("Chưa thu đủ 0,5 giây nền cho bài di tần; chưa phát multitone.");
                var captured = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 0, settings.DurationSeconds + .3,
                    cancellationToken: token, recordingChannel: setup.CaptureChannel, customSweepSamples: stimulus);
                int actualRate = engine.RecordingSampleRate;
                if (quiet.DutSampleRate != actualRate || rate != 0 && rate != actualRate) throw new InvalidOperationException("Sample rate thay đổi giữa FBX tắt/bật.");
                rate = actualRate;
                string prefix = Path.Combine(directory, "MicFrequencyShift-" + (enabled ? "on" : "off"));
                Save(prefix + $"-attempt-{attempt}.wav", captured, rate); Save(prefix + $"-attempt-{attempt}-quiet.wav", quiet.DutSamples, rate);
                var analysis = await Task.Run(() => FrequencyShiftDiagnostics.AnalyzeCapture(captured, quiet.DutSamples, rate, settings.DurationSeconds), cancellation);
                attempts.Add(new(enabled, attempt, analysis));
                File.WriteAllText(prefix + $"-attempt-{attempt}.json", JsonSerializer.Serialize(analysis, Json));
                // Keep conventional names for the selected attempt and all raw attempts for diagnosis.
                Save(prefix + ".wav", captured, rate); Save(prefix + "-quiet.wav", quiet.DutSamples, rate);
                return analysis;
            }, token);
            var off = await Capture(false); var on = await Capture(true);
            var result = FrequencyShiftDiagnostics.Compare(off.Carriers, on.Carriers, settings) with { SampleRate = rate, CaptureAttempts = attempts };
            if (!off.IsValid || !on.IsValid)
                result = result with { IsValid = false, Detected = null, Summary = "Di tần chưa xác định. "
                    + string.Join(" ", off.Problems.Select(p => "FBX TẮT: " + p).Concat(on.Problems.Select(p => "FBX BẬT: " + p)))
                    + " " + FrequencyShiftDiagnostics.LevelChangeSummary(off.Carriers, on.Carriers) };
            int retries = attempts.Count - 2;
            if (retries > 0) result = result with { Summary = result.Summary + $" Đã thu lại {retries} lượt do bản thu chưa ổn định; "
                + (result.IsValid ? "lượt được chọn đã đủ chất lượng." : "vẫn chưa đủ chất lượng để kết luận di tần.") };
            File.WriteAllText(Path.Combine(directory, "MicFrequencyShift-result.json"), JsonSerializer.Serialize(new { sampleRate = rate, settings, result }, Json));
            string status = FrequencyShiftDiagnostics.Status(result, settings);
            return new(C360ElectronicStage.MicFrequencyShift, status, result.Summary) { FrequencyShift = result };
        }
        finally
        {
            StopAudio();
            try { await SetFbx(initial, CancellationToken.None); }
            finally { (engine.PlaybackSampleRate, engine.PlaybackChannel, engine.RecordingChannel, engine.UseExclusivePlayback, engine.RecordingGain) = previous; }
        }
    }
}
