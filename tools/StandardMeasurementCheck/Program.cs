using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using SoncaAudioInspector;
if (args.Contains("--impedance")) { ImpedanceChecks.Run(); return; }
if (args.Contains("--reverb-decay")) { ReverbDecayChecks.Run(); return; }

if (args.Contains("--distortion-audit")) { DistortionAuditChecks.Run(); return; }
if (args.Contains("--distortion-preservation")) { DistortionPreservationChecks.Run(); return; }
if (args.Contains("--captured-effects"))
{
    int index = Array.IndexOf(args, "--captured-effects");
    ImpulseEffectsChecks.RunCaptured(Path.GetFullPath(args[index + 1]), Path.GetFullPath(args[index + 2]));
    return;
}

if (args.Contains("--signal-diagnostics")) { SignalDiagnosticChecks.Run(Path.GetFullPath(args[Array.IndexOf(args, "--signal-diagnostics") + 1])); return; }

if (args.Contains("--mechanical")) { MechanicalChecks.Run(Path.GetFullPath(args[Array.IndexOf(args, "--mechanical") + 1])); return; }

if (args.Contains("--fft-backend"))
{
    Console.WriteLine(SweepFourier.BackendName);
    if (args.Contains("--require-native") && !SweepFourier.BackendName.Contains("MKL")) throw new Exception("Native FFT was not loaded.");
    return;
}

if (args.Contains("--sweep-fft")) { SweepFourierChecks.Run(); return; }

if (args.Contains("--impulse-plot"))
{
    ImpulsePlotChecks.Run();
    return;
}

if (args.Contains("--impulse-effects"))
{
    ImpulseEffectsChecks.Run(Path.GetFullPath(args[Array.IndexOf(args, "--impulse-effects") + 1]));
    return;
}

if (args.Contains("--rew-regression"))
{
    RewRegressionChecks.Run();
    return;
}

const int sampleRate = 48000;
var settings = new LogSweepSettings(
    SampleRate: sampleRate,
    StartFrequencyHz: 30,
    EndFrequencyHz: 18000,
    DurationSeconds: 1.0,
    Amplitude: 0.20,
    DecayAnalysisSeconds: 1.2);
float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
if (sweep.Length != sampleRate || sweep.Max(value => Math.Abs(value)) > 0.201f)
    throw new InvalidOperationException("Log-sweep generation failed.");

var delayed = new float[sweep.Length + 2400];
Array.Copy(sweep, 0, delayed, 1200, sweep.Length);
StandardAcousticResult flat = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, delayed, settings);
if (Math.Abs(flat.DirectArrivalMs - 25.0) > 0.2)
    throw new InvalidOperationException($"Impulse delay failed: {flat.DirectArrivalMs:F3} ms.");
if (flat.Validity != "DIAGNOSTIC" || !flat.HasPlausibleDirectArrival
    || flat.Diagnostics.All(item => item.Code != "ACQUISITION_OK"))
    throw new InvalidOperationException("Valid sweep acquisition was not recognized.");
double responseSpread = flat.FrequencyResponse
    .Where(point => point.FrequencyHz >= 80 && point.FrequencyHz <= 12000)
    .Max(point => Math.Abs(point.NormalizedLevelDb));
if (responseSpread > 0.8)
    throw new InvalidOperationException($"Flat transfer response spread too high: {responseSpread:F3} dB.");
if (flat.UngatedFrequencyResponse.Count == 0 || flat.FrequencyDependentWindowing
    || flat.ImpulseWindowKind != ImpulseWindowKind.Tukey25
    || Math.Abs(flat.ImpulseWindowLeftMs - settings.ImpulseWindowLeftMs) > .02
    || Math.Abs(flat.ImpulseWindowRightMs - settings.ImpulseWindowRightMs) > 0.1
    || Math.Abs(flat.FrequencyDependentWindowCycles - settings.FrequencyDependentWindowCycles) > 1e-9
    || flat.GatedMinimumFrequencyHz > settings.StartFrequencyHz)
    throw new InvalidOperationException("Tukey IR window / disabled FDW metadata was not retained.");

// Simulate independent playback/capture clocks: the recorded sweep is 400 ppm longer.
const double injectedClockScale = 1.0004;
const int clockDelaySamples = 1200;
int stretchedLength = (int)Math.Ceiling(sweep.Length * injectedClockScale);
var driftedCapture = new float[clockDelaySamples + stretchedLength + 2400];
double sweepRatio = settings.EndFrequencyHz / settings.StartFrequencyHz;
double sweepLogRatio = Math.Log(sweepRatio);
double sweepDuration = sweep.Length / (double)sampleRate;
int sweepCycles = Math.Max(1, (int)Math.Round(settings.StartFrequencyHz * sweepDuration / sweepLogRatio * (sweepRatio - 1.0)));
double sweepPhaseScale = 2.0 * Math.PI * sweepCycles / (sweepRatio - 1.0);
double sweepOctaves = sweepLogRatio / Math.Log(2.0);
int sweepFadeIn = Math.Max(16, (int)Math.Round(sweepDuration / sweepOctaves * sampleRate));
int sweepFadeOut = Math.Max(16, (int)Math.Round(sweepDuration / (12.0 * sweepOctaves) * sampleRate));
for (int index = 0; index < stretchedLength; index++)
{
    double sourcePosition = index / injectedClockScale;
    if (sourcePosition < 0 || sourcePosition >= sweep.Length)
        continue;
    double timeRatio = sourcePosition / Math.Max(1, sweep.Length - 1);
    double phase = sweepPhaseScale * (Math.Pow(sweepRatio, timeRatio) - 1.0);
    double fade = sourcePosition < sweepFadeIn
        ? 0.5 - 0.5 * Math.Cos(Math.PI * sourcePosition / sweepFadeIn)
        : sourcePosition >= sweep.Length - sweepFadeOut
            ? 0.5 - 0.5 * Math.Cos(Math.PI * (sweep.Length - 1 - sourcePosition) / sweepFadeOut)
            : 1.0;
    driftedCapture[clockDelaySamples + index] = (float)(settings.Amplitude * fade * Math.Sin(phase));
}

StandardAcousticResult driftCorrected = StandardAcousticMeasurement.AnalyzeLogSweep(
    sweep, driftedCapture, settings);
if (!driftCorrected.ClockDriftCorrectionApplied
    || Math.Abs(driftCorrected.EstimatedClockDriftPpm - 400.0) > 120.0)
    throw new InvalidOperationException(
        $"Sweep clock correction failed: {driftCorrected.EstimatedClockDriftPpm:F1} ppm, applied={driftCorrected.ClockDriftCorrectionApplied}.");
double driftCorrectedSpread = driftCorrected.FrequencyResponse
    .Where(point => point.FrequencyHz >= 80 && point.FrequencyHz <= 12000)
    .Max(point => Math.Abs(point.NormalizedLevelDb));
if (driftCorrectedSpread > 1.0)
    throw new InvalidOperationException($"Clock-corrected response spread too high: {driftCorrectedSpread:F3} dB at {driftCorrected.EstimatedClockDriftPpm:F1} ppm.");
Console.WriteLine(
    $"Clock correction passed: injected +400.0 ppm, estimated {driftCorrected.EstimatedClockDriftPpm:+0.0;-0.0;0.0} ppm, fit {driftCorrected.ClockFitRmsSamples:F2} samples.");

// A gain change must survive deconvolution, impulse gating and display conversion.
var halfCapture = delayed.Select(value => value * 0.5f).ToArray();
var halfLevel = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, halfCapture, settings);
double expectedGainDelta = 20 * Math.Log10(0.5);
foreach (var pair in new[] { (flat.FrequencyResponse, halfLevel.FrequencyResponse), (flat.UngatedFrequencyResponse, halfLevel.UngatedFrequencyResponse) })
{
    foreach (var point in pair.Item1.Where(p => p.FrequencyHz >= 80 && p.FrequencyHz <= 12000))
    {
        var reduced = pair.Item2.Single(p => p.FrequencyHz == point.FrequencyHz);
        double delta = reduced.LevelDb - point.LevelDb;
        if (Math.Abs(delta - expectedGainDelta) > 0.03)
            throw new InvalidOperationException($"Absolute gain lost at {point.FrequencyHz}: {delta} dB.");
    }
}
var quieterSettings = settings with { Amplitude = settings.Amplitude * 0.5 };
float[] quieterSweep = StandardAcousticMeasurement.GenerateLogSweep(quieterSettings);
var quieterDelayed = new float[delayed.Length];
Array.Copy(quieterSweep, 0, quieterDelayed, 1200, quieterSweep.Length);
var quieterResult = StandardAcousticMeasurement.AnalyzeLogSweep(quieterSweep, quieterDelayed, quieterSettings);
var original1k = flat.FrequencyResponse.MinBy(p => Math.Abs(p.FrequencyHz - 1000))!;
var quieter1k = quieterResult.FrequencyResponse.Single(p => p.FrequencyHz == original1k.FrequencyHz);
double displayDelta = quieter1k.LevelDb + quieterResult.ExcitationRmsDbFs - original1k.LevelDb - flat.ExcitationRmsDbFs;
if (Math.Abs(displayDelta - expectedGainDelta) > 0.03)
    throw new InvalidOperationException($"Playback level disappeared from displayed dBFS: {displayDelta} dB.");
Console.WriteLine($"Absolute level checks passed: capture gain and playback gain retained ({displayDelta:F4} dB), gated and ungated.");
SweepHarmonicPoint flatHarmonicAt1k = flat.SweepHarmonics
    .Single(point => Math.Abs(point.FundamentalFrequencyHz - 1000.0) < 1e-9);
if (flatHarmonicAt1k.ThdPercent > 0.001)
    throw new InvalidOperationException($"Linear ESS harmonic floor is too high: {flatHarmonicAt1k.ThdPercent:F6}%.");

var lowDistortion = new float[sweep.Length + sampleRate / 10];
for (int index = 0; index < sweep.Length; index++)
{
    double sample = sweep[index];
    // With sweep amplitude 0.2, coefficient 0.00072 produces H2/A1 = 0.0072%.
    lowDistortion[index + sampleRate / 50] = (float)(sample + 0.00072 * sample * sample);
}
StandardAcousticResult lowDistortionResult = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, lowDistortion, settings);
SweepHarmonicPoint lowDistortionAt1k = lowDistortionResult.SweepHarmonics
    .Single(point => Math.Abs(point.FundamentalFrequencyHz - 1000.0) < 1e-9);
if (Math.Abs(100.0 * Math.Pow(10.0, lowDistortionAt1k.H2Dbc / 20.0) - 0.0072) > 0.0005)
    throw new InvalidOperationException($"Low-level ESS H2 mismatch: {lowDistortionAt1k.H2Dbc:F3} dBc.");
Console.WriteLine($"Low-level ESS H2 at 48 kHz: {100.0 * Math.Pow(10.0, lowDistortionAt1k.H2Dbc / 20.0):F6}%.");

var duplicateHarmonicTransfers = new Complex[10][];
for (int order = 1; order <= 9; order++)
{
    Complex[] transfer = lowDistortionResult.HarmonicTransferFunctions[order];
    duplicateHarmonicTransfers[order] = transfer.Length == 0
        ? Array.Empty<Complex>()
        : StandardAcousticMeasurement.AverageComplexTransfers(new[] { transfer, transfer }).Transfer;
}
SweepHarmonicPoint duplicateAverageAt1k = StandardAcousticMeasurement
    .BuildSweepHarmonicPointsFromTransfers(duplicateHarmonicTransfers, settings)
    .Single(point => Math.Abs(point.FundamentalFrequencyHz - 1000.0) < 1e-9);
double duplicateAverageH2Percent = 100.0 * Math.Pow(10.0, duplicateAverageAt1k.H2Dbc / 20.0);
if (Math.Abs(duplicateAverageH2Percent - 0.0072) > 0.0005)
    throw new InvalidOperationException($"Coherent harmonic pre-average changed the calibrated H2: {duplicateAverageH2Percent:F6}%.");

var settings44100 = settings with { SampleRate = 44100 };
float[] sweep44100 = StandardAcousticMeasurement.GenerateLogSweep(settings44100);
var lowDistortion44100 = new float[sweep44100.Length + 4410];
for (int index = 0; index < sweep44100.Length; index++)
{
    double sample = sweep44100[index];
    lowDistortion44100[index + 882] = (float)(sample + 0.00072 * sample * sample);
}
StandardAcousticResult lowDistortionResult44100 = StandardAcousticMeasurement.AnalyzeLogSweep(
    sweep44100,
    lowDistortion44100,
    settings44100);
SweepHarmonicPoint lowDistortionAt1k44100 = lowDistortionResult44100.SweepHarmonics
    .Single(point => Math.Abs(point.FundamentalFrequencyHz - 1000.0) < 1e-9);
double h2Percent44100 = 100.0 * Math.Pow(10.0, lowDistortionAt1k44100.H2Dbc / 20.0);
if (Math.Abs(h2Percent44100 - 0.0072) > 0.0005)
    throw new InvalidOperationException($"Low-level 44.1 kHz ESS H2 mismatch: {h2Percent44100:F6}%.");
Console.WriteLine($"Low-level ESS H2 at 44.1 kHz: {h2Percent44100:F6}%.");

var mistimedDistortion = new float[sweep.Length + sampleRate / 5];
int linearDelay = sampleRate / 50;
int nonlinearDelay = linearDelay + sampleRate / 200; // Deliberately 5 ms late.
for (int index = 0; index < sweep.Length; index++)
{
    double sample = sweep[index];
    mistimedDistortion[index + linearDelay] += (float)sample;
    mistimedDistortion[index + nonlinearDelay] += (float)(0.00072 * sample * sample);
}
StandardAcousticResult mistimedResult = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, mistimedDistortion, settings);
SweepHarmonicPoint? mistimedAt1k = mistimedResult.SweepHarmonics
    .FirstOrDefault(point => Math.Abs(point.FundamentalFrequencyHz - 1000.0) < 1e-9);
double delayedH2Percent = mistimedAt1k == null || !double.IsFinite(mistimedAt1k.H2Dbc)
    ? double.NaN
    : 100.0 * Math.Pow(10.0, mistimedAt1k.H2Dbc / 20.0);
if (!double.IsFinite(delayedH2Percent) || Math.Abs(delayedH2Percent - 0.0072) > 0.0005)
    throw new InvalidOperationException(
        $"ESS discarded or attenuated an H2 response that remained inside its Farina time window: {delayedH2Percent:F6}%.");

var frequencyShapedH2 = new float[sweep.Length + sampleRate / 10];
double previousSquared = 0.0;
for (int index = 0; index < sweep.Length; index++)
{
    double sample = sweep[index];
    double squared = sample * sample;
    frequencyShapedH2[index + sampleRate / 50] = (float)(sample + 0.5 * (squared - previousSquared));
    previousSquared = squared;
}
StandardAcousticResult shapedH2Result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, frequencyShapedH2, settings);
SweepHarmonicPoint shapedH2At1k = shapedH2Result.SweepHarmonics
    .Single(point => Math.Abs(point.FundamentalFrequencyHz - 1000.0) < 1e-9);
double expectedShapedH2Percent = 0.5 * settings.Amplitude
    * Math.Sin(2.0 * Math.PI * shapedH2At1k.FundamentalFrequencyHz / sampleRate) * 100.0;
double measuredShapedH2Percent = 100.0 * Math.Pow(10.0, shapedH2At1k.H2Dbc / 20.0);
if (Math.Abs(measuredShapedH2Percent - expectedShapedH2Percent) > expectedShapedH2Percent * 0.08)
    throw new InvalidOperationException(
        $"ESS H2 output-frequency mapping failed: expected {expectedShapedH2Percent:F4}%, actual {measuredShapedH2Percent:F4}%.");

var multipath = new float[sweep.Length + sampleRate];
for (int index = 0; index < sweep.Length; index++)
{
    multipath[index + sampleRate / 50] += sweep[index] * 0.25f;
    multipath[index + sampleRate / 20] -= sweep[index] * 0.50f;
}
StandardAcousticResult earliestArrival = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, multipath, settings);
if (Math.Abs(earliestArrival.DirectArrivalMs - 20.0) > 0.3 || earliestArrival.Polarity != "NORMAL")
    throw new InvalidOperationException($"Earliest direct arrival lost to stronger reflection: {earliestArrival.DirectArrivalMs:F3} ms, {earliestArrival.Polarity}, clock={earliestArrival.EstimatedClockDriftPpm:F1} ppm/{earliestArrival.ClockDriftCorrectionApplied}, fit={earliestArrival.ClockFitRmsSamples:F2}.");

var nonlinear = new float[sweep.Length + sampleRate / 10];
for (int index = 0; index < sweep.Length; index++)
    nonlinear[index + sampleRate / 50] = (float)(sweep[index] + 0.5 * sweep[index] * sweep[index]);
StandardAcousticResult nonlinearResult = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, nonlinear, settings);
SweepHarmonicPoint harmonicAt1k = nonlinearResult.SweepHarmonics.MinBy(point => Math.Abs(point.FundamentalFrequencyHz - 1000))!;
if (Math.Abs(harmonicAt1k.H2Dbc + 26.02) > 2.0 || harmonicAt1k.H3Dbc > -70)
    throw new InvalidOperationException($"ESS harmonic separation failed: H2={harmonicAt1k.H2Dbc:F2}, H3={harmonicAt1k.H3Dbc:F2} dBc.");
if (new[] { harmonicAt1k.H6Dbc, harmonicAt1k.H7Dbc, harmonicAt1k.H8Dbc, harmonicAt1k.H9Dbc }.Any(value => !double.IsFinite(value)))
    throw new InvalidOperationException("ESS sweep did not expose all available H6-H9 components at 1 kHz.");
SweepDistortionSummary sweepWide = SweepDistortionSummary.AggregateSweep(
    nonlinearResult.SweepHarmonics,
    nonlinearResult.SignalLevelDbFs,
    nonlinearResult.NoiseFloorDbFs,
    nonlinearResult.IsClipped,
    nonlinearResult.Validity,
    -60.0);
if (!sweepWide.IsValid || sweepWide.FrequencyPointCount < 2 || !double.IsFinite(sweepWide.ThdPercent))
    throw new InvalidOperationException($"Whole-sweep distortion aggregation failed: {sweepWide.Status}");
double summedOrderEnergy = Enumerable.Range(2, 8)
    .Select(order => sweepWide.HarmonicPercent[order] / 100.0)
    .Sum(ratio => ratio * ratio);
double reconstructedThd = 100.0 * Math.Sqrt(summedOrderEnergy);
if (Math.Abs(reconstructedThd - sweepWide.ThdPercent) > 1e-9)
    throw new InvalidOperationException($"Whole-sweep H2-H9 energy sum mismatch: {reconstructedThd:F9}% vs {sweepWide.ThdPercent:F9}%.");
var knownSweepPoints = new[]
{
    new SweepHarmonicPoint(100.0, -20.0, -40.0, double.NaN, double.NaN, double.NaN)
    {
        FundamentalLevelDb = 0.0
    },
    new SweepHarmonicPoint(1000.0, -6.020599913, double.NaN, double.NaN, double.NaN, double.NaN)
    {
        FundamentalLevelDb = -6.020599913
    }
};
SweepDistortionSummary knownSweepSummary = SweepDistortionSummary.AggregateSweep(
    knownSweepPoints, -20.0, -80.0, false, "DIAGNOSTIC", -60.0);
double expectedKnownSweepThd = 100.0 * Math.Sqrt((0.1 * 0.1 + 0.25 * 0.25 + 0.01 * 0.01) / (1.0 * 1.0 + 0.5 * 0.5));
if (Math.Abs(knownSweepSummary.ThdPercent - expectedKnownSweepThd) > 1e-6)
    throw new InvalidOperationException($"Whole-sweep amplitude weighting failed: {knownSweepSummary.ThdPercent:F9}% vs {expectedKnownSweepThd:F9}%.");
StandardAcousticResult preNoiseResult = StandardAcousticMeasurement.AnalyzeLogSweep(
    sweep, nonlinear, settings, measuredPreSweepNoiseFloorDbFs: -72.5);
if (Math.Abs(preNoiseResult.NoiseFloorDbFs + 72.5) > 1E-9)
    throw new InvalidOperationException("Measured pre-sweep noise floor was not retained.");

float[] fractionalDelayed = FractionalDelay(sweep, 1200.35, sweep.Length + 2400);
StandardAcousticResult fractional = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, fractionalDelayed, settings);
Complex[] normalizedFlat = StandardAcousticMeasurement.NormalizeComplexTransferAtFrequency(flat.AlignedTransferFunction, sampleRate);
Complex[] normalizedFractional = StandardAcousticMeasurement.NormalizeComplexTransferAtFrequency(fractional.AlignedTransferFunction, sampleRate);
ComplexTransferAverage coherentAverage = StandardAcousticMeasurement.AverageComplexTransfers(new[] { normalizedFlat, normalizedFractional });
double minimumVectorStrength = Enumerable.Range(0, coherentAverage.VectorStrength.Length / 2)
    .Where(bin => bin * sampleRate / (double)coherentAverage.VectorStrength.Length is >= 80 and <= 12000)
    .Select(bin => coherentAverage.VectorStrength[bin])
    .Min();
if (minimumVectorStrength < 0.95)
    throw new InvalidOperationException($"Fractional-delay alignment lost coherence: {minimumVectorStrength:F4}.");
IReadOnlyList<FrequencyResponsePoint> coherentResponse = StandardAcousticMeasurement.BuildFrequencyResponseFromAlignedTransfer(
    coherentAverage.Transfer, sampleRate, 30, 18000, 12);
if (coherentResponse.Count < 50 || coherentResponse.Any(point => !double.IsFinite(point.LevelDb)))
    throw new InvalidOperationException("Complex-vector averaged response is incomplete.");

var opposite = normalizedFlat.Select(value => -value).ToArray();
ComplexTransferAverage cancelledAverage = StandardAcousticMeasurement.AverageComplexTransfers(new[] { normalizedFlat, opposite });
int oneKhzBin = (int)Math.Round(1000.0 * cancelledAverage.VectorStrength.Length / sampleRate);
if (cancelledAverage.VectorStrength[oneKhzBin] > 1e-9)
    throw new InvalidOperationException("Opposite-phase transfer was not identified as incoherent.");

var phaseShifted = new Complex[normalizedFlat.Length];
for (int bin = 0; bin < phaseShifted.Length; bin++)
{
    int signedBin = bin <= phaseShifted.Length / 2 ? bin : bin - phaseShifted.Length;
    phaseShifted[bin] = normalizedFlat[bin] * Complex.FromPolarCoordinates(
        1.0, 0.20 - 2.0 * Math.PI * signedBin * 2.75 / phaseShifted.Length);
}
ComplexPhaseAlignment phaseAlignment = StandardAcousticMeasurement.AlignComplexTransferToReference(
    normalizedFlat, phaseShifted, sampleRate);
if (!phaseAlignment.IsValid || Math.Abs(phaseAlignment.ResidualDelaySamples - 2.75) > 0.05
    || phaseAlignment.FitRmsDegrees > 0.1)
    throw new InvalidOperationException($"Residual phase/delay lock failed: delay={phaseAlignment.ResidualDelaySamples:F3}, RMS={phaseAlignment.FitRmsDegrees:F3}°.");
ComplexTransferAverage lockedAverage = StandardAcousticMeasurement.AverageComplexTransfers(
    new[] { normalizedFlat, phaseAlignment.Transfer });
if (lockedAverage.VectorStrength[oneKhzBin] < 0.999)
    throw new InvalidOperationException("Phase-locked transfers did not average coherently.");

var subsonicProbe = new float[sampleRate * 2];
for (int index = 0; index < subsonicProbe.Length; index++)
{
    double time = index / (double)sampleRate;
    subsonicProbe[index] = (float)(0.2 * Math.Sin(2.0 * Math.PI * 22.0 * time)
        + 0.02 + 0.01 * index / subsonicProbe.Length);
}
float[] conditionedProbe = StandardAcousticMeasurement.ApplySubsonicConditioning(subsonicProbe, sampleRate, 10.0);
int settledStart = sampleRate;
double originalSettledMean = subsonicProbe.Skip(settledStart).Average(value => (double)value);
double original22Rms = Math.Sqrt(subsonicProbe.Skip(settledStart)
    .Average(value => Math.Pow(value - originalSettledMean, 2)));
double conditioned22Rms = Math.Sqrt(conditionedProbe.Skip(settledStart).Average(value => (double)value * value));
double attenuation22Db = 20.0 * Math.Log10(conditioned22Rms / original22Rms);
if (attenuation22Db < -0.35 || attenuation22Db > 0.05)
    throw new InvalidOperationException($"10 Hz subsonic conditioning biased 22 Hz by {attenuation22Db:F3} dB.");

var edgeResponse = new Dictionary<double, double>
{
    [20] = 0, [21] = 0, [22] = 12, [23] = 0, [24] = 0,
    [1000] = 1.25,
    [18000] = 0, [18500] = 0, [19000] = 12, [19500] = 0, [20000] = 0
};
Dictionary<double, double> smoothedEdges = StandardAcousticMeasurement.ApplyEdgeFractionalOctaveSmoothing(edgeResponse);
if (smoothedEdges[22] >= 10 || smoothedEdges[19000] >= 10 || Math.Abs(smoothedEdges[1000] - 1.25) > 1e-9)
    throw new InvalidOperationException("Edge-only 1/3-octave smoothing failed.");

var guardedSettings = settings with { StartFrequencyHz = 18, EndFrequencyHz = 22000 };
float[] guardedSweep = StandardAcousticMeasurement.GenerateLogSweep(guardedSettings);
if (guardedSweep.Length != sampleRate || guardedSweep.Any(value => !float.IsFinite(value)))
    throw new InvalidOperationException("18 Hz - 22 kHz guarded sweep generation failed.");

foreach (int inputRate in new[] { 44100, 48000 })
{
    const double playbackScale = 0.91875;
    var physicalSettings = guardedSettings with
    {
        SampleRate = inputRate,
        StartFrequencyHz = guardedSettings.StartFrequencyHz * playbackScale,
        EndFrequencyHz = guardedSettings.EndFrequencyHz * playbackScale
    };
    float[] physicalSweep = StandardAcousticMeasurement.GenerateLogSweep(physicalSettings);
    var recorded = new float[physicalSweep.Length + inputRate];
    Array.Copy(physicalSweep, 0, recorded, inputRate / 50, physicalSweep.Length);
    StandardAcousticResult scaledResult = StandardAcousticMeasurement.AnalyzeLogSweep(
        physicalSweep, recorded, physicalSettings);
    if (scaledResult.Validity == "INVALID")
        throw new InvalidOperationException($"Scaled sweep acquisition invalid at {inputRate} Hz.");
    double scaledSpread = scaledResult.FrequencyResponse
        .Where(point => point.FrequencyHz >= 80 * playbackScale
            && point.FrequencyHz <= 12000 * playbackScale)
        .Max(point => Math.Abs(point.NormalizedLevelDb));
    if (scaledSpread > 1.0)
        throw new InvalidOperationException($"Scaled sweep not flat at {inputRate} Hz: {scaledSpread:F3} dB.");
    Console.WriteLine($"Scaled log sweep verified at input {inputRate} Hz: mapped physical curve is flat.");
}

var silence = new float[delayed.Length];
foreach (float bad in new[] { float.NaN, float.PositiveInfinity })
{
    var corrupt = delayed.ToArray();
    corrupt[100] = bad;
    bool rejected = false;
    try { StandardAcousticMeasurement.AnalyzeLogSweep(sweep, corrupt, settings); }
    catch (ArgumentException) { rejected = true; }
    if (!rejected) throw new Exception("Nonfinite sweep accepted");
}
StandardAcousticResult noAudio = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, silence, settings);
if (noAudio.ImpulsePlot == null || !double.IsNaN(noAudio.ImpulsePlot.ClarityC20Db)
    || !double.IsNaN(noAudio.ImpulsePlot.CentreTimeSeconds))
    throw new InvalidOperationException("Invalid acquisition exposed numeric C20 / centre time.");
if (noAudio.Validity != "INVALID" || noAudio.Polarity != "UNKNOWN"
    || noAudio.Diagnostics.All(item => item.Code != "NO_AUDIO_CAPTURED"))
    throw new InvalidOperationException("No-audio acquisition feedback failed.");

var unrelatedNoise = new float[delayed.Length];
var noiseRandom = new Random(20260904);
for (int index = 0; index < unrelatedNoise.Length; index++)
    unrelatedNoise[index] = (float)(0.01 * (noiseRandom.NextDouble() * 2.0 - 1.0));
StandardAcousticResult noSweep = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, unrelatedNoise, settings);
if (noSweep.Polarity != "UNKNOWN" || double.IsFinite(noSweep.ClarityC50Db)
    || noSweep.DecayEstimates.Any(item => item.IsValid)
    || StandardAcousticMeasurement.CompareWithGolden(noSweep, flat).Status != "INVALID")
    throw new Exception("Invalid sweep leaked acoustic verdicts");
if (noSweep.Validity != "INVALID"
    || noSweep.Diagnostics.All(item => item.Code != "SWEEP_NOT_DETECTED"))
    throw new InvalidOperationException($"Uncorrelated-audio feedback failed: {noSweep.ImpulsePeakToBackgroundDb:F1} dB.");

var lateSweep = new float[sweep.Length + sampleRate * 2];
Array.Copy(sweep, 0, lateSweep, (int)(sampleRate * 1.6), sweep.Length);
StandardAcousticResult noDirectPath = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, lateSweep, settings);
if (noDirectPath.Validity != "INVALID" || noDirectPath.HasPlausibleDirectArrival
    || noDirectPath.Diagnostics.All(item => item.Code != "NO_DIRECT_ACOUSTIC_PATH"))
    throw new InvalidOperationException($"Implausible-arrival feedback failed: {noDirectPath.DirectArrivalMs:F1} ms.");

float[] postGainOverload = delayed.Select(value => value * 6.0f).ToArray();
StandardAcousticResult softwareGain = StandardAcousticMeasurement.AnalyzeLogSweep(
    sweep,
    postGainOverload,
    settings with { RecordingGain = 6.0 });
if (softwareGain.Validity != "INVALID"
    || softwareGain.Diagnostics.All(item => item.Code != "POST_GAIN_OVERLOAD")
    || softwareGain.Diagnostics.Any(item => item.Code == "INPUT_CLIPPING"))
    throw new InvalidOperationException("Software recording-gain overload was not distinguished from input clipping.");

var impulse = new float[sampleRate];
const int directSample = 400;
impulse[directSample] = 1.0f;
var random = new Random(1379);
const double expectedRt = 0.60;
for (int index = directSample + 1; index < impulse.Length; index++)
{
    double time = (index - directSample) / (double)sampleRate;
    double envelope = Math.Pow(10.0, -60.0 * time / expectedRt / 20.0);
    impulse[index] = (float)(0.20 * envelope * (random.NextDouble() * 2.0 - 1.0));
}
float[] reverberant = Convolve(sweep, impulse);
float reverberantPeak = reverberant.Max(value => Math.Abs(value));
if (reverberantPeak > 0.5f)
{
    double scale = 0.5 / reverberantPeak;
    for (int index = 0; index < reverberant.Length; index++) reverberant[index] = (float)(reverberant[index] * scale);
}
StandardAcousticResult decay = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, reverberant, settings);
DecayEstimate t20 = decay.DecayEstimates.Single(item => item.Name == "T20");
if (!t20.IsValid || Math.Abs(t20.Rt60Seconds - expectedRt) > 0.18)
    throw new InvalidOperationException($"T20 failed: {t20.Rt60Seconds:F3} s, R2={t20.RSquared:F3}.");
if (decay.Waterfall.Count == 0 || decay.ImpulseResponse.Count == 0 || decay.StepResponse.Count == 0)
    throw new InvalidOperationException("Time/frequency diagnostic views were not generated.");

IReadOnlyList<ImpedancePoint> impedance = StandardAcousticMeasurement.AnalyzeImpedance(
    new[] { new ImpedanceInputPoint(1000, 0.8, 0.1, 30) },
    1.0);
if (Math.Abs(impedance[0].MagnitudeOhm - 8.0) > 1e-6
    || Math.Abs(impedance[0].ResistanceOhm - 6.9282) > 0.001)
    throw new InvalidOperationException("Impedance calculation failed.");

MaximumLinearOutputResult maximum = StandardAcousticMeasurement.AssessMaximumLinearOutput(new[]
{
    new OutputLevelStep(-20, 90, 0.2, 1.0, 0.99),
    new OutputLevelStep(-15, 95, 0.5, 2.0, 0.97),
    new OutputLevelStep(-10, 98, 3.5, 12.0, 0.80)
});
if (!maximum.IsValid || Math.Abs(maximum.MaximumLinearSplDb - 95) > 1e-6)
    throw new InvalidOperationException("Maximum linear output assessment failed.");

IReadOnlyList<DirectivityPoint> directivity = StandardAcousticMeasurement.AnalyzeAxisymmetricDirectivity(new[]
{
    new DirectivityInput(0, flat.FrequencyResponse),
    new DirectivityInput(-30, flat.FrequencyResponse.Select(point => point with { NormalizedLevelDb = point.NormalizedLevelDb - 3 }).ToArray()),
    new DirectivityInput(30, flat.FrequencyResponse.Select(point => point with { NormalizedLevelDb = point.NormalizedLevelDb - 3 }).ToArray())
});
if (directivity.Count == 0 || directivity.Any(point => point.DirectivityIndexDb < 1.6 || point.DirectivityIndexDb > 1.9))
    throw new InvalidOperationException("Axisymmetric directivity calculation failed.");

SpeakerAbnormalityResult sameAsGolden = StandardAcousticMeasurement.CompareWithGolden(flat, flat);
if (sameAsGolden.Status != "PASS_DIAGNOSTIC")
    throw new InvalidOperationException("Golden comparison should pass identical data.");
StandardAcousticResult inverted = flat with { Polarity = "INVERTED" };
SpeakerAbnormalityResult invertedResult = StandardAcousticMeasurement.CompareWithGolden(inverted, flat);
if (invertedResult.Status != "FAIL" || invertedResult.Findings.All(item => item.Code != "POLARITY"))
    throw new InvalidOperationException("Golden polarity anomaly was not detected.");

float[] pinkSamples = CreatePinkNoise(sampleRate * 2, sampleRate);
PinkNoiseResult pink = StandardAcousticMeasurement.AnalyzePinkNoise(pinkSamples, sampleRate);
if (pink.Validity != "DIAGNOSTIC" || pink.Bands.Count < 20 || pink.BandLevelStandardDeviationDb > 1.2)
    throw new InvalidOperationException($"Pink-noise analysis failed: {pink.Validity}, spread={pink.BandLevelStandardDeviationDb:F3} dB.");
PinkNoiseComparison pinkSame = StandardAcousticMeasurement.ComparePinkNoiseWithGolden(pink, pink);
if (pinkSame.Status != "PASS_DIAGNOSTIC")
    throw new InvalidOperationException("Identical pink-noise golden comparison should pass.");

Console.WriteLine("Standard measurement checks passed: acquisition feedback (no audio, unrelated audio, missing direct path, software-gain overload), log-sweep, pink noise, transfer/IR, phase/group delay, RT60/T20, waterfall, time views, impedance, directivity, golden comparison and maximum-linear-output logic.");

static float[] Convolve(float[] left, float[] right)
{
    int length = 1;
    while (length < left.Length + right.Length - 1) length <<= 1;
    var a = new Complex[length];
    var b = new Complex[length];
    for (int index = 0; index < left.Length; index++) a[index] = left[index];
    for (int index = 0; index < right.Length; index++) b[index] = right[index];
    Fourier.Forward(a, FourierOptions.Matlab);
    Fourier.Forward(b, FourierOptions.Matlab);
    for (int index = 0; index < length; index++) a[index] *= b[index];
    Fourier.Inverse(a, FourierOptions.Matlab);
    var output = new float[left.Length + right.Length - 1];
    for (int index = 0; index < output.Length; index++) output[index] = (float)a[index].Real;
    return output;
}

static float[] FractionalDelay(float[] input, double delaySamples, int outputLength)
{
    var output = new float[outputLength];
    for (int index = 0; index < output.Length; index++)
    {
        double source = index - delaySamples;
        int lower = (int)Math.Floor(source);
        if (lower < 0 || lower + 1 >= input.Length) continue;
        double fraction = source - lower;
        output[index] = (float)(input[lower] * (1.0 - fraction) + input[lower + 1] * fraction);
    }
    return output;
}

static float[] CreatePinkNoise(int count, int sampleRate)
{
    int length = 1;
    while (length < count) length <<= 1;
    var spectrum = new Complex[length];
    var random = new Random(20260903);
    for (int bin = 1; bin < length / 2; bin++)
    {
        double frequency = bin * sampleRate / (double)length;
        double magnitude = 1.0 / Math.Sqrt(Math.Max(1.0, frequency));
        double phase = random.NextDouble() * 2.0 * Math.PI;
        spectrum[bin] = Complex.FromPolarCoordinates(magnitude, phase);
        spectrum[length - bin] = Complex.Conjugate(spectrum[bin]);
    }
    Fourier.Inverse(spectrum, FourierOptions.Matlab);
    double peak = spectrum.Take(count).Max(value => Math.Abs(value.Real));
    var output = new float[count];
    for (int index = 0; index < count; index++) output[index] = (float)(0.5 * spectrum[index].Real / peak);
    return output;
}
