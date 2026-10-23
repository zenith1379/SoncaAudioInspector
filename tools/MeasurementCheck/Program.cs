using SoncaAudioInspector;

const int sampleRate = 48000;
const int sampleCount = sampleRate;

var validEvidence = new MeasurementDiagnosisEvidence
{
    ResponseValid = true, DistortionValid = true, HasComparableReference = true,
    SignalDbFs = -30, NoiseDbFs = -90, SnrDb = 60,
    Response = new[] { 100.0, 1000.0, 10000.0 }.Select(f => new DiagnosisResponsePoint(f, -36, -30, 3)).ToArray()
};
string Describe(MeasurementDiagnosisEvidence e) => string.Join(" ", SpeakerMeasurementDiagnosis.DescribeMeasurement(e));
if (!Describe(validEvidence).Contains("yếu hơn line chuẩn khoảng 6.0 dB"))
    throw new InvalidOperationException("Uniform volume loss was hidden from diagnosis.");
var normalized = validEvidence with
{
    NormalizedAtOneKilohertz = true, MeasuredOneKilohertzDbFs = -36, ReferenceOneKilohertzDbFs = -30,
    Response = validEvidence.Response.Select(p => p with { MeasuredDbFs = 0, TargetDbFs = 0 }).ToArray()
};
if (!Describe(normalized).Contains("yếu hơn line chuẩn khoảng 6.0 dB")) throw new Exception("Normalized curves hid absolute volume loss.");
if (!Describe(normalized with { MeasuredOneKilohertzDbFs = -28 }).Contains("mạnh hơn line chuẩn khoảng 2.0 dB")) throw new Exception("Positive volume delta has wrong sign.");
if (!Describe(normalized with { MeasuredOneKilohertzDbFs = -30 }).Contains("tương đương")) throw new Exception("Equal levels were called weak/strong.");
if (Describe(normalized with { ReferenceOneKilohertzDbFs = double.NaN }).Contains("Loa yếu hơn")) throw new Exception("Missing absolute reference produced a volume verdict.");
foreach (var effect in new[] { (false, false, "Chưa phát hiện echo/reverb"), (true, false, "Có echo trong bản thu"),
    (false, true, "Có reverb trong bản thu"), (true, true, "Có reverb trong bản thu") })
{
    var evidence = validEvidence with { HasEffectEvidence = true, EffectClassificationReliable = true,
        EchoDetected = effect.Item1, ReverbDetected = effect.Item2, EchoDelayMs = 200 };
    if (!Describe(evidence).Contains(effect.Item3)) throw new Exception("Effect classification label mismatch.");
    var uncertain = evidence with { EffectClassificationReliable = false, ResponseValid = false, NoiseDbFs = -49 };
    if (effect.Item1 && !Describe(uncertain).Contains("Có echo trong bản thu")
        || effect.Item2 && !Describe(uncertain).Contains("Có reverb trong bản thu")) throw new Exception("A detected effect was hidden by another measurement's validity.");
    if ((!effect.Item1 || !effect.Item2) && Describe(uncertain).Contains("Chưa phát hiện")) throw new Exception("Uncertain absence was classified.");
    if (Describe(evidence with { Clipped = true }).Contains("Có echo") || Describe(evidence with { SignalTooWeak = true }).Contains("Có reverb"))
        throw new Exception("Clipped/silent capture produced an effect observation.");
    if (Describe(evidence).Contains("tham khảo") || Describe(evidence).Contains("chưa phán đoán") || Describe(evidence).Length > 420)
        throw new Exception("Summary is verbose or retained boilerplate.");
}
var inverted = validEvidence with { ReliableImpulse = true, Polarity = "INVERTED" };
if (Describe(inverted).Contains("đảo cực") || Describe(inverted).Contains("đấu ngược"))
    throw new InvalidOperationException("A single impulse sign was used to diagnose reversed wiring.");
foreach (var invalid in new[] { inverted with { ResponseValid = false }, inverted with { Clipped = true },
    inverted with { SignalTooWeak = true }, inverted with { NoiseDbFs = -49 }, inverted with { ReliableImpulse = false },
    inverted with { SnrDb = 10 } })
    if (Describe(invalid).Contains("đảo cực") || Describe(invalid).Contains("đấu ngược"))
        throw new InvalidOperationException("Unsupported polarity diagnosis from invalid or absent impulse.");
if (!Describe(validEvidence with { HasComparableReference = false }).Contains("Chưa có line chuẩn"))
    throw new InvalidOperationException("Missing reference was treated as a valid loudness verdict.");
if (Describe(validEvidence with { DistortionValid = false, Distortion = new[] { new DiagnosisDistortionPoint(1000, 9, 0.5) } }).Contains("phi tuyến"))
    throw new InvalidOperationException("Invalid THD blamed the speaker/amplifier.");
Console.WriteLine("Local diagnosis checks passed: volume loss, weak/noisy/clipped acquisition, no single-impulse polarity verdict, missing reference, invalid THD.");

static float[] Tone(double frequency, params (double Frequency, double Amplitude)[] additions)
{
    var samples = new float[sampleCount];
    for (int index = 0; index < samples.Length; index++)
    {
        double time = (double)index / sampleRate;
        double value = 0.70 * Math.Sin(2.0 * Math.PI * frequency * time);
        foreach (var addition in additions)
        {
            value += addition.Amplitude * Math.Sin(2.0 * Math.PI * addition.Frequency * time);
        }
        samples[index] = (float)value;
    }
    return samples;
}

static void Near(double actual, double expected, double tolerance, string label)
{
    if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance)
        throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
}

ToneQualityMetrics pure = AdvancedAudioMeasurement.AnalyzeTone(Tone(997.3), sampleRate, 1000);
if (!pure.IsValid) throw new InvalidOperationException("Pure tone was not locked.");
Near(pure.FundamentalFrequencyHz, 997.3, 0.05, "Frequency lock");
if (pure.ThdPercent > 0.01) throw new InvalidOperationException($"Pure-tone THD too high: {pure.ThdPercent}");

ToneQualityMetrics harmonic = AdvancedAudioMeasurement.AnalyzeTone(
    Tone(1000, (2000, 0.007), (3000, 0.0035)),
    sampleRate,
    1000);
Near(harmonic.ThdPercent, Math.Sqrt(0.007 * 0.007 + 0.0035 * 0.0035) / 0.70 * 100.0, 0.03, "THD");
Near(harmonic.SfdrDb, 40.0, 1.2, "SFDR includes largest harmonic spur");
var h2ToH9 = AdvancedAudioMeasurement.AnalyzeTone(
    Tone(1000, (2000, 0.007), (9000, 0.0035), (10000, 0.035)), sampleRate, 1000.0, maxHarmonic: 9);
Near(h2ToH9.ThdPercent,
    Math.Sqrt(0.007 * 0.007 + 0.0035 * 0.0035) / 0.70 * 100.0,
    0.04, "THD sums H2-H9 and excludes H10");
if (h2ToH9.Harmonics.Any(point => point.Order > 9))
    throw new InvalidOperationException("THD unexpectedly includes H10.");
if (h2ToH9.ThdNPercent <= h2ToH9.ThdPercent)
    throw new InvalidOperationException("THD+N must include residual outside H2-H9.");
foreach (double frequency in new[] { 80.0, 1000.0, 4000.0 })
{
    ToneQualityMetrics point = AdvancedAudioMeasurement.AnalyzeTone(
        Tone(frequency, (2 * frequency, 0.007)), sampleRate, frequency);
    Near(point.ThdPercent, 1.0, 0.03, $"Three-point THD {frequency:0} Hz");
    if (!point.IsValid || point.SpectrumFrequenciesHz.Length == 0
        || point.SpectrumFrequenciesHz.Length != point.SpectrumMagnitudes.Length)
        throw new InvalidOperationException($"AnalyzeTone spectrum missing at {frequency:0} Hz.");
}

float[] noisySamples = Tone(1000);
var random = new Random(42);
double targetNoiseRms = (0.70 / Math.Sqrt(2.0)) / 100.0; // SNR 40 dB
for (int index = 0; index < noisySamples.Length; index += 2)
{
    double radius = Math.Sqrt(-2.0 * Math.Log(Math.Max(random.NextDouble(), 1e-12)));
    double angle = 2.0 * Math.PI * random.NextDouble();
    noisySamples[index] += (float)(targetNoiseRms * radius * Math.Cos(angle) + 0.02);
    if (index + 1 < noisySamples.Length)
        noisySamples[index + 1] += (float)(targetNoiseRms * radius * Math.Sin(angle) + 0.02);
}
ToneQualityMetrics noisy = AdvancedAudioMeasurement.AnalyzeTone(noisySamples, sampleRate, 1000);
Near(noisy.SnrDb, 40.0, 0.4, "SNR");
Near(noisy.DcOffset, 0.02, 0.001, "DC offset");
Near(noisy.ThdNPercent, 1.0, 0.08, "THD+N");

RubBuzzMetrics harmonicOnly = AdvancedAudioMeasurement.AnalyzeRubBuzz(
    Tone(70, (140, 0.03), (210, 0.015)),
    sampleRate,
    70);
if (harmonicOnly.RubBuzzPercent > 0.02)
    throw new InvalidOperationException($"Normal harmonics leaked into Rub/Buzz: {harmonicOnly.RubBuzzPercent}");

RubBuzzMetrics buzzing = AdvancedAudioMeasurement.AnalyzeRubBuzz(
    Tone(70, (2500, 0.005)),
    sampleRate,
    70);
Near(buzzing.RubBuzzPercent, 0.005 / 0.70 * 100.0, 0.08, "Rub/Buzz residual");
Near(buzzing.ResidualPeakFrequencyHz, 2500, 2.0, "Rub/Buzz peak");

var transientLevels = new List<double>();
foreach (double position in new[] { 0.05, 0.50, 0.95 })
{
    float[] transient = Tone(80);
    int begin = (int)(position * sampleRate);
    int length = sampleRate / 200;
    for (int index = 0; index < length; index++)
        transient[begin + index] += (float)(0.03 * Math.Sin(2 * Math.PI * 2500 * index / sampleRate)
            * Math.Sin(Math.PI * index / (length - 1)));
    RubBuzzMetrics measured = AdvancedAudioMeasurement.AnalyzeRubBuzz(transient, sampleRate, 80, 500, 10000);
    if (!measured.IsValid || measured.TransientEvents.Count == 0)
        throw new InvalidOperationException($"Transient at {position:F2}s was not detected.");
    double expectedPeakDbc = 20.0 * Math.Log10(0.03 / (0.70 / Math.Sqrt(2.0)));
    if (Math.Abs(measured.TransientEvents.Max(item => item.PeakDbc) - expectedPeakDbc) > 2.0)
        throw new InvalidOperationException($"Transient peak at {position:F2}s was biased.");
    transientLevels.Add(measured.RubBuzzPercent);
}
if (transientLevels.Max() / transientLevels.Min() > 1.05)
    throw new InvalidOperationException("Rub/Buzz energy depends on transient position in the record.");

RtaSpectrumResult rta = AdvancedAudioMeasurement.AnalyzeRtaSpectrum(Tone(1000), sampleRate);
RtaBandPoint strongestBand = rta.Bands.MaxBy(point => point.LevelDbFs)!;
if (!rta.IsValid || strongestBand.CenterFrequencyHz < 900 || strongestBand.CenterFrequencyHz > 1100)
    throw new InvalidOperationException($"RTA band detection failed: {strongestBand.CenterFrequencyHz:F1} Hz.");

float[] imdSamples = Tone(
    60,
    (7000, 0.175),
    (6940, 0.0035),
    (7060, 0.0035));
ImdMeasurementResult imd = AdvancedAudioMeasurement.AnalyzeSmpteImd(imdSamples, sampleRate);
if (!imd.IsValid) throw new InvalidOperationException("SMPTE IMD analysis was invalid.");
Near(imd.ImdPercent, Math.Sqrt(2.0) * 0.0035 / 0.175 * 100.0, 0.20, "SMPTE IMD");

float[] clippedSamples = Tone(1000);
for (int index = 0; index < 100; index++) clippedSamples[index] = 1.0f;
ToneQualityMetrics clipped = AdvancedAudioMeasurement.AnalyzeTone(clippedSamples, sampleRate, 1000);
if (clipped.ClippedSamplePercent < 0.20)
    throw new InvalidOperationException("Clipped samples were not detected.");

NoiseSpectrumMetrics noise = ProductionMeasurement.AnalyzeNoise(Tone(50, (120, 0.01)), sampleRate, null);
if (noise.DominantHumHz != 50 || noise.CrestFactor <= 1)
    throw new InvalidOperationException("Noise/hum diagnostics failed.");

Console.WriteLine("Measurement checks passed: frequency lock, THD, THD+N, Rub/Buzz, RTA, SMPTE IMD, clipping and noise/hum.");

var timer = System.Diagnostics.Stopwatch.StartNew();
foreach (double f in new[] { 63.37, 997.3, 4007.3, 8013.7 })
{
    var measured = AdvancedAudioMeasurement.AnalyzeTone(Tone(f, (2 * f, 0.007)), sampleRate, Math.Round(f / 10) * 10);
    Console.WriteLine($"Offset tone {f}: f0={measured.FundamentalFrequencyHz:F5}, THD={measured.ThdPercent:F5}%, THDN={measured.ThdNPercent:F5}%");
    Near(measured.FundamentalFrequencyHz, f, 0.03, "Offset frequency");
    Near(measured.ThdPercent, 1, 0.03, "Offset harmonic");
}
Console.WriteLine($"Four tone analyses: {timer.ElapsedMilliseconds} ms");

var noiseOnly = Enumerable.Range(0, sampleRate).Select(_ => (float)((random.NextDouble() - 0.5) * 0.1)).ToArray();
if (AdvancedAudioMeasurement.AnalyzeTone(noiseOnly, sampleRate, 1000).IsValid)
    throw new Exception("Noise must not be accepted as the requested tone.");
var nonFinite = Tone(1000); nonFinite[123] = float.NaN;
if (AdvancedAudioMeasurement.AnalyzeTone(nonFinite, sampleRate, 1000).IsValid)
    throw new Exception("Non-finite capture accepted.");
if (AdvancedAudioMeasurement.AnalyzeRtaSpectrum(nonFinite, sampleRate).IsValid
    || AdvancedAudioMeasurement.AnalyzeSmpteImd(nonFinite, sampleRate).IsValid)
    throw new Exception("Non-finite RTA/IMD capture accepted.");
if (AdvancedAudioMeasurement.AnalyzeSmpteImd(noiseOnly, sampleRate).IsValid)
    throw new Exception("Noise without the requested SMPTE tones was accepted.");
var noHarmonics = AdvancedAudioMeasurement.AnalyzeTone(Tone(12000), sampleRate, 12000);
if (!double.IsNaN(noHarmonics.ThdPercent)
    || SpeakerMeasurementDiagnosis.ToneValidity(noHarmonics, 12000) != "INVALID_HARMONIC_BANDWIDTH")
    throw new Exception("Unavailable THD must not be reported as measured zero.");
if (SpeakerMeasurementDiagnosis.ToneValidity(clipped, 1000) != "INVALID_CLIPPING")
    throw new Exception("Clipping must block speaker diagnosis.");
Console.WriteLine("Validity checks passed: noise, NaN, harmonic bandwidth and clipping diagnosis.");

double[] trebleGrid = Enumerable.Range(0, 23).Select(i => Math.Round(4000 * Math.Pow(5, i / 22.0))).ToArray();
static float[] MultitoneCapture(double[] tones, int rate, double timeOffset, double clockRatio, double gain)
{
    return Enumerable.Range(0, rate * 4).Select(i =>
        (float)tones.Select((f, k) => gain * 0.01 * Math.Sin(2 * Math.PI * f * (i / (double)rate * clockRatio + timeOffset) + k * 0.27)).Sum()).ToArray();
}
foreach (int rate in new[] { 44100, 48000 })
{
    var reference = DspProcessor.CalculateMultitoneResponse(MultitoneCapture(trebleGrid, rate, 0, 1, 1), rate, trebleGrid);
    var repeat = DspProcessor.CalculateMultitoneResponse(MultitoneCapture(trebleGrid, rate, 0.317, 1.0002, 1), rate, trebleGrid);
    var attenuated = DspProcessor.CalculateMultitoneResponse(MultitoneCapture(trebleGrid, rate, 0.14, 0.9998, 0.5), rate, trebleGrid);
    double worst = trebleGrid.Max(f => Math.Abs(reference[f] - repeat[f]));
    if (worst > 0.05) throw new Exception($"Treble repeatability at {rate}: {worst} dB");
    foreach (double f in trebleGrid) Near(attenuated[f] - reference[f], 20 * Math.Log10(0.5), 0.05, "Real treble loss retained");
    Console.WriteLine($"Treble 23-tone repeatability ({rate} Hz, 200 ppm clock offset): max {worst:F5} dB; real -6.02 dB loss detected.");

    const double playbackScale = 0.91875;
    double[] physicalTones = trebleGrid.Select(frequency => frequency * playbackScale).ToArray();
    var scaledCapture = MultitoneCapture(physicalTones, rate, 0.11, 1, 1);
    var physicalLevels = DspProcessor.CalculateMultitoneResponse(scaledCapture, rate, physicalTones);
    double[] nominalCurve = trebleGrid.Select(frequency => physicalLevels[frequency * playbackScale]).ToArray();
    if (nominalCurve.Max() - nominalCurve.Min() > 0.1)
        throw new Exception($"Scaled multitone curve is not flat at input {rate} Hz.");
    foreach (double nominalFrequency in new[] { 80.0, 1000.0, 4000.0 })
    {
        double physicalFrequency = nominalFrequency * playbackScale;
        float[] toneSamples = Enumerable.Range(0, rate * 2)
            .Select(index => (float)(0.5 * Math.Sin(2 * Math.PI * physicalFrequency * index / rate)))
            .ToArray();
        ToneQualityMetrics scaledTone = AdvancedAudioMeasurement.AnalyzeTone(toneSamples, rate, physicalFrequency);
        if (!scaledTone.IsValid || scaledTone.ThdPercent > 0.05)
            throw new Exception($"Scaled THD tone invalid at input {rate} Hz / {nominalFrequency} Hz.");
        Near(scaledTone.FundamentalFrequencyHz, physicalFrequency, 0.1, "Scaled tone frequency lock");
    }
    Console.WriteLine($"Scaled FEQ/THD verified at input {rate} Hz: nominal grid maps to physical scale {playbackScale:F5}.");
}
try
{
    DspProcessor.CalculateMultitoneResponse(new float[48000], 24000, trebleGrid);
    throw new Exception("Unsupported treble bandwidth accepted.");
}
catch (ArgumentException) { Console.WriteLine("Insufficient treble sample rate rejected."); }
