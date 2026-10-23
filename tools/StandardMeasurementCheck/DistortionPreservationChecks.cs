using SoncaAudioInspector;

internal static class DistortionPreservationChecks
{
    public static void Run()
    {
        foreach (int rate in new[] { 44100, 48000 })
        foreach (double noiseRms in new[] { 0.0, .00035 })
        {
            const int length = 524288;
            var settings = new LogSweepSettings(rate, 18, rate * .47, length / (double)rate, .2,
                PointsPerOctave: 24, DistortionEvaluationFrequenciesHz: new[] { 1000.0 });
            float[] input = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[length + rate];
            var random = new Random(831);
            for (int i = 0; i < capture.Length; i++) capture[i] = (float)(noiseRms * Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble()));
            double ratio = settings.EndFrequencyHz / settings.StartFrequencyHz;
            double scale = 2 * Math.PI * settings.StartFrequencyHz * settings.DurationSeconds / Math.Log(ratio);
            for (int i = 0; i < input.Length; i++)
            {
                double phase = scale * (Math.Pow(ratio, i / (double)(length - 1)) - 1);
                double fade = Math.Abs(Math.Sin(phase)) > 1e-5 ? input[i] / (.2 * Math.Sin(phase)) : 0;
                capture[1200 + i] += (float)(.25 * input[i] + .05 * fade * (.12 * Math.Sin(2 * phase) + .04 * Math.Sin(3 * phase)));
            }
            var result = StandardAcousticMeasurement.AnalyzeLogSweep(input, capture, settings);
            var point = result.SweepHarmonics.Single(p => p.FundamentalFrequencyHz == 1000);
            double expected = Math.Sqrt(12 * 12 + 4 * 4);
            Console.WriteLine($"{rate} Hz; noise={noiseRms}: true THD={expected:F6}%, measured={point.ThdPercent:F6}%.");
            if (Math.Abs(point.ThdPercent - expected) > .2) throw new Exception("Real high distortion was suppressed by the harmonic window.");
        }
        Console.WriteLine("PASS genuine 12% H2 + 4% H3 survives frequency-dependent isolation, including independent noise.");
    }
}
