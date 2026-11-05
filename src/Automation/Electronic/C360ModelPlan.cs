using System.Text.Json;

namespace SoncaAudioInspector;

public sealed record C360ModelEndpoint(string Id, string Name, bool Capture);
public interface IC360StageSettings { TestConfig ConfigurationFor(C360ElectronicStage stage); }

public sealed record C360ModelPlan(InOutConfig Configuration, C360ElectronicSetup Setup, C360ElectronicStage[] Stages)
{
    public IReadOnlyDictionary<C360ElectronicStage, string> PlaybackIds { get; init; } = new Dictionary<C360ElectronicStage, string>();
    public IReadOnlyDictionary<C360ElectronicStage, string> CaptureIds { get; init; } = new Dictionary<C360ElectronicStage, string>();
    public static C360ModelPlan Create(InOutConfig source, IReadOnlyList<C360ModelEndpoint> endpoints, C360ElectronicSetup defaults)
    {
        var config = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(source))!;
        C360ModelDefinition.Validate(config);
        foreach (var test in config.Tests) test.ModelVolume = config.TestVolume;
        var tests = config.Tests.Where(t => t.IsEnabled).ToArray();
        string Resolve(string name, bool capture, bool optional = false)
        {
            var matches = endpoints.Where(e => e.Capture == capture && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) matches = endpoints.Where(e => e.Capture == capture && e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) return matches[0].Id;
            if (matches.Length == 0 && optional) return "";
            throw new InvalidOperationException(matches.Length == 0 ? $"Chưa thấy ngõ {(capture ? "thu" : "phát")}: {name}. Sửa thiết bị trong cấu hình model."
                : $"Có nhiều thiết bị cùng tên {name}; chọn tên ngõ cụ thể trong cấu hình model.");
        }
        string Output(C360ElectronicStage stage)
        {
            if (stage == C360ElectronicStage.Bluetooth) return ""; // Resolve from the live BLE speaker on this PC, after source switching.
            var test = tests.FirstOrDefault(t => C360ModelDefinition.Stage(t) == stage);
            return test == null ? "" : Resolve(config.Devices.Input[test.PlaybackOut], false, stage is C360ElectronicStage.UsbA or C360ElectronicStage.Bluetooth);
        }
        string mic = Output(C360ElectronicStage.MicEffects), line = Output(C360ElectronicStage.LineIn);
        if (mic.Length == 0) mic = Output(C360ElectronicStage.MicFrequencyShift);
        if (mic.Length == 0) mic = Output(C360ElectronicStage.MicDry);
        var defaultCaptureTest = tests.FirstOrDefault(t => C360ModelDefinition.Stage(t) != C360ElectronicStage.AcousticMic) ?? tests[0];
        var setup = defaults with
        {
            AnalogPlaybackId = mic.Length > 0 ? mic : line.Length > 0 ? line : "unused",
            LinePlaybackId = line.Length > 0 ? line : null, UsbPlaybackId = Output(C360ElectronicStage.UsbA),
            BluetoothPlaybackId = Output(C360ElectronicStage.Bluetooth),
            BluetoothAutoPair = config.BluetoothAutoPair,
            BluetoothPlaybackName = tests.FirstOrDefault(t => C360ModelDefinition.Stage(t) == C360ElectronicStage.Bluetooth) is { } bt ? config.Devices.Input[bt.PlaybackOut] : null,
            UsbPlaybackName = tests.FirstOrDefault(t => C360ModelDefinition.Stage(t) == C360ElectronicStage.UsbA) is { } usb
                ? config.Devices.Input[usb.PlaybackOut] : null,
            LineOutCaptureId = Resolve(config.Devices.Output[defaultCaptureTest.RecordingIn], true),
            CaptureChannel = defaultCaptureTest.RecordingChannel - 1
        };
        foreach (var test in tests) Configure(setup, test).Validate();
        return new(config, setup, tests.Select(C360ModelDefinition.Stage).ToArray())
        {
            PlaybackIds = tests.ToDictionary(C360ModelDefinition.Stage, t => C360ModelDefinition.Stage(t) == C360ElectronicStage.Bluetooth ? ""
                : Resolve(config.Devices.Input[t.PlaybackOut], false, C360ModelDefinition.Stage(t) == C360ElectronicStage.UsbA)),
            CaptureIds = tests.ToDictionary(C360ModelDefinition.Stage, t => Resolve(config.Devices.Output[t.RecordingIn], true))
        };
    }

    public bool RequiresStopOnEndpointLoss(C360ElectronicStage? activeStage, string deviceId)
        => CaptureIds.Values.Contains(deviceId, StringComparer.OrdinalIgnoreCase)
            || activeStage.HasValue && PlaybackIds.TryGetValue(activeStage.Value, out string? active)
                && string.Equals(active, deviceId, StringComparison.OrdinalIgnoreCase);

    public C360ElectronicSetup SetupFor(TestConfig test)
    {
        var setup = Configure(Setup, test);
        if (C360ModelDefinition.Stage(test) == C360ElectronicStage.Bluetooth)
            return setup with { BluetoothPlaybackId = "", LineOutCaptureId = CaptureIds.GetValueOrDefault(C360ElectronicStage.Bluetooth, setup.LineOutCaptureId) };
        string id = PlaybackIds.GetValueOrDefault(C360ModelDefinition.Stage(test), setup.PlaybackId(C360ModelDefinition.Stage(test)));
        return setup with { AnalogPlaybackId = id, LinePlaybackId = id, UsbPlaybackId = id, BluetoothPlaybackId = id,
            LineOutCaptureId = CaptureIds.GetValueOrDefault(C360ModelDefinition.Stage(test), setup.LineOutCaptureId) };
    }

    public static C360ElectronicSetup Configure(C360ElectronicSetup setup, TestConfig test)
    {
        double level = test.PlaybackLevelDbfs ?? setup.PlaybackLevel(C360ModelDefinition.Stage(test));
        return setup with { SweepSeconds = test.ResolveSweepSeconds(setup.SampleRate),
            LevelDbFs = level, LineLevelDbFs = level, UsbLevelDbFs = level,
            AnalogPlaybackChannel = test.PlaybackChannel - 1, CaptureChannel = test.RecordingChannel - 1,
            ThdLimitPercent = test.Thd1000Hz, TestConfiguration = test };
    }

    public IC360ElectronicBench Bind(IC360ElectronicBench bench) => new ConfiguredBench(this, bench);
    private sealed class ConfiguredBench(C360ModelPlan plan, IC360ElectronicBench bench) : IC360ElectronicBench, IC360StageSettings
    {
        public TestConfig ConfigurationFor(C360ElectronicStage stage) => plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage);
        public string DeviceIdentity => bench.DeviceIdentity;
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => bench.ReadAsync(token);
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => bench.WriteAsync(register, value, token);
        public void StopAudio() => bench.StopAudio();
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
            => bench.CaptureAsync(stage, plan.SetupFor(ConfigurationFor(stage)), token);
    }
}
