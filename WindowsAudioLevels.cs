using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class TestConfig
{
    public double? WindowsPlaybackPercent { get; set; }
    public double? WindowsRecordingPercent { get; set; }
    public void ValidateWindowsLevels()
    {
        if (new[] { WindowsPlaybackPercent, WindowsRecordingPercent }.Any(v => v.HasValue && (!double.IsFinite(v.Value) || v < 1 || v > 100)))
            throw new ArgumentException($"{id}: Windows Levels phải từ 1 đến 100%; để trống để giữ mức hiện tại.");
    }
}

/// <summary>Only the selected endpoints; readback and restoration also cover partial apply failures.</summary>
public sealed class WindowsAudioLevels : IDisposable
{
    private readonly List<(MMDevice Device, float Before, bool Mute)> _changed = new();
    public WindowsAudioLevels(MMDevice output, MMDevice input, TestConfig? test)
    {
        test?.ValidateWindowsLevels();
        try
        {
            Apply(output, test?.WindowsPlaybackPercent);
            Apply(input, test?.WindowsRecordingPercent);
        }
        catch { Dispose(); throw; }
    }
    private void Apply(MMDevice device, double? percent)
    {
        if (percent == null) return;
        var volume = device.AudioEndpointVolume;
        float before = volume.MasterVolumeLevelScalar, requested = (float)(percent.Value / 100);
        if (Math.Abs(before - requested) < .002) return;
        _changed.Add((device, before, volume.Mute));
        volume.MasterVolumeLevelScalar = requested;
        if (Math.Abs(volume.MasterVolumeLevelScalar - requested) > .01)
            throw new InvalidOperationException("Windows chưa xác nhận mức âm lượng của " + device.FriendlyName);
    }
    public void Dispose()
    {
        var errors = new List<string>();
        foreach (var item in _changed.AsEnumerable().Reverse())
        {
            try
            {
                item.Device.AudioEndpointVolume.MasterVolumeLevelScalar = item.Before;
                if (Math.Abs(item.Device.AudioEndpointVolume.MasterVolumeLevelScalar - item.Before) > .01)
                    errors.Add(item.Device.FriendlyName);
                // This lease never changes mute. An external mute change is left alone.
            }
            catch (Exception ex) { errors.Add(item.Device.ID + ": " + ex.Message); }
        }
        _changed.Clear();
        if (errors.Count > 0) throw new InvalidOperationException("Chưa khôi phục đủ Windows Levels: " + string.Join("; ", errors));
    }
}
