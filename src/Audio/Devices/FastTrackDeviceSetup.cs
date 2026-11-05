using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoncaAudioInspector;

/// <summary>Endpoint identity survives a USB port change; list order and endpoint GUID do not.</summary>
public static class FastTrackDeviceSetup
{
    public const int RecordingSampleRate = 44100;
    public const string Playback12Name = "Analog Connector 1/2";
    private static readonly PropertyKey DriverInf = new(new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), 5);

    public static bool IsFastTrack(MMDevice device)
    {
        // Removed endpoints can retain their label while their adapter property throws.
        try { if (IsFastTrackName(device.FriendlyName)) return true; } catch { }
        try { return IsFastTrackName(device.DeviceFriendlyName); } catch { return false; }
    }

    public static bool IsFastTrackName(string? name) =>
        (name ?? "").Contains("FastTrack Pro", StringComparison.OrdinalIgnoreCase);

    public static string GetPlaybackIdentity(MMDevice device)
    {
        if (device.DataFlow != DataFlow.Render || !IsFastTrack(device)) return "";
        try
        {
            string connector = device.DeviceTopology.GetConnector(0).ConnectedToConnectorId;
            device.Properties.TryGetValue<string>(DriverInf, out var driver);
            return NormalizeConnectorIdentity(connector, driver);
        }
        catch { return ""; }
    }

    public static string NormalizeConnectorIdentity(string? connector, string? driver)
    {
        // Keep USB VID/PID/interface, driver, KS filter and pin; remove only the USB instance.
        // A pin number alone cannot identify a physical output across different drivers/cards.
        var match = Regex.Match(connector ?? "", @"usb#(vid_0763&pid_2012&mi_\d+)#[^#]+#(\{[0-9a-f-]+\}\\[^/]+/[0-9a-f]{8})$", RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(driver)) return "";
        return (match.Groups[1].Value + "|" + driver + "|" + match.Groups[2].Value).ToLowerInvariant();
    }

    public static string LearnPlayback12Identity(IEnumerable<(string Name, string Identity)> endpoints, string savedIdentity)
    {
        string[] identities = endpoints.Where(e => IsFastTrackName(e.Name)
                && e.Name.Contains(Playback12Name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(e.Identity))
            .Select(e => e.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Conflicting historical labels must never cause a blind rename.
        return identities.Length == 1 ? identities[0] : identities.Length == 0 ? savedIdentity : "";
    }

    public static string CanonicalPlayback12Name(string adapterName) => $"{Playback12Name} ({adapterName})";

    public static bool IsUnnamedAnalog(string name) => Regex.IsMatch(name,
        @"^Analog Connector\s*\([^)]*FastTrack Pro\)$", RegexOptions.IgnoreCase);

    public static MMDevice? SelectUnique(IEnumerable<MMDevice> devices, string preferredId, Func<MMDevice, bool> fallback)
    {
        var list = devices.ToList();
        var preferred = list.FirstOrDefault(d => string.Equals(d.ID, preferredId, StringComparison.OrdinalIgnoreCase));
        if (preferred != null) return preferred;
        var matches = list.Where(fallback).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static string NormalizeFriendlyName(string? name) => Regex.Replace(name ?? "",
        @"\(\d+\s*-\s*", "(").Trim();

    public static int ReadMixSampleRate(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        using var client = device.AudioClient;
        return client.MixFormat.SampleRate;
    }

    public static bool TryRenamePlayback12(MMDevice device, string knownIdentity, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(knownIdentity) || GetPlaybackIdentity(device) != knownIdentity
            || !IsUnnamedAnalog(device.FriendlyName)) return false;
        string name = CanonicalPlayback12Name(device.DeviceFriendlyName);
        try
        {
            WindowsEndpointPolicy.Rename(device.ID, Playback12Name);
            using var enumerator = new MMDeviceEnumerator();
            using var fresh = enumerator.GetDevice(device.ID);
            if (!string.Equals(fresh.FriendlyName, name, StringComparison.Ordinal))
                throw new InvalidOperationException("Windows chưa xác nhận tên ngõ mới.");
            message = "Đã đặt tên ngõ phát: " + name;
            return true;
        }
        catch (Exception ex) { message = "Chưa đổi được tên ngõ FastTrack: " + ex.Message; return false; }
    }

    public static bool TrySetRecordingSampleRate(MMDevice device, out string message)
    {
        message = "";
        if (device.DataFlow != DataFlow.Capture || !IsFastTrack(device)) return false;
        try
        {
            if (AudioSampleRateSetup.ReadDefaultFormat(device.ID).SampleRate == RecordingSampleRate) return true;
            WindowsEndpointPolicy.SetSampleRate(device.ID, RecordingSampleRate);
            int actual = AudioSampleRateSetup.ReadDefaultFormat(device.ID).SampleRate;
            if (actual != RecordingSampleRate)
                throw new InvalidOperationException($"Windows vẫn báo {actual.ToString(CultureInfo.InvariantCulture)} Hz.");
            message = "Đã đổi ngõ thu FastTrack sang 44,1 kHz.";
            return true;
        }
        catch (Exception ex)
        {
            message = "Chưa đổi được Record FastTrack sang 44,1 kHz: " + ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Windows PolicyConfig is an undocumented compatibility interface. Keep it isolated,
/// check every HRESULT, read settings back, and never replace formats through registry writes.
/// No default endpoint, gain, mute, bit depth or channel selection is changed here.
/// </summary>
internal static class WindowsEndpointPolicy
{
    private static IPolicyConfig Create() => (IPolicyConfig)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;

    public static void Rename(string endpointId, string label)
    {
        var policy = Create();
        // FriendlyName is derived/read-only; the editable endpoint label is DeviceDesc.
        IntPtr text = Marshal.StringToCoTaskMemUni(label);
        var value = new PropVariant { vt = 31, pointerValue = text }; // VT_LPWSTR
        var key = PropertyKeys.PKEY_Device_DeviceDesc;
        try { Marshal.ThrowExceptionForHR(policy.SetPropertyValue(endpointId, false, ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(text); Marshal.ReleaseComObject(policy); }
    }

    public static void SetSampleRate(string endpointId, int sampleRate)
    {
        var policy = Create();
        IntPtr endpoint = IntPtr.Zero, mix = IntPtr.Zero;
        byte[]? originalEndpoint = null, originalMix = null;
        try
        {
            Marshal.ThrowExceptionForHR(policy.GetDeviceFormat(endpointId, false, out endpoint));
            Marshal.ThrowExceptionForHR(policy.GetMixFormat(endpointId, out mix));
            originalEndpoint = CopyFormat(endpoint);
            originalMix = CopyFormat(mix);
            ChangeRate(endpoint, sampleRate);
            ChangeRate(mix, sampleRate);
            Marshal.ThrowExceptionForHR(policy.SetDeviceFormat(endpointId, endpoint, mix));
            if (AudioSampleRateSetup.ReadDefaultFormat(endpointId).SampleRate != sampleRate)
                throw new InvalidOperationException("Không đọc lại được định dạng thu yêu cầu.");
        }
        catch
        {
            // Some drivers partially apply a failed change. Restore the exact prior format.
            if (originalEndpoint != null && originalMix != null)
            {
                Marshal.Copy(originalEndpoint, 0, endpoint, originalEndpoint.Length);
                Marshal.Copy(originalMix, 0, mix, originalMix.Length);
                int restore = policy.SetDeviceFormat(endpointId, endpoint, mix);
                if (restore < 0) System.Diagnostics.Trace.WriteLine($"Khôi phục định dạng FastTrack: 0x{restore:X8}");
            }
            throw;
        }
        finally
        {
            Marshal.FreeCoTaskMem(endpoint);
            Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(policy);
        }
    }

    private static byte[] CopyFormat(IntPtr format)
    {
        if (format == IntPtr.Zero) throw new InvalidOperationException("Windows không trả về định dạng âm thanh.");
        int length = checked(18 + (ushort)Marshal.ReadInt16(format, 16));
        var bytes = new byte[length];
        Marshal.Copy(format, bytes, 0, length);
        return bytes;
    }

    internal static void ChangeRate(IntPtr format, int sampleRate)
    {
        if (sampleRate is not (44100 or 48000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int blockAlign = (ushort)Marshal.ReadInt16(format, 12);
        if (blockAlign == 0) throw new InvalidOperationException("Định dạng âm thanh có block align không hợp lệ.");
        Marshal.WriteInt32(format, 4, sampleRate);
        Marshal.WriteInt32(format, 8, checked(sampleRate * blockAlign));
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaults, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaults, IntPtr period, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fx, ref PropertyKey key, ref PropVariant value);
    }
}
