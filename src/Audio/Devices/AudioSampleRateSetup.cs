using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SoncaAudioInspector;

public static class AudioSampleRateSetup
{
    public const int StandardRate = 44100;
    // Set by the source-owned offline UI harness before constructing any views.
    internal static bool SuppressDeviceWrites = false;

    // The Sound control panel's Device Format is distinct from the processing
    // format returned by IAudioClient.GetMixFormat (often 32-bit float).
    public static WaveFormat ReadDefaultFormat(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var current = enumerator.GetDevice(endpointId);
        if (!current.Properties.TryGetValue<byte[]>(PropertyKeys.PKEY_AudioEngine_DeviceFormat, out var bytes)
            || bytes.Length < 18 || bytes.Length < 18 + BitConverter.ToUInt16(bytes, 16))
            throw new InvalidOperationException("Windows không trả về Default Format hợp lệ.");
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return WaveFormat.MarshalFromPtr(pinned.AddrOfPinnedObject()); }
        finally { pinned.Free(); }
    }

    public static bool EnsureStandard(MMDevice device, out string message)
    {
        if (SuppressDeviceWrites) { message = ""; return false; }
        try { return EnsureStandard(device.FriendlyName, () => ReadDefaultFormat(device.ID).SampleRate,
            rate => WindowsEndpointPolicy.SetSampleRate(device.ID, rate), out message); }
        catch (Exception ex) { message = "Không truy cập được thiết bị để đồng bộ 44.1 kHz: " + ex.Message; return false; }
    }

    internal static bool EnsureStandard(string name, Func<int> readRate, Action<int> setRate, out string message)
    {
        message = "";
        try
        {
            int before = readRate();
            if (before == StandardRate) return true;
            setRate(StandardRate);
            int actual = readRate();
            if (actual != StandardRate) throw new InvalidOperationException($"Windows vẫn báo {actual} Hz.");
            message = $"Đã đồng bộ {name}: {before} → {actual} Hz; giữ nguyên bit depth, kênh, volume/mute.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Chưa đồng bộ được {name} về 44.1 kHz: {ex.Message}";
            return false;
        }
    }
}
