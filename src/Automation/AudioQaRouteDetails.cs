namespace SoncaAudioInspector;

public static class AudioQaRouteDetails
{
    public static string Header(AutoTestCaseItem row, InOutConfig? model)
    {
        string playback = row.Acquisition?.PlaybackDeviceName ?? "", recording = row.Acquisition?.RecordingDeviceName ?? "";
        if (string.IsNullOrWhiteSpace(playback)) playback = model?.Devices.Input.GetValueOrDefault(row.Config.PlaybackOut) ?? "Chưa xác định";
        if (string.IsNullOrWhiteSpace(recording)) recording = model?.Devices.Output.GetValueOrDefault(row.Config.RecordingIn) ?? "Chưa xác định";
        string rates = row.Acquisition is { } a ? $"\nRate phát app / endpoint / Default: {a.PlaybackSampleRate} / {a.PlaybackEndpointSampleRate?.ToString() ?? "chưa ghi"} / {a.PlaybackDefaultSampleRate?.ToString() ?? "chưa ghi"} Hz\nRate thu thực / Default: {a.RecordingSampleRate} / {a.RecordingDefaultSampleRate?.ToString() ?? "chưa ghi"} Hz"
            + (a.SampleRateContinuationAccepted ? "\nĐã chọn tiếp tục ở sample rate khác chuẩn 44100 Hz." : "") : "";
        return $"{row.Id} — {row.Name}\nPHÁT → {row.Config.PlaybackOut}\nThiết bị phát: {playback}\nTHU ← {row.Config.RecordingIn}\nThiết bị thu: {recording}" + rates;
    }
}
