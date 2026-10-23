using System.Numerics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SoncaAudioInspector;

internal static class SweepNumericalComparison
{
    public static void Run(string baselinePath)
    {
        var context = new AssemblyLoadContext("managed-sweep-baseline", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name)
            ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll"));
        Assembly baseline = context.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
        Type oldSettingsType = baseline.GetType("SoncaAudioInspector.LogSweepSettings")!;
        MethodInfo oldAnalyze = baseline.GetType("SoncaAudioInspector.StandardAcousticMeasurement")!.GetMethods()
            .Single(m => m.Name == "AnalyzeLogSweep" && m.GetParameters().Length == 5);
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Extrema decimation may select a different near-zero sample when
            // FFT roundoff changes ties. Compare the full-resolution IR samples
            // and complex spectra, rather than indices of reduced plot points.
            foreach (var property in info.Properties.Where(p => p.Name is "AlignedTransferFunction" or "HarmonicTransferFunctions"
                or "ImpulseResponse" or "StepResponse" or "EnergyTimeCurve" or "Impulse" or "Envelope").ToArray())
                info.Properties.Remove(property);
        });
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, TypeInfoResolver = resolver };
        string backend = (string)typeof(StandardAcousticMeasurement).Assembly.GetType("SoncaAudioInspector.SweepFourier")!
            .GetProperty("BackendName", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        if (!backend.Contains("MKL")) throw new Exception("Native comparison did not load MKL: " + backend);
        bool failed = false;
        foreach (string scenario in new[] { "512k", "clipped", "silence", "noise", "clock-corrected" })
        {
            int rate = scenario == "512k" ? 44100 : 48000;
            int length = scenario == "512k" ? 524288 : scenario == "clock-corrected" ? 48000 : 65536;
            var settings = new LogSweepSettings(rate, 30, scenario == "clock-corrected" ? 18000 : Math.Min(20000, rate * .47), length / (double)rate, .2,
                scenario == "512k" ? 192 : 24, EffectsSettings: new());
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[length + rate * 3];
            var random = new Random(458);
            for (int i = 0; i < capture.Length; i++)
            {
                int source = i - 1200;
                double value = source >= 0 && source < sweep.Length ? sweep[source] : 0;
                capture[i] = scenario switch
                {
                    "silence" => 0,
                    "noise" => (float)((random.NextDouble() - .5) * .1),
                    "clipped" => (float)Math.Clamp(value * 9, -1, 1),
                    _ => (float)(value * .25 + .02 * value * value + (random.NextDouble() - .5) * 1e-6)
                };
            }
            if (scenario == "clock-corrected") capture = ClockCapture(sweep, settings);
            object oldSettings = JsonSerializer.Deserialize(JsonSerializer.Serialize(settings), oldSettingsType)!;
            object before = oldAnalyze.Invoke(null, new object?[] { sweep, capture, oldSettings, null, -100.0 })!;
            var after = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings, null, -100);
            var beforeJson = JsonSerializer.SerializeToElement(before, before.GetType(), options);
            var afterJson = JsonSerializer.SerializeToElement(after, options);
            var issues = new List<string>(); double maxNumberError = 0, maxFloorTraceErrorDb = 0; int numbers = 0;
            void Compare(JsonElement a, JsonElement b, string path)
            {
                if (a.ValueKind != b.ValueKind) { issues.Add(path + ": kind"); return; }
                if (a.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in a.EnumerateObject())
                        if (b.TryGetProperty(p.Name, out var right)) Compare(p.Value, right, path + "." + p.Name);
                        else issues.Add(path + "." + p.Name + ": missing");
                }
                else if (a.ValueKind == JsonValueKind.Array)
                {
                    if (a.GetArrayLength() != b.GetArrayLength()) { issues.Add(path + ": length"); return; }
                    for (int i = 0; i < a.GetArrayLength(); i++) Compare(a[i], b[i], path + "[" + i + "]");
                }
                else if (a.ValueKind == JsonValueKind.Number)
                {
                    double x = a.GetDouble(), y = b.GetDouble(); numbers++;
                    double error = Math.Abs(x - y) / Math.Max(1, Math.Abs(x)); maxNumberError = Math.Max(maxNumberError, error);
                    // At the display floor log conversion magnifies roundoff.
                    // Compare low-level decay in normalized linear amplitude
                    // (1e-10), retaining the tighter bound for measured metrics.
                    bool lowTrace = x < -80 && (path.StartsWith("result.EnergyDecayCurve[") || path.StartsWith("result.Waterfall[")) && path.EndsWith(".LevelDb");
                    if (lowTrace) maxFloorTraceErrorDb = Math.Max(maxFloorTraceErrorDb, Math.Abs(x - y));
                    if (lowTrace ? Math.Abs(Math.Pow(10, x / 20) - Math.Pow(10, y / 20)) > 1e-10 : error > 1e-8)
                        issues.Add($"{path}: {x:G17} vs {y:G17}");
                }
                else if (a.GetRawText() != b.GetRawText()) issues.Add(path + ": " + a.GetRawText() + " vs " + b.GetRawText());
            }
            Compare(beforeJson, afterJson, "result");
            double complexError = 0;
            void CompareComplex(Complex[] a, Complex[] b)
            {
                if (a == null && b == null) return;
                if (a == null || b == null || a.Length != b.Length) { issues.Add("Complex array length"); return; }
                double error = 0, scale = 1;
                for (int i = 0; i < a.Length; i++) { error = Math.Max(error, (a[i] - b[i]).Magnitude); scale = Math.Max(scale, a[i].Magnitude); }
                complexError = Math.Max(complexError, error / scale);
                if (error / scale > 1e-10) issues.Add("Complex spectrum outside 1e-10 relative/absolute bound");
            }
            CompareComplex((Complex[])before.GetType().GetProperty("AlignedTransferFunction")!.GetValue(before)!, after.AlignedTransferFunction);
            var oldHarmonics = (IReadOnlyList<Complex[]>)before.GetType().GetProperty("HarmonicTransferFunctions")!.GetValue(before)!;
            if (oldHarmonics.Count != after.HarmonicTransferFunctions.Count) issues.Add("Harmonic count");
            else for (int i = 0; i < oldHarmonics.Count; i++) CompareComplex(oldHarmonics[i], after.HarmonicTransferFunctions[i]);
            Console.WriteLine(JsonSerializer.Serialize(new { scenario, backend, numbers, maxNumberError, maxFloorTraceErrorDb, complexError,
                after.Validity, after.ClockDriftCorrectionApplied, issues = issues.Take(12).ToArray(), issueCount = issues.Count,
                issueKinds = issues.GroupBy(x => System.Text.RegularExpressions.Regex.Replace(x.Split(':')[0], @"\[\d+\]", "[]")).ToDictionary(g => g.Key, g => g.Count()) }));
            failed |= issues.Count > 0;
            if (scenario == "clock-corrected" && !after.ClockDriftCorrectionApplied) throw new Exception("Clock fixture did not correct clock.");
        }
        context.Unload();
        if (failed) throw new Exception("Native numerical comparison failed; see scenario details.");
    }

    private static float[] ClockCapture(float[] sweep, LogSweepSettings settings)
    {
        const double scale = 1.0004;
        var capture = new float[(int)Math.Ceiling(sweep.Length * scale) + 3600];
        double ratio = settings.EndFrequencyHz / settings.StartFrequencyHz, log = Math.Log(ratio);
        int cycles = Math.Max(1, (int)Math.Round(settings.StartFrequencyHz * settings.DurationSeconds / log * (ratio - 1)));
        double phaseScale = 2 * Math.PI * cycles / (ratio - 1), octaves = log / Math.Log(2);
        int fadeIn = Math.Max(16, (int)Math.Round(settings.DurationSeconds / octaves * settings.SampleRate));
        int fadeOut = Math.Max(16, (int)Math.Round(settings.DurationSeconds / (12 * octaves) * settings.SampleRate));
        for (int i = 0; i < sweep.Length * scale && i + 1200 < capture.Length; i++)
        {
            double p = i / scale;
            double fade = p < fadeIn ? .5 - .5 * Math.Cos(Math.PI * p / fadeIn)
                : p >= sweep.Length - fadeOut ? .5 - .5 * Math.Cos(Math.PI * (sweep.Length - 1 - p) / fadeOut) : 1;
            capture[i + 1200] = (float)(settings.Amplitude * fade * Math.Sin(phaseScale * (Math.Pow(ratio, p / (sweep.Length - 1)) - 1)));
        }
        return capture;
    }
}
