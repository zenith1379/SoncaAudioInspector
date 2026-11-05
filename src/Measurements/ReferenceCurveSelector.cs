using System;
using System.Collections.Generic;
using System.Linq;

namespace SoncaAudioInspector;

public sealed record ReferenceCurvePair(int FirstRun, int SecondRun, double MaximumDifferenceDb,
    double MaximumDifferenceFrequencyHz, double RmsDifferenceDb, Dictionary<double, double> AverageCurve);

/// <summary>Select whole acquisitions, never a different pair at each frequency.</summary>
public static class ReferenceCurveSelector
{
    public static ReferenceCurvePair SelectClosestPair(IReadOnlyList<Dictionary<double, double>> validRuns,
        Func<IReadOnlyList<Dictionary<double, double>>, bool>? acceptPair = null,
        double evaluationMinHz = 0, double evaluationMaxHz = double.PositiveInfinity, Func<double, bool>? evaluateFrequency = null)
    {
        if (validRuns.Count < 2) throw new ArgumentException("Cần ít nhất hai lượt thu hợp lệ.");
        double[] frequencies = validRuns[0].Keys.OrderBy(f => f).ToArray();
        if (frequencies.Length < 2 || frequencies.Any(f => !double.IsFinite(f) || f <= 0)
            || validRuns.Any(run => run.Count != frequencies.Length
                || frequencies.Any(f => !run.TryGetValue(f, out double db) || !double.IsFinite(db))))
            throw new ArgumentException("Các lượt thu phải đủ cùng lưới tần số và giá trị hữu hạn; không được bỏ điểm lỗi.");
        double[] evaluatedFrequencies = frequencies.Where(f => f >= evaluationMinHz && f <= evaluationMaxHz && (evaluateFrequency?.Invoke(f) ?? true)).ToArray();
        if (evaluatedFrequencies.Length < 2)
            throw new ArgumentException("Không đủ hai điểm trong dải tần đánh giá.");

        var pairs = new List<ReferenceCurvePair>();
        for (int first = 0; first < validRuns.Count - 1; first++)
        for (int second = first + 1; second < validRuns.Count; second++)
        {
            if (acceptPair != null && !acceptPair(new[] { validRuns[first], validRuns[second] })) continue;
            double[] differences = evaluatedFrequencies.Select(f => Math.Abs(validRuns[first][f] - validRuns[second][f])).ToArray();
            double maximum = differences.Max();
            var average = frequencies.ToDictionary(f => f, f => (validRuns[first][f] + validRuns[second][f]) / 2.0);
            pairs.Add(new ReferenceCurvePair(first + 1, second + 1, maximum,
                evaluatedFrequencies[Array.IndexOf(differences, maximum)], Math.Sqrt(differences.Average(d => d * d)), average));
        }
        if (pairs.Count == 0) throw new InvalidOperationException("Không có cặp lượt thu đạt điều kiện ổn định.");
        // Minimise the worst discrepancy first; RMS breaks ties. Do not remove a
        // level offset here: a gain change must count as a real disagreement.
        return pairs.OrderBy(pair => pair.MaximumDifferenceDb).ThenBy(pair => pair.RmsDifferenceDb)
            .ThenBy(pair => pair.FirstRun).ThenBy(pair => pair.SecondRun).First();
    }
}
