using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public sealed record DipWindowConfig
{
    [JsonConverter(typeof(ConfigBooleanConverter))] public bool Enabled { get; set; } = true;
    public double MinHz { get; set; }
    public double MaxHz { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }
    [JsonIgnore] public bool Exclude => Mode == "Exclude";
    public bool Contains(double frequency) => frequency >= MinHz && frequency <= MaxHz;
}

public partial class TestConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigBooleanConverter))]
    public bool? DipWindowsEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DipWindowConfig>? DipWindows { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigBooleanConverter))]
    public bool? BandAverageEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BandAverageToleranceDb { get; set; }
    [JsonIgnore] public bool IsBandAverageEnabled => BandAverageEnabled == true;
    [JsonIgnore] public double EffectiveBandAverageToleranceDb => BandAverageToleranceDb ?? 1.5;

    public void ValidateDipWindows()
    {
        if ((BandAverageToleranceDb.HasValue || IsBandAverageEnabled)
            && (!double.IsFinite(EffectiveBandAverageToleranceDb) || EffectiveBandAverageToleranceDb < .1 || EffectiveBandAverageToleranceDb > 12))
            throw new ArgumentException($"{id}: giới hạn lệch trung bình dải cần từ 0.1 đến 12.0 dB, hữu hạn.");
        if (DipWindows == null) return;
        if (DipWindows.Count > 12) throw new ArgumentException($"{id}: tối đa 12 khoảng xét hõm.");
        foreach (var window in DipWindows)
        {
            if (window == null || !double.IsFinite(window.MinHz) || !double.IsFinite(window.MaxHz)
                || window.MinHz < 20 || window.MaxHz > 20000 || window.MinHz >= window.MaxHz)
                throw new ArgumentException($"{id}: khoảng xét hõm cần 20 ≤ từ Hz < đến Hz ≤ 20000.");
            if (window.Mode is not (null or "LowerOnly" or "Exclude"))
                throw new ArgumentException($"{id}: chế độ khoảng xét hõm chỉ nhận LowerOnly hoặc Exclude.");
        }
        if (DipWindowsEnabled != true) return;
        var enabled = DipWindows.Where(w => w.Enabled).OrderBy(w => w.MinHz).ToArray();
        for (int i = 0; i < enabled.Length; i++)
        {
            if (i > 0 && enabled[i].MinHz <= enabled[i - 1].MaxHz)
                throw new ArgumentException($"{id}: các khoảng xét hõm đang bật không được chồng nhau hoặc chung điểm biên.");
            if (CriticalZones?.Any(z => enabled[i].MinHz <= Math.Max(z.MinHz, z.MaxHz) && enabled[i].MaxHz >= Math.Min(z.MinHz, z.MaxHz)) == true)
                throw new ArgumentException($"{id}: khoảng xét hõm chồng vùng CRITICAL; vùng CRITICAL luôn phải xét.");
        }
    }

    public string DipWindowWarnings() => string.Join(Environment.NewLine, (DipWindows ?? []).SelectMany((w, i) =>
    {
        var notes = new List<string>();
        if (w.MinHz > 0 && w.MaxHz > w.MinHz && Math.Log2(w.MaxHz / w.MinHz) > 1d / 3)
            notes.Add($"Khoảng #{i + 1} rộng {Math.Log2(w.MaxHz / w.MinHz):0.00} octave; khoảng rộng có thể che lỗi thật, nên ≤1/4 octave quanh tâm hõm.");
        if (w.MinHz < EvaluationRange.MinimumHz || w.MaxHz > EvaluationRange.MaximumHz)
            notes.Add($"Khoảng #{i + 1}: phần ngoài freq_range không có tác dụng.");
        return notes;
    }));
}

public sealed record ResponseLimitSettings(FrequencyEvaluationRange Range, double ToleranceDb, bool DipWindowsEnabled,
    IReadOnlyList<DipWindowConfig> DipWindows, IReadOnlyList<CriticalFrequencyZone> CriticalZones,
    bool BandAverageEnabled = false, double BandAverageToleranceDb = 1.5)
{
    public bool HasActiveWindows => DipWindowsEnabled && DipWindows.Any(w => w.Enabled);
    public static ResponseLimitSettings Capture(TestConfig? test, double tolerance) => new(MeasurementCriteria.Range(test), tolerance,
        test?.DipWindowsEnabled == true, test?.DipWindows?.Select(w => w with { }).ToArray() ?? [],
        test?.CriticalZones?.Select(z => new CriticalFrequencyZone(z.MinHz, z.MaxHz)).ToArray() ?? [],
        test?.IsBandAverageEnabled == true, test?.EffectiveBandAverageToleranceDb ?? 1.5);
    public string DescribeWindows() => !HasActiveWindows ? "Khoảng xét hõm: tắt."
        : "Khoảng xét hõm: " + string.Join("; ", DipWindows.Select((w, i) => (w, i)).Where(p => p.w.Enabled)
            .Select(p => $"#{p.i + 1} {p.w.MinHz:0.##}–{p.w.MaxHz:0.##} Hz ({(p.w.Exclude ? "loại khỏi xét" : "bỏ giới hạn dưới")})"));
}

public enum ResponseLimitIssue { None, MissingReference, MissingData }
public enum ResponseLimitInvalidKind { None, NoData, MissingData, MissingReference, NothingToEvaluate }

public sealed record ResponseLimitPoint(double FrequencyHz, double TargetDb, double? LowerDb, double? UpperDb,
    bool Critical, bool Evaluated, bool HasReference, bool IsValid, bool Outside, bool CountInQuota, int? WindowIndex, string Reason,
    double MeasuredDb = double.NaN, ResponseLimitIssue Issue = ResponseLimitIssue.None)
{
    public bool HardFailure => Outside && (Critical || WindowIndex.HasValue);
}

public sealed record ResponseLimitAssessment(IReadOnlyList<ResponseLimitPoint> Points, double AllowedPercent, bool HasActiveWindows,
    bool BandAverageEnabled = false, double BandAverageToleranceDb = 1.5)
{
    public int Outside => Points.Count(p => p.CountInQuota && p.Outside);
    public int Total => Points.Count(p => p.CountInQuota);
    public int Excluded => Points.Count(p => p.WindowIndex.HasValue && p.IsValid);
    public ResponseLimitInvalidKind InvalidKind => Points.Count == 0 ? ResponseLimitInvalidKind.NoData
        : Points.Any(p => p.Issue == ResponseLimitIssue.MissingData) ? ResponseLimitInvalidKind.MissingData
        : Points.Any(p => p.Issue == ResponseLimitIssue.MissingReference || !p.IsValid) ? ResponseLimitInvalidKind.MissingReference
        : !Points.Any(p => p.Evaluated && p.HasReference) ? ResponseLimitInvalidKind.NothingToEvaluate
        : ResponseLimitInvalidKind.None;
    public bool IsInvalid => InvalidKind != ResponseLimitInvalidKind.None;
    public IReadOnlyList<BandAverageResult> BandAverages => new[] { "Bass", "Mid", "Treble" }
        .Select(name => (name, points: Points.Where(p => TestRunner.FrequencyBandName(p.FrequencyHz) == name
            && p.CountInQuota && p.IsValid && double.IsFinite(p.MeasuredDb) && double.IsFinite(p.TargetDb)).ToArray()))
        .Where(b => b.points.Length > 0)
        .Select(b => new BandAverageResult(b.name, b.points.Average(p => p.MeasuredDb - p.TargetDb), b.points.Length)).ToArray();
    public bool BandAveragePassed(string name) => !BandAverageEnabled
        || BandAverages.All(b => b.Name != name || Math.Abs(b.AverageDeltaDb) <= BandAverageToleranceDb);
    public string BandAverageSummary => !BandAverageEnabled ? "" : "Trung bình dải (ngoài hõm): "
        + string.Join(", ", BandAverages.Select(b => $"{b.Name} {b.AverageDeltaDb:+0.00;-0.00;0.00} dB"))
        + $" (giới hạn ±{BandAverageToleranceDb:0.00} dB)";
    public double? MinimumMarginToEffectiveLimitsDb => TryGetMinimumEffectiveMarginDb(out double margin) ? margin : null;
    public bool TryGetMinimumEffectiveMarginDb(out double margin)
    {
        margin = double.NaN;
        if (IsInvalid || !Passed || Outside != 0 || Points.Any(p => p.HardFailure)) return false;
        var evaluated = Points.Where(p => p.Evaluated && p.IsValid).ToArray();
        if (evaluated.Length == 0) return false;
        double minimum = double.PositiveInfinity;
        foreach (var p in evaluated)
        {
            if (!double.IsFinite(p.MeasuredDb) || p.UpperDb is not double upper || !double.IsFinite(upper)) return false;
            double distance = upper - p.MeasuredDb;
            if (p.LowerDb is double lower)
            {
                if (!double.IsFinite(lower)) return false;
                distance = Math.Min(distance, p.MeasuredDb - lower);
            }
            else if (!p.WindowIndex.HasValue || p.Critical || p.CountInQuota) return false;
            minimum = Math.Min(minimum, distance);
        }
        if (!double.IsFinite(minimum) || minimum < 0) return false;
        margin = minimum;
        return true;
    }
    public bool Passed => !IsInvalid && (!BandAverageEnabled || BandAverages.All(b => Math.Abs(b.AverageDeltaDb) <= BandAverageToleranceDb)) && !Points.Any(p => p.HardFailure)
        && (Total == 0 || new LineOutlierAssessment(Outside, Total, AllowedPercent).Passed);
    public IReadOnlyList<string> IssueSummaries()
    {
        static string Span(IReadOnlyList<ResponseLimitPoint> pts) => pts.Count == 1
            ? $"{pts[0].FrequencyHz:0.##} Hz"
            : $"{pts.Min(p => p.FrequencyHz):0.##}–{pts.Max(p => p.FrequencyHz):0.##} Hz";
        static double Excess(ResponseLimitPoint p) => !double.IsFinite(p.MeasuredDb) ? 0
            : p.UpperDb is double u && p.MeasuredDb > u ? p.MeasuredDb - u
            : p.LowerDb is double l && p.MeasuredDb < l ? p.MeasuredDb - l : 0;
        var items = new List<string>();
        var missingData = Points.Where(p => p.Issue == ResponseLimitIssue.MissingData).OrderBy(p => p.FrequencyHz).ToArray();
        if (missingData.Length > 0) items.Add($"thiếu dữ liệu hữu hạn: {missingData.Length} điểm ({Span(missingData)})");
        var missingRef = Points.Where(p => !p.IsValid && p.Issue != ResponseLimitIssue.MissingData).OrderBy(p => p.FrequencyHz).ToArray();
        if (missingRef.Length > 0) items.Add($"thiếu LINE CHUẨN/biên: {missingRef.Length} điểm ({Span(missingRef)})");
        var criticals = Points.Where(p => p.Critical && p.Outside).OrderBy(p => p.FrequencyHz).ToArray();
        if (criticals.Length > 0)
        {
            var worst = criticals.MaxBy(p => Math.Abs(Excess(p)))!;
            double diff = Excess(worst);
            items.Add($"CRITICAL vượt biên {criticals.Length} điểm ({Span(criticals)})"
                + (diff != 0 ? $", lớn nhất {diff:+0.##;-0.##;0.##} dB tại {worst.FrequencyHz:0.##} Hz" : ""));
        }
        foreach (var group in Points.Where(p => !p.Critical && p.WindowIndex.HasValue && p.Outside).GroupBy(p => p.WindowIndex!.Value).OrderBy(g => g.Key))
        {
            var worst = group.MaxBy(p => Math.Abs(Excess(p)))!;
            items.Add(worst.Reason + (group.Count() > 1 ? $" ({group.Count()} điểm)" : ""));
        }
        if (InvalidKind is ResponseLimitInvalidKind.NoData or ResponseLimitInvalidKind.NothingToEvaluate)
            items.Add("Không còn điểm có biên hợp lệ để kết luận.");
        if (BandAverageEnabled)
            items.AddRange(BandAverages.Where(b => Math.Abs(b.AverageDeltaDb) > BandAverageToleranceDb)
                .Select(b => $"trung bình dải {b.Name} lệch {b.AverageDeltaDb:+0.00;-0.00;0.00} dB (vượt ±{BandAverageToleranceDb:0.00} dB)"));
        string joined = string.Join("; ", items);
        if (joined.Length <= 300) return items;
        var capped = new List<string>();
        int remaining = 300;
        foreach (string item in items)
        {
            int sep = capped.Count > 0 ? 2 : 0;
            if (remaining <= sep + 1) break;
            if (item.Length + sep <= remaining) { capped.Add(item); remaining -= item.Length + sep; }
            else { capped.Add(item[..(remaining - sep - 1)] + "…"); break; }
        }
        return capped;
    }
    public string Describe()
    {
        string counts = HasActiveWindows
            ? $"Ngoài biên: {Outside}/{Total} điểm (không tính khoảng xét hõm; đã loại {Excluded} điểm), cho phép ≤{AllowedPercent:0.##}%."
            : new LineOutlierAssessment(Outside, Total, AllowedPercent, Points.Any(p => p.Critical && p.Outside)).Describe();
        string reasons = string.Join("; ", IssueSummaries());
        if (reasons.Length > 300) reasons = reasons[..299] + "…";
        return (reasons.Length == 0 ? counts : counts + " " + reasons)
            + (BandAverageEnabled ? " " + BandAverageSummary : "");
    }
}

public sealed record BandAverageResult(string Name, double AverageDeltaDb, int PointCount);

public sealed record ResponseLimitPreviewData(IReadOnlyDictionary<double, double> StandardCurve,
    IReadOnlyDictionary<double, double> MeasuredCurve, IReadOnlyDictionary<double, FrequencyLimitPoint> FrequencyLimits,
    double ToleranceDb, string Source);

/// <summary>One policy for judgement, plots and exports. Acquisition validity is never waived by a dip window.</summary>
public sealed class ResponseLimitPolicy(ResponseLimitSettings settings, IReadOnlyDictionary<double, double>? standard,
    IReadOnlyDictionary<double, FrequencyLimitPoint>? limits)
{
    public ResponseLimitSettings Settings => settings;
    public double ToleranceAt(double frequency) => settings.ToleranceDb;
    private bool BaseLimit(double frequency, out double target, out double lower, out double upper, out bool critical)
    {
        critical = settings.CriticalZones.Any(z => z.Contains(frequency));
        if (ProductionMeasurement.TryGetFrequencyLimit(limits, frequency, out var limit))
        {
            target = limit.TargetDb; lower = Math.Min(limit.LowerDb, limit.UpperDb); upper = Math.Max(limit.LowerDb, limit.UpperDb);
            critical |= limit.Critical;
            return double.IsFinite(target) && double.IsFinite(lower) && double.IsFinite(upper);
        }
        bool found = TryInterpolate(standard, frequency, out target);
        lower = target - ToleranceAt(frequency); upper = target + ToleranceAt(frequency);
        return found && double.IsFinite(lower) && double.IsFinite(upper) && settings.ToleranceDb >= 0;
    }

    public bool SkipRepeatabilityAt(double frequency)
    {
        BaseLimit(frequency, out _, out _, out _, out bool critical);
        return settings.Range.Contains(frequency) && !critical && settings.DipWindowsEnabled
            && settings.DipWindows.Any(w => w.Enabled && w.Contains(frequency));
    }

    public ResponseLimitPoint Evaluate(double frequency, double measured)
    {
        if (!settings.Range.Contains(frequency))
            return new(frequency, double.NaN, null, null, false, false, false, true, false, false, null, "Ngoài freq_range — không xét.", measured);
        bool found = BaseLimit(frequency, out double target, out double lower, out double upper, out bool critical);
        if (!double.IsFinite(measured) || !found)
            return new(frequency, target, null, null, critical, false, found, false, false, false, null,
                $"{frequency:0.##} Hz: {(!double.IsFinite(measured) ? "thiếu dữ liệu hữu hạn" : "thiếu LINE CHUẨN/biên hợp lệ")} — không kết luận.",
                measured, !double.IsFinite(measured) ? ResponseLimitIssue.MissingData : ResponseLimitIssue.MissingReference);
        int index = settings.DipWindowsEnabled ? Enumerable.Range(0, settings.DipWindows.Count)
            .FirstOrDefault(i => settings.DipWindows[i].Enabled && settings.DipWindows[i].Contains(frequency), -1) : -1;
        if (index >= 0 && !critical)
        {
            var window = settings.DipWindows[index];
            if (window.Exclude)
                return new(frequency, target, null, null, false, false, true, true, false, false, index + 1, $"Không xét trong khoảng xét hõm #{index + 1}.", measured);
            if (!BaseLimit(window.MinHz, out _, out _, out double left, out _)
                || !BaseLimit(window.MaxHz, out _, out _, out double right, out _))
                return new(frequency, target, null, null, false, false, true, false, false, false, index + 1, $"Thiếu biên để bắc cầu khoảng xét hõm #{index + 1}.", measured, ResponseLimitIssue.MissingReference);
            upper = Math.Max(upper, left + (right - left) * Math.Log(frequency / window.MinHz) / Math.Log(window.MaxHz / window.MinHz));
            bool failed = measured > upper;
            return new(frequency, target, null, upper, false, true, true, true, failed, false, index + 1,
                failed ? $"vượt biên trên trong khoảng xét hõm #{index + 1} ({frequency:0.##} Hz, +{measured - upper:0.##} dB)"
                    : $"Trong khoảng xét hõm #{index + 1}: chỉ xét biên trên; không tính vào X/N.", measured);
        }
        bool outside = measured < lower || measured > upper;
        return new(frequency, target, lower, upper, critical, true, true, true, outside, index < 0, index >= 0 ? index + 1 : null,
            critical && outside ? $"CRITICAL vượt biên tại {frequency:0.##} Hz." : outside ? $"Vượt biên tại {frequency:0.##} Hz." : "Trong biên.", measured);
    }

    public ResponseLimitAssessment Assess(IReadOnlyDictionary<double, double> measured, double allowedPercent, IEnumerable<double>? expected = null)
        => new((expected ?? measured.Keys).Distinct().Where(settings.Range.Contains)
            .Select(f => Evaluate(f, measured.TryGetValue(f, out double value) ? value : double.NaN)).ToArray(), allowedPercent, settings.HasActiveWindows,
                settings.BandAverageEnabled, settings.BandAverageToleranceDb);

    public static bool TryInterpolate(IReadOnlyDictionary<double, double>? curve, double frequency, out double value)
    {
        value = double.NaN;
        if (curve == null || !double.IsFinite(frequency) || frequency <= 0) return false;
        if (curve.TryGetValue(frequency, out value)) return double.IsFinite(value);
        var lower = curve.Where(p => p.Key > 0 && p.Key < frequency).OrderByDescending(p => p.Key).FirstOrDefault();
        var upper = curve.Where(p => p.Key > frequency).OrderBy(p => p.Key).FirstOrDefault();
        if (lower.Key <= 0 || upper.Key <= lower.Key || !double.IsFinite(lower.Value) || !double.IsFinite(upper.Value)) return false;
        value = lower.Value + (upper.Value - lower.Value) * Math.Log(frequency / lower.Key) / Math.Log(upper.Key / lower.Key);
        return double.IsFinite(value);
    }

    public static string CsvRow(ResponseLimitPoint point, double raw, double measured, bool includePolicyColumns = true)
    {
        string lower = point.LowerDb?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        string upper = point.UpperDb?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        string status = !point.IsValid ? "INVALID" : !point.Evaluated ? "NOT_EVALUATED" : point.Outside ? "FAIL" : "PASS";
        if (!includePolicyColumns)
            return FormattableString.Invariant($"{point.FrequencyHz},{raw:F2},{measured:F2},{lower},{upper},{point.Critical},{(point.Evaluated ? status : point.IsValid ? "N/A" : "INVALID")}");
        string reason = "\"" + point.Reason.Replace("\"", "\"\"") + "\"";
        return FormattableString.Invariant($"{point.FrequencyHz},{raw:F2},{measured:F2},{lower},{upper},{point.Critical},{status},{point.WindowIndex},{point.CountInQuota},{reason}");
    }
}
