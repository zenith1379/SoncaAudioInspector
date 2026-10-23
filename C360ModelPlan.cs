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
            var test = tests.FirstOrDefault(t => C360ModelDefinition.Stage(t) == stage);
            return test == null ? "" : Resolve(config.Devices.Input[test.PlaybackOut], false, stage == C360ElectronicStage.UsbA);
        }
        string mic = Output(C360ElectronicStage.MicEffects), line = Output(C360ElectronicStage.LineIn);
        if (mic.Length == 0) mic = Output(C360ElectronicStage.MicFrequencyShift);
        var setup = defaults with
        {
            AnalogPlaybackId = mic.Length > 0 ? mic : line.Length > 0 ? line : "unused",
            LinePlaybackId = line.Length > 0 ? line : null, UsbPlaybackId = Output(C360ElectronicStage.UsbA),
            UsbPlaybackName = tests.FirstOrDefault(t => C360ModelDefinition.Stage(t) == C360ElectronicStage.UsbA) is { } usb
                ? config.Devices.Input[usb.PlaybackOut] : null,
            LineOutCaptureId = Resolve(config.Devices.Output[tests[0].RecordingIn], true),
            CaptureChannel = tests[0].RecordingChannel - 1
        };
        foreach (var test in tests) Configure(setup, test).Validate();
        return new(config, setup, tests.Select(C360ModelDefinition.Stage).ToArray())
        {
            PlaybackIds = tests.ToDictionary(C360ModelDefinition.Stage, t => Resolve(config.Devices.Input[t.PlaybackOut], false, C360ModelDefinition.Stage(t) == C360ElectronicStage.UsbA)),
            CaptureIds = tests.ToDictionary(C360ModelDefinition.Stage, t => Resolve(config.Devices.Output[t.RecordingIn], true))
        };
    }

    public C360ElectronicSetup SetupFor(TestConfig test)
    {
        var setup = Configure(Setup, test);
        string id = PlaybackIds.GetValueOrDefault(C360ModelDefinition.Stage(test), setup.PlaybackId(C360ModelDefinition.Stage(test)));
        return setup with { AnalogPlaybackId = id, LinePlaybackId = id, UsbPlaybackId = id,
            LineOutCaptureId = CaptureIds.GetValueOrDefault(C360ModelDefinition.Stage(test), setup.LineOutCaptureId) };
    }

    public static C360ElectronicSetup Configure(C360ElectronicSetup setup, TestConfig test)
    {
        double level = test.PlaybackLevelDbfs ?? setup.PlaybackLevel(C360ModelDefinition.Stage(test));
        return setup with { SweepSeconds = test.LogSweepDurationSeconds ?? setup.SweepSeconds,
            LevelDbFs = level, LineLevelDbFs = level, UsbLevelDbFs = level,
            AnalogPlaybackChannel = test.PlaybackChannel - 1, CaptureChannel = test.RecordingChannel - 1,
            ThdLimitPercent = test.ThdLimit ?? setup.ThdLimitPercent, TestConfiguration = test };
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
