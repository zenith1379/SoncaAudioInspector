using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoncaAudioInspector;

public sealed record ExternalAudioSession(string DeviceName, string Direction, string ProcessName, uint ProcessId,
    bool SignalDetected, bool IsSystemSoundsSession = false, string DeviceId = "")
{
    // The Windows input meter receives microphone samples; it does not emit a test signal.
    // Keep it in diagnostics without treating it as a competing measurement app.
    public bool IsWindowsInputMeter => Direction == "thu"
        && string.Equals(ProcessName, "SystemSettings", StringComparison.OrdinalIgnoreCase);
    public bool RequiresAttention => !IsWindowsInputMeter && (!IsSystemSoundsSession || SignalDetected);
    public string Description => $"• {(IsWindowsInputMeter ? "Windows Settings — kiểm tra mức thu" : ProcessName)} (PID {ProcessId}) — {Direction}: {DeviceName}"
        + (Direction == "phát" && SignalDetected ? " — có dữ liệu phát" : " — luồng đang mở");
}
public sealed record AudioUsageReport(DateTime CheckedAt, IReadOnlyList<ExternalAudioSession> Sessions, IReadOnlyList<string> UnreadableDevices)
{
    public IReadOnlyList<ExternalAudioSession> AttentionSessions => Sessions.Where(s => s.RequiresAttention).ToArray();
    public string Summary => AttentionSessions.Count > 0
        ? $"Âm thanh: {AttentionSessions.Count} phiên cần kiểm tra trên ngõ đo ({CheckedAt:HH:mm:ss})."
        : UnreadableDevices.Count > 0 ? "Âm thanh: chưa kiểm tra được hết thiết bị."
        : $"Âm thanh: chưa thấy phiên của ứng dụng khác cần cảnh báo ({CheckedAt:HH:mm:ss}); {Sessions.Count} phiên được ghi nhận.";

    public string Details => FormatDetails(Sessions);
    public string WarningDetails => FormatDetails(AttentionSessions)
        + "\nKiểm tra hoặc dừng các luồng trên trước khi đo nếu không cần dùng.";

    private string FormatDetails(IReadOnlyList<ExternalAudioSession> sessions) => string.Join("\n", sessions.Select(s => s.Description))
        + (UnreadableDevices.Count > 0 ? "\nChưa đọc được: " + string.Join(", ", UnreadableDevices) : "")
        + "\nLuồng đang mở không xác nhận thiết bị bị khóa độc quyền. App sẽ báo riêng nếu WASAPI từ chối mở ngõ đo.";
}

public static class AudioSessionDiagnostics
{
    // Read session metadata/meters only. Never initialize a playback/capture
    // stream just to probe startup: that can disturb the FastTrack ADC state.
    public static AudioUsageReport Scan(IReadOnlySet<string>? endpointIds = null)
    {
        var sessions = new List<ExternalAudioSession>();
        var unreadable = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                using (device)
                {
                    if (endpointIds != null && !endpointIds.Contains(device.ID)) continue;
                    string name = "Thiết bị âm thanh";
                    try
                    {
                        name = device.FriendlyName;
                        var manager = device.AudioSessionManager;
                        try
                        {
                        var collection = manager.Sessions;
                        for (int i = 0; i < collection.Count; i++)
                        {
                            using var session = collection[i];
                            if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                            uint pid = session.GetProcessID;
                            if (pid == Environment.ProcessId) continue;
                            string processName = pid == 0 ? "Âm thanh hệ thống" : $"Tiến trình {pid}";
                            try { using var process = Process.GetProcessById((int)pid); processName = process.ProcessName; } catch { }
                            bool signal = false;
                            try { signal = session.AudioMeterInformation.MasterPeakValue > 0.000001f; } catch { }
                            bool systemSounds = false;
                            try { systemSounds = session.IsSystemSoundsSession; } catch { }
                            sessions.Add(new ExternalAudioSession(name, device.DataFlow == DataFlow.Render ? "phát" : "thu",
                                processName, pid, signal, systemSounds, device.ID));
                        }
                        }
                        finally { manager.Dispose(); }
                    }
                    catch { unreadable.Add(name); }
                }
            }
        }
        catch { unreadable.Add("Windows Audio"); }
        return new AudioUsageReport(DateTime.Now, sessions, unreadable);
    }

    public static bool IsDeviceInUse(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current.HResult == unchecked((int)0x8889000A)) return true; // AUDCLNT_E_DEVICE_IN_USE
        return false;
    }
}
