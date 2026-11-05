using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public partial class TestConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SweepLengthSamples { get; set; }

    public double ResolveSweepSeconds(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        return SweepLengthSamples.HasValue ? SweepLengthSamples.Value / (double)sampleRate
            : LogSweepDurationSeconds ?? SweepLengthSettings.DefaultSamples / (double)sampleRate;
    }
}

public static class SweepLengthSettings
{
    public const int DefaultSamples = 65536;
    public static readonly int[] SupportedSamples = [32768, 65536, 131072, 262144, 524288, 1048576];
    public static string Label(int samples) => samples == 1048576 ? "1M" : $"{samples / 1024}K";
    public static void Validate(TestConfig test)
    {
        if (test.SweepLengthSamples is int samples && !SupportedSamples.Contains(samples))
            throw new ArgumentException($"{test.id}: Sweep length cần 32K, 64K, 128K, 256K, 512K hoặc 1M.");
        if (test.SweepLengthSamples == null && test.LogSweepDurationSeconds is double seconds
            && (!double.IsFinite(seconds) || seconds < .5 || seconds > 30))
            throw new ArgumentException($"{test.id}: thời lượng sweep cần từ 0,5 đến 30 giây.");
    }
}
