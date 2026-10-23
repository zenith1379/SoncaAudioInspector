using System.Collections.Generic;
using System.Linq;

namespace SoncaAudioInspector;

public sealed record SweepDistortionSummary(
    double ReferenceFrequencyHz,
    double FundamentalLevelDb,
    IReadOnlyDictionary<int, double> HarmonicPercent,
    double ThdPercent,
    double NoiseFloorPercent,
    double NoiseFloorDbFs,
    string IncludedHarmonics,
    bool IsValid,
    string Status)
{
    public bool IsSweepWide { get; init; }
    public int FrequencyPointCount { get; init; }
    public double StartFrequencyHz { get; init; } = double.NaN;
    public double EndFrequencyHz { get; init; } = double.NaN;

    // Acceptance limits do not invent an SNR gate or suppress measured harmonics.
    public SweepDistortionSummary WithNoiseAssessment(double acquisitionSnrDb, double limitPercent) => this;

    public static SweepDistortionSummary AggregateSweep(
        IReadOnlyList<SweepHarmonicPoint> sweepHarmonics,
        double signalLevelDbFs,
        double noiseFloorDbFs,
        bool isClipped,
        string validity,
        double minimumInputSignalDbFs)
    {
        SweepHarmonicPoint[] points = sweepHarmonics
            .Where(point => point.FundamentalFrequencyHz > 0.0 && double.IsFinite(point.FundamentalLevelDb)
                && double.IsFinite(noiseFloorDbFs) && point.FundamentalLevelDb >= noiseFloorDbFs + 10.0
                && double.IsFinite(point.ThdPercent) && point.ThdPercent >= 0.0 && point.ThdPercent <= 100.0)
            .OrderBy(point => point.FundamentalFrequencyHz)
            .ToArray();
        var harmonicEnergyByOrder = Enumerable.Range(2, 8).ToDictionary(order => order, _ => 0.0);
        double fundamentalEnergy = 0.0;
        double totalHarmonicEnergy = 0.0;
        int usedPoints = 0;

        foreach (SweepHarmonicPoint point in points)
        {
            double fundamentalAmplitude = Math.Pow(10.0, point.FundamentalLevelDb / 20.0);
            if (!double.IsFinite(fundamentalAmplitude) || fundamentalAmplitude <= 0.0)
                continue;

            bool hasMeasuredHarmonic = false;
            for (int order = 2; order <= 9; order++)
            {
                double dbc = point.GetHarmonicDbc(order);
                if (!double.IsFinite(dbc)) continue;

                double harmonicAmplitude = fundamentalAmplitude * Math.Pow(10.0, dbc / 20.0);
                double harmonicEnergy = harmonicAmplitude * harmonicAmplitude;
                harmonicEnergyByOrder[order] += harmonicEnergy;
                totalHarmonicEnergy += harmonicEnergy;
                hasMeasuredHarmonic = true;
            }

            if (!hasMeasuredHarmonic) continue;
            fundamentalEnergy += fundamentalAmplitude * fundamentalAmplitude;
            usedPoints++;
        }

        var harmonicPercent = new Dictionary<int, double>();
        for (int order = 2; order <= 9; order++)
            harmonicPercent[order] = fundamentalEnergy > 0.0
                ? 100.0 * Math.Sqrt(harmonicEnergyByOrder[order] / fundamentalEnergy)
                : double.NaN;

        double thdPercent = fundamentalEnergy > 0.0
            ? 100.0 * Math.Sqrt(totalHarmonicEnergy / fundamentalEnergy)
            : double.NaN;
        double fundamentalLevelDb = usedPoints > 0
            ? 10.0 * Math.Log10(fundamentalEnergy / usedPoints)
            : double.NaN;
        double noiseFloorPercent = double.IsFinite(noiseFloorDbFs) && double.IsFinite(signalLevelDbFs)
            ? 100.0 * Math.Pow(10.0, (noiseFloorDbFs - signalLevelDbFs) / 20.0)
            : double.NaN;
        bool captureValid = validity != "INVALID"
            && !isClipped
            && signalLevelDbFs >= minimumInputSignalDbFs
            && usedPoints >= 2
            && double.IsFinite(thdPercent) && thdPercent >= 0.0 && thdPercent <= 100.0;
        string status = captureValid
            ? $"THD toàn sweep = √(ΣfΣH2..H9 Ah² / Σf A1²), dùng {usedPoints} điểm tần số có harmonic đo được."
            : isClipped
                ? "Sweep bị clipping; không cộng năng lượng harmonic."
                : signalLevelDbFs < minimumInputSignalDbFs
                    ? $"Sweep quá yếu ({signalLevelDbFs:F1} dBFS < {minimumInputSignalDbFs:F1} dBFS); không cộng năng lượng harmonic."
                    : "Sweep không hợp lệ; không kết luận THD toàn dải.";

        return new SweepDistortionSummary(
            double.NaN,
            fundamentalLevelDb,
            captureValid ? harmonicPercent : harmonicPercent.ToDictionary(item => item.Key, _ => double.NaN),
            captureValid ? thdPercent : double.NaN,
            noiseFloorPercent,
            noiseFloorDbFs,
            "H2 .. H9 · toàn sweep",
            captureValid,
            status)
        {
            IsSweepWide = true,
            FrequencyPointCount = usedPoints,
            StartFrequencyHz = points.FirstOrDefault()?.FundamentalFrequencyHz ?? double.NaN,
            EndFrequencyHz = points.LastOrDefault()?.FundamentalFrequencyHz ?? double.NaN
        };
    }
}
