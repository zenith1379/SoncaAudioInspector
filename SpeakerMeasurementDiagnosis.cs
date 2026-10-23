using System;
using System.Collections.Generic;
using System.Linq;

namespace SoncaAudioInspector;

public sealed record DiagnosisResponsePoint(double FrequencyHz, double MeasuredDbFs, double TargetDbFs, double ToleranceDb);
public sealed record DiagnosisDistortionPoint(double FrequencyHz, double ThdPercent, double LimitPercent);

/// <summary>Local UI evidence only; never part of an upload or a product verdict.</summary>
public sealed record MeasurementDiagnosisEvidence
{
    public bool SignalTooWeak { get; init; }
    public bool Clipped { get; init; }
    public double SignalDbFs { get; init; } = double.NaN;
    public double NoiseDbFs { get; init; } = double.NaN;
    public double SnrDb { get; init; } = double.NaN;
    public bool ResponseValid { get; init; }
    public string AcquisitionIssue { get; init; } = "";
    public bool DistortionValid { get; init; }
    public bool HasComparableReference { get; init; }
    public bool NormalizedAtOneKilohertz { get; init; }
    public double MeasuredOneKilohertzDbFs { get; init; } = double.NaN;
    public double ReferenceOneKilohertzDbFs { get; init; } = double.NaN;
    public bool HasEffectEvidence { get; init; }
    public bool EffectClassificationReliable { get; init; }
    public bool EchoDetected { get; init; }
    public bool ReverbDetected { get; init; }
    public double EchoDelayMs { get; init; } = double.NaN;
    public string EffectObservation { get; init; } = "";
    public bool ReliableImpulse { get; init; }
    public string Polarity { get; init; } = "UNKNOWN";
    public double ToneThdPercent { get; init; } = double.NaN;
    public double ToneThdNPercent { get; init; } = double.NaN;
    public IReadOnlyList<DiagnosisResponsePoint> Response { get; init; } = Array.Empty<DiagnosisResponsePoint>();
    public IReadOnlyList<DiagnosisDistortionPoint> Distortion { get; init; } = Array.Empty<DiagnosisDistortionPoint>();
}

/// <summary>Evidence and next checks, not a model-independent speaker acceptance verdict.</summary>
public static class SpeakerMeasurementDiagnosis
{
    public static IReadOnlyList<string> DescribeMeasurement(MeasurementDiagnosisEvidence evidence)
    {
        var notes = new List<string>();
        // A limit on ruling out effects must not hide an effect actually detected
        // in a valid impulse. Response/reference/THD validity is independent.
        if (evidence.HasEffectEvidence && !evidence.SignalTooWeak && !evidence.Clipped)
        {
            if (!string.IsNullOrWhiteSpace(evidence.EffectObservation))
                notes.Add(evidence.EffectObservation);
            else
            {
                var effects = new List<string>();
                if (evidence.EchoDetected)
                    effects.Add("Có echo trong bản thu" + (double.IsFinite(evidence.EchoDelayMs) ? $" (lặp {evidence.EchoDelayMs:F0} ms)." : "."));
                if (evidence.ReverbDetected) effects.Add("Có reverb trong bản thu.");
                if (!evidence.EchoDetected || !evidence.ReverbDetected)
                {
                    string missing = evidence.EchoDetected ? "reverb" : evidence.ReverbDetected ? "echo" : "echo/reverb";
                    effects.Add(evidence.EffectClassificationReliable
                        ? $"Chưa phát hiện {missing} trong bản thu."
                        : $"{(missing == "reverb" ? "Reverb" : missing == "echo" ? "Echo" : "Echo/reverb")} chưa xác định: nền IR cao hoặc đuôi thu thiếu.");
                }
                notes.Add(string.Join(" ", effects));
            }
        }
        if (evidence.SignalTooWeak)
            notes.Add($"Tín hiệu quá yếu ({Level(evidence.SignalDbFs)}); kiểm tra ngõ phát/thu và mute.");
        if (evidence.Clipped)
            notes.Add("Ngõ thu bị clipping; giảm mức phát hoặc gain rồi đo lại.");
        bool noisy = double.IsFinite(evidence.NoiseDbFs) && evidence.NoiseDbFs > -55.0;
        if (noisy)
            notes.Add($"Nhiễu nền {Level(evidence.NoiseDbFs)}" + (double.IsFinite(evidence.SnrDb) ? $", SNR {evidence.SnrDb:F1} dB." : "."));
        if (!evidence.ResponseValid || evidence.SignalTooWeak || evidence.Clipped || noisy)
        {
            if (!string.IsNullOrWhiteSpace(evidence.AcquisitionIssue)) notes.Add(evidence.AcquisitionIssue);
            if (notes.Count == 0) notes.Add("Chưa có bản thu hợp lệ.");
            return notes;
        }

        var points = evidence.Response.Where(p => p.FrequencyHz >= 50 && p.FrequencyHz <= 18000
            && double.IsFinite(p.MeasuredDbFs) && double.IsFinite(p.TargetDbFs)).ToArray();
        if (evidence.HasComparableReference && points.Length >= 3)
        {
            bool absoluteAvailable = !evidence.NormalizedAtOneKilohertz
                || double.IsFinite(evidence.MeasuredOneKilohertzDbFs) && double.IsFinite(evidence.ReferenceOneKilohertzDbFs);
            double absoluteOffset = evidence.NormalizedAtOneKilohertz
                ? evidence.MeasuredOneKilohertzDbFs - evidence.ReferenceOneKilohertzDbFs : 0;
            double[] offsets = points.Select(p => p.MeasuredDbFs - p.TargetDbFs).OrderBy(v => v).ToArray();
            double offset = Median(offsets);
            double tolerance = Median(points.Select(p => Math.Max(0.1, p.ToleranceDb)).OrderBy(v => v).ToArray());
            bool mostlySameLevelChange = offsets.Count(v => Math.Abs(v - offset) <= 1.5) >= Math.Ceiling(points.Length * 0.8);
            if (absoluteAvailable)
            {
                double levelDelta = offset + absoluteOffset;
                notes.Add(Math.Abs(levelDelta) < .05
                    ? "Mức âm tương đương line chuẩn."
                    : $"Loa {(levelDelta < 0 ? "yếu" : "mạnh")} hơn line chuẩn khoảng {Math.Abs(levelDelta):F1} dB.");
            }
            else
                notes.Add("Thiếu mức dBFS gốc để so âm lượng với line chuẩn.");
            var bands = new[] { (Name: "bass", Low: 50.0, High: 250.0), (Name: "mid", Low: 250.0, High: 4000.0), (Name: "treble", Low: 4000.0, High: 18001.0) };
            var bandsOutsideLimits = bands.Where(b => points.Any(p => p.FrequencyHz >= b.Low && p.FrequencyHz < b.High
                && Math.Abs(p.MeasuredDbFs - p.TargetDbFs) > p.ToleranceDb)).Select(b => b.Name).ToArray();
            if (bandsOutsideLimits.Length > 0 && !(mostlySameLevelChange && Math.Abs(offset) > tolerance))
                notes.Add($"Đáp tuyến vượt dung sai ở {string.Join(" / ", bandsOutsideLimits)}.");
        }
        else
            notes.Add("Chưa có line chuẩn để so mức âm và đáp tuyến.");

        // The sign of one impulse is relative to the whole playback/capture chain.
        // Without a saved impulse polarity from the same route it cannot diagnose wiring.
        if (!evidence.DistortionValid)
            notes.Add("THD chưa đo hợp lệ.");
        else
        {
            var over = evidence.Distortion.Where(p => double.IsFinite(p.ThdPercent) && p.ThdPercent > p.LimitPercent)
                .OrderByDescending(p => p.ThdPercent / Math.Max(0.001, p.LimitPercent)).FirstOrDefault();
            if (over != null)
                notes.Add($"THD {over.ThdPercent:F3}% tại {over.FrequencyHz:0} Hz vượt giới hạn {over.LimitPercent:F3}%.");
            if (double.IsFinite(evidence.ToneThdNPercent) && double.IsFinite(evidence.ToneThdPercent)
                && evidence.ToneThdNPercent > Math.Max(0.1, evidence.ToneThdPercent * 2))
                notes.Add($"THD+N {evidence.ToneThdNPercent:F3}%, THD {evidence.ToneThdPercent:F3}%; phần dư ngoài hài cao.");
        }
        if (!noisy && double.IsFinite(evidence.SnrDb) && evidence.SnrDb < 30)
            notes.Add($"SNR {evidence.SnrDb:F1} dB; nhiễu có thể che méo nhỏ.");
        if (notes.Count == 0)
            notes.Add("Các giá trị đo nằm trong giới hạn đang dùng.");
        return notes;
    }

    private static double Median(double[] sorted) => (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    private static string Level(double value) => double.IsFinite(value) ? $"{value:F1} dBFS" : "chưa xác định mức";

    public static string ToneValidity(ToneQualityMetrics tone, double expectedFrequency)
    {
        if (!tone.IsValid) return "INVALID_TONE_NOT_DETECTED";
        if (tone.PeakSample >= 0.995 || tone.ClippedSamplePercent > 0.01) return "INVALID_CLIPPING";
        if (Math.Abs(tone.FundamentalFrequencyHz - expectedFrequency) > Math.Max(5, expectedFrequency * 0.005))
            return "INVALID_WRONG_FREQUENCY";
        if (!double.IsFinite(tone.ThdPercent)) return "INVALID_HARMONIC_BANDWIDTH";
        return "DIAGNOSTIC";
    }

    public static string DescribeTone(ToneQualityMetrics tone, double expectedFrequency)
    {
        string validity = ToneValidity(tone, expectedFrequency);
        if (validity == "INVALID_CLIPPING")
            return "Ngõ thu quá tải: giảm gain phần cứng/volume rồi đo lại; chưa quy méo cho loa.";
        if (validity == "INVALID_HARMONIC_BANDWIDTH")
            return "Không đủ băng thông để đo hài H2: THD không khả dụng, không phải 0%.";
        if (validity != "DIAGNOSTIC")
            return "Chưa khóa được đúng tone đủ mạnh: kiểm tra ngõ phát/thu, kênh mic và DSP di tần; chưa chẩn đoán loa.";
        var strongest = tone.Harmonics.OrderByDescending(h => h.LevelDbc).FirstOrDefault();
        string evidence = $"{expectedFrequency:0} Hz: THD {tone.ThdPercent:F3}%, THD+N {tone.ThdNPercent:F3}%, SNR {tone.SnrDb:F1} dB; đo đến H{tone.Harmonics.Count + 1}. ";
        if (tone.ThdNPercent > Math.Max(0.1, tone.ThdPercent * 2))
            return evidence + "Phần dư ngoài hài chiếm ưu thế: kiểm tra nhiễu nền, rung/rè, DSP và độ ổn định tone; đo nền và chạy bass rè/rung để phân biệt.";
        if (strongest != null)
            return evidence + $"Hài trội H{strongest.Order} ({strongest.LevelDbc:F1} dBc). Nếu vượt golden cùng mức: nghi phi tuyến ampli/củ loa; giảm mức phát 6 dB và đo lại, kiểm tra ampli bằng loopback. Chưa xác định được bộ phận hỏng từ riêng THD.";
        return evidence + "Cần golden cùng model/mức để đánh giá bất thường.";
    }
}
