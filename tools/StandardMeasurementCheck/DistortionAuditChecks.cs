using System.Text.Json;
using SoncaAudioInspector;

internal static class DistortionAuditChecks
{
    public static void Run()
    {
        // Independent signal equations: H2=.15%, H3=.20%, RSS THD=.25%.
        foreach (int rate in new[] { 44100, 48000 })
        foreach (double snr in new[] { double.PositiveInfinity, 40.0, 20.0, 10.0 })
        {
            var random = new Random(5298);
            double Noise() => Math.Sqrt(-2 * Math.Log(Math.Max(1e-15, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble());
            double noiseRms = .05 / Math.Sqrt(2) * Math.Pow(10, -snr / 20);
            var tone = new float[rate * 2];
            for (int i = 0; i < tone.Length; i++)
            {
                double phase = 2 * Math.PI * 1000 * i / rate;
                tone[i] = (float)(.05 * (Math.Sin(phase) + .0015 * Math.Sin(2 * phase) + .002 * Math.Sin(3 * phase)) + noiseRms * Noise());
            }
            var quality = AdvancedAudioMeasurement.AnalyzeTone(tone, rate, 1000, 9);
            if (!quality.IsValid || (snr >= 20 && Math.Abs(quality.ThdPercent - .25) > (snr >= 40 ? .025 : .18)))
                throw new Exception($"Known tone THD failed: rate={rate}, SNR={snr}, THD={quality.ThdPercent}");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "tone", rate, snr = double.IsPositiveInfinity(snr) ? "clean" : snr.ToString(), expectedThd = .25, quality.ThdPercent, quality.ThdNPercent, quality.SnrDb }));

            const int length = 524288;
            var settings = new LogSweepSettings(rate, 18, rate * .47, length / (double)rate, .2,
                PointsPerOctave: 48, DistortionEvaluationFrequenciesHz: new[] { 80.0, 1000.0, 4000.0 });
            float[] excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[length + rate];
            for (int i = 0; i < capture.Length; i++) capture[i] = (float)(noiseRms * Noise());
            double ratio = settings.EndFrequencyHz / settings.StartFrequencyHz;
            double scale = 2 * Math.PI * settings.StartFrequencyHz * settings.DurationSeconds / Math.Log(ratio);
            for (int i = 0; i < length; i++)
            {
                double phase = scale * (Math.Pow(ratio, i / (double)(length - 1)) - 1);
                // Same fade as the excitation; avoid inferring phase from asin(x).
                double fade = Math.Abs(Math.Sin(phase)) > 1e-5 ? excitation[i] / (.2 * Math.Sin(phase)) : 0;
                capture[1200 + i] += (float)(.25 * excitation[i] + .05 * fade * (.0015 * Math.Sin(2 * phase) + .002 * Math.Sin(3 * phase)));
            }
            var result = StandardAcousticMeasurement.AnalyzeLogSweep(excitation, capture, settings);
            var point = result.SweepHarmonics.Single(p => p.FundamentalFrequencyHz == 1000);
            var aggregate = SweepDistortionSummary.AggregateSweep(result.SweepHarmonics, result.SignalLevelDbFs, result.NoiseFloorDbFs, result.IsClipped, result.Validity, -75);
            if (double.IsPositiveInfinity(snr) && Math.Abs(point.ThdPercent - .25) > .04)
                throw new Exception($"Known sweep THD failed: rate={rate}, THD={point.ThdPercent}");
            var raw = new SweepDistortionSummary(1000, point.FundamentalLevelDb, new Dictionary<int, double>(), point.ThdPercent,
                0, result.NoiseFloorDbFs, "H2-H9", true, "Synthetic sweep");
            var assessed = raw.WithNoiseAssessment(result.EstimatedSnrDb, .5);
            if (!assessed.IsValid || assessed.ThdPercent != point.ThdPercent)
                throw new Exception("Measured raw harmonics were rejected or changed.");
            if (snr >= 40 && Math.Abs(point.ThdPercent - .25) > .08)
                throw new Exception("Frequency-dependent isolation biased a measurable .25% harmonic.");
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "sweep", rate, snr = double.IsPositiveInfinity(snr) ? "clean" : snr.ToString(), expectedThd = .25, point.ThdPercent, point.H2Dbc, point.H3Dbc, point.FundamentalLevelDb, result.EstimatedSnrDb, aggregateThd = aggregate.ThdPercent, result.Validity }));
        }
        var low = new SweepDistortionSummary(1000, -30, new Dictionary<int, double>(), .25, 0, -50, "H2-H9", true, "Test");
        if (!low.WithNoiseAssessment(20, .5).IsValid) throw new Exception("Under-limit SNR warning changed acceptance.");
        var high = low with { ThdPercent = 14 };
        if (!high.WithNoiseAssessment(70, .5).IsValid || !high.WithNoiseAssessment(10, .5).IsValid)
            throw new Exception("Broadband SNR fabricated a THD validity threshold.");
        Console.WriteLine("PASS known .25% RSS THD, calibration-independent tone/sweep audit and raw measured harmonic preservation.");
    }
}
