namespace SoncaAudioInspector;

public static class MeasurementSignalStatus
{
    public static bool MissingStimulus(StandardAcousticResult? result, bool silentInput = false)
        => silentInput || result?.Diagnostics.Any(d => d.Code is "NO_AUDIO_CAPTURED" or "SWEEP_NOT_DETECTED" or "INVALID_LOW_SIGNAL") == true;

    public static string? LowSignalWarning(StandardAcousticResult? result)
    {
        if (result == null || MissingStimulus(result) || result.IsClipped || !double.IsFinite(result.PeakSample) || result.PeakSample <= 0) return null;
        double headroom = -20 * Math.Log10(result.PeakSample);
        bool lowHeadroomUse = headroom > 40;
        bool lowSnr = double.IsFinite(result.EstimatedSnrDb) && result.EstimatedSnrDb < 30;
        if (!lowHeadroomUse && !lowSnr) return null;
        return $"TÍN HIỆU ĐO THẤP · RMS {result.SignalLevelDbFs:F1} dBFS · headroom {headroom:F1} dB · SNR {result.EstimatedSnrDb:F1} dB. "
            + (lowHeadroomUse ? "Mức đỉnh thấp hơn -40 dBFS. " : "")
            + (lowSnr ? "Tín hiệu cao hơn nền chưa tới 30 dB. " : "")
            + "Nhiễu có thể làm THD đo được tăng. Kiểm tra đúng ngõ, mức thu/gain và nền nhiễu; giữ đủ headroom để tránh clipping. Số THD vẫn được giữ nguyên.";
    }

    public static string MissingStimulusMessage(string playback, string recording, StandardAcousticResult? result = null)
    {
        string evidence = result == null ? "Chưa nhận được tín hiệu thử."
            : $"Mức thu {result.SignalLevelDbFs:F1} dBFS · SNR {result.EstimatedSnrDb:F1} dB. Không nhận ra sweep trong bản thu.";
        return $"KHÔNG NHẬN ĐƯỢC TÍN HIỆU THỬ THU/PHÁT\n\nNgõ phát: {playback}\nNgõ thu: {recording}\n{evidence}\n\n"
            + "Kiểm tra đúng ngõ trên loa, dây nối, mute và mức âm lượng phát/thu rồi đo lại. Có nhiễu ở ngõ thu không có nghĩa là đã nhận được tín hiệu thử.\n\nChưa tính THD cho lượt này.";
    }
}
