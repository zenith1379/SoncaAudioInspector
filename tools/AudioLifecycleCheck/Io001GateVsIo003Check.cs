using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MathNet.Numerics.IntegralTransforms;
using SkiaSharp;
using SoncaAudioInspector;

// Offline only: consumes saved, peak-normalized IRs. Never instantiates an audio,
// BLE, application, server or routing object. No production acceptance threshold.
internal static class Io001GateVsIo003Check
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const double DryRightMs = 100, LeakageLimitDb = -35;
    private sealed record Capture(string Source, ImpulsePlotData Ir, bool AcquisitionValid,
        string Stage, string Identity, string Context);
    private sealed record Pair(string Id, string Kind, Capture Wet, Capture Dry);
    private sealed record Metrics(double Mean, double Median, double Max, double Rms, double BandMax);
    private sealed record Comparison(double RightMs, double[] Levels, string Status, string Reason,
        double LeakageDb, ImpulseEffectResult Effects);
    private sealed record Curves(Pair Pair, double[] Frequencies, double[] Dry, double[] Ungated,
        Comparison[] Gates);
    private static string Number(double n) => double.IsFinite(n) ? n.ToString("G12", Invariant) : "";
    private static string Csv(object? value) => "\"" + Convert.ToString(value, Invariant)!.Replace("\"", "\"\"") + "\"";
    private static void Write(string path, string text) => File.WriteAllText(path,
        text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
    private static string? Option(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?.Split('=', 2)[1];
    private static double Numeric(string[] args, string key, double fallback)
    {
        string? value = Option(args, key);
        if (value == null) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, Invariant, out double n) || !double.IsFinite(n))
            throw new ArgumentException($"{key}: expected a finite invariant number.");
        return n;
    }
    private static string Repository()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "ARTIFACTS.md")) && Directory.Exists(Path.Combine(d.FullName, "src"))) return d.FullName;
        throw new DirectoryNotFoundException("Run inside the SoncaAudioInspector repository.");
    }
    private static void RequireArtifactOutput(string repo, string output)
    {
        string root = Path.Combine(repo, "artifacts") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("--output must be under repository artifacts/.");
        for (var d = new DirectoryInfo(output); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse output is not allowed.");
        // Prevent a typo from overwriting an existing capture or a previous run.
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Output must be a new or empty artifact subdirectory.");
    }
    public static void Run(string[] args)
    {
        string repo = Repository();
        string output = Path.GetFullPath(Option(args, "--output") ?? Path.Combine(AppContext.BaseDirectory, "p6-checks"));
        RequireArtifactOutput(repo, output);
        double left = Numeric(args, "--gate-left-ms", 2), requested = Numeric(args, "--gate-right-ms", 10);
        double min = Numeric(args, "--min-freq-hz", 500), max = Numeric(args, "--max-freq-hz", 18000);
        if (left < 0 || left > 125 || requested <= 0 || requested > 500 || min < 20 || max > 20000 || min >= max)
            throw new ArgumentException("Invalid gate or frequency bounds (left 0..125 ms, right >0..500 ms, 20 <= min < max <= 20000 Hz).");
        Directory.CreateDirectory(output);
        string native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "app", "runtimes", "win-x64", "native", "libSkiaSharp.dll"));
        if (File.Exists(native)) System.Runtime.InteropServices.NativeLibrary.Load(native);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidDataException("P6: " + message); checks.Add(message); }
        var pairs = Synthetic(Check);
        var inventory = new List<string> { "Source,Bytes,Sha256,Stage,Identity,Disposition" };
        var notes = new List<string>();
        pairs.AddRange(Discover(repo, output, inventory, notes));
        double[] gates = new[] { 5d, 10, 20, requested }.Distinct().Order().ToArray();
        var ranges = new[] { (100d, 18000d), (500d, 18000d), (1000d, 18000d), (min, max) }.Distinct().ToArray();
        double[] frequencies = ranges.SelectMany(r => Grid(r.Item1, r.Item2)).Append(250).Append(4000).Distinct().Order().ToArray();
        var summary = new StringBuilder("PairId,SourceKind,WetSource,DrySource,LeftMs,RightMs,DryRightMs,MinFreqHz,MaxFreqHz,Points,DeltaFHz,RecommendedMinHz,MeanLevelOffsetDb,MedianLevelOffsetDb,MaxShapeDeviationDb,RmsShapeDeviationDb,BandCompensatedMaxShapeDeviationDb,BassMeanOffsetDb,MidMeanOffsetDb,TrebleMeanOffsetDb,LeakageDb,EchoDetected,EchoDelayMs,ReverbDetected,Status,Reason\n");
        var curvesCsv = new StringBuilder("PairId,SourceKind,FrequencyHz,DryDb,WetUngatedDb,WetGated5msDb,WetGated10msDb,WetGated20msDb,Delta10msDb,ShapeError10msDb,Status5ms,Status10ms,Status20ms,ShapeCompensationMinHz,ShapeCompensationMaxHz,AdditionalGateRightMs,AdditionalWetGatedDb\n");
        Curves? plot = null;
        string plots = Path.Combine(output, "plots"); Directory.CreateDirectory(plots);
        foreach (Pair pair in pairs)
        {
            var curves = Compare(pair, frequencies, left, pair.Id == "echo-inside" ? gates.Append(40).Distinct().Order().ToArray() : gates);
            if (pair.Kind == "SYNTHETIC" && pair.Id == "clean-late") plot = curves;
            if (pair.Id == "legacy-live-reverb-on-1-vs-live-dry-1") plot = curves;
            if (Usable(pair.Wet) && Usable(pair.Dry)) Plot(curves, Path.Combine(plots, pair.Id + ".png"));
            foreach (Comparison gate in curves.Gates)
            foreach (var range in ranges)
            {
                int[] indices = Grid(range.Item1, range.Item2).Select(hz => Array.BinarySearch(frequencies, hz)).ToArray();
                double[] delta = indices.Select(i => gate.Levels[i] - curves.Dry[i]).ToArray();
                Metrics m = Measure(indices.Select(i => frequencies[i]).ToArray(), delta);
                double BandMean(string band) => indices.Where(i => TestRunner.FrequencyBandName(frequencies[i]) == band)
                    .Select(i => gate.Levels[i] - curves.Dry[i]).DefaultIfEmpty(double.NaN).Average();
                string status = gate.Status;
                if (indices.Any(i => frequencies[i] >= pair.Wet.Ir.SampleRate / 2d || frequencies[i] >= pair.Dry.Ir.SampleRate / 2d) || !double.IsFinite(m.Median)) status = "INVALID_GATE";
                summary.AppendLine(string.Join(",", new object?[] { pair.Id, pair.Kind, pair.Wet.Source, pair.Dry.Source,
                    Number(left), Number(gate.RightMs), Number(DryRightMs), Number(range.Item1), Number(range.Item2), indices.Length,
                    Number(1000 / gate.RightMs), Number(5000 / gate.RightMs), Number(m.Mean), Number(m.Median), Number(m.Max), Number(m.Rms), Number(m.BandMax),
                    Number(BandMean("Bass")), Number(BandMean("Mid")), Number(BandMean("Treble")), Number(gate.LeakageDb), gate.Effects.EchoDetected,
                    Number(gate.Effects.EchoDelayMs), gate.Effects.ReverbDetected, status, gate.Reason }.Select(Csv)));
            }
            Comparison At(double right) => curves.Gates.Single(g => g.RightMs == right);
            double[] mainDelta = Grid(min, max).Select(hz => Array.BinarySearch(frequencies, hz))
                .Select(i => At(10).Levels[i] - curves.Dry[i]).ToArray();
            double offset = Median(mainDelta);
            for (int i = 0; i < frequencies.Length; i++)
                curvesCsv.AppendLine(string.Join(",", new object?[] { pair.Id, pair.Kind, Number(frequencies[i]), Number(curves.Dry[i]), Number(curves.Ungated[i]),
                    Number(At(5).Levels[i]), Number(At(10).Levels[i]), Number(At(20).Levels[i]), Number(At(10).Levels[i] - curves.Dry[i]),
                    Number(At(10).Levels[i] - curves.Dry[i] - offset), At(5).Status, At(10).Status, At(20).Status, Number(min), Number(max),
                    gates.Contains(requested) && requested != 5 && requested != 10 && requested != 20 ? Number(requested) : "",
                    requested != 5 && requested != 10 && requested != 20 ? Number(At(requested).Levels[i]) : "" }.Select(Csv)));
        }
        Write(Path.Combine(output, "io001-gate-vs-io003-summary.csv"), summary.ToString());
        Write(Path.Combine(output, "io001-gate-vs-io003-curves.csv"), curvesCsv.ToString());
        Write(Path.Combine(output, "input-inventory.csv"), string.Join("\n", inventory));
        Plot(plot!, Path.Combine(output, "io001-gate-vs-io003-comparison.png"));
        var artifact = new DirectoryInfo(output);
        while (artifact.Parent != null && !File.Exists(Path.Combine(artifact.FullName, "artifact.json"))) artifact = artifact.Parent;
        if (File.Exists(Path.Combine(artifact.FullName, "artifact.json")))
        {
            string screenshots = Path.Combine(artifact.FullName, "screenshots"); Directory.CreateDirectory(screenshots);
            string destination = Path.Combine(screenshots, "io001-gate-vs-io003-comparison.png");
            if (!File.Exists(destination)) File.Copy(Path.Combine(output, "io001-gate-vs-io003-comparison.png"), destination);
        }
        Write(Path.Combine(output, "qualification-template.vi.md"), Qualification);
        Write(Path.Combine(output, "checks.txt"), string.Join("\n", checks));
        Write(Path.Combine(output, "P6_REPORT.vi.md"), $"# P6 — Đối chiếu offline IO001 gate / IO003\n\n" +
            $"Synthetic: PASS {checks.Count} assertions. Tổng {pairs.Count} cặp, {pairs.Count(p => p.Kind != "SYNTHETIC")} cặp IR lưu trên đĩa.\n\n" +
            "Không mở audio/BLE/server; nguồn đọc FileAccess.Read, FileShare.ReadWrite. Không đổi hành vi app.\n\n" +
            "## Phương pháp và giới hạn\n\n" +
            "Samples được nhân 10^(PeakTransferGainDb/20) trước FFT Matlab không chuẩn hóa forward. Tukey25 của app, time=0 tại DirectIndex đã lưu từ StandardAcousticMeasurement; không suy direct bằng đỉnh mạnh nhất của echo. Dry phải 100 ms. Không smooth/normalize đáp tuyến.\n\n" +
            "Mỗi dải dùng 61 điểm log (các lưới hợp nhất, có 250/4000 Hz); mean có dấu, shape trừ median toàn dải; BandCompensated trừ median từng dải TestRunner.FrequencyBandName. Bass trống khi không có điểm.\n\n" +
            "Gate phải trước TailStartMs=50 ms và echo tap đầu; đây không phải bằng chứng pre-delay sạch. Leakage là năng lượng IR thô trong 2 ms ngay sau biên / năng lượng direct trong [-left, min(right,5)] ms; ngưỡng bảo thủ -35 dB. Dữ liệu lỗi, tail ngắn/cụt, thiếu mép trái/phải, dry có effect hoặc leak vượt ngưỡng => INVALID_GATE. Early contamination có thể vẫn nằm trọn trong gate: phải xem shape, không thể chứng minh sạch chỉ bằng leak.\n\n" +
            "5/10/20 ms: Δf≈200/100/50 Hz; dải khởi điểm khuyến nghị ≥1000/500/250 Hz. Zero padding không tăng độ phân giải vật lý. 100 Hz chỉ là khảo sát. Status DIAGNOSTIC_ONLY/SYNTHETIC_ONLY không phải PASS qualification; PNG chính ưu tiên đối chứng lịch sử reverb-on-1 / dry-1, nếu thiếu thì dùng synthetic clean-late; nguồn và status được ghi trên ảnh. Ảnh từng cặp IR hữu hạn ở plots/, mọi cặp nằm trong CSV.\n\n" +
            $"CLI left={Number(left)} ms; requested right={Number(requested)} ms; range={Number(min)}–{Number(max)} Hz. --config/--baseline-assembly được chấp nhận cho pipeline nhưng không dùng (không nạp/chạy assembly baseline).\n\n" +
            "## Nguồn lưu trên đĩa\n\n" + string.Join("\n", notes.Select(n => "- " + n)) + "\n\n" + Qualification);
        Console.WriteLine($"PASS P6 offline: {checks.Count} assertions, {pairs.Count} pairs; {output}");
    }
    private static double[] Grid(double min, double max) => Enumerable.Range(0, 61).Select(i => min * Math.Pow(max / min, i / 60d)).ToArray();
    private static double Median(double[] values)
    {
        if (values.Length == 0 || values.Any(v => !double.IsFinite(v))) return double.NaN;
        double[] sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
    private static Metrics Measure(double[] f, double[] delta)
    {
        double median = Median(delta);
        if (!double.IsFinite(median)) return new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        double[] shape = delta.Select(d => d - median).ToArray();
        double bandMax = f.Select((frequency, i) => (Band: TestRunner.FrequencyBandName(frequency), Delta: delta[i]))
            .GroupBy(p => p.Band).SelectMany(g => { double m = Median(g.Select(p => p.Delta).ToArray()); return g.Select(p => Math.Abs(p.Delta - m)); }).Max();
        return new(delta.Average(), median, shape.Max(Math.Abs), Math.Sqrt(shape.Average(d => d * d)), bandMax);
    }
    private static bool Usable(Capture c) => c.AcquisitionValid && c.Ir.SampleRate >= 8000 && c.Ir.SampleRate <= 384000
        && c.Ir.DirectIndex >= 0 && c.Ir.DirectIndex < c.Ir.Samples.Length && double.IsFinite(c.Ir.PeakTransferGainDb)
        && double.IsFinite(Math.Pow(10, c.Ir.PeakTransferGainDb / 20)) && Math.Pow(10, c.Ir.PeakTransferGainDb / 20) > 0
        && c.Ir.Samples.All(v => double.IsFinite(v * Math.Pow(10, c.Ir.PeakTransferGainDb / 20))) && c.Ir.Samples.Any(v => v != 0);
    private static double[] Absolute(Capture c) => c.Ir.Samples.Select(v => v * Math.Pow(10, c.Ir.PeakTransferGainDb / 20)).ToArray();
    private static double[] Spectrum(Capture c, double[] f, double left, double? right)
    {
        if (!Usable(c)) return f.Select(_ => double.NaN).ToArray();
        double[] samples = Absolute(c);
        if (right.HasValue) samples = ImpulsePlotAnalysis.ApplyWindow(samples, c.Ir.SampleRate, c.Ir.DirectIndex, left, right.Value, ImpulseWindowKind.Tukey25);
        int size = 1; while (size < samples.Length * 2) size <<= 1;
        var fft = new Complex[size]; for (int i = 0; i < samples.Length; i++) fft[i] = samples[i];
        Fourier.Forward(fft, FourierOptions.Matlab);
        return f.Select(hz =>
        {
            if (hz >= c.Ir.SampleRate / 2d) return double.NaN;
            double bin = hz * size / c.Ir.SampleRate; int i = (int)bin;
            double amplitude = fft[i].Magnitude + (bin - i) * (fft[i + 1].Magnitude - fft[i].Magnitude);
            return amplitude > 1e-15 ? 20 * Math.Log10(amplitude) : double.NaN;
        }).ToArray();
    }
    private static Curves Compare(Pair pair, double[] f, double left, double[] gates)
    {
        bool wetValid = Usable(pair.Wet), dryValid = Usable(pair.Dry);
        var w = pair.Wet.Ir; var d = pair.Dry.Ir;
        var effects = ImpulseEffects.Analyze(w.Samples, w.SampleRate, w.DirectIndex, wetValid);
        var dryEffects = ImpulseEffects.Analyze(d.Samples, d.SampleRate, d.DirectIndex, dryValid);
        var comparisons = new List<Comparison>();
        foreach (double right in gates)
        {
            var reasons = new List<string>();
            if (!wetValid || !dryValid || !effects.IsValid || !dryEffects.IsValid) reasons.Add("invalid acquisition/IR/effects analysis");
            if (effects.TailTruncated || dryEffects.TailTruncated || effects.CapturedTailSeconds < 1.3 || dryEffects.CapturedTailSeconds < 1.3) reasons.Add("short/truncated effect tail");
            if (dryEffects.EchoDetected || dryEffects.ReverbDetected) reasons.Add("reference is not dry");
            double firstEcho = effects.EchoTaps.Select(t => t.DelayMs).Append(effects.EchoDelayMs).Where(double.IsFinite).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (firstEcho <= right) reasons.Add("echo inside gate");
            if (right >= new ImpulseEffectSettings().TailStartMs) reasons.Add("gate reaches TailStartMs (not pre-delay)");
            if (w.DirectIndex < left * w.SampleRate / 1000 || d.DirectIndex < left * d.SampleRate / 1000
                || w.Samples.Length - w.DirectIndex - 1 < (right + 2) * w.SampleRate / 1000
                || d.Samples.Length - d.DirectIndex - 1 < DryRightMs * d.SampleRate / 1000) reasons.Add("missing requested gate support");
            double Energy(double start, double end) => w.Samples.Select((v, i) => (v, t: (i - w.DirectIndex) * 1000d / Math.Max(1, w.SampleRate)))
                .Where(p => p.t >= start && p.t < end).Sum(p => p.v * p.v);
            double direct = Energy(-left, Math.Min(right, 5));
            double leakage = 10 * Math.Log10(Math.Max(1e-30, Energy(right, right + 2) / Math.Max(1e-30, direct)));
            if (!double.IsFinite(leakage) || leakage > LeakageLimitDb) reasons.Add("post-gate 2ms leakage exceeds -35 dB");
            comparisons.Add(new(right, Spectrum(pair.Wet, f, left, right), reasons.Count > 0 ? "INVALID_GATE" : pair.Kind == "SYNTHETIC" ? "SYNTHETIC_ONLY" : "DIAGNOSTIC_ONLY",
                string.Join("; ", reasons), leakage, effects));
        }
        return new(pair, f, Spectrum(pair.Dry, f, left, DryRightMs), Spectrum(pair.Wet, f, left, null), comparisons.ToArray());
    }
    private static List<Pair> Synthetic(Action<bool, string> check)
    {
        const int rate = 48000, direct = 128;
        var dry = new double[rate * 3 + direct + 1]; dry[direct] = .6; dry[direct + 1] = .108; dry[direct + 2] = -.042;
        double[] Wet(double gain, bool early = false)
        {
            double[] samples = dry.ToArray();
            for (int n = 1; n <= 10; n++) samples[direct + n * rate / 5] += .6 * Math.Pow(Math.Pow(10, -12d / 20), n);
            var random = new Random(94);
            for (int i = direct + rate * 35 / 1000; i < samples.Length; i++) samples[i] += .018 * (random.NextDouble() - .5) * Math.Exp(-Math.Log(1000) * (i - direct) / (3d * rate));
            if (early) samples[direct + rate * 6 / 1000] += .18;
            return samples.Select(v => v * Math.Pow(10, gain / 20)).ToArray();
        }
        Capture C(string name, double[] samples, bool valid = true) => new(name, ImpulsePlotAnalysis.Build(samples, rate, direct), valid, "", "synthetic", "mathematical FIR");
        var dryCapture = C("synthetic-dry", dry);
        var pairs = new List<Pair> {
            new("clean-late", "SYNTHETIC", C("clean-late", Wet(0)), dryCapture),
            new("gain-minus-2.50", "SYNTHETIC", C("gain-minus-2.50", Wet(-2.5)), dryCapture),
            new("early-6ms", "SYNTHETIC", C("early-6ms", Wet(-2.5, true)), dryCapture),
            new("invalid-acquisition", "SYNTHETIC", C("invalid", Wet(0), false), dryCapture),
            new("short-tail", "SYNTHETIC", C("short", dry.Take(rate / 5).ToArray()), dryCapture)
        };
        var corruptIr = dryCapture.Ir with { Samples = dryCapture.Ir.Samples.ToArray() }; corruptIr.Samples[direct + 1] = double.NaN;
        pairs.Add(new("corrupt-nan", "SYNTHETIC", dryCapture with { Ir = corruptIr }, dryCapture));
        double[] inside = dry.ToArray(); for (int n = 1; n <= 10; n++) inside[direct + n * rate * 30 / 1000] += .6 * Math.Pow(.4, n);
        pairs.Add(new("echo-inside", "SYNTHETIC", C("echo-30ms", inside), dryCapture));
        double[] boundary = Wet(0); boundary[direct + rate * 21 / 1000] += .12;
        pairs.Add(new("leakage-21ms", "SYNTHETIC", C("leakage-21ms", boundary), dryCapture));
        double[] f = Grid(500, 18000);
        var clean = Compare(pairs[0], f, 2, [5, 10, 20]);
        var cg = clean.Gates.Single(g => g.RightMs == 10);
        var cm = Measure(f, cg.Levels.Zip(clean.Dry, (a, b) => a - b).ToArray());
        check(cg.Effects.IsValid && cg.Effects.EchoDetected && Math.Abs(cg.Effects.EchoDelayMs - 200) < 1 && cg.Effects.ReverbDetected, "Clean full IR detects echo ~200 ms and diffuse reverb after 35 ms onset");
        check(cg.Status == "SYNTHETIC_ONLY" && Math.Abs(cm.Median) <= .05 && cm.Max <= .05 && cm.Rms <= .05, "10ms gate recovers known direct within 0.05 dB level and shape");
        double[] known = f.Select(hz => 20 * Math.Log10((.6 + .108 * Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / rate) - .042 * Complex.FromPolarCoordinates(1, -4 * Math.PI * hz / rate)).Magnitude)).ToArray();
        check(clean.Dry.Zip(known, (a, b) => Math.Abs(a - b)).Max() < .001, "Absolute dry FFT agrees with analytic 3-tap FIR within 0.001 dB");
        var gain = Compare(pairs[1], f, 2, [10]);
        var gm = Measure(f, gain.Gates[0].Levels.Zip(gain.Dry, (a, b) => a - b).ToArray());
        check(Math.Abs(gm.Median + 2.5) <= .001 && Math.Abs(gm.Mean + 2.5) <= .001 && gm.Max < .001, "PeakTransferGainDb restores signed -2.50 dB offset (median and mean)");
        var early = Compare(pairs[2], f, 2, [5, 20]);
        Metrics Em(int i) => Measure(f, early.Gates[i].Levels.Zip(early.Dry, (a, b) => a - b).ToArray());
        check(Em(0).Max < .001 && Em(1).Max > 1 && Em(1).Rms > .5, "6ms contamination increases 20ms shape error; 5ms excludes it");
        foreach (Pair bad in pairs.Skip(3)) check(Compare(bad, f, 2, [bad.Id == "echo-inside" ? 40 : 20]).Gates[0].Status == "INVALID_GATE", bad.Id + " is INVALID_GATE, never qualification PASS");
        check(Compare(pairs[0], f, 2, [50]).Gates[0].Status == "INVALID_GATE", "Gate at TailStartMs is rejected");
        check(Compare(pairs[0], f, 10, [10]).Gates[0].Status == "INVALID_GATE", "Insufficient left support is rejected");
        check(TestRunner.FrequencyBandName(249) == "Bass" && TestRunner.FrequencyBandName(250) == "Mid" && TestRunner.FrequencyBandName(4000) == "Treble", "D12 uses actual TestRunner band boundaries");
        Metrics bands = Measure([100, 200, 500, 1000, 5000, 10000], [-3, -3, -2, -2, -1, -1]);
        check(bands.BandMax == 0 && bands.Max == 1 && bands.Mean == -2, "Signed band offsets and band-compensated shape are independent");
        // Exercise the real sweep deconvolver/direct finder on a software-only delayed capture.
        var settings = new LogSweepSettings(SampleRate: rate, DurationSeconds: .5, DecayAnalysisSeconds: 1.5);
        float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        var recorded = new float[sweep.Length + rate * 2]; for (int i = 0; i < sweep.Length; i++) recorded[i + 480] = sweep[i] * .6f;
        var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, recorded, settings, measuredPreSweepNoiseFloorDbFs: -120);
        check(result.ImpulsePlot is { Samples.Length: > 0 } && Math.Abs(result.DirectArrivalSample - 480) < 5, "StandardAcousticMeasurement software sweep supplies actual DirectIndex/ImpulsePlot");
        return pairs;
    }
    private static IEnumerable<string> Files(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else yield return entry;
            }
        }
    }
    private static List<Pair> Discover(string repo, string output, List<string> inventory, List<string> notes)
    {
        string artifacts = Path.Combine(repo, "artifacts"), standards = Path.Combine(repo, "save standards");
        var current = new DirectoryInfo(output);
        while (current.Parent != null && !File.Exists(Path.Combine(current.FullName, "artifact.json"))) current = current.Parent;
        string excluded = File.Exists(Path.Combine(current.FullName, "artifact.json")) ? current.FullName : output;
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Files(standards).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) paths.Add(path);
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string path in Directory.EnumerateFiles(artifacts, "c360-*.json", enumeration)) paths.Add(path);
        foreach (string directory in Directory.EnumerateDirectories(artifacts).Where(p => Path.GetFileName(p).Contains("echo-reverb", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Contains("io001-reverb", StringComparison.OrdinalIgnoreCase)))
        foreach (string path in Files(directory))
        {
            if (Path.GetFileName(path).EndsWith("effects-result.json", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        }
        var captures = new List<Capture>();
        foreach (string path in paths)
        {
            if (path.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(repo, path), disposition = "no full ImpulsePlot.Samples / stage identity", sha = "";
            long size = new FileInfo(path).Length;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                sha = Convert.ToHexString(SHA256.HashData(stream)); stream.Position = 0;
                using var document = JsonDocument.Parse(stream);
                int before = captures.Count;
                Visit(document.RootElement, "", "", "", "", "", relative, captures);
                if (captures.Count > before) disposition = $"full IR: {captures.Count - before}; pairing requires same identity or documented legacy ON/OFF session";
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
            { disposition = "READ_REJECTED: " + ex.Message; }
            inventory.Add(string.Join(",", new object[] { relative, size, sha, string.Join(";", captures.Where(c => c.Source.StartsWith(relative + "#", StringComparison.Ordinal)).Select(c => c.Stage)), "see pair provenance", disposition }.Select(Csv)));
        }
        var pairs = new List<Pair>();
        // Explicit stages and complete device/model keys only; never infer MicDry from IO003.
        foreach (var wet in captures.Where(c => c.Stage == "MicEffects" && c.Identity.Length > 0))
        foreach (var dry in captures.Where(c => c.Stage == "MicDry" && c.Identity == wet.Identity && c.Context == wet.Context))
            pairs.Add(new($"saved-{pairs.Count + 1:D3}", "SAVED_STAGE_PAIR", wet, dry));
        // Original October session has no SpeakerStage fields. Its ARTIFACT.md documents
        // the same route and user-confirmed hardware states; label as historical controls.
        const string legacy = "20261020-072130-047Z-echo-reverb-observed-summary";
        string Folder(Capture c) => Path.GetFileName(Path.GetDirectoryName(c.Source.Split('#')[0]))!;
        var old = captures.Where(c => c.Source.Contains(legacy, StringComparison.Ordinal)).ToArray();
        foreach (var wet in old.Where(c => Folder(c).StartsWith("live-reverb-only-", StringComparison.Ordinal)
            || Folder(c).StartsWith("live-reverb-on-", StringComparison.Ordinal) || Folder(c).StartsWith("holdout-both-on-", StringComparison.Ordinal) || Folder(c) == "live-final-both-on"))
        foreach (var dry in old.Where(c => Folder(wet).StartsWith("holdout", StringComparison.Ordinal) ? Folder(c) == "holdout-dry" : Folder(c).StartsWith("live-dry-", StringComparison.Ordinal)))
            pairs.Add(new($"legacy-{Folder(wet)}-vs-{Folder(dry)}", "HISTORICAL_ON_OFF_CONTROL", wet, dry));
        notes.Add($"Đã đọc {inventory.Count - 1} JSON chọn lọc (bỏ artifact hiện tại); {captures.Count} IR đầy đủ; {pairs.Count(p => p.Kind == "SAVED_STAGE_PAIR")} cặp SpeakerStage=MicEffects/MicDry có device/model/context khớp; {pairs.Count(p => p.Kind == "HISTORICAL_ON_OFF_CONTROL")} cặp ON/OFF lịch sử cùng phiên/route. Hash và lý do bỏ qua: input-inventory.csv.");
        notes.Add("save standards/: " + (captures.Any(c => c.Source.StartsWith("save standards", StringComparison.Ordinal) && c.Identity.Length > 0 && pairs.Any(p => p.Wet == c || p.Dry == c)) ? "xem các cặp trong CSV" : "chưa có dữ liệu cặp IO001 (MicEffects) / IO003 (MicDry) cùng loa"));
        notes.Add("20261023 io001-off chỉ TẮT reverb, vẫn có echo ~200 ms; IO003 cũ là LineIn. Không ghép làm MicDry. Các nguồn không đủ stage/device/model được giữ trong inventory, không đoán theo ID.");
        notes.Add("Các cặp ON/OFF 20261020 là đối chứng lịch sử theo ARTIFACT.md nguồn, không phải cặp IO003 MicDry hiện hành đã qualification; clock-fit/gain/fixture/firmware chưa được khống chế lại. Không suy rộng từ dữ liệu một route.");
        return pairs;
    }
    private static string Text(JsonElement e, params string[] names)
    {
        foreach (string name in names) if (e.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        return "";
    }
    private static double ReadDouble(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.TryParse(e.ToString(), NumberStyles.Float, Invariant, out double value) ? value : double.NaN;
    private static void Visit(JsonElement node, string stage, string model, string device, string context, string location, string source, List<Capture> captures)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            int index = 0; foreach (var child in node.EnumerateArray()) { if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Visit(child, stage, model, device, context, location + "[" + index + "]", source, captures); index++; }
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        string Inherit(string current, params string[] names) { string value = Text(node, names); return value.Length > 0 ? value : current; }
        stage = Inherit(stage, "SpeakerStage"); model = Inherit(model, "ModelName", "Model"); device = Inherit(device, "DeviceId", "DeviceAddress", "BleAddress", "SerialNumber"); context = Inherit(context, "ConfigurationKey", "SettingsKey", "RouteKey");
        if (node.TryGetProperty("ImpulsePlot", out var plot) && plot.ValueKind == JsonValueKind.Object && plot.TryGetProperty("Samples", out var samples) && samples.ValueKind == JsonValueKind.Array
            && samples.GetArrayLength() > 0 && plot.TryGetProperty("SampleRate", out var rate) && plot.TryGetProperty("DirectIndex", out var direct) && plot.TryGetProperty("PeakTransferGainDb", out var gain))
        {
            double[] values = samples.EnumerateArray().Select(ReadDouble).ToArray();
            var ir = new ImpulsePlotData([], [], ReadDouble(gain), double.NaN, double.NaN) { Samples = values, SampleRate = rate.GetInt32(), DirectIndex = direct.GetInt32() };
            string validity = Text(node, "Validity");
            bool valid = validity is "VALID" or "DIAGNOSTIC" or "PASS" && !(node.TryGetProperty("IsClipped", out var clip) && clip.ValueKind == JsonValueKind.True)
                && !(node.TryGetProperty("HasPlausibleDirectArrival", out var arrival) && arrival.ValueKind == JsonValueKind.False);
            captures.Add(new(source + "#" + location, ir, valid, stage, model.Length > 0 && device.Length > 0 ? model + "|" + device : "", context.Length > 0 ? context : Path.GetDirectoryName(source)!));
        }
        foreach (var property in node.EnumerateObject())
            if (property.Name != "ImpulsePlot" && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Visit(property.Value, stage, model, device, context, location + "/" + property.Name, source, captures);
    }
    private static void Plot(Curves c, string path)
    {
        using var bitmap = new SKBitmap(1200, 720); using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.White);
        using var paint = new SKPaint { IsAntialias = true }; using var font = new SKFont(SKTypeface.Default, 17);
        void Label(string s, float x, float y, SKColor color) { paint.Color = color; canvas.DrawText(s, x, y, SKTextAlign.Left, font, paint); }
        Label("P6 OFFLINE - " + c.Pair.Id, 45, 30, SKColors.Black);
        Label(c.Pair.Kind + " / " + c.Gates.Single(g => g.RightMs == 10).Status + " / not hardware qualification", 45, 55, SKColors.Black);
        float X(double f) => (float)(90 + 1060 * Math.Log(f / 100) / Math.Log(18000d / 100));
        var gated = c.Gates.Single(g => g.RightMs == 10);
        double[] delta = gated.Levels.Zip(c.Dry, (a, b) => a - b).ToArray();
        double deltaLow = Math.Min(-1, Math.Floor(delta.Where(double.IsFinite).Min() - 1));
        double deltaHigh = Math.Max(1, Math.Ceiling(delta.Where(double.IsFinite).Max() + 1));
        double[] finite = c.Dry.Concat(c.Ungated).Concat(gated.Levels).Where(double.IsFinite).ToArray();
        double low = Math.Floor(finite.Min() - 2), high = Math.Ceiling(finite.Max() + 2);
        float Y(double db, bool difference) => difference ? (float)(640 - (db - deltaLow) / (deltaHigh - deltaLow) * 160) : (float)(410 - (db - low) / (high - low) * 300);
        paint.Color = SKColors.LightGray; paint.StrokeWidth = 1;
        foreach (double f in new[] { 100d, 250, 500, 1000, 4000, 10000, 18000 }) { paint.Color = SKColors.LightGray; canvas.DrawLine(X(f), 100, X(f), 660, paint); Label(Number(f), X(f) - 12, 690, SKColors.Black); }
        for (int i = 0; i <= 5; i++) { double db = low + i * (high - low) / 5; paint.Color = SKColors.LightGray; canvas.DrawLine(90, Y(db, false), 1150, Y(db, false), paint); Label(db.ToString("F1", Invariant), 15, Y(db, false), SKColors.Black); }
        foreach (double db in new[] { deltaLow, 0, deltaHigh }) { paint.Color = SKColors.LightGray; canvas.DrawLine(90, Y(db, true), 1150, Y(db, true), paint); Label(Number(db), 35, Y(db, true), SKColors.Black); }
        Label("Transfer dB (absolute gain restored)", 90, 90, SKColors.Black); Label("Delta dB = Wet10 - Dry", 90, 460, SKColors.Black);
        void Line(double[] values, SKColor color, bool delta)
        {
            using var line = new SKPath(); bool started = false;
            for (int i = 0; i < values.Length; i++) if (double.IsFinite(values[i])) { if (!started) line.MoveTo(X(c.Frequencies[i]), Y(values[i], delta)); else line.LineTo(X(c.Frequencies[i]), Y(values[i], delta)); started = true; }
            paint.Color = color; paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2; canvas.DrawPath(line, paint); paint.Style = SKPaintStyle.Fill;
        }
        Line(c.Ungated, SKColors.OrangeRed, false); Line(c.Dry, SKColors.RoyalBlue, false); Line(gated.Levels, SKColors.ForestGreen, false);
        Line(delta, SKColors.Purple, true);
        Label("Frequency Hz (log)", 970, 713, SKColors.Black);
        Label("Dry", 700, 90, SKColors.RoyalBlue); Label("Ungated wet", 770, 90, SKColors.OrangeRed); Label("Gated wet 10ms", 950, 90, SKColors.ForestGreen);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); using var stream = File.Create(path); data.SaveTo(stream);
    }
    private const string Qualification = """
        ## Khung qualification nhà máy — CHƯA thay thế IO003

        | Tiêu chí | Tham khảo / cách ghi | Quyết định người dùng |
        |---|---|---|
        | Số bo N | 30 bo từ ≥3 lô; mỗi trạng thái 3 lượt | N = [   ] bo; lô = [   ]; lượt = [   ] |
        | Lệch mức có dấu | Xét cả mean và median, từng dải Bass/Mid/Treble | x = [   ] dB |
        | Lệch hình dạng | Max/RMS sau bù median; kiểm thêm bù từng dải | x = [   ] dB |
        | Dải áp dụng | Lệch mức và hình dạng ≤ x dB trong dải ≥ y Hz | y = [   ] Hz; fmax = [   ] Hz |
        | Điều kiện | Cùng loa/model/firmware/route/gain/mức/fixture; đảo thứ tự ON/OFF, ghi repeatability; không dùng INVALID_GATE | [   ] |
        | Gate | 5/10/20 ms; Δf≈200/100/50 Hz; đề nghị ≥1000/500/250 Hz | [   ] ms |
        | Xác nhận OFF và THD thay thế | Quy trình riêng phải được duyệt và qualification | [   ] |

        Ba lý do kỹ thuật hiện tại CHƯA thay IO003 bằng gate IO001 (DECISIONS.md / codex-opt-reply.md):

        1. Khảo sát lịch sử: direct lệch khoảng -1,77 đến -2,77 dB; hình dạng lệch tới 1,63–1,67 dB từ 500 Hz. Đây là số khảo sát trước P6, không phải kết quả mọi cặp vừa chạy và không chứng minh qualification.
        2. Echo/Reverb ON: Farina sweep không tách được THD khô đã xác minh; đuôi echo/reverb chồng lấn cửa sổ hài H2/H3. Gate đáp tuyến không khôi phục THD khô.
        3. IO003 MicDry còn xác nhận phần cứng/firmware thật sự tắt Echo/Reverb qua BLE và đọc lại thanh ghi; công cụ offline không thực hiện việc đó.

        Ước tính tiết kiệm 14–17 giây/loa nếu bỏ hoàn toàn 2 lượt MicDry 64K, hoặc 7–9 giây/loa với chính sách 1 lượt.
        **Chỉ áp dụng khi qualification đạt và người dùng chấp nhận phương án kiểm tra OFF/THD thay thế**.
        Thời gian thực sau khi thêm phép thay thế chưa xác minh; không suy ra độ chính xác tương đương REW từ build/synthetic.
        """;
}
