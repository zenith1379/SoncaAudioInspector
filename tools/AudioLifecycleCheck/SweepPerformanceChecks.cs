using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoncaAudioInspector;

internal static class SweepPerformanceChecks
{
    // No endpoints are opened. Match the production grid, conditioning and tail;
    // Report Analyze separately: a production single-run test ends there.
    // The extra coherent finalization is used only for multi-run averaging.
    public static void RunProduction(int length, int runs, bool silent = false)
    {
        var settings = new LogSweepSettings(44100, 18, 44100 * .47, length / 44100.0, .25,
            ResponseSmoothing.SourcePointsPerOctave, EffectsSettings: new ImpulseEffectSettings());
        float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        var recorded = new float[sweep.Length + 44100 * 3 + 26460];
        var random = new Random(458);
        if (!silent)
        {
            for (int i = 0; i < recorded.Length; i++) recorded[i] = (float)((random.NextDouble() - .5) * 1e-6);
            for (int i = 0; i < sweep.Length; i++) recorded[i + 2048] += sweep[i] * .25f;
        }
        recorded = StandardAcousticMeasurement.ApplySubsonicConditioning(recorded, settings.SampleRate);
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var overload = typeof(StandardAcousticMeasurement).GetMethods().SingleOrDefault(m =>
            m.Name == nameof(StandardAcousticMeasurement.AnalyzeLogSweep) && m.GetParameters().Length == 6);
        for (int run = 0; run < runs; run++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var stages = new Dictionary<string, double>();
            Action<string, double> stage = (name, ms) => stages[name] = ms;
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            var result = overload == null ? StandardAcousticMeasurement.AnalyzeLogSweep(sweep, recorded, settings, null, -125)
                : (StandardAcousticResult)overload.Invoke(null, new object?[] { sweep, recorded, settings, null, -125.0, stage })!;
            double analysisMs = timer.Elapsed.TotalMilliseconds;
            var average = StandardAcousticMeasurement.AverageComplexTransfers(new[] { result.AlignedTransferFunction });
            var finalResponse = StandardAcousticMeasurement.BuildFrequencyResponseFromAlignedTransfer(average.Transfer,
                settings.SampleRate, settings.StartFrequencyHz, 22000, settings.PointsPerOctave, null, settings);
            double totalMs = timer.Elapsed.TotalMilliseconds;
            long allocation = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            string fullResultSha256 = Digest(result, options);
            string finalResponseSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(finalResponse, options)));
            Console.WriteLine(JsonSerializer.Serialize(new { length, run, seconds = settings.DurationSeconds,
                settings.PointsPerOctave, analysisMs, finalizationMs = totalMs - analysisMs, totalMs, allocation,
                stages, fullResultSha256, finalResponseSha256, result.Validity }));
        }
    }

    public static void Run(int repeats = 5)
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        foreach (double seconds in new[] { 65536.0 / 44100, 3.0 })
        {
            var settings = new LogSweepSettings(44100, 18, 20000, seconds, .4);
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[sweep.Length + 4410];
            for (int index = 0; index < sweep.Length; index++) capture[index + 128] = sweep[index] * .25f;
            if (repeats > 1) _ = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
            var timings = new List<double>(); var allocations = new List<long>(); string? hash = null;
            for (int run = 0; run < repeats; run++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
                watch.Stop();
                allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocated);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                if (hash == null) hash = Digest(result, options);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { seconds, medianMs = timings.Order().ElementAt(repeats / 2),
                medianAllocatedBytes = allocations.Order().ElementAt(repeats / 2), fullResultSha256 = hash, timings }));
        }
        var checks = new LogSweepSettings(48000, 20, 20000, 1.5, .4);
        float[] stimulus = StandardAcousticMeasurement.GenerateLogSweep(checks);
        foreach (string scenario in new[] { "clipped", "clock-drift", "silent", "unrelated-noise" })
        {
            var capture = new float[stimulus.Length + 4800];
            var random = new Random(42);
            for (int index = 0; index < capture.Length; index++)
            {
                double position = (index - 128) * (scenario == "clock-drift" ? 1.0004 : 1);
                int first = (int)Math.Floor(position);
                double value = first >= 0 && first + 1 < stimulus.Length
                    ? stimulus[first] + (stimulus[first + 1] - stimulus[first]) * (position - first) : 0;
                capture[index] = scenario switch
                {
                    "clipped" => (float)Math.Clamp(value * 5, -1, 1),
                    "clock-drift" => (float)(value * .25),
                    "unrelated-noise" => (float)((random.NextDouble() - .5) * .1),
                    _ => 0
                };
            }
            var result = StandardAcousticMeasurement.AnalyzeLogSweep(stimulus, capture, checks);
            string hash = Digest(result, options);
            Console.WriteLine(JsonSerializer.Serialize(new { scenario, fullResultSha256 = hash,
                result.Validity, result.ClockDriftCorrectionApplied, result.EstimatedClockDriftPpm }));
        }
    }

    private static string Digest(StandardAcousticResult result, JsonSerializerOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Complex arrays dominate size. Hash their exact real/imaginary bits;
        // serialize every other result field, including all validity/diagnostic text.
        var rest = result with { AlignedTransferFunction = Array.Empty<Complex>(),
            HarmonicTransferFunctions = Array.Empty<Complex[]>() };
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(rest, options));
        void Add(Complex[]? values)
        {
            hash.AppendData(BitConverter.GetBytes(values?.Length ?? -1));
            if (values != null) hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        Add(result.AlignedTransferFunction);
        hash.AppendData(BitConverter.GetBytes(result.HarmonicTransferFunctions.Count));
        foreach (var values in result.HarmonicTransferFunctions) Add(values);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void CheckClockCorrection()
    {
        var settings = new LogSweepSettings(48000, 30, 18000, 1, .2, DecayAnalysisSeconds: 1.2);
        float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        const double scale = 1.0004;
        int length = (int)Math.Ceiling(sweep.Length * scale);
        var capture = new float[length + 3600];
        double ratio = settings.EndFrequencyHz / settings.StartFrequencyHz;
        double logRatio = Math.Log(ratio);
        int cycles = Math.Max(1, (int)Math.Round(settings.StartFrequencyHz * settings.DurationSeconds / logRatio * (ratio - 1)));
        double phaseScale = 2 * Math.PI * cycles / (ratio - 1);
        double octaves = logRatio / Math.Log(2);
        int fadeIn = Math.Max(16, (int)Math.Round(settings.DurationSeconds / octaves * settings.SampleRate));
        int fadeOut = Math.Max(16, (int)Math.Round(settings.DurationSeconds / (12 * octaves) * settings.SampleRate));
        for (int index = 0; index < length; index++)
        {
            double position = index / scale;
            if (position >= sweep.Length) continue;
            double fade = position < fadeIn ? .5 - .5 * Math.Cos(Math.PI * position / fadeIn)
                : position >= sweep.Length - fadeOut ? .5 - .5 * Math.Cos(Math.PI * (sweep.Length - 1 - position) / fadeOut) : 1;
            capture[1200 + index] = (float)(settings.Amplitude * fade * Math.Sin(phaseScale * (Math.Pow(ratio, position / (sweep.Length - 1)) - 1)));
        }
        var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
        if (!result.ClockDriftCorrectionApplied) throw new InvalidOperationException("Clock test did not enter the correction branch.");
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        Console.WriteLine(JsonSerializer.Serialize(new { scenario = "clock-corrected", fullResultSha256 = Digest(result, options),
            result.ClockDriftCorrectionApplied, result.EstimatedClockDriftPpm }));
    }
}
