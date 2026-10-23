using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoncaAudioInspector;

public enum C360ElectronicStage { MicEffects, LineIn, UsbA, MicFrequencyShift, AcousticMic }

public sealed record C360ElectronicSetup(string AnalogPlaybackId, string UsbPlaybackId, string LineOutCaptureId,
    int SampleRate = 44100, double SweepSeconds = 5.944308390022676, double LevelDbFs = -24,
    int? AnalogPlaybackChannel = null, int? CaptureChannel = null, double ThdLimitPercent = .5,
    string? LinePlaybackId = null, double? LineLevelDbFs = null, double? UsbLevelDbFs = null, ulong? BleControllerAddress = null)
{
    [JsonIgnore] public TestConfig? TestConfiguration { get; init; }
    public string? UsbPlaybackName { get; init; }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AnalogPlaybackId) || string.IsNullOrWhiteSpace(LineOutCaptureId))
            throw new ArgumentException("Chọn ngõ phát soundcard và ngõ thu LINE OUT (C360 USB audio).");
        if (SampleRate is not (44100 or 48000) || !double.IsFinite(SweepSeconds) || SweepSeconds < 3 || SweepSeconds > 20
            || !double.IsFinite(LevelDbFs) || LevelDbFs < -60 || LevelDbFs > -6
            || AnalogPlaybackChannel is < 0 or > 1 || CaptureChannel is < 0 or > 1
            || !double.IsFinite(ThdLimitPercent) || ThdLimitPercent <= 0 || ThdLimitPercent > 100
            || LinePlaybackId != null && string.IsNullOrWhiteSpace(LinePlaybackId)
            || new[] { LineLevelDbFs, UsbLevelDbFs }.Any(v => v.HasValue && (!double.IsFinite(v.Value) || v.Value < -60 || v.Value > -6))
            || BleControllerAddress.HasValue && (BleControllerAddress == 0 || BleControllerAddress > 0xFFFFFFFFFFFFUL))
            throw new ArgumentException("Kiểm tra rate, length, kênh và mức phát (-60 đến -6 dBFS) của bài C360.");
    }
    public string PlaybackId(C360ElectronicStage stage) => stage switch
    { C360ElectronicStage.UsbA => UsbPlaybackId, C360ElectronicStage.LineIn => LinePlaybackId ?? AnalogPlaybackId, _ => AnalogPlaybackId };
    public double PlaybackLevel(C360ElectronicStage stage) => stage switch
    { C360ElectronicStage.LineIn => LineLevelDbFs ?? LevelDbFs, C360ElectronicStage.UsbA => UsbLevelDbFs ?? LevelDbFs, _ => LevelDbFs };
}

public sealed record C360ElectronicResult(C360ElectronicStage Stage, string Status, string Summary, StandardAcousticResult? Measurement = null)
{
    public FrequencyShiftResult? FrequencyShift { get; init; }
    public C360Reference? Reference { get; init; }
    public static string Name(C360ElectronicStage stage) => stage switch
    { C360ElectronicStage.MicEffects => "1. MIC IN · Echo / Reverb", C360ElectronicStage.MicFrequencyShift => "2. MIC IN · Di tần (FBX)", C360ElectronicStage.LineIn => "3. LINE IN", C360ElectronicStage.UsbA => "4. USB A", _ => "5. Mic thực tế · Âm học" };
    public string StageName => Name(Stage);
}

public interface IC360ElectronicBench
{
    string DeviceIdentity { get; }
    Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token);
    Task WriteAsync(ushort register, ushort value, CancellationToken token);
    Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token);
    void StopAudio();
}

public interface IC360AcquisitionPreparation
{
    Task<C360ElectronicSetup> PrepareAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token);
}

public sealed class C360ElectronicSuite
{
    private int _running;
    public async Task<IReadOnlyList<C360ElectronicResult>> RunAsync(IC360ElectronicBench bench, C360ElectronicSetup setup,
        IProgress<C360ElectronicResult>? progress = null, CancellationToken token = default, IReadOnlyList<C360ElectronicStage>? stages = null)
    {
        setup.Validate();
        var requestedStages = stages?.ToArray() ?? new[] { C360ElectronicStage.MicEffects, C360ElectronicStage.MicFrequencyShift, C360ElectronicStage.LineIn, C360ElectronicStage.UsbA };
        if (requestedStages.Length == 0 || requestedStages.Any(s => !Enum.IsDefined(s)) || requestedStages.Distinct().Count() != requestedStages.Length) throw new ArgumentException("Danh sách ngõ đo không hợp lệ.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("Bài C360 đang chạy hoặc đang khôi phục loa.");
        var baseline = new Dictionary<ushort, ushort>();
        var written = new List<ushort>();
        var results = new List<C360ElectronicResult>();
        string identity = bench.DeviceIdentity;
        try
        {
            baseline = (await bench.ReadAsync(token)).ToDictionary(p => p.Key, p => p.Value);
            if (!baseline.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort caps)
                || requestedStages.Any(s => s is C360ElectronicStage.LineIn or C360ElectronicStage.AcousticMic) && !SoncaBleMixerProfile.SupportsInputSource(caps, 4) || requestedStages.Contains(C360ElectronicStage.UsbA) && !SoncaBleMixerProfile.SupportsInputSource(caps, 1))
                throw new InvalidOperationException("Loa chưa xác nhận hỗ trợ LINE IN và USB A.");
            foreach (ushort reg in new[] { SoncaBleMixerProfile.MicVolume, SoncaBleMixerProfile.MicMute, SoncaBleMixerProfile.MusicMute,
                SoncaBleMixerProfile.MasterMute, SoncaBleMixerProfile.LiveMute, SoncaBleMixerProfile.EchoEnable, SoncaBleMixerProfile.ReverbEnable, SoncaBleMixerProfile.InputSource })
            {
                if (!baseline.TryGetValue(reg, out ushort value) || !SoncaBleMixerProfile.CanWrite(reg, value))
                    throw new InvalidOperationException($"Chưa đọc được trạng thái C360 hợp lệ (0x{reg:X4}); chưa phát tín hiệu.");
            }
            if (requestedStages.Any(s => s is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift) && baseline[SoncaBleMixerProfile.MicVolume] == 0) throw new InvalidOperationException("Mức MIC trên loa đang bằng 0. Chỉnh MIC trước khi đo.");
            async Task Set(ushort reg, ushort value)
            {
                token.ThrowIfCancellationRequested();
                if (bench.DeviceIdentity != identity) throw new InvalidOperationException("Thiết bị BLE đã đổi; dừng bài C360.");
                // Record before sending: a lost acknowledgement can still mean
                // the device applied the write, so cleanup must restore it.
                if (!written.Contains(reg)) written.Add(reg);
                await bench.WriteAsync(reg, value, token);
            }
            foreach (var stage in requestedStages)
            {
                token.ThrowIfCancellationRequested(); bench.StopAudio();
                progress?.Report(new(stage, "RUNNING", "Đang chuyển và đọc lại trạng thái loa…"));
                await Set(SoncaBleMixerProfile.MasterMute, 0);
                await Set(SoncaBleMixerProfile.LiveMute, 0);
                // FBX is a bit in the option/capability word. The production bench
                // uses SetOptionAsync so source/capability bits are never overwritten.
                await Set(SoncaBleMixerProfile.InputCapabilities, SoncaBleMixerProfile.WithOption(caps, SoncaMixerOption.MicFbx, false));
                bool microphone = stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift;
                var stageTest = (bench as IC360StageSettings)?.ConfigurationFor(stage) ?? setup.TestConfiguration;
                ushort echoEnabled = stage == C360ElectronicStage.MicEffects && stageTest?.ImpulseEffects?.ExpectEcho != false ? (ushort)1 : (ushort)0;
                ushort reverbEnabled = stage == C360ElectronicStage.MicEffects && stageTest?.ImpulseEffects?.ExpectReverb != false ? (ushort)1 : (ushort)0;
                if (microphone)
                {
                    await Set(SoncaBleMixerProfile.MicVolume, baseline[SoncaBleMixerProfile.MicVolume]);
                    await Set(SoncaBleMixerProfile.MusicMute, 1);
                    await Set(SoncaBleMixerProfile.MicMute, 0);
                    await Set(SoncaBleMixerProfile.EchoEnable, echoEnabled);
                    await Set(SoncaBleMixerProfile.ReverbEnable, reverbEnabled);
                }
                else
                {
                    await Set(SoncaBleMixerProfile.MicMute, 1);
                    await Set(SoncaBleMixerProfile.MicVolume, 0);
                    await Set(SoncaBleMixerProfile.EchoEnable, 0);
                    await Set(SoncaBleMixerProfile.ReverbEnable, 0);
                    await Set(SoncaBleMixerProfile.MusicMute, 0);
                    await Set(SoncaBleMixerProfile.InputSource, stage is C360ElectronicStage.LineIn or C360ElectronicStage.AcousticMic ? (ushort)4 : (ushort)1);
                }
                // Verify the whole stage, not only individual SET acknowledgements.
                var state = await bench.ReadAsync(token);
                if (bench.DeviceIdentity != identity || (state[SoncaBleMixerProfile.InputCapabilities] & SoncaBleMixerProfile.OptionMask(SoncaMixerOption.MicFbx)) != 0 || state[SoncaBleMixerProfile.MasterMute] != 0 || state[SoncaBleMixerProfile.LiveMute] != 0
                    || (microphone
                        ? state[SoncaBleMixerProfile.MusicMute] != 1 || state[SoncaBleMixerProfile.MicMute] != 0
                            || state[SoncaBleMixerProfile.EchoEnable] != echoEnabled || state[SoncaBleMixerProfile.ReverbEnable] != reverbEnabled
                        : state[SoncaBleMixerProfile.MicMute] != 1 || state[SoncaBleMixerProfile.MicVolume] != 0
                            || state[SoncaBleMixerProfile.EchoEnable] != 0 || state[SoncaBleMixerProfile.ReverbEnable] != 0
                            || state[SoncaBleMixerProfile.MusicMute] != 0 || state[SoncaBleMixerProfile.InputSource] != (stage is C360ElectronicStage.LineIn or C360ElectronicStage.AcousticMic ? 4 : 1)))
                    throw new InvalidOperationException("Loa chưa giữ đúng trạng thái của bài đo; chưa phát sweep.");
                progress?.Report(new(stage, "RUNNING", stage == C360ElectronicStage.MicFrequencyShift ? "Đang đối chiếu ba tone khi FBX tắt / bật…" : stage == C360ElectronicStage.AcousticMic ? "Đang thu mic đo ngoài và phát sweep qua LINE IN…" : "Đang thu LINE OUT và phát một sweep…"));
                var result = await bench.CaptureAsync(stage, setup, token);
                token.ThrowIfCancellationRequested();
                results.Add(result); progress?.Report(result);
                // A missing or invalid capture on one input must not hide the
                // measurements of the remaining inputs. Route failures throw
                // before playback; capture failures stay attached to that stage.
            }
            return results;
        }
        finally
        {
            try
            {
                bench.StopAudio();
                if (written.Count > 0 && bench.DeviceIdentity != identity)
                    throw new InvalidOperationException("BLE đã ngắt/đổi thiết bị; chưa khôi phục được trạng thái loa ban đầu.");
                var failures = new List<string>();
                foreach (ushort reg in written.AsEnumerable().Reverse())
                {
                    try { await bench.WriteAsync(reg, baseline[reg], CancellationToken.None); }
                    catch (Exception ex) { failures.Add($"0x{reg:X4}: {ex.Message}"); }
                }
                if (written.Count > 0)
                {
                    var restored = await bench.ReadAsync(CancellationToken.None);
                    foreach (ushort reg in written)
                        if (!restored.TryGetValue(reg, out ushort value) || (reg == SoncaBleMixerProfile.InputCapabilities ? (value & 1) != (baseline[reg] & 1) : value != baseline[reg])) failures.Add($"0x{reg:X4} chưa trở về giá trị trước đo");
                }
                if (failures.Count > 0) throw new InvalidOperationException("Không khôi phục đủ trạng thái loa: " + string.Join("; ", failures));
            }
            finally { Volatile.Write(ref _running, 0); }
        }
    }
}

public sealed partial class C360AudioBench(AudioEngine engine, SoncaBleManager ble, string directory) : IC360ElectronicBench, IC360AcquisitionPreparation
{
    public bool VerifyReverbAlways { get; init; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public string DeviceIdentity => ble.IsConnected && ble.SupportsMixerControls ? ble.ConnectedDeviceAddress : "";
    public async Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ble.IsConnected || !ble.SupportsMixerControls || !await ble.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket()))
            throw new InvalidOperationException("Chưa đọc được C360 qua BLE. Kết nối điều khiển loa rồi chạy lại.");
        var state = ble.MixerState.ToDictionary(p => p.Key, p => p.Value);
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "control-readbacks.jsonl"), JsonSerializer.Serialize(new
        { utc = DateTime.UtcNow, device = DeviceIdentity, state = state.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) }) + Environment.NewLine);
        return state;
    }
    public async Task WriteAsync(ushort register, ushort value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ble.IsConnected || !ble.SupportsMixerControls) throw new InvalidOperationException("BLE đã ngắt.");
        if (register == SoncaBleMixerProfile.InputCapabilities)
        {
            if (ble.MixerState.TryGetValue(register, out ushort options) && (options & 1) == (value & 1)) return;
            if (!await ble.SetOptionAsync(SoncaMixerOption.MicFbx, (value & SoncaBleMixerProfile.OptionMask(SoncaMixerOption.MicFbx)) != 0))
                throw new InvalidOperationException("Chưa xác nhận trạng thái Mic FBX.");
            return;
        }
        if (ble.MixerState.TryGetValue(register, out ushort current) && current == value) return;
        var packet = register == SoncaBleMixerProfile.InputSource ? SoncaBleProtocol.BuildInputSourcePacket((byte)value)
            : SoncaBleProtocol.BuildParamPacket(0, register, value);
        if (!await ble.SendPacketAsync(packet, $"C360 đo điện tử: 0x{register:X4} = {value}"))
            throw new InvalidOperationException(register == SoncaBleMixerProfile.InputSource
                ? $"Loa chưa mở được ngõ {SoncaBleMixerProfile.InputSourceName((byte)value)}. Kiểm tra kết nối/phát USB A hoặc LINE IN."
                : $"Loa chưa xác nhận điều khiển 0x{register:X4}.");
    }
    public void StopAudio() => engine.StopAllAudio();
    public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        setup = await PrepareAsync(stage, setup, token);
        using var endpoints = new MMDeviceEnumerator();
        using var output = string.IsNullOrWhiteSpace(setup.PlaybackId(stage)) ? null : endpoints.GetDevice(setup.PlaybackId(stage));
        using var input = endpoints.GetDevice(setup.LineOutCaptureId);
        using var windowsLevels = output == null ? null : new WindowsAudioLevels(output, input, setup.TestConfiguration);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, stage + "-acquisition.json"), JsonSerializer.Serialize(setup, Json));
        File.WriteAllText(Path.Combine(directory, stage + "-calibration.json"), JsonSerializer.Serialize(new
        { setup.TestConfiguration?.MicrophoneCalibration?.FileName, setup.TestConfiguration?.MicrophoneCalibration?.Sha256,
          applied = setup.TestConfiguration?.MicrophoneCalibration != null }, Json));
        File.WriteAllText(Path.Combine(directory, stage + "-windows-levels.json"), JsonSerializer.Serialize(new
        { output = output?.FriendlyName, input = input.FriendlyName,
          playbackPercent = output == null ? (float?)null : output.AudioEndpointVolume.MasterVolumeLevelScalar * 100,
          recordingPercent = input.AudioEndpointVolume.MasterVolumeLevelScalar * 100 }, Json));
        int runs = stage == C360ElectronicStage.MicFrequencyShift ? 1 : Math.Clamp(setup.TestConfiguration?.LogSweepRuns ?? 1, 1, 3);
        if (runs == 1) return await CaptureSingleAsync(stage, setup, token);
        var measured = new List<C360ElectronicResult>();
        for (int run = 1; run <= runs; run++)
        {
            token.ThrowIfCancellationRequested();
            var repeated = new C360AudioBench(engine, ble, Path.Combine(directory, "verification-" + run)) { VerifyReverbAlways = VerifyReverbAlways };
            measured.Add(await repeated.CaptureSingleAsync(stage, setup, token));
            if (measured[^1].Status is "NO_SIGNAL" or "INVALID") break;
        }
        var selected = measured.FirstOrDefault(r => r.Status is "NO_SIGNAL" or "INVALID") ?? measured.FirstOrDefault(r => r.Status == "FAIL") ?? measured.Last();
        return selected with { Summary = $"{measured.Count}/{runs} lượt xác minh: " + string.Join(", ", measured.Select(r => r.Status)) + ". " + selected.Summary };
    }
    private async Task<C360ElectronicResult> CaptureSingleAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        if (stage == C360ElectronicStage.MicFrequencyShift) return await CaptureFrequencyShiftAsync(setup, token);
        if (stage == C360ElectronicStage.UsbA && string.IsNullOrWhiteSpace(setup.UsbPlaybackId))
            return new(stage, "NO_SIGNAL", "Đã chuyển và xác nhận BLE ở nguồn USB A, nhưng chưa chọn được thiết bị phát USB A từ máy tính. USB-C recording vẫn là đường thu LINE OUT; chọn đúng ngõ phát nối với USB A rồi đo lại. Chưa có kết quả THD USB A.");
        using var enumerator = new MMDeviceEnumerator();
        using var playback = enumerator.GetDevice(setup.PlaybackId(stage));
        using var recording = enumerator.GetDevice(setup.LineOutCaptureId);
        if (playback.State != DeviceState.Active || recording.State != DeviceState.Active || playback.DataFlow != DataFlow.Render || recording.DataFlow != DataFlow.Capture)
            throw new InvalidOperationException("Thiết bị phát hoặc ngõ thu LINE OUT đã mất kết nối.");
        if (AudioEngine.IsWindowsMonoAudioEnabled()) throw new InvalidOperationException("Tắt Windows Mono audio trước khi đo.");
        var previous = (engine.PlaybackSampleRate, engine.PlaybackChannel, engine.RecordingChannel, engine.UseExclusivePlayback, engine.RecordingGain);
        try
        {
            engine.PlaybackSampleRate = setup.SampleRate; engine.UseExclusivePlayback = false; engine.RecordingGain = 1;
            engine.PlaybackChannel = stage == C360ElectronicStage.UsbA ? null : setup.AnalogPlaybackChannel;
            engine.RecordingChannel = setup.CaptureChannel;
            var quiet = await engine.CaptureSilenceAsync(recording, null, .6, token);
            if (quiet.DutSamples.Length < quiet.DutSampleRate / 4) throw new IOException("Không đủ dữ liệu nền tại LINE OUT.");
            double noise = 10 * Math.Log10(quiet.DutSamples.Select(v => (double)v * v).Average() + 1e-18);
            var effects = setup.TestConfiguration?.ImpulseEffects ?? new ImpulseEffectSettings { ExpectEcho = stage == C360ElectronicStage.MicEffects ? true : null,
                ExpectReverb = stage == C360ElectronicStage.MicEffects ? true : null };
            var test = setup.TestConfiguration;
            var calibration = test?.MicrophoneCalibration?.Resolve();
            var frequencies = new[] { test?.BassThdFrequency ?? 80, test?.MidThdFrequency ?? 1000, test?.TrebleThdFrequency ?? 4000 };
            var limits = new[] { test?.BassThdLimit ?? setup.ThdLimitPercent, test?.MidThdLimit ?? setup.ThdLimitPercent, test?.TrebleThdLimit ?? setup.ThdLimitPercent };
            var settings = new LogSweepSettings(setup.SampleRate, 18, Math.Min(22000, setup.SampleRate * .47), setup.SweepSeconds,
                Math.Pow(10, setup.PlaybackLevel(stage) / 20), PointsPerOctave: 48, DecayAnalysisSeconds: effects.CaptureTailSeconds,
                DistortionEvaluationFrequenciesHz: frequencies, EffectsSettings: effects,
                ApplyDistortionCalibration: calibration != null);
            float[] excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
            float[] capture = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0, settings.DurationSeconds + settings.DecayAnalysisSeconds,
                cancellationToken: token, customSweepSamples: excitation);
            int rate = engine.RecordingSampleRate;
            Save(Path.Combine(directory, stage + "-recorded.wav"), capture, rate);
            Save(Path.Combine(directory, stage + "-quiet.wav"), quiet.DutSamples, quiet.DutSampleRate);
            if (rate != quiet.DutSampleRate) throw new IOException("Rate thu thay đổi giữa nền và sweep; đo lại.");
            if (settings.SampleRate != rate) { settings = settings with { SampleRate = rate }; excitation = StandardAcousticMeasurement.GenerateLogSweep(settings); }
            Save(Path.Combine(directory, stage + "-excitation.wav"), excitation, rate);
            var result = await Task.Run(() => StandardAcousticMeasurement.AnalyzeLogSweep(excitation,
                StandardAcousticMeasurement.ApplySubsonicConditioning(capture, rate), settings, calibration, noise, null, token), token);
            if (stage == C360ElectronicStage.MicEffects && !MeasurementSignalStatus.MissingStimulus(result)
                && result.Validity != "INVALID" && !result.IsClipped && result.ImpulseEffects is { } fxBefore
                && result.SignalLevelDbFs >= (test?.MinimumInputSignalDbFs ?? -100)
                && (!fxBefore.ReverbDetected || VerifyReverbAlways))
            {
                var verification = await VerifyReverbAsync(setup, playback, recording, token);
                var verified = fxBefore with { ReverbVerification = verification, ReverbDetected = fxBefore.ReverbDetected || verification.Detected,
                    DiffuseTailDurationMs = fxBefore.ReverbDetected || !verification.Detected ? fxBefore.DiffuseTailDurationMs : verification.Bands.Min(b => b.DurationMs) };
                var assessment = ConfiguredAcousticTests.Effect(verified, effects, null);
                result = result with { ImpulseEffects = verified with { Verdict = assessment.Status == "INVALID" ? "INCONCLUSIVE" : assessment.Status,
                    Findings = assessment.Status == "PASS" ? Array.Empty<string>() : new[] { assessment.Reason } } };
            }
            File.WriteAllText(Path.Combine(directory, stage + "-result.json"), JsonSerializer.Serialize(result with
            { AlignedTransferFunction = Array.Empty<System.Numerics.Complex>(), HarmonicTransferFunctions = Array.Empty<System.Numerics.Complex[]>() }, Json));
            if (MeasurementSignalStatus.MissingStimulus(result))
                return new(stage, "NO_SIGNAL", MeasurementSignalStatus.MissingStimulusMessage(playback.FriendlyName, recording.FriendlyName, result), result);
            if (test?.MinimumInputSignalDbFs is double minimum && result.SignalLevelDbFs < minimum)
                return new(stage, "NO_SIGNAL", $"Tín hiệu thu thấp: {result.SignalLevelDbFs:F1} dBFS < ngưỡng cấu hình {minimum:F1} dBFS. Kiểm tra đường phát/thu và mức tín hiệu.", result);
            if (test?.MinimumSnrDb is double minimumSnr && result.EstimatedSnrDb < minimumSnr)
                return new(stage, "INVALID", $"SNR thu {result.EstimatedSnrDb:F1} dB < ngưỡng cấu hình {minimumSnr:F1} dB; chưa đủ chất lượng tín hiệu để kết luận.", result);
            bool digitalSilenceBaseline = quiet.DutSamples.All(v => v == 0);
            if (test?.MinimumSnrDb != null && digitalSilenceBaseline)
                return new(stage, "INVALID", "Bản thu nền toàn số 0; chưa xác định được SNR để đối chiếu ngưỡng cấu hình.", result);
            if (result.Validity == "INVALID" || result.IsClipped)
                return new(stage, "INVALID", string.Join(" ", result.Diagnostics.Where(d => d.Severity == "INVALID").Select(d => d.Message)), result);
            if (stage == C360ElectronicStage.MicEffects)
            {
                var fx = result.ImpulseEffects;
                var assessed = ConfiguredAcousticTests.Effect(fx, effects, null);
                string summary = fx?.Describe(effects) ?? "Echo/reverb chưa đo hợp lệ.";
                if (fx?.ReverbVerification is { IsValid: true, Detected: true } verifiedReverb)
                    summary += $" Đã xác minh reverb bằng mẫu khô/ướt, tắt echo{(verifiedReverb.Attempts > 1 ? $" ({verifiedReverb.Attempts} lượt, mức {verifiedReverb.StimulusLevelDbFs:0.#} dBFS)" : "")}.";
                if (assessed.Status == "FAIL") summary += " " + assessed.Reason;
                return new(stage, assessed.Status, summary, result);
            }
            var reader = new TestRunner(engine);
            var points = frequencies.Select(f => reader.BuildSweepDistortionSummary(result, f)).ToArray();
            string values = string.Join(" · ", points.Select(p => $"{p.ReferenceFrequencyHz:0} Hz: {(double.IsFinite(p.ThdPercent) ? p.ThdPercent.ToString("F3") + "%" : "chưa có hài")} "));
            string status = points.Any(p => !p.IsValid) ? "INVALID" : points.Where((p, index) => p.ThdPercent > limits[index]).Any() ? "FAIL" : "PASS";
            string snrText = digitalSilenceBaseline ? "SNR chưa xác định (nền thu toàn số 0)" : $"SNR {result.EstimatedSnrDb:F1} dB";
            return new(stage, status, $"THD {values}· Mức thu {result.SignalLevelDbFs:F1} dBFS · {snrText}"
                + (MeasurementSignalStatus.LowSignalWarning(result) is string warning ? "\n" + warning : ""), result);
        }
        finally
        {
            StopAudio();
            (engine.PlaybackSampleRate, engine.PlaybackChannel, engine.RecordingChannel, engine.UseExclusivePlayback, engine.RecordingGain) = previous;
        }
    }
    private static void Save(string file, float[] samples, int rate) { using var wav = new WaveFileWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)); wav.WriteSamples(samples, 0, samples.Length); }
}
