using SoncaAudioInspector;
using System.Numerics;
using System.Text.Json;
using MathNet.Numerics.IntegralTransforms;

internal static class ImpulsePlotChecks
{
    public static void Run()
    {
        double[] times = Enumerable.Range(0, 5001).Select(i => (i - 2000) / 1000.0).ToArray();
        var occupied = new double[times.Length];
        occupied[2000] = 1; occupied[2800] = .01; occupied[10] = 1e-7;
        var range = ImpulsePlotAnalysis.InitialTimeRange(new(times, occupied), .125, .5);
        if (range.Start < -.2 || range.Start > -.125 || range.End < .8 || range.End > .9)
            throw new Exception("Initial IR view wastes empty margins or hides the window/late echo.");
        occupied[500] = -.1; occupied[4500] = -.02;
        range = ImpulsePlotAnalysis.InitialTimeRange(new(times, occupied), .125, .5);
        if (range.Start > -1.5 || range.End < 2.5 || occupied[10] != 1e-7)
            throw new Exception("Initial IR view loses significant signed pre/post peaks or mutates raw data.");
        var silence = ImpulsePlotAnalysis.InitialTimeRange(new(times, new double[times.Length]), .125, .5);
        var empty = ImpulsePlotAnalysis.InitialTimeRange(new([], []), .125, .5);
        var single = ImpulsePlotAnalysis.InitialTimeRange(new([0], [1]), .125, .5);
        if (silence.Start < -.2 || silence.End > .6 || empty.End <= empty.Start || single.End <= single.Start)
            throw new Exception("Silent/empty/single-sample IR has an empty or excessive initial range.");
        Console.WriteLine("PASS focused opening excludes quiet margins, retains signed peaks/window/late echo, leaves samples intact and handles silence/empty/single sample.");
        var signed = new[] { new TimeDomainPoint(0, 1), new TimeDomainPoint(.001, -3), new TimeDomainPoint(.002, -1) };
        var log = ImpulsePlotAnalysis.LogMagnitudeTrace(signed);
        if (log.Count != 4 || Math.Abs(log[1].TimeSeconds - .00025) > 1e-12 || log[1].Value != -180
            || Math.Abs(log[2].Value - 20 * Math.Log10(3)) > 1e-12 || signed[1].Value != -3)
            throw new Exception("IR log rendering lost the interpolated zero crossing or modified a stored sample.");
        var onePolarity = new[] { new TimeDomainPoint(0, .1), new TimeDomainPoint(.001, .001), new TimeDomainPoint(.002, .1) };
        if (ImpulsePlotAnalysis.LogMagnitudeTrace(onePolarity).Any(p => p.Value <= -180)
            || ImpulsePlotAnalysis.LogMagnitudeTrace(signed, includeZeroCrossings: false).Count != signed.Length)
            throw new Exception("IR renderer invented floor crossings inside a same-polarity notch or in legacy reduced samples.");
        Console.WriteLine("PASS signed IR crossing before abs/log, preserved same-polarity notches, unchanged stored samples and legacy fallback.");
        var denseSource = Enumerable.Range(0, 40000).Select(i => Math.Sin(i * .73) * Math.Exp(-i / 25000.0)).ToArray();
        var reference = ImpulsePlotAnalysis.LogMagnitudeTrace(denseSource.Select((v, i) => new TimeDomainPoint((i - 10000) / 44100.0, v)).ToArray());
        var prepared = ImpulsePlotAnalysis.PrepareLogMagnitude(denseSource, 44100, 10000);
        if (reference.Count != prepared.Times.Length || reference.Where((p, i) => p.TimeSeconds != prepared.Times[i] || p.Value != prepared.Values[i]).Any())
            throw new Exception("Fast array preparation differs from the full-resolution IR/zero-crossing reference.");
        Console.WriteLine("PASS allocation-light IR arrays exactly match every full-resolution reference coordinate and level.");
        if (ImpulsePlotAnalysis.WindowWeight(0, .002, .5) != 1
            || Math.Abs(ImpulsePlotAnalysis.WindowWeight(-.001, .002, .5, ImpulseWindowKind.HalfHann) - .5) > 1e-12
            || Math.Abs(ImpulsePlotAnalysis.WindowWeight(.25, .002, .5, ImpulseWindowKind.HalfHann) - .5) > 1e-12
            || ImpulsePlotAnalysis.WindowWeight(-.003, .002, .5) != 0
            || ImpulsePlotAnalysis.WindowWeight(.501, .002, .5) != 0)
            throw new Exception("Displayed half-Hann IR window does not match the analyzer's weighting.");
        if (ImpulsePlotAnalysis.SampleCount(125, 44100) != 5513
            || ImpulsePlotAnalysis.WindowWeight(-.075, .125, .5) != 1
            || ImpulsePlotAnalysis.WindowWeight(.3, .125, .5) != 1
            || Math.Abs(ImpulsePlotAnalysis.WindowWeight(-.109375, .125, .5) - .5) > 1e-12
            || Math.Abs(ImpulsePlotAnalysis.WindowWeight(.4375, .125, .5) - .5) > 1e-12)
            throw new Exception("Tukey 0.25 shape or sample-grid rounding differs from the REW window.");
        foreach (int rate in new[] { 44100, 48000 })
        {
            var samples = new double[rate * 3];
            samples[rate] = 1; samples[rate + rate / 5] = .5; samples[rate + rate * 2 / 5] = .25;
            samples[rate / 2] = .01;
            var data = ImpulsePlotAnalysis.Build(samples, rate, rate);
            if (data.Samples.Length != samples.Length || data.SampleRate != rate || data.DirectIndex != rate)
                throw new Exception("Full-resolution impulse was not retained for plotting/windowing.");
            if (Math.Abs(data.Impulse[0].TimeSeconds + 1) > .001 || data.Impulse.MaxBy(p => Math.Abs(p.Value))!.TimeSeconds != 0)
                throw new Exception("Full pre-arrival window or direct t=0 was lost.");
            var echo = data.Impulse.First(p => Math.Abs(p.TimeSeconds - .2) < .00001);
            if (Math.Abs(20 * Math.Log10(Math.Abs(echo.Value)) + 6.020599913) > 1e-6)
                throw new Exception("Display decimation lost the echo peak or its real relative level.");
            if (Math.Abs(data.ClarityC20Db - 10 * Math.Log10(1 / .3125)) > 1e-8
                || Math.Abs(data.CentreTimeSeconds - .075 / 1.3125) > 1e-8)
                throw new Exception("C20 / centre time includes pre-arrival energy or has the wrong time reference.");
            var quiet = ImpulsePlotAnalysis.Build(samples.Select(v => v * .1).ToArray(), rate, rate);
            if (Math.Abs(quiet.PeakTransferGainDb - data.PeakTransferGainDb + 20) > 1e-9
                || quiet.Envelope.Zip(data.Envelope).Any(p => Math.Abs(p.First.Value - p.Second.Value) > 1e-6))
                throw new Exception("View normalisation lost its gain metadata or modified the relative envelope.");
            // A constant-amplitude sinusoid has a constant analytic envelope,
            // including at real-signal zero crossings.
            var tone = Enumerable.Range(0, rate).Select(i => Math.Sin(2 * Math.PI * 1000 * i / rate)).ToArray();
            double[] envelope = ImpulsePlotAnalysis.EnvelopeDb(tone);
            int crossing = rate / 2;
            if (Math.Abs(tone[crossing]) > 1e-9 || Math.Abs(envelope[crossing]) > .05
                || Enumerable.Range(rate / 4, rate / 2).Max(i => Math.Abs(envelope[i])) > .06)
                throw new Exception("ETC is not the Hilbert envelope (or has excessive interior ripple).");
            Console.WriteLine($"PASS {rate} Hz: full pre-IR, exact echo peak, analytic ETC, C20/TS, gain-independent display.");
        }
        // A peak outside L and a smaller peak inside L share one reduction block.
        // Gating a chosen display peak would discard the real windowed maximum.
        var edge = new double[160000]; const int direct = 100000;
        edge[direct] = 1; edge[direct - 10] = .9; edge[direct - 1] = .8;
        var full = ImpulsePlotAnalysis.Build(edge, 1000, direct);
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var reloaded = JsonSerializer.Deserialize<ImpulsePlotData>(JsonSerializer.Serialize(full, options), options)!;
        var gated = ImpulsePlotAnalysis.ApplyWindow(reloaded.Samples, 1000, direct, 2, 500, ImpulseWindowKind.HalfHann);
        var reduced = ImpulsePlotAnalysis.Reduce(gated, 1000, direct, absolute: true);
        if (Math.Abs(reduced.Single(p => p.TimeSeconds == -.001).Value - .4) > 1e-12
            || !full.Impulse.Any(p => p.TimeSeconds == -.009 && p.Value == 0))
            throw new Exception("Full-resolution gate / narrow valleys did not survive reduction and reload.");
        // Fixed Tukey must retain a real echo 100 ms after direct at high frequencies.
        // An accidentally active 15-cycle FDW would remove it at 1 kHz.
        var transfer = new Complex[48000]; transfer[0] = 1; transfer[4800] = .5; transfer[^3600] = .25;
        Fourier.Forward(transfer, FourierOptions.Matlab);
        var response = StandardAcousticMeasurement.BuildFrequencyResponseFromAlignedTransfer(transfer, 48000, 100, 12000, 24);
        foreach (var point in response)
        {
            Complex expected = 1 + .5 * Complex.FromPolarCoordinates(1, -2 * Math.PI * point.FrequencyHz * .1)
                + .25 * Complex.FromPolarCoordinates(1, 2 * Math.PI * point.FrequencyHz * .075);
            if (Math.Abs(point.LevelDb - 20 * Math.Log10(expected.Magnitude)) > 1e-7)
                throw new Exception("Coherent-average response did not use the fixed Tukey 125/500 ms window with FDW off.");
        }
        Console.WriteLine("PASS full-sample persistence, gate before reduction, narrow valleys and fixed Tukey coherent response with FDW off.");
    }
}
