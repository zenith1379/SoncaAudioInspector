using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record LogSweepSettings(
    int SampleRate = 48000,
    double StartFrequencyHz = 20,
    double EndFrequencyHz = 20000,
    double DurationSeconds = 5,
    double Amplitude = 0.20,
    int PointsPerOctave = 12,
    double DecayAnalysisSeconds = 3.0,
    double RecordingGain = 1.0,
    double ImpulseWindowLeftMs = 125.0,
    double ImpulseWindowRightMs = 500.0,
    double FrequencyDependentWindowCycles = 15.0,
    IReadOnlyList<double>? DistortionEvaluationFrequenciesHz = null,
    bool ApplyDistortionCalibration = false,
    bool UseHarmonicFrequencyAsDistortionReference = false,
    ImpulseEffectSettings? EffectsSettings = null,
    bool FrequencyDependentWindowing = false,
    ImpulseWindowKind ImpulseWindowKind = ImpulseWindowKind.Tukey25);

public sealed record FrequencyResponsePoint(
    double FrequencyHz,
    double LevelDb,
    double NormalizedLevelDb,
    double PhaseDegrees,
    double GroupDelayMs);

public sealed record DecayEstimate(
    string Name,
    double StartDb,
    double EndDb,
    double Rt60Seconds,
    double SlopeDbPerSecond,
    double RSquared,
    bool IsValid,
    string Status);

public sealed record DecayCurvePoint(double TimeSeconds, double LevelDb);

public sealed record WaterfallPoint(double TimeMs, double FrequencyHz, double LevelDb);

public sealed record TimeDomainPoint(double TimeSeconds, double Value);

public sealed record SweepHarmonicPoint(
    double FundamentalFrequencyHz,
    double H2Dbc,
    double H3Dbc,
    double H4Dbc,
    double H5Dbc,
    double ThdPercent)
{
    public double FundamentalLevelDb { get; init; } = double.NaN;
    public double H6Dbc { get; init; } = double.NaN;
    public double H7Dbc { get; init; } = double.NaN;
    public double H8Dbc { get; init; } = double.NaN;
    public double H9Dbc { get; init; } = double.NaN;

    public double GetHarmonicDbc(int order) => order switch
    {
        2 => H2Dbc,
        3 => H3Dbc,
        4 => H4Dbc,
        5 => H5Dbc,
        6 => H6Dbc,
        7 => H7Dbc,
        8 => H8Dbc,
        9 => H9Dbc,
        _ => double.NaN
    };
}

public sealed record RewDistortionPoint(
    double FrequencyHz,
    double ThdPercent,
    double H2Dbc,
    double H3Dbc,
    double H4Dbc,
    double H5Dbc);

public sealed record RewDistortionStats(
    double AverageThdPercent,
    double MaxThdPercent,
    double MaxThdFrequencyHz,
    string DominantHarmonic,
    double AverageH2Dbc,
    double AverageH3Dbc,
    double AverageH4Dbc,
    double AverageH5Dbc,
    IReadOnlyList<RewDistortionPoint> TablePoints);

public sealed record BandRt60Result(
    double FrequencyHz,
    double EdtSeconds,
    double T20Seconds,
    double T30Seconds,
    double RSquared,
    bool IsValid);

public sealed record PinkNoiseBandPoint(double CenterFrequencyHz, double LevelDb, double NormalizedLevelDb);

public sealed record PinkNoiseResult(
    int SampleRate,
    double SignalLevelDbFs,
    double PeakSample,
    double CrestFactor,
    bool IsClipped,
    double BandLevelStandardDeviationDb,
    IReadOnlyList<PinkNoiseBandPoint> Bands,
    string Validity,
    string Warning);

public sealed record PinkNoiseComparison(
    string Status,
    double FailedBandRatio,
    double MaximumDeviationDb,
    IReadOnlyList<AbnormalityFinding> Findings);

public sealed record MeasurementDiagnostic(
    string Code,
    string Severity,
    string Message,
    string Action);

public sealed record StandardAcousticResult(
    int SampleRate,
    int DirectArrivalSample,
    double DirectArrivalMs,
    double PeakSample,
    bool IsClipped,
    string Polarity,
    IReadOnlyList<TimeDomainPoint> ImpulseResponse,
    IReadOnlyList<TimeDomainPoint> StepResponse,
    IReadOnlyList<TimeDomainPoint> EnergyTimeCurve,
    IReadOnlyList<FrequencyResponsePoint> FrequencyResponse,
    IReadOnlyList<DecayCurvePoint> EnergyDecayCurve,
    IReadOnlyList<DecayEstimate> DecayEstimates,
    IReadOnlyList<WaterfallPoint> Waterfall,
    double ClarityC50Db,
    double ClarityC80Db,
    double DefinitionD50Percent,
    string Validity,
    string Warning)
{
    public double SignalLevelDbFs { get; init; } = -180;
    // RMS level of the generated sine sweep, used to turn transfer gain into
    // received tone-equivalent dBFS without anchoring the curve at any frequency.
    public double ExcitationRmsDbFs { get; init; } = double.NaN;
    public double NoiseFloorDbFs { get; init; } = -180;
    public double EstimatedSnrDb { get; init; }
    public double EstimatedPreGainPeak { get; init; }
    public double ImpulsePeakToBackgroundDb { get; init; }
    public double SweepCorrelation { get; init; }
    public bool HasPlausibleDirectArrival { get; init; }
    public IReadOnlyList<MeasurementDiagnostic> Diagnostics { get; init; } = Array.Empty<MeasurementDiagnostic>();
    public IReadOnlyList<FrequencyResponsePoint> UngatedFrequencyResponse { get; init; } = Array.Empty<FrequencyResponsePoint>();
    public IReadOnlyList<SweepHarmonicPoint> SweepHarmonics { get; init; } = Array.Empty<SweepHarmonicPoint>();
    // Each order is a complex transfer function whose time origin has been aligned to
    // its Farina impulse. Keeping these enables coherent multi-sweep pre-averaging.
    public IReadOnlyList<Complex[]> HarmonicTransferFunctions { get; init; } = Array.Empty<Complex[]>();
    public double ImpulseWindowLeftMs { get; init; }
    public double ImpulseWindowRightMs { get; init; }
    public ImpulseWindowKind ImpulseWindowKind { get; init; } = ImpulseWindowKind.HalfHann;
    public double GatedMinimumFrequencyHz { get; init; }
    public bool ClockDriftCorrectionApplied { get; init; }
    public double EstimatedClockDriftPpm { get; init; }
    public double ClockFitRmsSamples { get; init; }
    public bool FrequencyDependentWindowing { get; init; }
    public double FrequencyDependentWindowCycles { get; init; }
    // Full transfer function after removing the measured integer/fractional
    // transport delay. Keep this in the linear complex domain so repeated
    // sweeps can be coherently averaged before converting to dB.
    public Complex[] AlignedTransferFunction { get; init; } = Array.Empty<Complex>();
    public IReadOnlyList<BandRt60Result> OctaveBandRt60 { get; init; } = Array.Empty<BandRt60Result>();
    public RewDistortionStats? RewDistortion { get; init; }
    public ImpulseEffectResult? ImpulseEffects { get; init; }
    public ImpulsePlotData? ImpulsePlot { get; init; }
    public HighOrderHarmonicResult? HighOrderHarmonics { get; init; }
}

public sealed record ComplexTransferAverage(
    Complex[] Transfer,
    double[] VectorStrength);

public sealed record ComplexPhaseAlignment(
    Complex[] Transfer,
    double ResidualDelaySamples,
    double ConstantPhaseDegrees,
    double FitRmsDegrees,
    bool IsValid);

public sealed record ImpedanceInputPoint(
    double FrequencyHz,
    double DutVoltageRms,
    double ShuntVoltageRms,
    double PhaseDifferenceDegrees);

public sealed record ImpedancePoint(
    double FrequencyHz,
    double MagnitudeOhm,
    double PhaseDegrees,
    double ResistanceOhm,
    double ReactanceOhm);

public sealed record DirectivityInput(
    double AngleDegrees,
    IReadOnlyList<FrequencyResponsePoint> Response);

public sealed record DirectivityPoint(
    double FrequencyHz,
    double OnAxisDb,
    double MeanOffAxisDb,
    double DirectivityIndexDb);

public sealed record OutputLevelStep(
    double InputLevelDb,
    double OutputSplDb,
    double CompressionDb,
    double ThdPercent,
    double Coherence);

public sealed record MaximumLinearOutputResult(
    bool IsValid,
    double MaximumLinearSplDb,
    int AcceptedSteps,
    string Reason);

public sealed record AbnormalityFinding(string Code, string Severity, string Details);

public sealed record SpeakerAbnormalityResult(
    string Status,
    double FailedResponsePointRatio,
    double MaximumResponseDeviationDb,
    IReadOnlyList<AbnormalityFinding> Findings);

/// <summary>
/// Analyzer primitives for diagnostic loudspeaker measurements. The algorithms
/// produce evidence; compliance still requires calibrated hardware, prescribed
/// geometry, traceable levels and the licensed test standard named by the recipe.
/// </summary>
public static partial class StandardAcousticMeasurement
{
    private const double Tiny = 1e-18;
	private sealed record SweepClockEstimate(
		bool IsReliable,
		double SampleScale,
		double DriftPpm,
		double FitRmsSamples)
	{
		public bool IsCorrectionApplied => IsReliable && Math.Abs(DriftPpm) >= 20.0;
	}

    public static float[] ApplySubsonicConditioning(
        IReadOnlyList<float> samples,
        int sampleRate,
        double cutoffHz = 10.0)
    {
        if (samples is null) throw new ArgumentNullException(nameof(samples));
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!double.IsFinite(cutoffHz) || cutoffHz <= 0 || cutoffHz >= sampleRate * 0.1)
            throw new ArgumentOutOfRangeException(nameof(cutoffHz));
        if (samples.Count == 0) return Array.Empty<float>();

        // Remove constant/ramp drift first, then use a conservative 10 Hz
        // Butterworth HPF. A 25-30 Hz HPF would attenuate the wanted 22 Hz bin.
        int count = samples.Count;
        double meanX = (count - 1) / 2.0;
        double meanY = samples.Average(value => (double)value);
        double covariance = 0.0;
        double variance = 0.0;
        for (int index = 0; index < count; index++)
        {
            double centeredX = index - meanX;
            covariance += centeredX * (samples[index] - meanY);
            variance += centeredX * centeredX;
        }
        double slope = variance > Tiny ? covariance / variance : 0.0;

        double omega = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosine = Math.Cos(omega);
        double sine = Math.Sin(omega);
        double alpha = sine / Math.Sqrt(2.0);
        double a0 = 1.0 + alpha;
        double b0 = (1.0 + cosine) / (2.0 * a0);
        double b1 = -(1.0 + cosine) / a0;
        double b2 = b0;
        double a1 = -2.0 * cosine / a0;
        double a2 = (1.0 - alpha) / a0;
        var output = new float[count];
        double x1 = 0.0, x2 = 0.0, y1 = 0.0, y2 = 0.0;
        for (int index = 0; index < count; index++)
        {
            double input = samples[index] - (meanY + slope * (index - meanX));
            double filtered = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            output[index] = (float)filtered;
            x2 = x1;
            x1 = input;
            y2 = y1;
            y1 = filtered;
        }
        return output;
    }

    public static Dictionary<double, double> ApplyEdgeFractionalOctaveSmoothing(
        IReadOnlyDictionary<double, double> response,
        double fraction = 3.0,
        double lowEdgeHz = 35.0,
        double highEdgeHz = 18000.0)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));
        if (!double.IsFinite(fraction) || fraction <= 0) throw new ArgumentOutOfRangeException(nameof(fraction));
        var finite = response
            .Where(point => double.IsFinite(point.Key) && point.Key > 0 && double.IsFinite(point.Value))
            .OrderBy(point => point.Key)
            .ToArray();
        double halfBandRatio = Math.Pow(2.0, 1.0 / (2.0 * fraction));
        var result = new Dictionary<double, double>(finite.Length);
        foreach (KeyValuePair<double, double> point in finite)
        {
            if (point.Key >= lowEdgeHz && point.Key <= highEdgeHz)
            {
                result[point.Key] = point.Value;
                continue;
            }
            double lower = point.Key / halfBandRatio;
            double upper = point.Key * halfBandRatio;
            double[] bandPowers = finite
                .Where(candidate => candidate.Key >= lower && candidate.Key <= upper)
                .Select(candidate => Math.Pow(10.0, candidate.Value / 10.0))
                .ToArray();
            result[point.Key] = bandPowers.Length == 0
                ? point.Value
                : 10.0 * Math.Log10(bandPowers.Average() + Tiny);
        }
        return result;
    }

    public static float[] GenerateLogSweep(LogSweepSettings settings)
    {
        ValidateSettings(settings);
        int count = Math.Max(1024, (int)Math.Round(settings.DurationSeconds * settings.SampleRate));
        var samples = new float[count];
        double ratio = settings.EndFrequencyHz / settings.StartFrequencyHz;
        double duration = count / (double)settings.SampleRate;
        double logRatio = Math.Log(ratio);

        // REW-aligned phase parameter with exact zero-crossing continuity at both boundaries:
        // phase(t) = phaseScale * (ratio^(t/T) - 1.0)
        // At t=0, phase(0) = 0 -> sin(0) = 0.
        // At t=T, phase(T) = phaseScale * (ratio - 1.0) = 2*PI*K (exact integer multiple of 2*PI) -> sin(2*PI*K) = 0.
        double targetCycles = settings.StartFrequencyHz * duration / logRatio;
        int totalCycleCount = Math.Max(1, (int)Math.Round(targetCycles * (ratio - 1.0)));
        double phaseScale = 2.0 * Math.PI * totalCycleCount / (ratio - 1.0);

        // REW-aligned octave fade: 1 octave fade-in at start, 1/12 octave fade-out at end (half-Hann window)
        double octaves = logRatio / Math.Log(2.0);
        double fadeInDurn = duration / octaves;
        double fadeOutDurn = duration / (12.0 * octaves);
        int fadeInSamples = Math.Max(16, (int)Math.Round(fadeInDurn * settings.SampleRate));
        int fadeOutSamples = Math.Max(16, (int)Math.Round(fadeOutDurn * settings.SampleRate));

        for (int index = 0; index < count; index++)
        {
            double timeRatio = index / (double)Math.Max(1, count - 1);
            double phase = phaseScale * (Math.Pow(ratio, timeRatio) - 1.0);
            double fade = 1.0;
            if (index < fadeInSamples)
            {
                fade = 0.5 - 0.5 * Math.Cos(Math.PI * index / (double)fadeInSamples);
            }
            else if (index >= count - fadeOutSamples)
            {
                fade = 0.5 - 0.5 * Math.Cos(Math.PI * (count - 1 - index) / (double)fadeOutSamples);
            }
            samples[index] = (float)(settings.Amplitude * fade * Math.Sin(phase));
        }

        return samples;
    }

    public static StandardAcousticResult AnalyzeLogSweep(
        float[] excitation,
        float[] recorded,
        LogSweepSettings settings,
        MicrophoneCalibration? micCalib = null,
        double? measuredPreSweepNoiseFloorDbFs = null)
        => AnalyzeLogSweep(excitation, recorded, settings, micCalib, measuredPreSweepNoiseFloorDbFs, null);

    public static StandardAcousticResult AnalyzeLogSweep(
        float[] excitation,
        float[] recorded,
        LogSweepSettings settings,
        MicrophoneCalibration? micCalib,
        double? measuredPreSweepNoiseFloorDbFs,
        Action<string, double>? stageCompleted)
        => AnalyzeLogSweep(excitation, recorded, settings, micCalib, measuredPreSweepNoiseFloorDbFs, stageCompleted, CancellationToken.None);

    public static StandardAcousticResult AnalyzeLogSweep(
        float[] excitation,
        float[] recorded,
        LogSweepSettings settings,
        MicrophoneCalibration? micCalib,
        double? measuredPreSweepNoiseFloorDbFs,
        Action<string, double>? stageCompleted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        void Stage(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            stageCompleted?.Invoke(name, System.Diagnostics.Stopwatch.GetElapsedTime(stageStart, end).TotalMilliseconds);
            stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        ValidateSettings(settings);
        if (excitation is null || excitation.Length < 1024)
            throw new ArgumentException("Tín hiệu sweep không hợp lệ.", nameof(excitation));
        if (recorded is null || recorded.Length < 1024)
            throw new ArgumentException("Tín hiệu thu không hợp lệ.", nameof(recorded));
        if (excitation.Any(value => !float.IsFinite(value)) || recorded.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("Dữ liệu thu/phát chứa NaN hoặc Infinity; phải đo lại.");

        var fft = new SweepFourier();
        SweepClockEstimate clockEstimate = EstimateSweepClockCancellable(excitation, recorded, settings.SampleRate, fft, cancellationToken);
        if (clockEstimate.IsReliable && Math.Abs(clockEstimate.DriftPpm) >= 20.0)
            recorded = CorrectSweepClock(recorded, clockEstimate.SampleScale);
        Stage("clock");

        int fftSize = NextPowerOfTwo(excitation.Length + recorded.Length - 1);
        var inputSpectrum = new Complex[fftSize];
        var outputSpectrum = new Complex[fftSize];
        for (int index = 0; index < excitation.Length; index++) inputSpectrum[index] = excitation[index];
        for (int index = 0; index < recorded.Length; index++) outputSpectrum[index] = recorded[index];
        fft.Forward(inputSpectrum, excitation.Length);
        fft.Forward(outputSpectrum, recorded.Length);

        // ── Wiener deconvolution ────────────────────────────────────────────────
        // Dùng cho transfer function / frequency response (ổn định, ít noise).
        var transfer = new Complex[fftSize];
        var sweepCrossCorrelation = new Complex[fftSize];
        double maxInputPower = inputSpectrum.Max(value => { double magnitude = value.Magnitude; return magnitude * magnitude; });
        double regularization = Math.Max(Tiny, maxInputPower * 1e-10);
        for (int bin = 0; bin < fftSize; bin++)
        {
            double inputMagnitude = inputSpectrum[bin].Magnitude;
            double inputPower = inputMagnitude * inputMagnitude;
            transfer[bin] = outputSpectrum[bin] * Complex.Conjugate(inputSpectrum[bin])
                / (inputPower + regularization);
            sweepCrossCorrelation[bin] = outputSpectrum[bin] * Complex.Conjugate(inputSpectrum[bin]);
        }
        fft.Inverse(sweepCrossCorrelation);

        // Bandpass filter the transfer function to suppress out-of-band division noise (outside sweep range)
        double fMin = settings.StartFrequencyHz * 0.7;
        double fMax = Math.Min(settings.SampleRate * 0.49, settings.EndFrequencyHz * 1.15);
        double df = (double)settings.SampleRate / fftSize;
        for (int bin = 0; bin < fftSize; bin++)
        {
            double freq = bin <= fftSize / 2 ? bin * df : (fftSize - bin) * df;
            double window = 1.0;
            if (freq < fMin)
            {
                window = 0.5 * (1.0 - Math.Cos(Math.PI * Math.Max(0, freq) / fMin));
            }
            else if (freq > fMax)
            {
                double rollOff = Math.Min(settings.SampleRate * 0.5, fMax * 1.2);
                if (freq >= rollOff) window = 0.0;
                else window = 0.5 * (1.0 + Math.Cos(Math.PI * (freq - fMax) / (rollOff - fMax)));
            }
            transfer[bin] *= window;
        }

        var impulseComplex = transfer.ToArray();
        fft.Inverse(impulseComplex);
        Stage("transfer");

        // ── Farina inverse filter deconvolution ───────────────────────────────
        // Theo Farina (2000): inverse filter = time-reversed sweep × e^(-t/L).
        // Hài bậc n xuất hiện trước direct sound ở offset duration*ln(n)/ln(f2/f1)*fs.
        // Dùng riêng cho BuildSweepHarmonicResponse — KHÔNG dùng cho frequency response.
        double logRatioFarina = Math.Log(settings.EndFrequencyHz / settings.StartFrequencyHz);
        double L = settings.DurationSeconds / logRatioFarina;          // time-scale constant
        int exLen = excitation.Length;
        var invFilter = new Complex[fftSize];
        for (int i = 0; i < exLen; i++)
        {
            // time-reversed index: excitation[exLen-1-i]
            // amplitude modulation: e^(-t/L) where t = i/SampleRate
            double t = i / (double)settings.SampleRate;
            invFilter[i] = excitation[exLen - 1 - i] * Math.Exp(-t / L);
        }
        fft.Forward(invFilter, exLen);
        // Give the inverse filter unity linear gain. Without this factor the
        // reported fundamental level grows with sweep length and is not dBFS.
        int inverseReferenceBin = Math.Clamp((int)Math.Round(
            Math.Clamp(1000.0, settings.StartFrequencyHz, settings.EndFrequencyHz)
            * fftSize / settings.SampleRate), 1, fftSize / 2 - 1);
        double inverseGain = Math.Max(Tiny, (inputSpectrum[inverseReferenceBin] * invFilter[inverseReferenceBin]).Magnitude);
        for (int bin = 0; bin < fftSize; bin++) invFilter[bin] /= inverseGain;
        // outputSpectrum is still the unmodified FFT of the clock-corrected capture.
        // Reuse it for Farina instead of allocating and transforming the same data again.
        var farinaImpulseSpectrum = new Complex[fftSize];
        for (int bin = 0; bin < fftSize; bin++)
            farinaImpulseSpectrum[bin] = outputSpectrum[bin] * invFilter[bin];
        fft.Inverse(farinaImpulseSpectrum);
        Stage("farina");
        // farinaImpulseSpectrum là impulse response Farina — harmonic nằm ở chỉ số âm (wrap-around cuối mảng)
        int usefulLength = Math.Min(recorded.Length, fftSize / 2);
        // Direct sound in an acoustic chamber or room arrives within 0 to 1500 ms (including sound card buffer latency).
        // Searching within this physical window prevents late room decay, soundcard stop clicks, or harmonic wrap-around artifacts from being mistaken as the direct impulse.
        int plausibleSearchLimit = usefulLength;
        int strongestSample = 0;
        double strongestPeak = 0;
        for (int index = 0; index < plausibleSearchLimit; index++)
        {
            double magnitude = Math.Abs(impulseComplex[index].Real);
            if (magnitude <= strongestPeak) continue;
            strongestPeak = magnitude;
            strongestSample = index;
        }
        double robustImpulseBackground = EstimateRobustImpulseBackground(impulseComplex, plausibleSearchLimit);
        double arrivalThreshold = Math.Max(strongestPeak * 0.20, robustImpulseBackground * 8.0);
        int directSample = FindEarliestImpulsePeak(impulseComplex, plausibleSearchLimit, arrivalThreshold, strongestSample);
		int correlationDelay = FindEarliestSweepCorrelationDelay(
			sweepCrossCorrelation, excitation, recorded, settings.SampleRate,
			measuredPreSweepNoiseFloorDbFs.HasValue ? 2500.0 : 1500.0, cancellationToken);
		double impulseDelayCorrelation = CalculateNormalizedSweepCorrelation(excitation, recorded, directSample);
		double correlationDelayCorrelation = CalculateNormalizedSweepCorrelation(excitation, recorded, correlationDelay);
		if (correlationDelayCorrelation >= 0.02
			&& correlationDelayCorrelation > impulseDelayCorrelation * 1.10
			&& correlationDelay < usefulLength)
		{
			directSample = FindStrongestImpulseNear(
				impulseComplex, correlationDelay,
				Math.Max(32, settings.SampleRate * 3 / 1000), usefulLength);
		}
		double peak = Math.Abs(impulseComplex[directSample].Real);

        double recordedPeak = recorded.Max(value => Math.Abs((double)value));
        double recordingGain = Math.Max(0.01, settings.RecordingGain);
        double estimatedPreGainPeak = recordedPeak / recordingGain;
        bool postGainOverload = recordedPeak >= 0.995 && estimatedPreGainPeak < 0.995;
        bool inputClipping = estimatedPreGainPeak >= 0.995;
        bool clipped = inputClipping || postGainOverload;
        (double signalLevelDbFs, double noiseFloorDbFs, double estimatedSnrDb) =
            CalculateAcquisitionLevels(recorded, settings.SampleRate);
        // Estimate the background from samples after the known excitation, not
        // from quiet frequency regions of the sweep itself.
        int postSweepSamples = recorded.Length - directSample - excitation.Length;
        if (postSweepSamples >= settings.SampleRate / 100)
        {
            int noiseCount = Math.Min(postSweepSamples, settings.SampleRate / 10);
            double noisePower = recorded.Skip(recorded.Length - noiseCount)
                .Average(value => (double)value * value);
            noiseFloorDbFs = 10 * Math.Log10(noisePower + Tiny);
            estimatedSnrDb = Math.Max(0, signalLevelDbFs - noiseFloorDbFs);
        }
        if (measuredPreSweepNoiseFloorDbFs.HasValue
            && double.IsFinite(measuredPreSweepNoiseFloorDbFs.Value))
        {
            // REW captures the noise floor before the measurement starts. Prefer
            // that real silent-window measurement over a tail estimate when the
            // caller supplied a pre-sweep capture.
            noiseFloorDbFs = measuredPreSweepNoiseFloorDbFs.Value;
            estimatedSnrDb = Math.Max(0, signalLevelDbFs - noiseFloorDbFs);
        }
        double impulseBackgroundRms = CalculateImpulseBackgroundRms(
            impulseComplex,
            usefulLength,
            directSample,
            Math.Max(32, settings.SampleRate / 100));
        double impulsePeakToBackgroundDb = 20.0 * Math.Log10((peak + Tiny) / (impulseBackgroundRms + Tiny));
        double fractionalPeakOffset = EstimateFractionalPeakOffset(impulseComplex, directSample, usefulLength);
        double directSamplePrecise = directSample + fractionalPeakOffset;
        double directArrivalMs = directSamplePrecise * 1000.0 / settings.SampleRate;
		double sweepCorrelation = Math.Max(impulseDelayCorrelation, correlationDelayCorrelation);
		double maximumPlausibleDirectArrivalMs = measuredPreSweepNoiseFloorDbFs.HasValue ? 2500.0 : 1500.0;
        var diagnostics = new List<MeasurementDiagnostic>();
		if (clockEstimate.IsReliable)
		{
			diagnostics.Add(new MeasurementDiagnostic(
				clockEstimate.IsCorrectionApplied ? "CLOCK_DRIFT_CORRECTED" : "CLOCK_DRIFT_OK",
				"INFO",
				$"Sai lệch clock phát/thu ước tính {clockEstimate.DriftPpm:+0.0;-0.0;0.0} ppm (fit RMS {clockEstimate.FitRmsSamples:F2} mẫu).",
				clockEstimate.IsCorrectionApplied
					? "Đã resample bản thu về clock của sweep trước khi deconvolution."
					: "Sai lệch nhỏ, không cần resample."));
		}
		else
		{
			diagnostics.Add(new MeasurementDiagnostic(
				"CLOCK_DRIFT_UNRESOLVED", "WARN",
				"Không đủ tương quan để ước tính chắc chắn sai lệch clock phát/thu.",
				"Giữ kết quả biên độ nhưng không dùng phép đo này để so pha/thời gian tuyệt đối."));
		}
        if (postSweepSamples < settings.SampleRate / 100)
            diagnostics.Add(new MeasurementDiagnostic("INCOMPLETE_SWEEP", "INVALID",
                "Thiếu phần cuối sweep hoặc đoạn thu nền sau sweep.", "Kiểm tra thiết bị, tăng thời gian thu và đo lại."));

        if (postGainOverload)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "POST_GAIN_OVERLOAD",
                "INVALID",
                $"Recording Gain {settings.RecordingGain * 100.0:F0}% làm peak sau gain đạt {recordedPeak:F3}. Tín hiệu tiền khuếch đại vẫn an toàn ({estimatedPreGainPeak:F3}).",
                "Nên giảm Recording Gain về 100% hoặc thấp hơn để tránh méo số."));
        }
        if (inputClipping)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "INPUT_CLIPPING",
                "INVALID",
                $"Peak ước tính trước gain là {estimatedPreGainPeak:F3}, ngõ thu có dấu hiệu chạm đỉnh (clipping).",
                "Nên giảm mức phát loa hoặc Windows input level một chút để đồ thị đạt độ chính xác tối đa."));
        }
        bool captureIsSilent = recordedPeak < 1e-5 || signalLevelDbFs < -105.0;
        if (captureIsSilent)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "NO_AUDIO_CAPTURED",
                "INVALID",
                $"Ngõ thu gần như im lặng: RMS {signalLevelDbFs:F1} dBFS, peak {20.0 * Math.Log10(recordedPeak + Tiny):F1} dBFS.",
                "Kiểm tra đúng mic/ngõ thu trong buồng, mute, dây, quyền microphone của Windows và đồng hồ mức input."));
        }

        // Robust sweep detection for chamber and real speaker environments:
        // When played through a physical speaker, acoustic phase shifts degrade raw dot-product correlation.
        // Therefore, the impulse peak-to-background ratio is the primary detector.
        // Peak prominence alone also accepts unrelated noise after deconvolution.
        // Require evidence tied to the known excitation as well as an impulse peak.
        // Long acoustic sweeps can lose raw sample-for-sample correlation when the
        // playback and capture clocks drift, even though deconvolution produces a
        // very clear impulse. Accept that strong-evidence case, while stationary
        // unrelated noise remains rejected because it has no capture SNR.
        bool correlatedSweep = sweepCorrelation >= 0.015 && estimatedSnrDb >= 10.0;
        bool prominentSweep = impulsePeakToBackgroundDb >= 20.0 && estimatedSnrDb >= 12.0;
        bool sweepDetected = !captureIsSilent && peak >= 1e-8 && (correlatedSweep || prominentSweep);
        if (!captureIsSilent && !sweepDetected)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "SWEEP_NOT_DETECTED",
                "INVALID",
                $"Có tín hiệu ở mic nhưng không nhận ra log-sweep (tương quan {sweepCorrelation:F3}, độ nổi impulse {impulsePeakToBackgroundDb:F1} dB).",
                "Kiểm tra loa có thật sự phát trong chamber, đúng ngõ phát, route/mute và bảo đảm mic nghe trực tiếp được loa."));
        }

		bool arrivalInRange = directArrivalMs <= maximumPlausibleDirectArrivalMs;
        if (!captureIsSilent && sweepDetected && !arrivalInRange)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "NO_DIRECT_ACOUSTIC_PATH",
                "INVALID",
                $"Đỉnh ứng viên xuất hiện ở {directArrivalMs:F1} ms (độ trễ soundcard/đường truyền khá cao).",
                "Không dùng kết quả này. Kiểm tra đường phát/thu và độ trễ thiết bị rồi đo lại."));
        }
        if (!captureIsSilent && estimatedSnrDb < 6.0)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "LOW_CAPTURE_SNR",
                "INVALID",
                $"SNR thu ước tính chỉ {estimatedSnrDb:F1} dB; nhiễu/rò âm đang gần mức tín hiệu.",
                "Tăng tín hiệu loa một cách an toàn hoặc giảm noise/gain, nhưng luôn giữ peak dưới 0,995."));
        }

        if (!captureIsSilent && sweepDetected && signalLevelDbFs < -55.0 && estimatedSnrDb >= 12.0)
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "LOW_LEVEL_CORRELATED_SWEEP",
                "WARN",
                $"Sweep được nhận diện và vẫn có SNR {estimatedSnrDb:F1} dB, nhưng mức RMS còn thấp ({signalLevelDbFs:F1} dBFS).",
                "Vẫn hiển thị đáp tuyến; chỉ tăng mức phát/độ nhạy nếu cần cải thiện Distortion và luôn tránh clipping."));
        }

        bool hasPlausibleDirectArrival = !captureIsSilent && sweepDetected && arrivalInRange
            && !diagnostics.Any(item => item.Severity == "INVALID");
        Stage("arrival-validity");
        // The deconvolution is circular. A direct arrival earlier than 125 ms
        // must not truncate the requested negative-time window.
        int preRoll = Math.Min(ImpulsePlotAnalysis.SampleCount(settings.ImpulseWindowLeftMs, settings.SampleRate), fftSize - 1);
        int tailCount = Math.Min(
            Math.Min(fftSize, usefulLength - directSample + preRoll),
            Math.Max(1024, (int)Math.Round(settings.DecayAnalysisSeconds * settings.SampleRate) + preRoll));
        var alignedImpulse = new double[Math.Max(0, tailCount)];
        for (int index = 0; index < alignedImpulse.Length; index++)
            alignedImpulse[index] = impulseComplex[Mod(directSample - preRoll + index, fftSize)].Real;

        double[] measurementImpulse = alignedImpulse.ToArray();
        double normalize = alignedImpulse.Select(Math.Abs).DefaultIfEmpty(0).Max();
        if (normalize > Tiny)
            for (int index = 0; index < alignedImpulse.Length; index++) alignedImpulse[index] /= normalize;

        IReadOnlyList<DecayCurvePoint> decayCurve = BuildEnergyDecayCurve(alignedImpulse, settings.SampleRate, preRoll);
        (IReadOnlyList<TimeDomainPoint> impulseResponse,
            IReadOnlyList<TimeDomainPoint> stepResponse,
            IReadOnlyList<TimeDomainPoint> energyTimeCurve) = BuildTimeDomainViews(alignedImpulse, settings.SampleRate, preRoll);
        // The display also retains pre-arrival samples outside the analysis window.
        int displayPostCount = alignedImpulse.Length - preRoll;
        int displayPreCount = Math.Min(settings.SampleRate, fftSize - displayPostCount);
        var displayImpulse = new double[displayPreCount + displayPostCount];
        for (int index = 0; index < displayImpulse.Length; index++)
            displayImpulse[index] = impulseComplex[Mod(directSample - displayPreCount + index, fftSize)].Real;
        ImpulsePlotData impulsePlot = ImpulsePlotAnalysis.Build(displayImpulse, settings.SampleRate, displayPreCount);
        var decayEstimates = new List<DecayEstimate>
        {
            FitDecay(decayCurve, "EDT", 0, -10),
            FitDecay(decayCurve, "T20", -5, -25),
            FitDecay(decayCurve, "T30", -5, -35)
        };
        Stage("impulse-views");
        Complex[] alignedTransfer = RemoveTransferDelay(transfer, directSamplePrecise);
        IReadOnlyList<FrequencyResponsePoint> ungatedResponse = BuildFrequencyResponse(
            alignedTransfer,
            settings.SampleRate,
            0,
            settings.StartFrequencyHz,
            settings.EndFrequencyHz,
            settings.PointsPerOctave,
            micCalib);
		double availableRightMs = Math.Max(0.1,
			(measurementImpulse.Length - preRoll - 1) * 1000.0 / settings.SampleRate);
        double actualLeftMs = preRoll * 1000.0 / settings.SampleRate;
        double adaptiveMaximumRightMs = Math.Min(ImpulsePlotAnalysis.SampleCount(settings.ImpulseWindowRightMs,
            settings.SampleRate) * 1000.0 / settings.SampleRate, availableRightMs);
        double gatedMinimumFrequencyHz = settings.StartFrequencyHz;
        IReadOnlyList<FrequencyResponsePoint> response = BuildFrequencyDependentWindowResponse(
            measurementImpulse, settings.SampleRate, preRoll,
            settings.StartFrequencyHz, settings.EndFrequencyHz, settings.PointsPerOctave,
			micCalib, actualLeftMs, adaptiveMaximumRightMs,
			settings.FrequencyDependentWindowCycles, settings.FrequencyDependentWindowing, settings.ImpulseWindowKind);
		var ungatedByFrequency = ungatedResponse.ToDictionary(point => point.FrequencyHz);
		var gateDeltas = response
			.Where(point => point.FrequencyHz >= 200.0 && point.FrequencyHz <= 10000.0)
			.Select(point =>
			{
				FrequencyResponsePoint nearestUngated = ungatedByFrequency.TryGetValue(point.FrequencyHz, out var exact)
                    ? exact : ungatedResponse
					.OrderBy(candidate => Math.Abs(Math.Log(candidate.FrequencyHz / point.FrequencyHz)))
					.First();
				return Math.Abs(point.NormalizedLevelDb - nearestUngated.NormalizedLevelDb);
			})
			.OrderBy(value => value)
			.ToArray();
		if (gateDeltas.Length > 0 && gateDeltas[gateDeltas.Length / 2] > 6.0)
		{
			diagnostics.Add(new MeasurementDiagnostic(
				"FREQUENCY_WINDOW_EFFECT",
				"WARN",
				$"Đáp tuyến qua cửa sổ IR lệch transfer ungated trung vị {gateDeltas[gateDeltas.Length / 2]:F1} dB.",
				"Kiểm tra direct impulse, phản xạ và cấu hình cửa sổ IR trước khi diễn giải đáp tuyến."));
		}
        // Tìm đỉnh direct sound trong Farina impulse (nửa đầu mảng — harmonic nằm ở wrap cuối).
        Stage("frequency-response");
        int farinaSearchLimit = Math.Min(fftSize / 2, recorded.Length);
        int farinaDirectSample = 0;
        double farinaPeak = 0;
        for (int i = 0; i < farinaSearchLimit; i++)
        {
            double mag = Math.Abs(farinaImpulseSpectrum[i].Real);
            if (mag > farinaPeak) { farinaPeak = mag; farinaDirectSample = i; }
        }
        IReadOnlyList<SweepHarmonicPoint> sweepHarmonics = BuildSweepHarmonicResponse(
            farinaImpulseSpectrum, (double)farinaDirectSample, settings, micCalib, fft, out Complex[][] harmonicTransferFunctions);
		SweepHarmonicPoint? referenceDistortion = sweepHarmonics
			.OrderBy(point => Math.Abs(Math.Log(point.FundamentalFrequencyHz / 1000.0)))
			.FirstOrDefault();
		if (referenceDistortion == null
			|| !double.IsFinite(referenceDistortion.ThdPercent)
			|| referenceDistortion.ThdPercent > 100.0)
		{
			diagnostics.Add(new MeasurementDiagnostic(
				"SWEEP_DISTORTION_UNRELIABLE",
				"WARN",
				"Không tách được H2-H9 tin cậy từ impulse của lần sweep này; đã bỏ dữ liệu Distortion thay vì vẽ số sai.",
				"Tăng mức tín hiệu Windows/phần cứng nếu cần nhưng giữ dưới clipping, rồi đo lại."));
			sweepHarmonics = Array.Empty<SweepHarmonicPoint>();
			harmonicTransferFunctions = Array.Empty<Complex[]>();
		}
        IReadOnlyList<WaterfallPoint> waterfall = BuildWaterfall(
            alignedImpulse,
            settings.SampleRate,
            preRoll,
            settings.StartFrequencyHz,
            settings.EndFrequencyHz);
        Stage("harmonics-waterfall");

        (double c50, double d50) = CalculateClarity(alignedImpulse, settings.SampleRate, preRoll, 0.050);
        (double c80, _) = CalculateClarity(alignedImpulse, settings.SampleRate, preRoll, 0.080);
        bool acquisitionInvalid = diagnostics.Any(item => item.Severity == "INVALID");
        if (!acquisitionInvalid && decayEstimates.All(item => !item.IsValid))
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "RT60_UNRELIABLE",
                "WARN",
                "Chưa đủ dữ liệu đáng tin cậy để tính RT60; không suy ra buồng có độ vang cực thấp.",
                "Kiểm tra mức nền, thời gian thu và đường suy giảm. Không dùng RT60 để kết luận."));
        }

        string polarity = hasPlausibleDirectArrival && alignedImpulse.Length > preRoll
            ? alignedImpulse[preRoll] < 0 ? "INVERTED" : "NORMAL"
            : "UNKNOWN";
        if (!acquisitionInvalid && polarity == "INVERTED")
        {
            diagnostics.Add(new MeasurementDiagnostic(
                "POLARITY_INVERTED",
                "WARN",
                "Cực tính đo được đang đảo so với tín hiệu phát.",
                "Xác minh dây, kênh và chiều cực của chuỗi phát/thu trước khi kết luận loa bị đảo cực."));
        }
        if (!acquisitionInvalid)
        {
            diagnostics.Insert(0, new MeasurementDiagnostic(
                "ACQUISITION_OK",
                "INFO",
                "Đã thu và nhận diện được log-sweep với đường âm trực tiếp hợp lệ.",
                "Có thể đọc response/impulse và các thông số chẩn đoán của loa hiện tại."));
        }

        string validity = acquisitionInvalid ? "INVALID" : "DIAGNOSTIC";
        var highOrders = acquisitionInvalid ? HighOrderHarmonicResult.Unavailable("Bản thu sweep không hợp lệ; đo lại trước khi đọc H10–H35.")
            : BuildHighOrderHarmonics(farinaImpulseSpectrum, farinaDirectSample, settings, micCalib, harmonicTransferFunctions, fft);
        Stage("high-order-harmonics");
        if (acquisitionInvalid)
        {
            c50 = c80 = d50 = double.NaN;
            impulsePlot = impulsePlot with { ClarityC20Db = double.NaN, CentreTimeSeconds = double.NaN };
			sweepHarmonics = Array.Empty<SweepHarmonicPoint>();
			harmonicTransferFunctions = Array.Empty<Complex[]>();
            decayEstimates = decayEstimates.Select(item => item with
            { IsValid = false, Rt60Seconds = double.NaN, Status = "Acquisition INVALID" }).ToList();
        }
        string warning = string.Join(" ", diagnostics
            .Where(item => item.Severity is "INVALID" or "WARN")
            .Select(item => item.Message + " " + item.Action));
        if (string.IsNullOrWhiteSpace(warning))
            warning = "Phép thu hợp lệ để chẩn đoán; chưa phải chứng nhận nếu thiếu mic hiệu chuẩn, timing reference và fixture chuẩn.";

        var result = new StandardAcousticResult(
            settings.SampleRate,
            directSample,
            directArrivalMs,
            recordedPeak,
            clipped,
            polarity,
            impulseResponse,
            stepResponse,
            energyTimeCurve,
            response,
            decayCurve,
            decayEstimates,
            waterfall,
            c50,
            c80,
            d50,
            validity,
            warning)
        {
            SignalLevelDbFs = signalLevelDbFs,
            ExcitationRmsDbFs = 20.0 * Math.Log10(Math.Max(Tiny, Math.Abs(settings.Amplitude)) / Math.Sqrt(2.0)),
            NoiseFloorDbFs = noiseFloorDbFs,
            EstimatedSnrDb = estimatedSnrDb,
            EstimatedPreGainPeak = estimatedPreGainPeak,
            ImpulsePeakToBackgroundDb = impulsePeakToBackgroundDb,
            SweepCorrelation = sweepCorrelation,
            HasPlausibleDirectArrival = hasPlausibleDirectArrival,
            ImpulsePlot = impulsePlot,
            ImpulseEffects = global::SoncaAudioInspector.ImpulseEffects.Analyze(measurementImpulse,
                settings.SampleRate, preRoll, !acquisitionInvalid && hasPlausibleDirectArrival, settings.EffectsSettings),
            Diagnostics = diagnostics,
            AlignedTransferFunction = alignedTransfer,
            UngatedFrequencyResponse = ungatedResponse,
            SweepHarmonics = sweepHarmonics,
            HighOrderHarmonics = highOrders,
            HarmonicTransferFunctions = harmonicTransferFunctions,
            ImpulseWindowLeftMs = actualLeftMs,
            ImpulseWindowRightMs = adaptiveMaximumRightMs,
            ImpulseWindowKind = settings.ImpulseWindowKind,
            GatedMinimumFrequencyHz = gatedMinimumFrequencyHz,
            ClockDriftCorrectionApplied = clockEstimate.IsCorrectionApplied,
            EstimatedClockDriftPpm = clockEstimate.DriftPpm,
            ClockFitRmsSamples = clockEstimate.FitRmsSamples,
            FrequencyDependentWindowing = settings.FrequencyDependentWindowing,
            FrequencyDependentWindowCycles = settings.FrequencyDependentWindowCycles,
            OctaveBandRt60 = CalculateOctaveBandRt60(alignedImpulse, settings.SampleRate, preRoll),
            RewDistortion = CalculateRewDistortionStats(sweepHarmonics)
        };
        Stage("decay-effects");
        return result;
    }

    public static Complex[] NormalizeComplexTransferAtFrequency(
        IReadOnlyList<Complex> transfer,
        int sampleRate,
        double referenceFrequencyHz = 1000.0)
    {
        if (transfer is null || transfer.Count < 4) throw new ArgumentException("Transfer function không hợp lệ.", nameof(transfer));
        if (sampleRate <= 0 || referenceFrequencyHz <= 0 || referenceFrequencyHz >= sampleRate / 2.0)
            throw new ArgumentOutOfRangeException(nameof(referenceFrequencyHz));
        int bin = Math.Clamp((int)Math.Round(referenceFrequencyHz * transfer.Count / sampleRate), 1, transfer.Count / 2 - 1);
        double referenceMagnitude = transfer[bin].Magnitude;
        if (!double.IsFinite(referenceMagnitude) || referenceMagnitude <= Tiny)
            throw new InvalidOperationException("Không thể chuẩn hóa transfer function tại 1 kHz.");
        return transfer.Select(value => value / referenceMagnitude).ToArray();
    }

    public static ComplexTransferAverage AverageComplexTransfers(IReadOnlyList<Complex[]> transfers)
    {
        if (transfers is null || transfers.Count == 0)
            throw new ArgumentException("Cần ít nhất một transfer function.", nameof(transfers));
        int length = transfers[0].Length;
        if (length < 4 || transfers.Any(item => item.Length != length))
            throw new ArgumentException("Các transfer function phải có cùng FFT size.", nameof(transfers));

        var average = new Complex[length];
        var vectorStrength = new double[length];
        for (int bin = 0; bin < length; bin++)
        {
            Complex sum = Complex.Zero;
            double magnitudeSum = 0;
            foreach (Complex[] transfer in transfers)
            {
                Complex value = transfer[bin];
                if (!double.IsFinite(value.Real) || !double.IsFinite(value.Imaginary))
                    throw new ArgumentException("Transfer function chứa NaN hoặc Infinity.", nameof(transfers));
                sum += value;
                magnitudeSum += value.Magnitude;
            }
            average[bin] = sum / transfers.Count;
            vectorStrength[bin] = magnitudeSum <= Tiny ? 0 : Math.Clamp(sum.Magnitude / magnitudeSum, 0, 1);
        }
        return new ComplexTransferAverage(average, vectorStrength);
    }

    public static ComplexPhaseAlignment AlignComplexTransferToReference(
        IReadOnlyList<Complex> reference,
        IReadOnlyList<Complex> candidate,
        int sampleRate,
        double minFrequencyHz = 50.0,
        double maxFrequencyHz = 15000.0)
    {
        if (reference is null || candidate is null || reference.Count < 4 || reference.Count != candidate.Count)
            throw new ArgumentException("Hai transfer function phải có cùng FFT size.");
        int fftSize = reference.Count;
        int firstBin = Math.Max(1, (int)Math.Ceiling(minFrequencyHz * fftSize / sampleRate));
        int lastBin = Math.Min(fftSize / 2 - 1, (int)Math.Floor(maxFrequencyHz * fftSize / sampleRate));
        double maximumReference = reference.Skip(firstBin).Take(lastBin - firstBin + 1).Max(value => value.Magnitude);
        double maximumCandidate = candidate.Skip(firstBin).Take(lastBin - firstBin + 1).Max(value => value.Magnitude);
        double thresholdReference = maximumReference * 1e-4;
        double thresholdCandidate = maximumCandidate * 1e-4;
        var samples = new List<(double Bin, double Phase)>();
        double previousRaw = 0;
        double unwrapped = 0;
        bool hasPrevious = false;
        for (int bin = firstBin; bin <= lastBin; bin++)
        {
            if (reference[bin].Magnitude < thresholdReference || candidate[bin].Magnitude < thresholdCandidate) continue;
            double raw = (candidate[bin] * Complex.Conjugate(reference[bin])).Phase;
            if (!hasPrevious)
            {
                unwrapped = raw;
                hasPrevious = true;
            }
            else
            {
                double delta = raw - previousRaw;
                while (delta > Math.PI) delta -= 2.0 * Math.PI;
                while (delta < -Math.PI) delta += 2.0 * Math.PI;
                unwrapped += delta;
            }
            previousRaw = raw;
            samples.Add((bin, unwrapped));
        }
        if (samples.Count < 100)
            return new ComplexPhaseAlignment(candidate.ToArray(), 0, 0, double.PositiveInfinity, false);

        static (double Intercept, double Slope) Fit(IReadOnlyList<(double Bin, double Phase)> values)
        {
            double meanX = values.Average(item => item.Bin);
            double meanY = values.Average(item => item.Phase);
            double numerator = 0, denominator = 0;
            foreach ((double x, double y) in values)
            {
                numerator += (x - meanX) * (y - meanY);
                denominator += (x - meanX) * (x - meanX);
            }
            double slope = denominator <= Tiny ? 0 : numerator / denominator;
            return (meanY - slope * meanX, slope);
        }

        (double intercept, double slope) = Fit(samples);
        var inliers = samples.Where(item => Math.Abs(item.Phase - (intercept + slope * item.Bin)) <= Math.PI / 2.0).ToArray();
        if (inliers.Length >= 100) (intercept, slope) = Fit(inliers);
        double rms = Math.Sqrt(inliers.DefaultIfEmpty().Average(item =>
        {
            double residual = item.Phase - (intercept + slope * item.Bin);
            return residual * residual;
        }));
        double wrappedIntercept = Math.Atan2(Math.Sin(intercept), Math.Cos(intercept));
        bool polarityPlausible = Math.Abs(wrappedIntercept) < Math.PI / 2.0;
        bool valid = inliers.Length >= 100 && double.IsFinite(rms) && rms <= Math.PI / 4.0 && polarityPlausible;
        var aligned = new Complex[fftSize];
        for (int bin = 0; bin < fftSize; bin++)
        {
            int signedBin = bin <= fftSize / 2 ? bin : bin - fftSize;
            double correction = intercept + slope * signedBin;
            aligned[bin] = candidate[bin] * Complex.FromPolarCoordinates(1.0, -correction);
        }
        double residualDelaySamples = -slope * fftSize / (2.0 * Math.PI);
        return new ComplexPhaseAlignment(
            aligned,
            residualDelaySamples,
            wrappedIntercept * 180.0 / Math.PI,
            rms * 180.0 / Math.PI,
            valid);
    }

    public static IReadOnlyList<FrequencyResponsePoint> BuildFrequencyResponseFromAlignedTransfer(
        Complex[] alignedTransfer,
        int sampleRate,
        double minFrequency,
        double maxFrequency,
        int pointsPerOctave,
        MicrophoneCalibration? micCalib = null,
        LogSweepSettings? windowSettings = null)
    {
        if (alignedTransfer is null || alignedTransfer.Length < 4)
            throw new ArgumentException("Transfer function không hợp lệ.", nameof(alignedTransfer));
		Complex[] impulse = alignedTransfer.ToArray();
		new SweepFourier().Inverse(impulse);
		var settings = windowSettings ?? new LogSweepSettings(SampleRate: sampleRate);
		int preRoll = Math.Min(impulse.Length - 2, ImpulsePlotAnalysis.SampleCount(settings.ImpulseWindowLeftMs, sampleRate));
		var realImpulse = new double[impulse.Length];
		for (int i = 0; i < realImpulse.Length; i++) realImpulse[i] = impulse[(i - preRoll + impulse.Length) % impulse.Length].Real;
		double maximumRightMs = Math.Min(settings.ImpulseWindowRightMs, (realImpulse.Length - preRoll - 1) * 1000.0 / sampleRate);
		return BuildFrequencyDependentWindowResponse(realImpulse, sampleRate, preRoll,
			minFrequency, maxFrequency, pointsPerOctave, micCalib, settings.ImpulseWindowLeftMs, maximumRightMs,
            settings.FrequencyDependentWindowCycles, settings.FrequencyDependentWindowing, settings.ImpulseWindowKind);
    }

    public static PinkNoiseResult AnalyzePinkNoise(float[] samples, int sampleRate)
    {
        if (samples is null || samples.Length < 4096)
            throw new ArgumentException("Cần ít nhất 4096 mẫu pink noise.", nameof(samples));
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Any(value => !float.IsFinite(value)))
            return new PinkNoiseResult(
                sampleRate, -180, 0, 0, false, double.NaN,
                Array.Empty<PinkNoiseBandPoint>(),
                "INVALID_NONFINITE_DATA",
                "Bản thu chứa NaN/Infinity; không tính đáp tuyến và phải đo lại.");

        int fftSize = 16384;
        while (fftSize > samples.Length && fftSize > 1024) fftSize >>= 1;
        int hop = fftSize / 2;
        int frameCount = 1 + (samples.Length - fftSize) / hop;
        var averagePower = new double[fftSize / 2 + 1];
        double windowPower = 0;
        for (int index = 0; index < fftSize; index++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / (fftSize - 1));
            windowPower += window * window;
        }

        for (int frame = 0; frame < frameCount; frame++)
        {
            int start = frame * hop;
            double mean = 0;
            for (int index = 0; index < fftSize; index++) mean += samples[start + index];
            mean /= fftSize;
            var spectrum = new Complex[fftSize];
            for (int index = 0; index < fftSize; index++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / (fftSize - 1));
                spectrum[index] = (samples[start + index] - mean) * window;
            }
            Fourier.Forward(spectrum, FourierOptions.Matlab);
            for (int bin = 1; bin < averagePower.Length; bin++)
                averagePower[bin] += spectrum[bin].Magnitude * spectrum[bin].Magnitude / windowPower;
        }
        for (int bin = 1; bin < averagePower.Length; bin++) averagePower[bin] /= frameCount;

        var rawBands = new List<(double Frequency, double Level)>();
        double maximumFrequency = Math.Min(16000, sampleRate * 0.45);
        for (int band = 0; ; band++)
        {
            double center = 31.5 * Math.Pow(2.0, band / 3.0);
            if (center > maximumFrequency) break;
            double low = center / Math.Pow(2.0, 1.0 / 6.0);
            double high = center * Math.Pow(2.0, 1.0 / 6.0);
            int lowBin = Math.Max(1, (int)Math.Ceiling(low * fftSize / sampleRate));
            int highBin = Math.Min(averagePower.Length - 1, (int)Math.Floor(high * fftSize / sampleRate));
            double power = 0;
            for (int bin = lowBin; bin <= highBin; bin++) power += averagePower[bin];
            rawBands.Add((center, 10.0 * Math.Log10(power + Tiny)));
        }
        int referenceIndex = rawBands.Select((value, index) => (index, error: Math.Abs(Math.Log(value.Frequency / 1000.0))))
            .OrderBy(item => item.error).First().index;
        double reference = rawBands[referenceIndex].Level;
        PinkNoiseBandPoint[] bands = rawBands
            .Select(item => new PinkNoiseBandPoint(item.Frequency, item.Level, item.Level - reference))
            .ToArray();

        double rms = Math.Sqrt(samples.Average(value => (double)value * value));
        double peak = samples.Max(value => Math.Abs((double)value));
        double signalLevel = 20.0 * Math.Log10(rms + Tiny);
        bool clipped = peak >= 0.995 || samples.Count(value => Math.Abs(value) >= 0.995f) > samples.Length * 0.0001;
        double meanBand = bands.Average(item => item.NormalizedLevelDb);
        double bandDeviation = Math.Sqrt(bands.Average(item => Math.Pow(item.NormalizedLevelDb - meanBand, 2)));
        string validity = signalLevel < -75 ? "INVALID_LOW_SIGNAL" : clipped ? "INVALID_CLIPPING" : "DIAGNOSTIC";
        string warning = signalLevel < -75
            ? "Pink noise thu được quá nhỏ; kiểm tra route, mute và gain."
            : clipped
                ? "Pink noise bị clipping; giảm volume hoặc recording gain."
                : "Đường pink-noise cho biết cân bằng dải tương đối của loa hiện tại; không tự động là kết luận PASS/FAIL.";
        return new PinkNoiseResult(
            sampleRate,
            signalLevel,
            peak,
            rms > Tiny ? peak / rms : 0,
            clipped,
            bandDeviation,
            bands,
            validity,
            warning);
    }

    public static PinkNoiseComparison ComparePinkNoiseWithGolden(
        PinkNoiseResult measured,
        PinkNoiseResult golden,
        double toleranceDb = 3.0,
        double maximumFailedBandRatio = 0.10)
    {
        var findings = new List<AbnormalityFinding>();
        if (measured.Validity.StartsWith("INVALID", StringComparison.Ordinal)
            || golden.Validity.StartsWith("INVALID", StringComparison.Ordinal))
        {
            findings.Add(new AbnormalityFinding("INVALID_PINK_NOISE", "INVALID", "DUT hoặc golden có low signal/clipping."));
            return new PinkNoiseComparison("INVALID", 0, 0, findings);
        }

        int compared = 0;
        int failed = 0;
        double maximum = 0;
        foreach (PinkNoiseBandPoint band in measured.Bands)
        {
            PinkNoiseBandPoint? reference = golden.Bands
                .OrderBy(item => Math.Abs(Math.Log(item.CenterFrequencyHz / band.CenterFrequencyHz)))
                .FirstOrDefault();
            if (reference is null || Math.Abs(Math.Log(reference.CenterFrequencyHz / band.CenterFrequencyHz)) > 0.02) continue;
            compared++;
            double deviation = Math.Abs(band.NormalizedLevelDb - reference.NormalizedLevelDb);
            maximum = Math.Max(maximum, deviation);
            if (deviation > toleranceDb) failed++;
        }
        double ratio = compared == 0 ? 1 : failed / (double)compared;
        if (compared == 0)
            findings.Add(new AbnormalityFinding("NO_COMMON_PINK_BANDS", "INVALID", "Không có dải pink-noise chung với golden."));
        else if (ratio > maximumFailedBandRatio)
            findings.Add(new AbnormalityFinding("PINK_RESPONSE", "FAIL", $"{failed}/{compared} dải lệch quá {toleranceDb:F1} dB; lớn nhất {maximum:F2} dB."));
        else if (failed > 0)
            findings.Add(new AbnormalityFinding("LOCAL_PINK_DEVIATION", "WARN", $"{failed}/{compared} dải lệch cục bộ; lớn nhất {maximum:F2} dB."));

        string status = findings.Any(item => item.Severity == "INVALID")
            ? "INVALID"
            : findings.Any(item => item.Severity == "FAIL")
                ? "FAIL"
                : findings.Count == 0 ? "PASS_DIAGNOSTIC" : "WARN";
        return new PinkNoiseComparison(status, ratio, maximum, findings);
    }

    public static IReadOnlyList<ImpedancePoint> AnalyzeImpedance(
        IEnumerable<ImpedanceInputPoint> samples,
        double shuntResistanceOhm)
    {
        if (!double.IsFinite(shuntResistanceOhm) || shuntResistanceOhm <= 0)
            throw new ArgumentOutOfRangeException(nameof(shuntResistanceOhm));

        return samples
            .Where(item => item.FrequencyHz > 0 && item.ShuntVoltageRms > 1e-9)
            .OrderBy(item => item.FrequencyHz)
            .Select(item =>
            {
                double current = item.ShuntVoltageRms / shuntResistanceOhm;
                double magnitude = item.DutVoltageRms / current;
                double phaseRad = item.PhaseDifferenceDegrees * Math.PI / 180.0;
                return new ImpedancePoint(
                    item.FrequencyHz,
                    magnitude,
                    item.PhaseDifferenceDegrees,
                    magnitude * Math.Cos(phaseRad),
                    magnitude * Math.Sin(phaseRad));
            })
            .ToArray();
    }

    public static IReadOnlyList<DirectivityPoint> AnalyzeAxisymmetricDirectivity(
        IEnumerable<DirectivityInput> measurements)
    {
        DirectivityInput[] sets = measurements.OrderBy(item => Math.Abs(item.AngleDegrees)).ToArray();
        DirectivityInput? onAxis = sets.FirstOrDefault(item => Math.Abs(item.AngleDegrees) < 0.1);
        if (onAxis is null || sets.Length < 3) return Array.Empty<DirectivityPoint>();

        var results = new List<DirectivityPoint>();
        foreach (FrequencyResponsePoint point in onAxis.Response)
        {
            var powers = new List<double>();
            foreach (DirectivityInput set in sets)
            {
                FrequencyResponsePoint? nearest = set.Response
                    .Where(value => Math.Abs(Math.Log(value.FrequencyHz / point.FrequencyHz)) < 0.03)
                    .OrderBy(value => Math.Abs(value.FrequencyHz - point.FrequencyHz))
                    .FirstOrDefault();
                if (nearest != null) powers.Add(Math.Pow(10.0, nearest.NormalizedLevelDb / 10.0));
            }
            if (powers.Count < 3) continue;
            double meanOffAxis = 10.0 * Math.Log10(powers.Average() + Tiny);
            results.Add(new DirectivityPoint(
                point.FrequencyHz,
                point.NormalizedLevelDb,
                meanOffAxis,
                point.NormalizedLevelDb - meanOffAxis));
        }
        return results;
    }

    public static MaximumLinearOutputResult AssessMaximumLinearOutput(
        IEnumerable<OutputLevelStep> steps,
        double maximumCompressionDb = 3.0,
        double maximumThdPercent = 10.0,
        double minimumCoherence = 0.91)
    {
        OutputLevelStep[] ordered = steps.OrderBy(item => item.InputLevelDb).ToArray();
        if (ordered.Length == 0)
            return new MaximumLinearOutputResult(false, double.NaN, 0, "Không có bước đo mức.");

        OutputLevelStep[] accepted = ordered.TakeWhile(item =>
            item.CompressionDb <= maximumCompressionDb
            && item.ThdPercent <= maximumThdPercent
            && item.Coherence >= minimumCoherence).ToArray();
        if (accepted.Length == 0)
            return new MaximumLinearOutputResult(false, double.NaN, 0, "Bước đầu tiên đã vượt giới hạn tuyến tính.");
        return new MaximumLinearOutputResult(
            true,
            accepted[^1].OutputSplDb,
            accepted.Length,
            accepted.Length == ordered.Length
                ? "Chưa tìm thấy điểm giới hạn; cần tăng mức theo recipe an toàn."
                : "Đã dừng tại bước đầu tiên vượt compression/THD/coherence.");
    }

    public static SpeakerAbnormalityResult CompareWithGolden(
        StandardAcousticResult measured,
        StandardAcousticResult golden,
        double responseToleranceDb = 3.0,
        double maximumFailedPointRatio = 0.10,
        double maximumGroupDelayDifferenceMs = 5.0,
        double maximumDecayRatio = 1.50)
    {
        var findings = new List<AbnormalityFinding>();
        if (measured.Validity == "INVALID" || golden.Validity == "INVALID")
        {
            findings.Add(new AbnormalityFinding(
                "INVALID_MEASUREMENT",
                "INVALID",
                "Phép đo DUT hoặc golden bị clipping, tín hiệu yếu hoặc decay không hợp lệ."));
            return new SpeakerAbnormalityResult("INVALID", 0, 0, findings);
        }

        if (!string.Equals(measured.Polarity, golden.Polarity, StringComparison.Ordinal))
            findings.Add(new AbnormalityFinding("POLARITY", "FAIL", $"Cực tính DUT {measured.Polarity}, golden {golden.Polarity}."));

        // Check absolute sensitivity delta at 1 kHz (detects weak magnet, wrong coil impedance, or bad winding)
        var measured1kPoint = measured.FrequencyResponse
            .OrderBy(item => Math.Abs(Math.Log(item.FrequencyHz / 1000.0)))
            .FirstOrDefault();
        var golden1kPoint = golden.FrequencyResponse
            .OrderBy(item => Math.Abs(Math.Log(item.FrequencyHz / 1000.0)))
            .FirstOrDefault();
        if (measured1kPoint != null && golden1kPoint != null)
        {
            double sensitivityDeltaDb = measured1kPoint.LevelDb - golden1kPoint.LevelDb;
            if (Math.Abs(sensitivityDeltaDb) > responseToleranceDb)
            {
                findings.Add(new AbnormalityFinding(
                    "SENSITIVITY",
                    "FAIL",
                    $"Độ nhạy tại 1 kHz lệch {sensitivityDeltaDb:+0.00;-0.00} dB so với golden (ngưỡng ±{responseToleranceDb:F1} dB)."));
            }
        }

        int compared = 0;
        int failed = 0;
        double maximumDeviation = 0;
        int excessiveDelayPoints = 0;
        foreach (FrequencyResponsePoint point in measured.FrequencyResponse.Where(item => item.FrequencyHz >= 40 && item.FrequencyHz <= 18000))
        {
            FrequencyResponsePoint? reference = golden.FrequencyResponse
                .OrderBy(item => Math.Abs(Math.Log(item.FrequencyHz / point.FrequencyHz)))
                .FirstOrDefault();
            if (reference is null || Math.Abs(Math.Log(reference.FrequencyHz / point.FrequencyHz)) > 0.02) continue;
            compared++;
            double deviation = Math.Abs(point.NormalizedLevelDb - reference.NormalizedLevelDb);
            maximumDeviation = Math.Max(maximumDeviation, deviation);
            if (deviation > responseToleranceDb) failed++;
            if (Math.Abs(point.GroupDelayMs - reference.GroupDelayMs) > maximumGroupDelayDifferenceMs)
                excessiveDelayPoints++;
        }
        double failedRatio = compared == 0 ? 1.0 : failed / (double)compared;
        if (compared == 0)
            findings.Add(new AbnormalityFinding("NO_COMMON_RESPONSE", "INVALID", "Không có điểm đáp tuyến chung với golden."));
        else if (failedRatio > maximumFailedPointRatio)
            findings.Add(new AbnormalityFinding(
                "FREQUENCY_RESPONSE",
                "FAIL",
                $"{failed}/{compared} điểm lệch quá {responseToleranceDb:F1} dB; lệch lớn nhất {maximumDeviation:F2} dB."));
        else if (failed > 0)
            findings.Add(new AbnormalityFinding(
                "LOCAL_RESPONSE_DEVIATION",
                "WARN",
                $"{failed}/{compared} điểm lệch cục bộ; lệch lớn nhất {maximumDeviation:F2} dB."));

        if (compared > 0 && excessiveDelayPoints / (double)compared > maximumFailedPointRatio)
            findings.Add(new AbnormalityFinding(
                "GROUP_DELAY",
                "FAIL",
                $"Group delay khác golden quá {maximumGroupDelayDifferenceMs:F1} ms tại {excessiveDelayPoints}/{compared} điểm."));

        DecayEstimate? measuredT20 = measured.DecayEstimates.FirstOrDefault(item => item.Name == "T20" && item.IsValid);
        DecayEstimate? goldenT20 = golden.DecayEstimates.FirstOrDefault(item => item.Name == "T20" && item.IsValid);
        if (measuredT20 != null && goldenT20 != null)
        {
            double ratio = measuredT20.Rt60Seconds / Math.Max(0.001, goldenT20.Rt60Seconds);
            if (ratio > maximumDecayRatio || ratio < 1.0 / maximumDecayRatio)
                findings.Add(new AbnormalityFinding(
                    "DECAY",
                    "WARN",
                    $"T20 DUT {measuredT20.Rt60Seconds:F3}s, golden {goldenT20.Rt60Seconds:F3}s (tỷ lệ {ratio:F2})."));
        }

        string status = findings.Any(item => item.Severity == "INVALID")
            ? "INVALID"
            : findings.Any(item => item.Severity == "FAIL")
                ? "FAIL"
                : findings.Count == 0 ? "PASS_DIAGNOSTIC" : "WARN";
        return new SpeakerAbnormalityResult(status, failedRatio, maximumDeviation, findings);
    }

    public static void ExportResult(StandardAcousticResult result, string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string safeName = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        string prefix = Path.Combine(folder, safeName);

        var summary = new StringBuilder();
        summary.AppendLine("SONCA STANDARD ACOUSTIC DIAGNOSTIC");
        summary.AppendLine($"CreatedUtc={DateTimeOffset.UtcNow:O}");
        summary.AppendLine($"Validity={result.Validity}");
        summary.AppendLine($"SampleRate={result.SampleRate}");
        summary.AppendLine($"DirectArrivalMs={result.DirectArrivalMs:F3}");
        summary.AppendLine($"DirectArrivalPlausible={result.HasPlausibleDirectArrival}");
        summary.AppendLine($"SignalLevelDbFS={Format(result.SignalLevelDbFs)}");
        summary.AppendLine($"NoiseFloorDbFS={Format(result.NoiseFloorDbFs)}");
        summary.AppendLine($"EstimatedSnrDb={Format(result.EstimatedSnrDb)}");
        summary.AppendLine($"PeakSample={result.PeakSample:F6}");
        summary.AppendLine($"EstimatedPreGainPeak={Format(result.EstimatedPreGainPeak)}");
        summary.AppendLine($"ImpulsePeakToBackgroundDb={Format(result.ImpulsePeakToBackgroundDb)}");
        summary.AppendLine($"SweepCorrelation={Format(result.SweepCorrelation)}");
        summary.AppendLine($"Clipped={result.IsClipped}");
        summary.AppendLine($"Polarity={result.Polarity}");
        summary.AppendLine($"C50dB={result.ClarityC50Db:F3}");
        summary.AppendLine($"C80dB={result.ClarityC80Db:F3}");
        summary.AppendLine($"D50Percent={result.DefinitionD50Percent:F3}");
        foreach (DecayEstimate decay in result.DecayEstimates)
            summary.AppendLine($"{decay.Name}={Format(decay.Rt60Seconds)} s; R2={Format(decay.RSquared)}; {decay.Status}");
        foreach (MeasurementDiagnostic diagnostic in result.Diagnostics)
        {
            summary.AppendLine($"Diagnostic={diagnostic.Severity},{diagnostic.Code},{diagnostic.Message}");
            summary.AppendLine($"Action={diagnostic.Code},{diagnostic.Action}");
        }
        summary.AppendLine($"Warning={result.Warning}");
        File.WriteAllText(prefix + "-summary.txt", summary.ToString(), new UTF8Encoding(false));

        var response = new StringBuilder("FrequencyHz,LevelDb,NormalizedLevelDb,PhaseDegrees,GroupDelayMs\n");
        foreach (FrequencyResponsePoint point in result.FrequencyResponse)
            response.AppendLine(string.Join(',', Format(point.FrequencyHz), Format(point.LevelDb),
                Format(point.NormalizedLevelDb), Format(point.PhaseDegrees), Format(point.GroupDelayMs)));
        File.WriteAllText(prefix + "-response.csv", response.ToString(), new UTF8Encoding(false));

        var decayCsv = new StringBuilder("TimeSeconds,EnergyDecayDb\n");
        foreach (DecayCurvePoint point in result.EnergyDecayCurve)
            decayCsv.AppendLine($"{Format(point.TimeSeconds)},{Format(point.LevelDb)}");
        File.WriteAllText(prefix + "-decay.csv", decayCsv.ToString(), new UTF8Encoding(false));

        var waterfallCsv = new StringBuilder("TimeMs,FrequencyHz,LevelDb\n");
        foreach (WaterfallPoint point in result.Waterfall)
            waterfallCsv.AppendLine($"{Format(point.TimeMs)},{Format(point.FrequencyHz)},{Format(point.LevelDb)}");
        File.WriteAllText(prefix + "-waterfall.csv", waterfallCsv.ToString(), new UTF8Encoding(false));

        ExportTimeDomain(prefix + "-impulse.csv", "Impulse", result.ImpulseResponse);
        ExportTimeDomain(prefix + "-step.csv", "Step", result.StepResponse);
        ExportTimeDomain(prefix + "-etc.csv", "EnergyDb", result.EnergyTimeCurve);
    }

    private static double EstimateFractionalPeakOffset(Complex[] impulse, int peakIndex, int usefulLength)
    {
        if (peakIndex <= 0 || peakIndex >= usefulLength - 1) return 0;
        double left = Math.Abs(impulse[peakIndex - 1].Real);
        double center = Math.Abs(impulse[peakIndex].Real);
        double right = Math.Abs(impulse[peakIndex + 1].Real);
        double denominator = left - 2.0 * center + right;
        if (Math.Abs(denominator) <= Tiny) return 0;
        return Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static Complex[] RemoveTransferDelay(IReadOnlyList<Complex> transfer, double delaySamples)
    {
        int fftSize = transfer.Count;
        var aligned = new Complex[fftSize];
        for (int bin = 0; bin < fftSize; bin++)
        {
            int signedBin = bin <= fftSize / 2 ? bin : bin - fftSize;
            aligned[bin] = transfer[bin] * Complex.FromPolarCoordinates(
                1.0,
                2.0 * Math.PI * signedBin * delaySamples / fftSize);
        }
        return aligned;
    }

    private static int FindEarliestImpulsePeak(
        IReadOnlyList<Complex> impulse,
        int searchLimit,
        double threshold,
        int fallback)
    {
        for (int index = 1; index < searchLimit - 1; index++)
        {
            double magnitude = Math.Abs(impulse[index].Real);
            if (magnitude >= threshold
                && magnitude >= Math.Abs(impulse[index - 1].Real)
                && magnitude >= Math.Abs(impulse[index + 1].Real))
                return index;
        }
        return fallback;
    }

	private static int FindStrongestImpulseNear(
		IReadOnlyList<Complex> impulse,
		int anchor,
		int radius,
		int searchLimit)
	{
		int start = Math.Max(0, anchor - Math.Max(1, radius));
		int end = Math.Min(Math.Min(searchLimit, impulse.Count) - 1, anchor + Math.Max(1, radius));
		int strongest = Math.Clamp(anchor, start, Math.Max(start, end));
		double strongestMagnitude = 0.0;
		for (int index = start; index <= end; index++)
		{
			double magnitude = Math.Abs(impulse[index].Real);
			if (magnitude <= strongestMagnitude) continue;
			strongestMagnitude = magnitude;
			strongest = index;
		}
		return strongest;
	}

	private static int FindStrongestCircularImpulseNear(
		IReadOnlyList<Complex> impulse,
		int anchor,
		int leftRadius,
		int rightRadius)
	{
		int count = impulse.Count;
		if (count == 0) return 0;
		int strongest = Mod(anchor, count);
		double strongestMagnitude = 0.0;
		for (int offset = -Math.Max(1, leftRadius); offset <= Math.Max(1, rightRadius); offset++)
		{
			int index = Mod(anchor + offset, count);
			double magnitude = Math.Abs(impulse[index].Real);
			if (magnitude <= strongestMagnitude) continue;
			strongestMagnitude = magnitude;
			strongest = index;
		}
		return strongest;
	}


    private static double EstimateRobustImpulseBackground(IReadOnlyList<Complex> impulse, int count)
    {
        int stride = Math.Max(1, count / 20000);
        double[] magnitudes = Enumerable.Range(0, (count + stride - 1) / stride)
            .Select(index => Math.Abs(impulse[Math.Min(count - 1, index * stride)].Real))
            .OrderBy(value => value)
            .ToArray();
        if (magnitudes.Length == 0) return 0;
        double median = magnitudes[magnitudes.Length / 2];
        return median / 0.6744897501960817;
    }

	private static SweepClockEstimate EstimateSweepClock(float[] excitation, float[] recorded, int sampleRate, SweepFourier fft)
		=> EstimateSweepClockCancellable(excitation, recorded, sampleRate, fft, CancellationToken.None);

	private static SweepClockEstimate EstimateSweepClockCancellable(float[] excitation, float[] recorded, int sampleRate, SweepFourier fft, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		int segmentLength = Math.Clamp(Math.Min(excitation.Length / 24, sampleRate / 24), 1024, 2048);
		if (excitation.Length < segmentLength * 4 || recorded.Length < segmentLength * 4)
			return new SweepClockEstimate(false, 1.0, 0.0, double.NaN);

		// All anchors search the same immutable capture. Reuse its FFT and power
		// prefix within this estimate, including refinement passes. Never cache
		// across measurements or mutate these spectra during convolution.
		var captureSpectra = new Dictionary<int, (Complex[] Capture, Complex[] Convolution)>();
		var prefixPower = new double[recorded.Length + 1];
		for (int index = 0; index < recorded.Length; index++)
			prefixPower[index + 1] = prefixPower[index] + recorded[index] * recorded[index];
		double[] positions = { 0.12, 0.215, 0.31, 0.405, 0.50, 0.595, 0.69, 0.785, 0.88 };
		List<(double ExcitationSample, double RecordedSample, double Correlation)> CollectAnchors(double templateScale)
		{
			var result = new List<(double, double, double)>();
			foreach (double position in positions)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int segmentStart = Math.Clamp(
					(int)Math.Round(position * (excitation.Length - segmentLength)),
					0, excitation.Length - segmentLength);
				(double recordedStart, double correlation) = FindBestSweepSegmentMatch(
					excitation, segmentStart, segmentLength, recorded, templateScale, captureSpectra, prefixPower, fft);
				if (recordedStart >= 0 && correlation >= 0.025)
					result.Add((segmentStart, recordedStart, correlation));
			}
			return result;
		}

		static (double Scale, double Intercept, double Rms) Fit(
			IReadOnlyList<(double ExcitationSample, double RecordedSample, double Correlation)> anchors)
		{
			double meanX = anchors.Average(point => point.ExcitationSample);
			double meanY = anchors.Average(point => point.RecordedSample);
			double denominator = anchors.Sum(point => Math.Pow(point.ExcitationSample - meanX, 2));
			if (denominator <= Tiny) return (1.0, 0.0, double.PositiveInfinity);
			double scale = anchors.Sum(point => (point.ExcitationSample - meanX) * (point.RecordedSample - meanY)) / denominator;
			double intercept = meanY - scale * meanX;
			double rms = Math.Sqrt(anchors.Average(point =>
				Math.Pow(point.RecordedSample - (intercept + scale * point.ExcitationSample), 2)));
			return (scale, intercept, rms);
		}

		var anchors = CollectAnchors(1.0);
		if (anchors.Count < 4) return new SweepClockEstimate(false, 1.0, 0.0, double.NaN);
		double templateScale = 1.0;
		for (int iteration = 0; iteration < 4; iteration++)
		{
			(double estimatedScale, _, _) = Fit(anchors);
			if (estimatedScale is < 0.995 or > 1.005 || Math.Abs(estimatedScale - templateScale) < 5e-6)
				break;
			templateScale = estimatedScale;
			var refinedAnchors = CollectAnchors(templateScale);
			if (refinedAnchors.Count < 4) break;
			anchors = refinedAnchors;
		}
		(double scale, double intercept, double fitRms) = Fit(anchors);
		double driftPpm = (scale - 1.0) * 1_000_000.0;
		bool reliable = scale is >= 0.995 and <= 1.005
			&& fitRms <= sampleRate * 0.0005
			&& intercept >= -sampleRate * 0.02
			&& intercept < recorded.Length - excitation.Length * Math.Min(1.0, scale) / 2.0;
		return new SweepClockEstimate(reliable, reliable ? scale : 1.0, reliable ? driftPpm : 0.0, fitRms);
	}

	private static (double Start, double Correlation) FindBestSweepSegmentMatch(
		float[] excitation,
		int segmentStart,
		int segmentLength,
		float[] recorded,
		double templateScale,
		Dictionary<int, (Complex[] Capture, Complex[] Convolution)> captureSpectra,
		double[] prefixPower,
		SweepFourier fft)
	{
		int templateLength = Math.Max(32, (int)Math.Round(segmentLength * templateScale));
		int fftSize = NextPowerOfTwo(recorded.Length + templateLength - 1);
		if (!captureSpectra.TryGetValue(fftSize, out var buffers))
		{
			buffers = (new Complex[fftSize], new Complex[fftSize]);
			for (int index = 0; index < recorded.Length; index++) buffers.Capture[index] = recorded[index];
			fft.Forward(buffers.Capture, recorded.Length);
			captureSpectra.Add(fftSize, buffers);
		}
		var recordedSpectrum = buffers.Convolution;
		var reversedTemplateSpectrum = buffers.Convolution;
		Array.Clear(reversedTemplateSpectrum);

		double mean = 0.0;
		for (int index = 0; index < templateLength; index++)
		{
			double source = segmentStart + index / templateScale;
			int lower = Math.Clamp((int)Math.Floor(source), 0, excitation.Length - 1);
			int upper = Math.Min(excitation.Length - 1, lower + 1);
			mean += excitation[lower] + (excitation[upper] - excitation[lower]) * (source - lower);
		}
		mean /= templateLength;
		double templatePower = 0.0;
		for (int index = 0; index < templateLength; index++)
		{
			double source = segmentStart + index / templateScale;
			int lower = Math.Clamp((int)Math.Floor(source), 0, excitation.Length - 1);
			int upper = Math.Min(excitation.Length - 1, lower + 1);
			double sample = excitation[lower] + (excitation[upper] - excitation[lower]) * (source - lower);
			double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / Math.Max(1, templateLength - 1));
			double value = (sample - mean) * hann;
			templatePower += value * value;
			reversedTemplateSpectrum[templateLength - 1 - index] = value;
		}
		fft.Forward(reversedTemplateSpectrum, templateLength);
		for (int bin = 0; bin < fftSize; bin++) recordedSpectrum[bin] = buffers.Capture[bin] * reversedTemplateSpectrum[bin];
		fft.Inverse(recordedSpectrum);

		int bestStart = -1;
		double bestCorrelation = 0.0;
		int maximumStart = recorded.Length - templateLength;
		for (int start = 0; start <= maximumStart; start++)
		{
			double outputPower = prefixPower[start + templateLength] - prefixPower[start];
			if (outputPower <= Tiny) continue;
			double correlation = Math.Abs(recordedSpectrum[start + templateLength - 1].Real)
				/ Math.Sqrt(templatePower * outputPower + Tiny);
			if (correlation <= bestCorrelation) continue;
			bestCorrelation = correlation;
			bestStart = start;
		}
		double refinedStart = bestStart;
		if (bestStart > 0 && bestStart < maximumStart)
		{
			double NormalizedCorrelationAt(int start)
			{
				double outputPower = prefixPower[start + templateLength] - prefixPower[start];
				return outputPower <= Tiny ? 0.0 : Math.Abs(recordedSpectrum[start + templateLength - 1].Real)
					/ Math.Sqrt(templatePower * outputPower + Tiny);
			}

			double before = NormalizedCorrelationAt(bestStart - 1);
			double center = NormalizedCorrelationAt(bestStart);
			double after = NormalizedCorrelationAt(bestStart + 1);
			double curvature = before - 2.0 * center + after;
			if (Math.Abs(curvature) > Tiny)
				refinedStart += Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5);
		}
		return (refinedStart, bestCorrelation);
	}

	private static float[] CorrectSweepClock(float[] recorded, double sampleScale)
	{
		if (!double.IsFinite(sampleScale) || sampleScale <= 0.0 || Math.Abs(sampleScale - 1.0) < 1e-12)
			return recorded;
		int outputLength = Math.Max(1024, (int)Math.Floor((recorded.Length - 1) / sampleScale) + 1);
		var corrected = new float[outputLength];
		const int sincRadius = 16;
		for (int index = 0; index < outputLength; index++)
		{
			double source = index * sampleScale;
			int center = (int)Math.Floor(source);
			double weighted = 0.0;
			double weightSum = 0.0;
			for (int tap = center - sincRadius + 1; tap <= center + sincRadius; tap++)
			{
				if ((uint)tap >= (uint)recorded.Length) continue;
				double distance = source - tap;
				double sinc = Math.Abs(distance) < 1e-12
					? 1.0
					: Math.Sin(Math.PI * distance) / (Math.PI * distance);
				double normalized = distance / sincRadius;
				double window = Math.Abs(normalized) >= 1.0
					? 0.0
					: 0.42 + 0.5 * Math.Cos(Math.PI * normalized) + 0.08 * Math.Cos(2.0 * Math.PI * normalized);
				double weight = sinc * window;
				weighted += recorded[tap] * weight;
				weightSum += weight;
			}
			corrected[index] = Math.Abs(weightSum) <= Tiny ? 0f : (float)(weighted / weightSum);
		}
		return corrected;
	}

	private static IReadOnlyList<FrequencyResponsePoint> BuildFrequencyDependentWindowResponse(
		IReadOnlyList<double> impulse,
		int sampleRate,
		int directIndex,
		double minFrequency,
		double maxFrequency,
		int pointsPerOctave,
		MicrophoneCalibration? micCalib,
		double leftMs,
		double maximumRightMs,
		double windowCycles,
        bool frequencyDependentWindowing,
        ImpulseWindowKind windowKind)
	{
		maxFrequency = Math.Min(maxFrequency, sampleRate * 0.475);
		int pointCount = Math.Max(2,
			(int)Math.Floor(Math.Log(maxFrequency / minFrequency, 2.0) * pointsPerOctave) + 1);
		var raw = new List<(double Frequency, double Level, double Phase)>(pointCount);
		int leftSamples = Math.Min(directIndex, Math.Max(0, ImpulsePlotAnalysis.SampleCount(leftMs, sampleRate)));
        double actualLeftSeconds = leftSamples / (double)sampleRate;
        int fixedRightSamples = Math.Min(impulse.Count - directIndex - 1,
            Math.Max(1, ImpulsePlotAnalysis.SampleCount(maximumRightMs, sampleRate)));
        double[]? fixedGate = frequencyDependentWindowing ? null : ImpulsePlotAnalysis.ApplyWindow(
            impulse, sampleRate, directIndex, leftSamples * 1000.0 / sampleRate,
            fixedRightSamples * 1000.0 / sampleRate, windowKind);
		for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
		{
			double frequency = minFrequency * Math.Pow(2.0, pointIndex / (double)pointsPerOctave);
			if (frequency > maxFrequency) break;
			double rightMs = frequencyDependentWindowing ? Math.Min(maximumRightMs, windowCycles * 1000.0 / frequency) : maximumRightMs;
			int rightSamples = Math.Min(impulse.Count - directIndex - 1,
				Math.Max(1, ImpulsePlotAnalysis.SampleCount(rightMs, sampleRate)));
			Complex value = Complex.Zero;
            Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / sampleRate);
            Complex phasor = Complex.FromPolarCoordinates(1, 2 * Math.PI * frequency * leftSamples / sampleRate);
			for (int offset = -leftSamples; offset <= rightSamples; offset++)
			{
				int sampleIndex = directIndex + offset;
                double sample = fixedGate is not null ? fixedGate[sampleIndex] : impulse[sampleIndex] *
                    ImpulsePlotAnalysis.WindowWeight(offset / (double)sampleRate, actualLeftSeconds,
                        rightSamples / (double)sampleRate, windowKind);
                value += sample * phasor;
                phasor *= step;
			}
			double level = 20.0 * Math.Log10(value.Magnitude + Tiny)
				- (micCalib?.GetGainCorrectionDb(frequency) ?? 0.0);
			raw.Add((frequency, level, value.Phase));
		}
		if (raw.Count == 0) return Array.Empty<FrequencyResponsePoint>();
		int referenceIndex = raw.Select((value, index) => (index, error: Math.Abs(Math.Log(value.Frequency / 1000.0))))
			.OrderBy(item => item.error).First().index;
		double referenceDb = raw[referenceIndex].Level;
		double[] unwrapped = UnwrapPhases(raw.Select(item => item.Phase).ToArray());
		var result = new List<FrequencyResponsePoint>(raw.Count);
		for (int index = 0; index < raw.Count; index++)
		{
			double groupDelayMs = 0.0;
			if (index > 0 && index < raw.Count - 1)
				groupDelayMs = -(unwrapped[index + 1] - unwrapped[index - 1])
					/ (2.0 * Math.PI * (raw[index + 1].Frequency - raw[index - 1].Frequency)) * 1000.0;
			result.Add(new FrequencyResponsePoint(raw[index].Frequency, raw[index].Level,
				raw[index].Level - referenceDb, unwrapped[index] * 180.0 / Math.PI, groupDelayMs));
		}
		return result;
	}

    private static Complex[] BuildTransferFromImpulse(IReadOnlyList<double> impulse, int fftSize)
    {
        var spectrum = new Complex[fftSize];
        for (int index = 0; index < Math.Min(impulse.Count, fftSize); index++) spectrum[index] = impulse[index];
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        return spectrum;
    }

    private static IReadOnlyList<SweepHarmonicPoint> BuildSweepHarmonicResponse(
        IReadOnlyList<Complex> impulse,
        double directSample,
        LogSweepSettings settings,
        MicrophoneCalibration? micCalib,
        SweepFourier fft,
        out Complex[][] harmonicTransferFunctions)
    {
        int fftSize = impulse.Count;
        double logRatio = Math.Log(settings.EndFrequencyHz / settings.StartFrequencyHz);
        double sampleRate = settings.SampleRate;

        // Tính offset sample cho từng order (theo Farina 2000):
        // offset_n = duration × ln(n) / ln(f2/f1) × fs
        var offsets = new double[11];
        for (int n = 1; n <= 10; n++)
            offsets[n] = n == 1 ? 0.0 : settings.DurationSeconds * Math.Log(n) / logRatio * sampleRate;

        // Isolate orders at their predicted Farina times. Longer sweeps allow
        // wider gates and retain more decay without merging adjacent orders.
        // Harmonic isolation is independent of the fundamental IR display/FEQ window.
        int defaultLeft  = Math.Max(8, (int)Math.Round(50.0 * sampleRate / 1000.0));
        // Harmonic orders need a compact, non-overlapping Farina window. The wider
        // IR/FDW limit used for frequency response must not merge adjacent orders.
        int defaultRight = Math.Max(32, (int)Math.Round(settings.ImpulseWindowRightMs * sampleRate / 1000.0));

	        var spectra = new Complex[10][];

        for (int order = 1; order <= 9; order++)
        {
            // Giới hạn window bởi khoảng cách đến order liền kề để tránh overlap.
            // gap_prev = offset[order] - offset[order-1],  gap_next = offset[order+1] - offset[order]
            double gapPrev = order > 1 ? offsets[order] - offsets[order - 1] : double.MaxValue;
            double gapNext = offsets[order + 1] - offsets[order];
            // Higher orders precede this impulse (left); lower orders follow it
            // (right). Keep the gates disjoint, but retain the causal decay.
            int maxHalfLeft  = (int)Math.Floor(gapNext * 0.45);
            int maxHalfRight = order > 1 ? (int)Math.Floor(gapPrev * 0.45) : defaultRight;
            int left  = Math.Clamp(defaultLeft,  8, Math.Max(8,  maxHalfLeft));
            int right = Math.Clamp(defaultRight, 8, Math.Max(8,  maxHalfRight));

	            int expectedCenter = Mod((int)Math.Round(directSample - offsets[order]), fftSize);
	            int center = expectedCenter;
	            var windowed = new Complex[fftSize];

            for (int rel = -left; rel <= right; rel++)
            {
                // Flat interior preserves delayed energy; taper only the outer
                // 10%. A half-Hann over the full gate attenuates real decay.
                double position = rel <= 0 ? -rel / (double)left : rel / (double)right;
                double w = position <= .9 ? 1.0 : .5 + .5 * Math.Cos(Math.PI * (position - .9) / .1);
                int src = Mod(center + rel, fftSize);
                int dst = rel + left;
                if (dst < fftSize)
                {
                    windowed[dst] = impulse[src] * w;
                }
            }

            fft.Forward(windowed, Math.Min(fftSize, left + right + 1));
            spectra[order] = windowed;
        }

	        harmonicTransferFunctions = spectra;
	        return BuildSweepHarmonicPointsFromTransfers(spectra, settings, micCalib);
    }

    /// <summary>
    /// Builds H2-H9 at each requested fundamental frequency from time-aligned
    /// Farina harmonic transfer functions. Callers can coherently average each
    /// transfer first when repeated sweeps share a clock.
    /// </summary>
    public static IReadOnlyList<SweepHarmonicPoint> BuildSweepHarmonicPointsFromTransfers(
        IReadOnlyList<Complex[]> harmonicTransfers,
        LogSweepSettings settings,
        MicrophoneCalibration? micCalib = null)
    {
        if (harmonicTransfers == null || harmonicTransfers.Count < 10 || harmonicTransfers[1].Length < 4)
            return Array.Empty<SweepHarmonicPoint>();
        int fftSize = harmonicTransfers[1].Length;
        if (harmonicTransfers.Skip(1).Any(transfer => transfer.Length != 0 && transfer.Length != fftSize))
            return Array.Empty<SweepHarmonicPoint>();

	        double minimumFundamental = Math.Max(
            settings.StartFrequencyHz,
            1000.0 / Math.Max(1.0, settings.ImpulseWindowLeftMs + settings.ImpulseWindowRightMs));
        double harmonicBandwidth = Math.Min(settings.EndFrequencyHz, settings.SampleRate * 0.5 * 0.95);
        double maximumFundamental = harmonicBandwidth / 2.0;
	        int count = Math.Max(2, (int)Math.Floor(Math.Log(maximumFundamental / minimumFundamental, 2) * settings.PointsPerOctave) + 1);
	        IEnumerable<double> exactFrequencies = settings.DistortionEvaluationFrequenciesHz
	            ?? new[] { 80.0, 1000.0, 4000.0 };
	        double[] evaluationFrequencies = Enumerable.Range(0, count)
	            .Select(index => minimumFundamental * Math.Pow(2.0, index / (double)settings.PointsPerOctave))
	            .Append(maximumFundamental)
	            .Concat(exactFrequencies)
	            .Where(frequency => frequency >= minimumFundamental && frequency <= maximumFundamental)
	            .Distinct()
	            .OrderBy(frequency => frequency)
	            .ToArray();
	        var output = new List<SweepHarmonicPoint>(evaluationFrequencies.Length);
        // A fixed, long harmonic IR gate includes broadband and late energy in
        // every harmonic. Evaluate each order through a frequency-dependent
        // time window instead of RMS-adding all FFT-bin magnitudes in a band.
        // Retain the raw complex transfers for coherent repeat averaging.
        var harmonicWindows = new Complex[10][];
        var harmonicCenters = new int[10];
        var harmonicFft = new SweepFourier();
        double sweepLogRatio = Math.Log(settings.EndFrequencyHz / settings.StartFrequencyHz);
        for (int order = 2; order <= 9; order++)
        {
            Complex[] spectrum = harmonicTransfers[order];
            if (spectrum.Length == 0) { harmonicWindows[order] = Array.Empty<Complex>(); continue; }
            var impulse = spectrum.ToArray();
            harmonicFft.Inverse(impulse);
            double Offset(int n) => settings.DurationSeconds * Math.Log(n) / sweepLogRatio * settings.SampleRate;
            int left = Math.Clamp((int)Math.Round(.05 * settings.SampleRate), 8,
                Math.Max(8, (int)Math.Floor((Offset(order + 1) - Offset(order)) * .45)));
            int right = Math.Clamp((int)Math.Round(settings.ImpulseWindowRightMs * settings.SampleRate / 1000), 8,
                Math.Max(8, (int)Math.Floor((Offset(order) - Offset(order - 1)) * .45)));
            int length = Math.Min(impulse.Length, left + right + 1);
            harmonicWindows[order] = impulse.Take(length).ToArray();
            // A delayed nonlinear path must retain its own arrival, including
            // the known delayed-harmonic regression. Avoid chasing weak noise.
            int peak = 0;
            for (int i = 1; i < length; i++) if (impulse[i].Magnitude > impulse[peak].Magnitude) peak = i;
            double median = impulse.Take(length).Select(v => v.Magnitude).OrderBy(v => v).ElementAt(length / 2);
            harmonicCenters[order] = impulse[peak].Magnitude > median * 8 ? peak : Math.Min(left, length - 1);
        }
        // Cache H1 bin powers for its overlapping normalization bands.
        double[][]? bandPowers = null;
        if (settings.PointsPerOctave > 24)
        {
            bandPowers = new double[10][];
            // Only H1 still uses spectral-band normalization. Harmonic
            // magnitudes below are evaluated through their time windows.
            {
                Complex[] spectrum = harmonicTransfers[1];
                var powers = new double[spectrum.Length / 2];
                for (int bin = 1; bin < powers.Length; bin++)
                {
                    double magnitude = spectrum[bin].Magnitude;
                    powers[bin] = magnitude * magnitude;
                }
                bandPowers[1] = powers;
            }
        }
	        foreach (double frequency in evaluationFrequencies)
	        {
            // REW sweep distortion uses 1/24-octave spectral smoothing. This
            // analysis bandwidth is independent of the 1/48-octave display grid.
            double fundamental = DistortionBandMagnitude(harmonicTransfers[1], frequency, settings.SampleRate, bandPowers?[1]);
            if (fundamental <= 1e-5) continue;
            var levels = Enumerable.Repeat(double.NaN, 10).ToArray();
            double harmonicPower = 0;
            for (int order = 2; order <= 9; order++)
            {
	            // The order-n impulse is indexed by its physical output frequency.
	            // An excitation at f therefore contributes at n*f in that spectrum.
	            // Skip only harmonics that exceed Nyquist and cannot be measured.
                double outputFrequency = order * frequency;
                if (outputFrequency > harmonicBandwidth)
	                {
	                    levels[order] = double.NaN;
	                    continue;
	                }
	                if (harmonicTransfers[order].Length == 0)
	                {
	                    levels[order] = double.NaN;
	                    continue;
	                }
	                // The separated order-n impulse response is indexed by output
	                // frequency. A fundamental at f therefore appears at n*f in
	                // that spectrum (Farina swept-sine distortion mapping).
                double reference = settings.UseHarmonicFrequencyAsDistortionReference
                    ? DistortionBandMagnitude(harmonicTransfers[1], outputFrequency, settings.SampleRate, bandPowers?[1]) : fundamental;
                double ratio = reference > Tiny ? DistortionWindowMagnitude(harmonicWindows[order], harmonicCenters[order], outputFrequency, settings.SampleRate) / reference : double.NaN;
                if (micCalib != null && settings.ApplyDistortionCalibration)
                    ratio *= Math.Pow(10.0, -(micCalib.GetGainCorrectionDb(outputFrequency)
                        - micCalib.GetGainCorrectionDb(settings.UseHarmonicFrequencyAsDistortionReference ? outputFrequency : frequency)) / 20.0);
                levels[order] = 20.0 * Math.Log10(ratio + Tiny);
                harmonicPower += ratio * ratio;
            }
            double fundamentalLevelDb = 20.0 * Math.Log10(fundamental + Tiny)
                + 20.0 * Math.Log10(Math.Max(Tiny, Math.Abs(settings.Amplitude)) / Math.Sqrt(2))
                - (settings.ApplyDistortionCalibration ? micCalib?.GetGainCorrectionDb(frequency) ?? 0.0 : 0.0);
	            bool hasMeasuredHarmonic = Enumerable.Range(2, 8).Any(order => double.IsFinite(levels[order]));
	            output.Add(new SweepHarmonicPoint(
	                frequency, levels[2], levels[3], levels[4], levels[5],
	                hasMeasuredHarmonic ? Math.Sqrt(harmonicPower) * 100.0 : double.NaN)
            {
                FundamentalLevelDb = fundamentalLevelDb,
                H6Dbc = levels[6],
                H7Dbc = levels[7],
                H8Dbc = levels[8],
                H9Dbc = levels[9]
            });
        }
        return output;
    }

    private static double DistortionWindowMagnitude(Complex[] impulse, int center, double frequency, int sampleRate)
    {
        // 48 cycles gives a relative frequency resolution of about 1/48 octave.
        // Shrinking the time support at higher frequencies rejects late noise
        // without subtracting a guessed noise level from measured harmonics.
        double halfWidth = Math.Max(4, 24.0 * sampleRate / frequency);
        int first = Math.Max(0, (int)Math.Ceiling(center - halfWidth));
        int last = Math.Min(impulse.Length - 1, (int)Math.Floor(center + halfWidth));
        double angle = -2 * Math.PI * frequency / sampleRate;
        var step = new Complex(Math.Cos(angle), Math.Sin(angle));
        var rotation = Complex.FromPolarCoordinates(1, angle * (first - center));
        Complex sum = Complex.Zero;
        for (int i = first; i <= last; i++)
        {
            double position = Math.Abs(i - center) / halfWidth;
            double weight = position <= .75 ? 1 : .5 + .5 * Math.Cos(Math.PI * (position - .75) / .25);
            sum += impulse[i] * rotation * weight;
            rotation *= step;
            if ((i & 1023) == 1023) rotation = Complex.FromPolarCoordinates(1, angle * (i + 1 - center));
        }
        return sum.Magnitude;
    }

    private static double DistortionBandMagnitude(Complex[] spectrum, double frequency, int sampleRate, double[]? binPowers = null)
    {
        double bin = frequency * spectrum.Length / sampleRate;
        double halfBand = Math.Pow(2.0, 1.0 / 48.0);
        int first = Math.Max(1, (int)Math.Ceiling(bin / halfBand));
        int last = Math.Min(spectrum.Length / 2 - 1, (int)Math.Floor(bin * halfBand));
        if (first > last) return spectrum[Math.Clamp((int)Math.Round(bin), 1, spectrum.Length / 2 - 1)].Magnitude;
        double power = 0;
        for (int i = first; i <= last; i++)
        {
            double magnitude = binPowers == null ? spectrum[i].Magnitude : 0;
            power += binPowers == null ? magnitude * magnitude : binPowers[i];
        }
        return Math.Sqrt(power / (last - first + 1));
    }

    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;

    private static IReadOnlyList<FrequencyResponsePoint> BuildFrequencyResponse(
        Complex[] transfer,
        int sampleRate,
        int delaySamples,
        double minFrequency,
        double maxFrequency,
        int pointsPerOctave,
        MicrophoneCalibration? micCalib = null)
    {
        int fftSize = transfer.Length;
        double nyquist = sampleRate / 2.0;
        maxFrequency = Math.Min(maxFrequency, nyquist * 0.95);
        int count = Math.Max(2, (int)Math.Floor(Math.Log(maxFrequency / minFrequency, 2) * pointsPerOctave) + 1);
        var raw = new List<(double Frequency, double Level, double Phase)>(count);
        for (int index = 0; index < count; index++)
        {
            double frequency = minFrequency * Math.Pow(2.0, index / (double)pointsPerOctave);
            if (frequency > maxFrequency) break;
            int bin = Math.Clamp((int)Math.Round(frequency * fftSize / sampleRate), 1, fftSize / 2 - 1);
            double halfBandRatio = Math.Pow(2.0, 1.0 / (pointsPerOctave * 2.0));
            int lowBin = Math.Max(1, (int)Math.Floor(bin / halfBandRatio));
            int highBin = Math.Min(fftSize / 2 - 1, (int)Math.Ceiling(bin * halfBandRatio));
            double power = 0;
            for (int candidate = lowBin; candidate <= highBin; candidate++)
                power += transfer[candidate].Magnitude * transfer[candidate].Magnitude;
            double magnitude = Math.Sqrt(power / Math.Max(1, highBin - lowBin + 1));
            Complex delayCorrected = transfer[bin] * Complex.FromPolarCoordinates(
                1.0,
                2.0 * Math.PI * bin * delaySamples / fftSize);
            double rawLevel = 20.0 * Math.Log10(magnitude + Tiny);
            double calibOffset = micCalib != null ? micCalib.GetGainCorrectionDb(frequency) : 0.0;
            raw.Add((frequency, rawLevel - calibOffset, delayCorrected.Phase));
        }

        int referenceIndex = raw.Select((value, index) => (index, error: Math.Abs(Math.Log(value.Frequency / 1000.0))))
            .OrderBy(item => item.error).First().index;
        double referenceDb = raw[referenceIndex].Level;
        var unwrapped = UnwrapPhases(raw.Select(item => item.Phase).ToArray());
        var result = new List<FrequencyResponsePoint>(raw.Count);
        for (int index = 0; index < raw.Count; index++)
        {
            double groupDelayMs = 0;
            if (index > 0 && index < raw.Count - 1)
            {
                double phaseDelta = unwrapped[index + 1] - unwrapped[index - 1];
                double frequencyDelta = raw[index + 1].Frequency - raw[index - 1].Frequency;
                groupDelayMs = -phaseDelta / (2.0 * Math.PI * frequencyDelta) * 1000.0;
            }
            result.Add(new FrequencyResponsePoint(
                raw[index].Frequency,
                raw[index].Level,
                raw[index].Level - referenceDb,
                unwrapped[index] * 180.0 / Math.PI,
                groupDelayMs));
        }
        return result;
    }

    private static IReadOnlyList<DecayCurvePoint> BuildEnergyDecayCurve(double[] impulse, int sampleRate, int directIndex)
    {
        if (impulse.Length <= directIndex) return Array.Empty<DecayCurvePoint>();
        int count = impulse.Length - directIndex;
        int noiseStart = directIndex + Math.Max(0, (int)(count * 0.9));
        double noisePower = impulse.Skip(noiseStart).Select(value => value * value).DefaultIfEmpty(0).Average();
        var cumulative = new double[count];
        double energy = 0;
        for (int source = impulse.Length - 1; source >= directIndex; source--)
        {
            int target = source - directIndex;
            energy += impulse[source] * impulse[source];
            double remainingNoise = noisePower * (impulse.Length - source);
            cumulative[target] = Math.Max(Tiny, energy - remainingNoise);
        }
        double reference = cumulative[0];
        int stride = Math.Max(1, count / 4000);
        var points = new List<DecayCurvePoint>();
        for (int index = 0; index < count; index += stride)
            points.Add(new DecayCurvePoint(index / (double)sampleRate, 10.0 * Math.Log10(cumulative[index] / reference + Tiny)));
        return points;
    }

    private static DecayEstimate FitDecay(IReadOnlyList<DecayCurvePoint> curve, string name, double startDb, double endDb)
    {
        int startIndex = -1;
        for (int index = 0; index < curve.Count; index++)
        {
            if (curve[index].LevelDb <= startDb)
            {
                startIndex = index;
                break;
            }
        }

        if (startIndex < 0)
            return new DecayEstimate(name, startDb, endDb, double.NaN, double.NaN, 0, false, $"Đường suy giảm chưa đạt {startDb:F0} dB.");

        int endIndex = -1;
        for (int index = startIndex; index < curve.Count; index++)
        {
            if (curve[index].LevelDb <= endDb)
            {
                endIndex = index;
                break;
            }
        }

        if (endIndex < 0)
            return new DecayEstimate(name, startDb, endDb, double.NaN, double.NaN, 0, false, $"Không đủ dynamic range đến {endDb:F0} dB.");

        DecayCurvePoint[] selected = curve.Skip(startIndex).Take(endIndex - startIndex + 1).ToArray();
        if (selected.Length < 20)
            return new DecayEstimate(name, startDb, endDb, double.NaN, double.NaN, 0, false, "Không đủ dynamic range.");
        double meanX = selected.Average(point => point.TimeSeconds);
        double meanY = selected.Average(point => point.LevelDb);
        double denominator = selected.Sum(point => Math.Pow(point.TimeSeconds - meanX, 2));
        if (denominator < Tiny)
            return new DecayEstimate(name, startDb, endDb, double.NaN, double.NaN, 0, false, "Khoảng thời gian hồi quy quá ngắn.");
        double slope = selected.Sum(point => (point.TimeSeconds - meanX) * (point.LevelDb - meanY)) / denominator;
        double intercept = meanY - slope * meanX;
        double total = selected.Sum(point => Math.Pow(point.LevelDb - meanY, 2));
        double residual = selected.Sum(point => Math.Pow(point.LevelDb - (intercept + slope * point.TimeSeconds), 2));
        double r2 = total > Tiny ? 1.0 - residual / total : 0;
        double rt60 = slope < -1e-6 ? -60.0 / slope : double.NaN;
        double capturedSeconds = curve.Count > 0 ? curve[^1].TimeSeconds : 0;
        bool captureLongEnough = double.IsFinite(rt60) && capturedSeconds >= rt60 * 1.2;
        bool valid = double.IsFinite(rt60) && rt60 > 0 && rt60 < 30 && r2 >= 0.90 && captureLongEnough;
        string status = valid ? "Hợp lệ chẩn đoán"
            : !captureLongEnough && double.IsFinite(rt60)
                ? $"Không tin cậy: đoạn decay {capturedSeconds:F2} s ngắn hơn 1,2 x RT60 ước tính."
                : $"Không tin cậy (R²={r2:F3}).";
        return new DecayEstimate(name, startDb, endDb, rt60, slope, r2, valid, status);
    }

    private static IReadOnlyList<WaterfallPoint> BuildWaterfall(
        double[] impulse,
        int sampleRate,
        int directIndex,
        double minFrequency,
        double maxFrequency)
    {
        int fftSize = 4096;
        if (impulse.Length - directIndex < fftSize) return Array.Empty<WaterfallPoint>();
        int hop = Math.Max(256, sampleRate / 100);
        int maxFrames = Math.Min(80, 1 + (impulse.Length - directIndex - fftSize) / hop);
        var output = new List<WaterfallPoint>(maxFrames * 48);
        double globalPeak = Tiny;
        var frames = new List<(double TimeMs, Complex[] Spectrum)>();
        for (int frame = 0; frame < maxFrames; frame++)
        {
            int start = directIndex + frame * hop;
            var spectrum = new Complex[fftSize];
            for (int index = 0; index < fftSize; index++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / (fftSize - 1));
                spectrum[index] = impulse[start + index] * window;
            }
            Fourier.Forward(spectrum, FourierOptions.Matlab);
            globalPeak = Math.Max(globalPeak, spectrum.Take(fftSize / 2).Max(item => item.Magnitude));
            frames.Add((frame * hop * 1000.0 / sampleRate, spectrum));
        }

        const int frequencyPoints = 64;
        foreach ((double timeMs, Complex[] spectrum) in frames)
        {
            for (int index = 0; index < frequencyPoints; index++)
            {
                double frequency = minFrequency * Math.Pow(maxFrequency / minFrequency, index / (double)(frequencyPoints - 1));
                int bin = Math.Clamp((int)Math.Round(frequency * fftSize / sampleRate), 1, fftSize / 2 - 1);
                output.Add(new WaterfallPoint(timeMs, frequency, 20.0 * Math.Log10(spectrum[bin].Magnitude / globalPeak + Tiny)));
            }
        }
        return output;
    }

    private static (double ClarityDb, double DefinitionPercent) CalculateClarity(
        double[] impulse,
        int sampleRate,
        int directIndex,
        double boundarySeconds)
    {
        int boundary = Math.Min(impulse.Length, directIndex + (int)Math.Round(boundarySeconds * sampleRate));
        double early = 0;
        double late = 0;
        for (int index = directIndex; index < impulse.Length; index++)
        {
            double energy = impulse[index] * impulse[index];
            if (index < boundary) early += energy;
            else late += energy;
        }
        double clarity = 10.0 * Math.Log10((early + Tiny) / (late + Tiny));
        double definition = 100.0 * early / (early + late + Tiny);
        return (clarity, definition);
    }

    private static (IReadOnlyList<TimeDomainPoint> Impulse,
        IReadOnlyList<TimeDomainPoint> Step,
        IReadOnlyList<TimeDomainPoint> Etc) BuildTimeDomainViews(
        double[] impulse,
        int sampleRate,
        int directIndex)
    {
        int stride = Math.Max(1, impulse.Length / 12000);
        double[] envelopeDb = ImpulsePlotAnalysis.EnvelopeDb(impulse);
        var cumulative = new double[impulse.Length];
        double running = 0;
        for (int index = 0; index < impulse.Length; index++)
        {
            running += impulse[index];
            cumulative[index] = running;
        }
        var impulsePoints = new List<TimeDomainPoint>();
        var stepPoints = new List<TimeDomainPoint>();
        var etcPoints = new List<TimeDomainPoint>();
        for (int start = 0; start < impulse.Length; start += stride)
        {
            int end = Math.Min(impulse.Length, start + stride);
            int index = start;
            for (int candidate = start + 1; candidate < end; candidate++)
                if (Math.Abs(impulse[candidate]) > Math.Abs(impulse[index])) index = candidate;
            double time = (index - directIndex) / (double)sampleRate;
            impulsePoints.Add(new TimeDomainPoint(time, impulse[index]));
            stepPoints.Add(new TimeDomainPoint(time, cumulative[index]));
            int envelopeIndex = start;
            for (int candidate = start + 1; candidate < end; candidate++)
                if (envelopeDb[candidate] > envelopeDb[envelopeIndex]) envelopeIndex = candidate;
            etcPoints.Add(new TimeDomainPoint((envelopeIndex - directIndex) / (double)sampleRate, envelopeDb[envelopeIndex]));
        }
        return (impulsePoints, stepPoints, etcPoints);
    }

    private static void ExportTimeDomain(string path, string valueHeader, IReadOnlyList<TimeDomainPoint> points)
    {
        var csv = new StringBuilder($"TimeSeconds,{valueHeader}\n");
        foreach (TimeDomainPoint point in points)
            csv.AppendLine($"{Format(point.TimeSeconds)},{Format(point.Value)}");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static double[] UnwrapPhases(double[] phase)
    {
        if (phase.Length == 0) return phase;
        var output = new double[phase.Length];
        output[0] = phase[0];
        for (int index = 1; index < phase.Length; index++)
        {
            double delta = phase[index] - phase[index - 1];
            while (delta > Math.PI) delta -= 2.0 * Math.PI;
            while (delta < -Math.PI) delta += 2.0 * Math.PI;
            output[index] = output[index - 1] + delta;
        }
        return output;
    }

    private static void ValidateSettings(LogSweepSettings settings)
    {
        if (settings.SampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(settings.SampleRate));
        if (settings.StartFrequencyHz <= 0 || settings.EndFrequencyHz <= settings.StartFrequencyHz
            || settings.EndFrequencyHz >= settings.SampleRate / 2.0)
            throw new ArgumentException("Dải sweep phải nằm dưới Nyquist.", nameof(settings));
        if (settings.DurationSeconds < 0.5 || settings.DurationSeconds > 30)
            throw new ArgumentOutOfRangeException(nameof(settings.DurationSeconds));
        // The routing control permits 0 dBFS peak (= 1). A sine multiplied by
        // a fade in [0,1] stays within full scale; do not silently reduce its level.
        if (!double.IsFinite(settings.Amplitude) || settings.Amplitude <= 0 || settings.Amplitude > 1.0)
            throw new ArgumentOutOfRangeException(nameof(settings.Amplitude), "Biên độ sweep phải lớn hơn 0 và không vượt quá 1 (0 dBFS).");
        if (!double.IsFinite(settings.RecordingGain) || settings.RecordingGain <= 0 || settings.RecordingGain > 20)
            throw new ArgumentOutOfRangeException(nameof(settings.RecordingGain));
        if (!Enum.IsDefined(settings.ImpulseWindowKind)) throw new ArgumentOutOfRangeException(nameof(settings.ImpulseWindowKind));
        if (!double.IsFinite(settings.ImpulseWindowLeftMs) || settings.ImpulseWindowLeftMs < 0.2 || settings.ImpulseWindowLeftMs > 500)
            throw new ArgumentOutOfRangeException(nameof(settings.ImpulseWindowLeftMs));
        if (!double.IsFinite(settings.ImpulseWindowRightMs) || settings.ImpulseWindowRightMs < 2 || settings.ImpulseWindowRightMs > 500)
            throw new ArgumentOutOfRangeException(nameof(settings.ImpulseWindowRightMs));
        if (!double.IsFinite(settings.FrequencyDependentWindowCycles)
            || settings.FrequencyDependentWindowCycles < 2 || settings.FrequencyDependentWindowCycles > 100)
            throw new ArgumentOutOfRangeException(nameof(settings.FrequencyDependentWindowCycles));
    }

    private static (double SignalLevelDbFs, double NoiseFloorDbFs, double EstimatedSnrDb)
        CalculateAcquisitionLevels(float[] samples, int sampleRate)
    {
        int frameLength = Math.Min(samples.Length, Math.Max(256, sampleRate / 20));
        var frameRms = new List<double>();
        for (int start = 0; start + frameLength <= samples.Length; start += frameLength)
        {
            double power = 0;
            for (int index = start; index < start + frameLength; index++)
                power += (double)samples[index] * samples[index];
            frameRms.Add(Math.Sqrt(power / frameLength));
        }
        if (frameRms.Count == 0)
        {
            double rms = Math.Sqrt(samples.Average(value => (double)value * value));
            frameRms.Add(rms);
        }

        double[] ordered = frameRms.OrderBy(value => value).ToArray();
        double noiseRms = Percentile(ordered, 0.10);
        double signalRms = Percentile(ordered, 0.90);
        double signalDbFs = 20.0 * Math.Log10(signalRms + Tiny);
        double noiseDbFs = 20.0 * Math.Log10(noiseRms + Tiny);
        double snrDb = Math.Max(0, 20.0 * Math.Log10((signalRms + Tiny) / (noiseRms + Tiny)));
        return (Math.Max(-180, signalDbFs), Math.Max(-180, noiseDbFs), Math.Min(180, snrDb));
    }

    private static double Percentile(double[] ordered, double fraction)
    {
        if (ordered.Length == 0) return 0;
        double position = Math.Clamp(fraction, 0, 1) * (ordered.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(ordered.Length - 1, lower + 1);
        double remainder = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * remainder;
    }

    private static double CalculateImpulseBackgroundRms(
        Complex[] impulse,
        int usefulLength,
        int peakIndex,
        int guardSamples)
    {
        double power = 0;
        int count = 0;
        int excludedStart = Math.Max(0, peakIndex - guardSamples);
        int excludedEnd = Math.Min(usefulLength, peakIndex + guardSamples + 1);
        for (int index = 0; index < usefulLength; index++)
        {
            if (index >= excludedStart && index < excludedEnd) continue;
            double value = impulse[index].Real;
            power += value * value;
            count++;
        }
        return count > 0 ? Math.Sqrt(power / count) : 0;
    }

    private static double CalculateNormalizedSweepCorrelation(
        float[] excitation,
        float[] recorded,
        int delaySamples)
    {
        if (delaySamples < 0 || delaySamples >= recorded.Length) return 0;
        int count = Math.Min(excitation.Length, recorded.Length - delaySamples);
        if (count < excitation.Length / 2) return 0;

        double dot = 0;
        double inputPower = 0;
        double outputPower = 0;
        for (int index = 0; index < count; index++)
        {
            double input = excitation[index];
            double output = recorded[delaySamples + index];
            dot += input * output;
            inputPower += input * input;
            outputPower += output * output;
        }
        return Math.Abs(dot) / Math.Sqrt(inputPower * outputPower + Tiny);
    }

    private static int FindEarliestSweepCorrelationDelay(
        IReadOnlyList<Complex> crossCorrelation,
        float[] excitation,
        float[] recorded,
        int sampleRate,
        double maximumDelayMs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int maximumByData = Math.Max(0, recorded.Length - Math.Max(1, excitation.Length / 2));
        int maximumByTime = Math.Max(0, (int)Math.Round(maximumDelayMs * sampleRate / 1000.0));
        int limit = Math.Min(Math.Min(maximumByData, maximumByTime), crossCorrelation.Count - 1);
        int strongestDelay = 0;
        double strongestMagnitude = 0.0;
        for (int delay = 0; delay <= limit; delay++)
        {
            if ((delay & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            double magnitude = crossCorrelation[delay].Magnitude;
            if (magnitude <= strongestMagnitude) continue;
            strongestMagnitude = magnitude;
            strongestDelay = delay;
        }

        // With a silent capture every lag is a flat zero "local maximum".
        // The old loop then recomputed a full N-sample dot product at every
        // delay (over 57 billion iterations for 512K / 2.5 s at 44.1 kHz).
        if (strongestMagnitude == 0) return strongestDelay;

        double[] inputPower = new double[excitation.Length + 1];
        double[] outputPower = new double[recorded.Length + 1];
        for (int i = 0; i < excitation.Length; i++)
        {
            if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            double sample = excitation[i]; inputPower[i + 1] = inputPower[i] + sample * sample;
        }
        for (int i = 0; i < recorded.Length; i++)
        {
            if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            double sample = recorded[i]; outputPower[i + 1] = outputPower[i] + sample * sample;
        }
        double NormalizedAt(int delay)
        {
            int count = Math.Min(excitation.Length, recorded.Length - delay);
            if (count < excitation.Length / 2) return 0;
            double power = outputPower[delay + count] - outputPower[delay];
            // The existing zero-padded FFT correlation already contains the
            // linear dot product for this lag. Keep direct summation around the
            // acceptance boundary (or unstable power subtraction) so the gate
            // cannot change due to FFT/prefix roundoff. Final reported scores
            // still use the original direct calculation below the caller.
            double normalized = Math.Abs(crossCorrelation[delay].Real) / Math.Sqrt(inputPower[count] * Math.Max(0, power) + Tiny);
            return !double.IsFinite(normalized) || power <= outputPower[delay + count] * 1e-10 || Math.Abs(normalized - .02) <= 1e-8
                ? CalculateNormalizedSweepCorrelation(excitation, recorded, delay) : normalized;
        }

        // A reflection can be stronger than the direct sound. Anchor the impulse gate to
        // the first credible correlation peak, not unconditionally to the largest peak.
        // This is the same direct-arrival rule used for the deconvolved impulse above.
        int stride = Math.Max(1, (limit + 1) / 20000);
        double[] sampledMagnitudes = Enumerable.Range(0, (limit + stride) / stride)
            .Select(index => crossCorrelation[Math.Min(limit, index * stride)].Magnitude)
            .OrderBy(value => value)
            .ToArray();
        double background = sampledMagnitudes.Length == 0
            ? 0.0
            : sampledMagnitudes[sampledMagnitudes.Length / 2] / 0.6744897501960817;
        double threshold = Math.Max(strongestMagnitude * 0.20, background * 8.0);
        for (int delay = 1; delay < limit; delay++)
        {
            if ((delay & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            double magnitude = crossCorrelation[delay].Magnitude;
            if (magnitude >= threshold
                && magnitude >= crossCorrelation[delay - 1].Magnitude
                && magnitude >= crossCorrelation[delay + 1].Magnitude
                && NormalizedAt(delay) >= 0.02)
                return delay;
        }
        return strongestDelay;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value && result < 1 << 29) result <<= 1;
        return result;
    }

    private static string Format(double value) =>
        double.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "NaN";

    public static RewDistortionStats CalculateRewDistortionStats(IReadOnlyList<SweepHarmonicPoint> points)
    {
        if (points == null || points.Count == 0)
        {
            return new RewDistortionStats(0, 0, 1000, "N/A", double.NaN, double.NaN, double.NaN, double.NaN, Array.Empty<RewDistortionPoint>());
        }

        var validPoints = points.Where(p => double.IsFinite(p.ThdPercent) && p.ThdPercent > 0).ToList();
        if (validPoints.Count == 0)
        {
            return new RewDistortionStats(0, 0, 1000, "N/A", double.NaN, double.NaN, double.NaN, double.NaN, Array.Empty<RewDistortionPoint>());
        }

        double avgThd = validPoints.Average(p => p.ThdPercent);
        var maxPoint = validPoints.MaxBy(p => p.ThdPercent) ?? validPoints[0];

        double avgH2 = points.Select(p => p.H2Dbc).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        double avgH3 = points.Select(p => p.H3Dbc).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        double avgH4 = points.Select(p => p.H4Dbc).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();
        double avgH5 = points.Select(p => p.H5Dbc).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average();

        string dominant = "H2";
        double maxLevel = avgH2;
        if (double.IsFinite(avgH3) && avgH3 > maxLevel) { dominant = "H3"; maxLevel = avgH3; }
        if (double.IsFinite(avgH4) && avgH4 > maxLevel) { dominant = "H4"; maxLevel = avgH4; }
        if (double.IsFinite(avgH5) && avgH5 > maxLevel) { dominant = "H5"; }

        double[] targetFreqs = { 100, 250, 500, 1000, 2000, 4000, 8000, 10000 };
        var table = new List<RewDistortionPoint>();

        foreach (double target in targetFreqs)
        {
            var closest = points.OrderBy(p => Math.Abs(Math.Log10(p.FundamentalFrequencyHz) - Math.Log10(target))).FirstOrDefault();
            if (closest != null && Math.Abs(Math.Log2(closest.FundamentalFrequencyHz / target)) < 0.35)
            {
                table.Add(new RewDistortionPoint(target, closest.ThdPercent, closest.H2Dbc, closest.H3Dbc, closest.H4Dbc, closest.H5Dbc));
            }
        }

        return new RewDistortionStats(avgThd, maxPoint.ThdPercent, maxPoint.FundamentalFrequencyHz, dominant, avgH2, avgH3, avgH4, avgH5, table);
    }

    public static IReadOnlyList<BandRt60Result> CalculateOctaveBandRt60(
        double[] impulse,
        int sampleRate,
        int directIndex)
    {
        double[] centerFrequencies = { 63, 125, 250, 500, 1000, 2000, 4000, 8000 };
        var results = new List<BandRt60Result>();
        if (impulse == null || impulse.Length <= directIndex || sampleRate <= 0) return results;

        int fftSize = NextPowerOfTwo(impulse.Length);
        var spectrum = new Complex[fftSize];
        for (int i = 0; i < impulse.Length; i++) spectrum[i] = impulse[i];
        Fourier.Forward(spectrum, FourierOptions.Matlab);

        foreach (double fc in centerFrequencies)
        {
            double fLower = fc / Math.Sqrt(2.0);
            double fUpper = fc * Math.Sqrt(2.0);
            if (fUpper >= sampleRate / 2.0) continue;

            var filteredSpectrum = new Complex[fftSize];
            for (int k = 0; k < fftSize / 2; k++)
            {
                double f = (double)k * sampleRate / fftSize;
                double gain = 0.0;
                if (f >= fLower && f <= fUpper)
                {
                    gain = 1.0;
                }
                else if (f > fLower * 0.75 && f < fLower)
                {
                    gain = 0.5 * (1.0 + Math.Cos(Math.PI * (fLower - f) / (fLower * 0.25)));
                }
                else if (f > fUpper && f < fUpper * 1.25)
                {
                    gain = 0.5 * (1.0 + Math.Cos(Math.PI * (f - fUpper) / (fUpper * 0.25)));
                }

                filteredSpectrum[k] = spectrum[k] * gain;
                if (k > 0)
                    filteredSpectrum[fftSize - k] = Complex.Conjugate(filteredSpectrum[k]);
            }

            Fourier.Inverse(filteredSpectrum, FourierOptions.Matlab);
            var filteredImpulse = new double[impulse.Length];
            for (int i = 0; i < impulse.Length; i++) filteredImpulse[i] = filteredSpectrum[i].Real;

            var edc = BuildEnergyDecayCurve(filteredImpulse, sampleRate, directIndex);
            var edt = FitDecay(edc, "EDT", 0, -10);
            var t20 = FitDecay(edc, "T20", -5, -25);
            var t30 = FitDecay(edc, "T30", -5, -35);

            double r2 = t20.IsValid ? t20.RSquared : (t30.IsValid ? t30.RSquared : edt.RSquared);
            bool isValid = t20.IsValid || t30.IsValid || edt.IsValid;
            results.Add(new BandRt60Result(fc, edt.Rt60Seconds, t20.Rt60Seconds, t30.Rt60Seconds, r2, isValid));
        }

        return results;
    }
}
