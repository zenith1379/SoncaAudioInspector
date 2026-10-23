using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public class TestStep
{
	public string Name { get; set; } = "";
	public string Status { get; set; } = "Waiting"; // Waiting, Running, Pass, Fail
	public string Details { get; set; } = "";
}

public class TestRunner
{
	public const string FrequencyResponseLevelBasis = "RECEIVED_RMS_DBFS_V1";
	public const string OneKilohertzNormalizedLevelBasis = "RECEIVED_RMS_DBR_1KHZ_V1";
	public sealed record FeqRepeatabilityPoint(double Frequency, double MinimumDb, double MaximumDb, double SpreadDb, IReadOnlyList<double> RunValues, string Band, bool Critical);

	public sealed record FeqRepeatabilityAssessment(bool IsInvalid, int CheckedPointCount, IReadOnlyList<FeqRepeatabilityPoint> UnstablePoints, int BassUnstable, int BassChecked, int MidUnstable, int MidChecked, int TrebleUnstable, int TrebleChecked)
	{
		public string BandSummary => $"Bass {BassUnstable}/{BassChecked}, Mid {MidUnstable}/{MidChecked}, Treble {TrebleUnstable}/{TrebleChecked}";
	}

	private sealed record DistortionPointResult(double FrequencyHz, double LimitPercent, ToneQualityMetrics Quality, int ValidRuns, bool AnySilent, bool AnyClipping, bool AnyWrongFrequency, bool AnyLowSnr, double DiagnosticThdPercent, double AcquisitionSnrDb = double.NaN);

	public static int BassCount = 23;

	public static int MidCount = 23;

	public static int TrebleCount = 23;

	private const double DistortionCaptureDurationSeconds = 3.0;

	private const double DistortionAnalysisDurationSeconds = 2.0;

	private const int DistortionInterPointSettleMilliseconds = 3000;

	private const int DistortionTargetValidRuns = 1;

	private const int DistortionMaxAttempts = 1;

	private const double DefaultBassDistortionSampleScale = 0.35;
	private const double AutoTestToneSampleScale = 0.5;

	private const double FeqPilotFrequencyHz = 1100.0;

	public const double LogSweepTailSeconds = 1.0;

	public const double LogSweepPrerollSeconds = 0.5;

	public const int LogSweepPointsPerOctave = 24;

	public const double LogSweepPlaybackStartHz = 18.0;

	public const double LogSweepPlaybackEndHz = 22000.0;

	public const double BassMin = 20.0;

	public const double BassMax = 250.0;

	public const double MidMin = 250.0;

	public const double MidMax = 4000.0;

	public const double TrebleMin = 4000.0;

	public const double TrebleMax = 20000.0;
	public double EvaluationMaxFrequencyHz { get; set; } = 18000.0;
	public bool RestrictFeqToEvaluationRange { get; set; }

	private readonly AudioEngine _audioEngine;

	private readonly Dictionary<double, double> _dutNoiseFloorByFrequencyDbFs = new Dictionary<double, double>();

	private List<TestStep> _steps = new List<TestStep>();

	private volatile bool _isCancelled;
	private CancellationTokenSource? _runCancellation;
	private CancellationToken RunCancellation => _runCancellation?.Token ?? CancellationToken.None;

	public double MultitoneDurationSeconds { get; set; } = 10.0;

	public double LogSweepDurationSeconds { get; set; } = 65536.0 / 44100.0;
	public bool ApplyDistortionCalibration { get; set; }
	public bool UseHarmonicFrequencyAsDistortionReference { get; set; }
	public ImpulseEffectSettings ImpulseEffectsSettings { get; set; } = new();
	public IReadOnlyDictionary<double, double> LastUnsmoothedFrequencyResponse { get; private set; } = new Dictionary<double, double>();
	public event Action<IReadOnlyDictionary<double, double>>? OnUnsmoothedResponseReady;

	public double PlaybackLevelDbFs { get; set; } = 0.0;

	public double PlaybackAmplitude => Math.Pow(10.0, PlaybackLevelDbFs / 20.0);

	public static double[] TestFrequencies => (from f in TestFrequencyBands.SelectMany(((string Name, double[] Frequencies) band) => band.Frequencies).Distinct()
		orderby f
		select f).ToArray();

	public static (string Name, double[] Frequencies)[] TestFrequencyBands
	{
		get
		{
			double[] item = (from f in GenerateLogFrequencyArray(20.0, 250.0, BassCount)
				where f < 250.0
				select f).ToArray();
			double[] array = (from f in GenerateLogFrequencyArray(250.0, 4000.0, MidCount)
				where f < 4000.0
				select f).ToArray();
			double[] item2 = GenerateLogFrequencyArray(4000.0, 20000.0, TrebleCount);
			if (!Enumerable.Contains(array, 1000.0))
			{
				array = (from f in array.Append(1000.0).Distinct()
					orderby f
					select f).ToArray();
			}
			return new(string, double[])[3]
			{
				("Bass", item),
				("Mid", array),
				("Treble", item2)
			};
		}
	}

	public double FreqResponseToleranceDb { get; set; } = 3.0;

	public double ThdLimitPercent { get; set; } = 0.5;

	public double? BassThdLimitPercent { get; set; }

	public double? MidThdLimitPercent { get; set; }

	public double? TrebleThdLimitPercent { get; set; }

	public double? ThdNLimitPercent { get; set; }

	public double? MinimumSinadDb { get; set; }

	public double? MinimumSnrDb { get; set; }

	public double? MaximumDcOffset { get; set; }

	public double MaximumToneFrequencyErrorHz { get; set; } = 5.0;

	public double MinimumInputSignalDbFs { get; set; } = -70.0;

	public double MaximumClippedSamplePercent { get; set; } = 0.01;

	public double BassDistortionSampleScale { get; set; } = 0.35;

	public double BassThdFrequencyHz { get; set; } = 80.0;

	public double MidThdFrequencyHz { get; set; } = 1000.0;

	public double TrebleThdFrequencyHz { get; set; } = 4000.0;

	// REW-style synchronous pre-averaging is useful for a shared-clock loopback.
	// Two sweeps improve S/N by nearly 3 dB while keeping the default run practical.
	public int LogSweepVerificationRuns { get; set; } = 1;

	public bool RequireLogSweepPhaseAlignment { get; set; } = true;

	public Dictionary<double, double>? StandardCurve { get; set; }

	public Dictionary<double, FrequencyLimitPoint>? FrequencyLimits { get; set; }

	public List<CriticalFrequencyZone> CriticalZones { get; set; } = new List<CriticalFrequencyZone>();

	public bool EnableNoiseDiagnostics { get; set; }

	public bool AutoTestOneKilohertzOnly { get; set; }

	public bool ReuseSuiteNoiseFloor { get; set; }

	public NoiseAssessment? SuiteSharedNoiseAssessment { get; set; }

	public Dictionary<double, double> SuiteSharedNoiseFloorByFrequency { get; set; } = new Dictionary<double, double>();

	public double? SuiteSharedNoiseFloorDbFs { get; set; }

	public bool UseLogSweepFrequencyResponse { get; set; }

	public bool UseCombinedMultitoneFrequencyResponse { get; set; } = true;

	public double PlaybackFrequencyScale { get; set; } = 1.0;

	public MMDevice? AmbientRecordingDevice { get; set; }

	public double? DutMicrophoneCalibrationOffsetDb { get; set; }

	public double? AmbientMicrophoneCalibrationOffsetDb { get; set; }

	public MicrophoneCalibration? FeqMicrophoneCalibration { get; set; }

	public double AmbientNoiseLimitDbSpl { get; set; } = 70.0;

	public double AmbientNoiseLimitDbFs { get; set; } = -45.0;

	public double DutNoiseSeparationMarginDb { get; set; } = 6.0;

	public int AmbientNoiseMaxRetries { get; set; } = 1;

	public double MinimumFeqSnrDb { get; set; } = 12.0;

	public double MaxLowSnrPointRatio { get; set; } = 0.1;

	public NoiseAssessment? LastNoiseAssessment { get; private set; }

	public double LastMaxDevPercent { get; private set; }

	public double LastAvgDevPercent { get; private set; }

	public bool HasComparedToStandard { get; private set; }

	public bool IsRubBuzzTest { get; set; }

	public double RubBuzzTestFreq { get; set; } = 70.0;

	public double RubBuzzLimit { get; set; } = 1.5;

	public double LastRubBuzzValue { get; private set; }

	public double LastFrequencyMaxDeviationDb { get; private set; }

	public double LastMeasuredThdPercent { get; private set; } = double.NaN;
	public string LastPreflightWarning { get; private set; } = "";

	public ToneQualityMetrics? LastToneQuality { get; private set; }

	public IReadOnlyDictionary<double, ToneQualityMetrics> LastToneQualities { get; private set; } = new Dictionary<double, ToneQualityMetrics>();

	public RubBuzzMetrics? LastRubBuzzMetrics { get; private set; }

	public double LastSignalLevelDbFs { get; private set; } = double.NaN;
	public double LastOneKilohertzLevelDbFs { get; private set; } = double.NaN;
	public bool NormalizeFrequencyResponseToOneKilohertz { get; set; }
	public string CurrentFrequencyResponseLevelBasis => NormalizeFrequencyResponseToOneKilohertz ? OneKilohertzNormalizedLevelBasis : FrequencyResponseLevelBasis;
	public event Action<double>? OnOneKilohertzLevelMeasured;
	public event Action<double, double>? OnOneKilohertzToneMeasured;

	public double LastFeqPeakSample { get; private set; }

	public bool InputClippingDetected { get; private set; }

	public bool FeqRepeatabilityInvalid { get; private set; }

	public bool ComplexPhaseAlignmentInvalid { get; private set; }

	public bool MissingFrequencyLimits { get; private set; }

	public bool ReferenceAcquisitionValid { get; private set; }

	public string LastFeqInvalidReason { get; private set; } = "";

	public bool LastFeqInvalidIsRetryableNoise { get; private set; }

	public bool SilentInputDetected { get; private set; }

	public bool BassPassed { get; private set; } = true;

	public bool MidPassed { get; private set; } = true;

	public bool TreblePassed { get; private set; } = true;

	public bool ThdPassed { get; private set; } = true;

	public bool ThdAcquisitionInvalid { get; private set; }

	public IReadOnlyList<TestStep> Steps => _steps;

	public event Action<List<TestStep>>? OnStepsChanged;

	public event Action<string, string>? OnLogMessage;

	public event Action<double, double>? OnFrequencyResponsePoint;

	public event Action<double, double[], double[], double, bool>? OnThdSpectrumReady;

	public event Action<bool>? OnTestCompleted;

	public event Action<string, string>? OnTestSubstatusChanged;

	public event Action<NoiseAssessment>? OnNoiseAssessmentReady;

	public event Action<float[]>? OnRealTimeRecordedSamples;

	public event Action<float[], double, double, double, bool>? OnMeasurementCaptured;

	public IReadOnlyList<SweepHarmonicPoint>? LastSweepHarmonics { get; private set; }

	public StandardAcousticResult? LastSweepResult { get; private set; }
	public event Action<StandardAcousticResult>? OnSweepResultReady;

	public SweepDistortionSummary? LastSweepDistortion { get; private set; }

	public event Action<SweepDistortionSummary>? OnSweepDistortionReady;

	public SweepHarmonicPoint? FindSweepDistortionPoint(IReadOnlyList<SweepHarmonicPoint>? points, double nominalFrequencyHz)
	{
		if (points == null || points.Count == 0 || nominalFrequencyHz <= 0.0)
		{
			return null;
		}
		double physicalFrequencyHz = ScalePlaybackFrequency(nominalFrequencyHz);
		SweepHarmonicPoint? sweepHarmonicPoint = points.Where((SweepHarmonicPoint point) => point.FundamentalFrequencyHz > 0.0).MinBy((SweepHarmonicPoint point) => Math.Abs(Math.Log(point.FundamentalFrequencyHz / physicalFrequencyHz)));
		return (sweepHarmonicPoint != null && Math.Abs(Math.Log(sweepHarmonicPoint.FundamentalFrequencyHz / physicalFrequencyHz, 2.0)) <= 0.35) ? sweepHarmonicPoint : null;
	}

	public SweepDistortionSummary BuildSweepDistortionSummary(StandardAcousticResult? result, double? frequencyHz = null)
	{
		double targetFreq = frequencyHz ?? MidThdFrequencyHz;
		if (targetFreq <= 0.0) targetFreq = 1000.0;
		if (result == null)
		{
			return new SweepDistortionSummary(targetFreq, double.NaN, new Dictionary<int, double>(), double.NaN, double.NaN, double.NaN, "N/A", false, "Chưa có kết quả sweep");
		}
		SweepHarmonicPoint? sweepHarmonicPoint = FindSweepDistortionPoint(result.SweepHarmonics, targetFreq);
		Dictionary<int, double> dictionary = new Dictionary<int, double>();
		if (sweepHarmonicPoint != null)
		{
			for (int i = 2; i <= 9; i++)
			{
				double harmonicDbc = sweepHarmonicPoint.GetHarmonicDbc(i);
				dictionary[i] = (double.IsFinite(harmonicDbc) ? (100.0 * Math.Pow(10.0, harmonicDbc / 20.0)) : double.NaN);
			}
		}
		double noiseFloorPercent = ((double.IsFinite(result.NoiseFloorDbFs) && double.IsFinite(result.SignalLevelDbFs)) ? (100.0 * Math.Pow(10.0, (result.NoiseFloorDbFs - result.SignalLevelDbFs) / 20.0)) : double.NaN);
		int num = (from item in dictionary
			where double.IsFinite(item.Value)
			select item.Key).DefaultIfEmpty(1).Max();
		bool captureValid = result.Validity != "INVALID"
			&& !result.IsClipped
			&& double.IsFinite(result.SignalLevelDbFs)
			&& result.SignalLevelDbFs >= MinimumInputSignalDbFs;
		bool flag = captureValid && sweepHarmonicPoint != null
			&& double.IsFinite(sweepHarmonicPoint.ThdPercent)
			&& sweepHarmonicPoint.ThdPercent >= 0.0 && sweepHarmonicPoint.ThdPercent <= 100.0
			&& double.IsFinite(sweepHarmonicPoint.FundamentalLevelDb)
			&& sweepHarmonicPoint.FundamentalLevelDb >= result.NoiseFloorDbFs + 10.0
			&& num >= 2;
		string includedHarmonics = ((num >= 2) ? $"H2 .. {num}" : "N/A");
		string validity = flag
			? "Đã phân tích H2-H9 từ cùng một log-sweep"
			: result.IsClipped
				? "Sweep bị clipping; không kết luận distortion"
				: result.SignalLevelDbFs < MinimumInputSignalDbFs
					? $"Sweep quá yếu ({result.SignalLevelDbFs:F1} dBFS < {MinimumInputSignalDbFs:F1} dBFS); không kết luận distortion"
					: "Sweep không hợp lệ; không kết luận distortion";
			return new SweepDistortionSummary(targetFreq, sweepHarmonicPoint?.FundamentalLevelDb ?? double.NaN,
			flag ? dictionary : dictionary.ToDictionary(item => item.Key, _ => double.NaN),
					flag ? sweepHarmonicPoint!.ThdPercent : double.NaN, noiseFloorPercent, result.NoiseFloorDbFs, includedHarmonics, flag, validity);
	}

	private SweepDistortionSummary BuildSweepWideDistortionSummary(StandardAcousticResult result)
	{
		return SweepDistortionSummary.AggregateSweep(
			result.SweepHarmonics,
			result.SignalLevelDbFs,
			result.NoiseFloorDbFs,
			result.IsClipped,
			result.Validity,
			MinimumInputSignalDbFs);
	}

	private static Complex[][]? AverageHarmonicTransfers(IReadOnlyList<Complex[][]> transfers)
	{
		if (transfers.Count == 0 || transfers.Any(run => run.Length < 10))
			return null;
		var average = new Complex[10][];
		for (int order = 1; order <= 9; order++)
		{
			Complex[][] orderTransfers = transfers
				.Select(run => run[order])
				.Where(transfer => transfer is { Length: > 0 })
				.ToArray();
			// A missing order in any valid run is not a repeatable harmonic. Do not
			// substitute a median or a one-off noise peak into the averaged result.
			if (orderTransfers.Length != transfers.Count)
			{
				average[order] = Array.Empty<Complex>();
				continue;
			}
			average[order] = StandardAcousticMeasurement.AverageComplexTransfers(orderTransfers).Transfer;
		}
		return average[1].Length > 0 ? average : null;
	}

	public ToneQualityMetrics? GetToneQualityAtFrequency(double frequencyHz)
	{
		if (LastToneQualities == null || LastToneQualities.Count == 0) return null;
		if (LastToneQualities.TryGetValue(frequencyHz, out ToneQualityMetrics? exact)) return exact;
		var closest = LastToneQualities.MinBy(pair => Math.Abs(Math.Log(pair.Key / Math.Max(1.0, frequencyHz))));
		if (Math.Abs(Math.Log(closest.Key / Math.Max(1.0, frequencyHz), 2.0)) <= 0.35)
		{
			return closest.Value;
		}
		return null;
	}

	private double ScalePlaybackFrequency(double requestedFrequencyHz)
	{
		return requestedFrequencyHz * PlaybackFrequencyScale;
	}

	private double[] ScalePlaybackFrequencies(IEnumerable<double> requestedFrequenciesHz)
	{
		return requestedFrequenciesHz.Select(ScalePlaybackFrequency).ToArray();
	}

	private static double[] GenerateLogFrequencyArray(double min, double max, int count)
	{
		List<double> list = new List<double>();
		GenerateLogFrequencies(min, max, count, list);
		return (from f in list.Distinct()
			orderby f
			select f).ToArray();
	}

	private static void GenerateLogFrequencies(double min, double max, int count, List<double> list)
	{
		if (count <= 0)
		{
			return;
		}
		if (count == 1)
		{
			list.Add(Math.Round(min));
			return;
		}
		double num = Math.Log(max / min);
		for (int i = 0; i < count; i++)
		{
			double a = min * Math.Exp(num * (double)i / (double)(count - 1));
			list.Add(Math.Round(a));
		}
	}

	private static double CalculateMedian(IEnumerable<double> values)
	{
		double[] array = values.OrderBy((double value) => value).ToArray();
		if (array.Length == 0)
		{
			return 0.0;
		}
		int num = array.Length / 2;
		if (array.Length % 2 != 1)
		{
			return (array[num - 1] + array[num]) / 2.0;
		}
		return array[num];
	}

	private static (int Start, int Count) GetCenteredAnalysisRange(float[] samples, int sampleRate, double durationSeconds)
	{
		if (samples == null || samples.Length == 0 || sampleRate <= 0 || durationSeconds <= 0.0)
		{
			return (Start: 0, Count: 0);
		}
		int val = Math.Max(1, (int)Math.Round((double)sampleRate * durationSeconds));
		int num = Math.Min(samples.Length, val);
		return (Start: Math.Max(0, (samples.Length - num) / 2), Count: num);
	}

	private static float[] ExtractCenteredAnalysisWindow(float[] samples, int sampleRate, double durationSeconds)
	{
		(int, int) centeredAnalysisRange = GetCenteredAnalysisRange(samples, sampleRate, durationSeconds);
		if (centeredAnalysisRange.Item2 == 0)
		{
			return Array.Empty<float>();
		}
		float[] array = new float[centeredAnalysisRange.Item2];
		Array.Copy(samples, centeredAnalysisRange.Item1, array, 0, centeredAnalysisRange.Item2);
		return array;
	}

	private static float[] ExtractTrailingAnalysisWindow(float[] samples, int sampleRate, double durationSeconds)
	{
		if (samples == null || samples.Length == 0 || sampleRate <= 0 || durationSeconds <= 0.0)
		{
			return Array.Empty<float>();
		}
		int num = Math.Min(samples.Length, Math.Max(1, (int)Math.Round((double)sampleRate * durationSeconds)));
		float[] array = new float[num];
		Array.Copy(samples, samples.Length - num, array, 0, num);
		return array;
	}

	private static ToneQualityMetrics AnalyzeStableToneCapture(float[] samples, int sampleRate, double expectedFrequencyHz)
	{
		const int segmentCount = 8;
		int num = samples.Length / segmentCount;
		if (num < 512)
		{
			return AdvancedAudioMeasurement.AnalyzeTone(samples, sampleRate, expectedFrequencyHz, maxHarmonic: 9);
		}
		double num2 = Math.Max(5.0, expectedFrequencyHz * 0.002);
		double capturePeak = samples.Select((float sample) => Math.Abs((double)sample)).DefaultIfEmpty().Max();
		double captureClippedPercent = samples.Count((float sample) => Math.Abs(sample) >= 0.98f) * 100.0 / samples.Length;
		List<ToneQualityMetrics> list = new List<ToneQualityMetrics>(segmentCount);
		for (int i = 0; i < segmentCount; i++)
		{
			float[] array = new float[num];
			Array.Copy(samples, i * num, array, 0, num);
			ToneQualityMetrics toneQualityMetrics = AdvancedAudioMeasurement.AnalyzeTone(array, sampleRate, expectedFrequencyHz, maxHarmonic: 9);
			if (toneQualityMetrics.IsValid && double.IsFinite(toneQualityMetrics.ThdPercent) && double.IsFinite(toneQualityMetrics.SnrDb) && Math.Abs(toneQualityMetrics.FundamentalFrequencyHz - expectedFrequencyHz) <= num2)
			{
				list.Add(toneQualityMetrics);
			}
		}
		if (list.Count < 2)
		{
			return AdvancedAudioMeasurement.AnalyzeTone(samples, sampleRate, expectedFrequencyHz, maxHarmonic: 9);
		}
		// A short driver/DSP burst can occur while the app closes one WASAPI stream
		// and opens the next THD tone. Frequency lock alone does not reject it: the
		// carrier is still present, but its residual noise can inflate THD badly.
		// Keep the repeatable, high-SNR part of the same capture. True harmonic
		// distortion remains in every segment and therefore is not filtered out.
		double bestSnrDb = list.Max((ToneQualityMetrics item) => item.SnrDb);
		List<ToneQualityMetrics> stableSegments = list
			.Where((ToneQualityMetrics item) => item.SnrDb >= bestSnrDb - 6.0)
			.ToList();
		if (stableSegments.Count >= 2)
		{
			list = stableSegments;
		}
		double medianThd = CalculateMedian(list.Select((ToneQualityMetrics item) => item.ThdPercent));
		return list.OrderBy((ToneQualityMetrics item) => Math.Abs(item.ThdPercent - medianThd)).First()with
		{
			FundamentalFrequencyHz = CalculateMedian(list.Select((ToneQualityMetrics item) => item.FundamentalFrequencyHz)),
			FundamentalRms = CalculateMedian(list.Select((ToneQualityMetrics item) => item.FundamentalRms)),
			SignalLevelDbFs = CalculateMedian(list.Select((ToneQualityMetrics item) => item.SignalLevelDbFs)),
			ThdPercent = medianThd,
			ThdNPercent = CalculateMedian(list.Select((ToneQualityMetrics item) => item.ThdNPercent)),
			SinadDb = CalculateMedian(list.Select((ToneQualityMetrics item) => item.SinadDb)),
			SnrDb = CalculateMedian(list.Select((ToneQualityMetrics item) => item.SnrDb)),
			DcOffset = CalculateMedian(list.Select((ToneQualityMetrics item) => item.DcOffset)),
			CrestFactor = CalculateMedian(list.Select((ToneQualityMetrics item) => item.CrestFactor)),
			PeakSample = capturePeak,
			ClippedSamplePercent = captureClippedPercent
		};
	}

	private float[] RemoveDigitalRecordingGain(float[] samples)
	{
		return samples;
	}

	private static double[] GenerateLogSweepEvaluationFrequencies(double min, double max, int pointsPerOctave)
	{
		int count = Math.Max(2, (int)Math.Floor(Math.Log(max / min, 2.0) * (double)pointsPerOctave) + 1);
		return (from frequency in (from index in Enumerable.Range(0, count)
				select min * Math.Pow(2.0, (double)index / (double)pointsPerOctave) into frequency
				where frequency <= max
				select frequency).Append(1000.0).Append(max).Distinct()
			orderby frequency
			select frequency).ToArray();
	}

	private static Dictionary<double, double> InterpolateLogSweepResponse(IReadOnlyList<FrequencyResponsePoint> response, IEnumerable<double> frequencies)
	{
		FrequencyResponsePoint[] array = (from point in response
			where double.IsFinite(point.FrequencyHz) && double.IsFinite(point.LevelDb)
			orderby point.FrequencyHz
			select point).ToArray();
		if (array.Length < 2)
		{
			return new Dictionary<double, double>();
		}
		Dictionary<double, double> dictionary = new Dictionary<double, double>();
		foreach (double frequency in frequencies)
		{
			int num = Array.FindIndex(array, (FrequencyResponsePoint point) => point.FrequencyHz >= frequency);
			if (num == 0)
			{
				dictionary[frequency] = array[0].LevelDb;
				continue;
			}
			if (num < 0)
			{
				dictionary[frequency] = array[^1].LevelDb;
				continue;
			}
			FrequencyResponsePoint frequencyResponsePoint = array[num - 1];
			FrequencyResponsePoint frequencyResponsePoint2 = array[num];
			double num2 = (Math.Log(frequency) - Math.Log(frequencyResponsePoint.FrequencyHz)) / (Math.Log(frequencyResponsePoint2.FrequencyHz) - Math.Log(frequencyResponsePoint.FrequencyHz));
			dictionary[frequency] = frequencyResponsePoint.LevelDb + (frequencyResponsePoint2.LevelDb - frequencyResponsePoint.LevelDb) * num2;
		}
		return dictionary;
	}

	private Dictionary<double, double> InterpolateScaledLogSweepResponse(IReadOnlyList<FrequencyResponsePoint> response, IEnumerable<double> nominalFrequencies)
	{
		double[] array = nominalFrequencies.ToArray();
		Dictionary<double, double> physical = InterpolateLogSweepResponse(response, array.Select(ScalePlaybackFrequency));
		if (physical.Count != array.Length)
		{
			return new Dictionary<double, double>();
		}
		// Transfer gain is input/output. Restore the actual generated stimulus
		// level so changes in playback level remain visible in received dBFS.
		double excitationRmsDbFs = PlaybackLevelDbFs - 10.0 * Math.Log10(2.0);
		return array.ToDictionary(frequency => frequency,
			frequency => physical[ScalePlaybackFrequency(frequency)] + excitationRmsDbFs);
	}

	private static Dictionary<double, double> ApplyThreePointDbSmoothing(IReadOnlyDictionary<double, double> response)
	{
		if (response.Count < 3)
		{
			return new Dictionary<double, double>(response);
		}
		Dictionary<double, double> dictionary = new Dictionary<double, double>(response.Count);
		List<double> list = response.Keys.OrderBy((double value) => value).ToList();
		for (int num = 0; num < list.Count; num++)
		{
			double key = list[num];
			if (num == 0)
			{
				dictionary[key] = (response[list[0]] * 2.0 + response[list[1]]) / 3.0;
			}
			else if (num == list.Count - 1)
			{
				dictionary[key] = (response[list[num - 1]] + response[key] * 2.0) / 3.0;
			}
			else
			{
				dictionary[key] = (response[list[num - 1]] + response[key] + response[list[num + 1]]) / 3.0;
			}
		}
		return dictionary;
	}

	private Dictionary<double, double> BuildSweepComparisonResponse(IReadOnlyList<FrequencyResponsePoint> response, double[] centers)
	{
		double level = PlaybackLevelDbFs - 10 * Math.Log10(2);
		var raw = response.ToDictionary(p => p.FrequencyHz / PlaybackFrequencyScale, p => p.LevelDb + level);
		var smoothed = ResponseSmoothing.Octave24(raw, centers);
		double reference = 0;
		if (NormalizeFrequencyResponseToOneKilohertz && TryGetStandardValue(smoothed, 1000, out reference))
			raw = raw.ToDictionary(p => p.Key, p => p.Value - reference);
		LastUnsmoothedFrequencyResponse = raw;
		OnUnsmoothedResponseReady?.Invoke(raw);
		return smoothed;
	}

	public static FeqRepeatabilityAssessment AssessFeqRepeatability(IReadOnlyList<Dictionary<double, double>> curves, bool highResolution, double toleranceDb, IReadOnlyList<CriticalFrequencyZone>? criticalZones = null)
	{
		return AssessFeqRepeatabilityInRange(curves, highResolution, toleranceDb, criticalZones, 18000.0);
	}

	public static FeqRepeatabilityAssessment AssessFeqRepeatabilityInRange(IReadOnlyList<Dictionary<double, double>> curves, bool highResolution, double toleranceDb, IReadOnlyList<CriticalFrequencyZone>? criticalZones, double maxFrequencyHz, double minFrequencyHz = 50.0)
	{
		double[] frequencies = (highResolution ? GenerateLogSweepEvaluationFrequencies(minFrequencyHz, maxFrequencyHz, LogSweepPointsPerOctave) : (from result in (from num5 in curves.SelectMany((Dictionary<double, double> curve) => curve.Keys)
					where num5 >= minFrequencyHz && num5 <= maxFrequencyHz
				select num5).Distinct()
			orderby result
			select result).ToArray());
		List<FeqRepeatabilityPoint> unstable = new List<FeqRepeatabilityPoint>();
		double[] array = frequencies;
		foreach (double frequency in array)
		{
			double[] array2 = curves.Select((Dictionary<double, double> curve) => (!TryGetStandardValue(curve, frequency, out var value)) ? double.NaN : value).Where(double.IsFinite).ToArray();
			if (array2.Length != curves.Count || array2.Length < 2)
			{
				continue;
			}
			double num2 = array2.Min();
			double num3 = array2.Max();
			double num4 = num3 - num2;
			if (!(num4 <= toleranceDb))
			{
				string band = ((frequency < 250.0) ? "Bass" : ((frequency < 4000.0) ? "Mid" : "Treble"));
				bool critical = criticalZones?.Any((CriticalFrequencyZone zone) => zone.Contains(frequency)) ?? false;
				unstable.Add(new FeqRepeatabilityPoint(frequency, num2, num3, num4, array2, band, critical));
			}
		}
			(int, int) tuple = CountBand(minFrequencyHz, 250.0);
		(int, int) tuple2 = CountBand(250.0, 4000.0);
		(int, int) tuple3 = CountBand(4000.0, maxFrequencyHz, includeMax: true);
		return new FeqRepeatabilityAssessment(unstable.Any((FeqRepeatabilityPoint point) => point.Critical) || (tuple.Item2 > 0 && (double)tuple.Item1 / (double)tuple.Item2 > 0.1) || (tuple2.Item2 > 0 && (double)tuple2.Item1 / (double)tuple2.Item2 > 0.1) || (tuple3.Item2 > 0 && (double)tuple3.Item1 / (double)tuple3.Item2 > 0.1), frequencies.Length, unstable.OrderByDescending((FeqRepeatabilityPoint point) => point.SpreadDb).ToArray(), tuple.Item1, tuple.Item2, tuple2.Item1, tuple2.Item2, tuple3.Item1, tuple3.Item2);
		(int Unstable, int Checked) CountBand(double min, double max, bool includeMax = false)
		{
			return (Unstable: unstable.Count((FeqRepeatabilityPoint point) => Contains(point.Frequency)), Checked: frequencies.Count(Contains));
			bool Contains(double num5)
			{
				if (num5 >= min)
				{
					if (!includeMax)
					{
						return num5 < max;
					}
					return num5 <= max;
				}
				return false;
			}
		}
	}

	public static string DescribeFeqRepeatability(FeqRepeatabilityAssessment assessment, int maxPoints = 12)
	{
		if (assessment.UnstablePoints.Count == 0)
		{
			return $"Ổn định tại {assessment.CheckedPointCount} điểm đại diện ({assessment.BandSummary}).";
		}
		string text = string.Join("; ", from point in assessment.UnstablePoints.Take(maxPoints)
			select $"{point.Band} {point.Frequency:F0} Hz: min {point.MinimumDb:F2}, max {point.MaximumDb:F2}, spread {point.SpreadDb:F2} dB, lượt [{string.Join(", ", point.RunValues.Select((double value) => value.ToString("F2", CultureInfo.InvariantCulture)))}]{(point.Critical ? " CRITICAL" : "")}");
		return assessment.BandSummary + ". " + text;
	}

	public static float[] AddLogSweepPreroll(float[] sweep, int sampleRate)
	{
		if (sweep == null || sweep.Length == 0 || sampleRate <= 0)
		{
			throw new ArgumentException("Log sweep hoặc sample rate không hợp lệ.");
		}
		int num = Math.Min(sweep.Length, (int)Math.Round(0.5 * (double)sampleRate));
		float[] array = new float[num + sweep.Length];
		Array.Copy(sweep, 0, array, 0, num);
		int num2 = Math.Min(num, Math.Max(32, sampleRate / 100));
		for (int i = 0; i < num2; i++)
		{
			double num3 = 0.5 + 0.5 * Math.Cos(Math.PI * (double)i / (double)Math.Max(1, num2 - 1));
			array[num - num2 + i] *= (float)num3;
		}
		Array.Copy(sweep, 0, array, num, sweep.Length);
		return array;
	}

	private bool CheckFeqInputClipping(float[] samples, string source)
	{
		if (samples.Length == 0)
		{
			return false;
		}
		double num = samples.Max((float sample) => Math.Abs((double)sample));
		double num2 = (double)samples.Count((float sample) => Math.Abs(sample) >= 0.995f) * 100.0 / (double)samples.Length;
		LastFeqPeakSample = Math.Max(LastFeqPeakSample, num);
		int num3;
		if (!(num >= 0.995))
		{
			num3 = ((num2 > MaximumClippedSamplePercent) ? 1 : 0);
			if (num3 == 0)
			{
				if (num >= 0.9)
				{
					Action<string, string>? action = OnLogMessage;
					if (action == null)
					{
						return (byte)num3 != 0;
					}
					action("Headroom", $"{source}: peak {num:F4}; tín hiệu gần quá tải nhưng chưa clipping.");
				}
				return (byte)num3 != 0;
			}
		}
		else
		{
			num3 = 1;
		}
		Action<string, string>? action2 = OnLogMessage;
		if (action2 != null)
		{
			action2("Warning", $"CRITICAL: Input clipping detected in {source}: peak {num:F4}, rail samples {num2:F4}%. Lower Playback Volume or hardware input gain.");
			return (byte)num3 != 0;
		}
		return (byte)num3 != 0;
	}

	private double ApplyFeqMicrophoneCalibration(double frequency, double measuredDb)
	{
		return measuredDb - (FeqMicrophoneCalibration?.GetGainCorrectionDb(frequency) ?? 0.0);
	}

	private static bool IsBandPassed(bool isSilent, int failedCount, int checkedCount, bool criticalFailure = false)
	{
		if (!isSilent && checkedCount > 0 && !criticalFailure)
		{
			return (double)failedCount / (double)checkedCount <= 0.1;
		}
		return false;
	}

	private static bool TryGetStandardValue(Dictionary<double, double>? standardCurve, double frequency, out double value)
	{
		value = 0.0;
		if (standardCurve == null || standardCurve.Count == 0)
		{
			return false;
		}
		if (standardCurve.TryGetValue(frequency, out value))
		{
			return true;
		}
		double num = double.NegativeInfinity;
		double num2 = double.PositiveInfinity;
		double num3 = 0.0;
		double num4 = 0.0;
		foreach (KeyValuePair<double, double> item in standardCurve)
		{
			if (item.Key < frequency && item.Key > num)
			{
				num = item.Key;
				num3 = item.Value;
			}
			if (item.Key > frequency && item.Key < num2)
			{
				num2 = item.Key;
				num4 = item.Value;
			}
		}
		if (double.IsNegativeInfinity(num) || double.IsPositiveInfinity(num2))
		{
			return false;
		}
		double num5 = Math.Log10(num);
		double num6 = Math.Log10(num2);
		double num7 = (Math.Log10(frequency) - num5) / (num6 - num5);
		value = num3 + (num4 - num3) * num7;
		return true;
	}

	private static Dictionary<double, double> NormalizeAtOneKilohertz(Dictionary<double, double> curve, out double levelDbFs)
	{
		if (!TryGetStandardValue(curve, 1000.0, out levelDbFs) || !double.IsFinite(levelDbFs))
			throw new InvalidDataException("Không có mức thu 1 kHz hợp lệ; không thể chuẩn hóa đáp tuyến.");
		double referenceLevel = levelDbFs;
		return curve.ToDictionary(point => point.Key, point => point.Value - referenceLevel);
	}

	private double[] FindMissingFrequencyLimits()
	{
		return (from f in TestFrequencies
			where f >= 50.0 && f <= EvaluationMaxFrequencyHz
			where !ProductionMeasurement.TryGetFrequencyLimit(FrequencyLimits, f, out FrequencyLimitPoint _) && !TryGetStandardValue(StandardCurve, f, out var _)
			select f).ToArray();
	}

	public TestRunner(AudioEngine audioEngine)
	{
		_audioEngine = audioEngine;
		InitializeSteps();
	}

	public void InitializeSteps()
	{
		if (IsRubBuzzTest)
		{
			_steps = new List<TestStep>
			{
				new TestStep
				{
					Name = "1. Kiểm tra kết nối thiết bị"
				},
				new TestStep
				{
					Name = "2. Đo tiếng rè/rung và hở khí"
				},
				new TestStep
				{
					Name = "3. Đo méo hài (không áp dụng)"
				},
				new TestStep
				{
					Name = "4. Tổng hợp kết luận"
				}
			};
		}
		else
		{
			_steps = new List<TestStep>
			{
				new TestStep
				{
					Name = "1. Kiểm tra kết nối thiết bị"
				},
				new TestStep
				{
					Name = "2. Đo đáp tuyến tần số (20 Hz - 20 kHz)"
				},
				new TestStep
				{
					Name = "3. Đo THD, THD+N, SINAD và SNR"
				},
				new TestStep
				{
					Name = "4. Tổng hợp kết luận"
				}
			};
		}
		OnStepsChanged?.Invoke(_steps);
	}

		public void Cancel()
		{
			_isCancelled = true;
				try { System.Threading.Volatile.Read(ref _runCancellation)?.Cancel(); }
				catch (ObjectDisposedException) { }
			_audioEngine.StopAllAudio();
	}

	public string BuildFailureDiagnosis()
	{
		List<string> list = new List<string>();
		if (_steps.FirstOrDefault()?.Status == "Fail")
		{
			list.Add("Không nhận đủ thiết bị phát/thu; kiểm tra dây, nguồn và ánh xạ audio trước khi đo lại.");
		}
		if (SilentInputDetected)
		{
			string text = (double.IsNaN(LastSignalLevelDbFs) ? "" : $" ({LastSignalLevelDbFs:F1} dBFS)");
			list.Add("Không có tín hiệu hợp lệ" + text + "; kiểm tra đúng ngõ vào/ra, mute và mức thu Windows/phần cứng.");
		}
		if (InputClippingDetected)
		{
			list.Add("Tín hiệu bị clipping; giảm âm lượng phát Windows hoặc gain phần cứng đầu vào rồi chạy lại.");
		}
		if (IsRubBuzzTest && !ThdPassed && !SilentInputDetected)
		{
			list.Add($"Rub & Buzz {LastRubBuzzValue:F3}% vượt giới hạn {RubBuzzLimit:F3}%; kiểm tra rò khí, màng loa, keo và chi tiết cơ khí bị rung.");
		}
		else
		{
			List<string> list2 = new List<string>();
			if (!BassPassed)
			{
				list2.Add("Bass");
			}
			if (!MidPassed)
			{
				list2.Add("Mid");
			}
			if (!TreblePassed)
			{
				list2.Add("Treble");
			}
			if (list2.Count > 0 && !SilentInputDetected)
			{
				list.Add($"Đáp tuyến lệch ở dải {string.Join("/", list2)}; độ lệch lớn nhất {LastFrequencyMaxDeviationDb:F2} dB so với giới hạn ±{FreqResponseToleranceDb:F2} dB. Kiểm tra loa, phân tần và đường tín hiệu tương ứng.");
			}
			if (!ThdPassed && !SilentInputDetected)
			{
				ToneQualityMetrics lastToneQuality = LastToneQuality;
				if (lastToneQuality?.ClippedSamplePercent > MaximumClippedSamplePercent)
				{
					list.Add("Tín hiệu thu bị clipping; giảm âm lượng phát Windows hoặc gain phần cứng đầu vào.");
				}
				if (lastToneQuality != null && Math.Abs(lastToneQuality.FundamentalFrequencyHz - MidThdFrequencyHz) > MaximumToneFrequencyErrorHz)
				{
					list.Add($"Tần số thực {lastToneQuality.FundamentalFrequencyHz:F2} Hz lệch tone chuẩn {MidThdFrequencyHz:F0} Hz; kiểm tra clock, nguồn phát và đường tín hiệu.");
				}
				if (ThdNLimitPercent.HasValue && lastToneQuality?.ThdNPercent > ThdNLimitPercent.Value)
				{
					list.Add($"THD+N {lastToneQuality.ThdNPercent:F3}% vượt giới hạn {ThdNLimitPercent:F3}%; kiểm tra nhiễu, rè cơ khí và ampli.");
				}
				if (LastMeasuredThdPercent > ThdLimitPercent)
				{
					list.Add($"THD {LastMeasuredThdPercent:F3}% vượt giới hạn {ThdLimitPercent:F3}%; kiểm tra méo do gain quá cao, ampli hoặc củ loa.");
				}
			}
		}
		if (list.Count == 0)
		{
			TestStep testStep = _steps.FirstOrDefault((TestStep step) => step.Status == "Fail");
			list.Add((testStep == null) ? "Bài test chưa hoàn tất; kiểm tra log và chạy lại." : ("Không đạt tại " + testStep.Name + ": " + testStep.Details));
		}
		return string.Join(" ", list);
	}

	private bool EvaluateFrequencyPoint(double frequency, double measuredDb, out double targetDb, out double lowerDb, out double upperDb, out bool critical)
	{
		critical = CriticalZones.Any((CriticalFrequencyZone zone) => zone.Contains(frequency));
		if (ProductionMeasurement.TryGetFrequencyLimit(FrequencyLimits, frequency, out FrequencyLimitPoint point))
		{
			targetDb = point.TargetDb;
			lowerDb = Math.Min(point.LowerDb, point.UpperDb);
			upperDb = Math.Max(point.LowerDb, point.UpperDb);
			critical |= point.Critical;
			if (!(measuredDb < lowerDb))
			{
				return measuredDb > upperDb;
			}
			return true;
		}
		if (TryGetStandardValue(StandardCurve, frequency, out targetDb))
		{
			lowerDb = targetDb - FreqResponseToleranceDb;
			upperDb = targetDb + FreqResponseToleranceDb;
			if (!(measuredDb < lowerDb))
			{
				return measuredDb > upperDb;
			}
			return true;
		}
		targetDb = 0.0;
		lowerDb = 0.0 - FreqResponseToleranceDb;
		upperDb = FreqResponseToleranceDb;
		return false;
	}

	private async Task RunFastNoiseDiagnosticsAsync(MMDevice recordingDevice, bool referenceAcquisition = false)
	{
		bool stablePreflight = AutoTestOneKilohertzOnly || referenceAcquisition
			|| (UseLogSweepFrequencyResponse && !AudioEngine.flagGenerateSeperateSine);
		int attempts = Math.Max(1, AmbientNoiseMaxRetries + 1);
		for (int attempt = 1; attempt <= attempts; attempt++)
		{
			OnLogMessage?.Invoke("Noise", $"Kiểm tra nhanh noise/hum 50/60 Hz ({attempt}/{attempts})...");
			AudioEngine.DualCaptureResult? dualCaptureResult = null;
			for (int captureAttempt = 1; captureAttempt <= (stablePreflight ? 2 : 1); captureAttempt++)
			{
				try
				{
					dualCaptureResult = await _audioEngine.CaptureSilenceAsync(recordingDevice, AmbientRecordingDevice, stablePreflight ? 1.0 : 0.4, RunCancellation);
					break;
				}
				catch (OperationCanceledException) when (RunCancellation.IsCancellationRequested) { throw; }
				catch (Exception ex)
				{
					OnLogMessage?.Invoke("Cảnh báo noise", $"Thu nền lần {captureAttempt} lỗi ({ex.GetType().Name}): {ex.Message}");
					if (captureAttempt < 2 && stablePreflight)
						await Task.Delay(250, RunCancellation);
				}
			}
			if (dualCaptureResult == null)
			{
				LastNoiseAssessment = null;
				OnLogMessage?.Invoke("Cảnh báo noise", "Không lấy được nền Mic DUT; dừng trước khi phát tone và không kết luận THD.");
				break;
			}
			float[] samples = RemoveDigitalRecordingGain(dualCaptureResult.DutSamples);
			NoiseSpectrumMetrics noiseSpectrumMetrics = ProductionMeasurement.AnalyzeNoise(samples, dualCaptureResult.DutSampleRate, DutMicrophoneCalibrationOffsetDb);
			_dutNoiseFloorByFrequencyDbFs.Clear();
			(string, double[])[] testFrequencyBands = TestFrequencyBands;
			for (int i = 0; i < testFrequencyBands.Length; i++)
			{
				(string, double[]) tuple = testFrequencyBands[i];
				foreach (KeyValuePair<double, double> item in DspProcessor.CalculateMultitoneResponse(samples, dualCaptureResult.DutSampleRate, tuple.Item2))
				{
					_dutNoiseFloorByFrequencyDbFs[item.Key] = item.Value;
				}
			}
			NoiseSpectrumMetrics noiseSpectrumMetrics2 = ((dualCaptureResult.AmbientSamples == null) ? null : ProductionMeasurement.AnalyzeNoise(dualCaptureResult.AmbientSamples, dualCaptureResult.AmbientSampleRate, AmbientMicrophoneCalibrationOffsetDb));
			LastNoiseAssessment = ProductionMeasurement.CompareNoise(noiseSpectrumMetrics, noiseSpectrumMetrics2, AmbientNoiseLimitDbSpl, AmbientNoiseLimitDbFs, DutNoiseSeparationMarginDb, attempt);
			OnNoiseAssessmentReady?.Invoke(LastNoiseAssessment);
			string value = ((!(noiseSpectrumMetrics2 == null)) ? $"mic môi trường {noiseSpectrumMetrics2.BroadbandDb:F1} {noiseSpectrumMetrics2.Unit}, hum {noiseSpectrumMetrics2.DominantHumHz:F0} Hz {noiseSpectrumMetrics2.DominantHumDb:F1} {noiseSpectrumMetrics2.Unit}" : ((dualCaptureResult.AmbientError == null) ? "không có mic ambient" : ("mic ambient lỗi: " + dualCaptureResult.AmbientError)));
			OnLogMessage?.Invoke("Nhiễu", $"Mic DUT {noiseSpectrumMetrics.BroadbandDb:F1} {noiseSpectrumMetrics.Unit}; hum {noiseSpectrumMetrics.DominantHumHz:F0} Hz {noiseSpectrumMetrics.DominantHumDb:F1} {noiseSpectrumMetrics.Unit}; đỉnh phổ {noiseSpectrumMetrics.DominantSpectralPeakHz:F0} Hz {noiseSpectrumMetrics.DominantSpectralPeakDb:F1} {noiseSpectrumMetrics.Unit}; DC {noiseSpectrumMetrics.DcOffset:F5}; crest {noiseSpectrumMetrics.CrestFactor:F2}. {value}. {LastNoiseAssessment.Classification}");
			if (LastNoiseAssessment.EnvironmentTooLoud && attempt < attempts)
			{
				OnLogMessage?.Invoke("Nhiễu", "Môi trường đang ồn; tự động đo lại sau 150 ms (không đổi kết quả DUT).");
				await Task.Delay(150, RunCancellation);
				continue;
			}
			break;
		}
	}

	public Task RunTestAsync(MMDevice playbackDevice, MMDevice recordingDevice, bool runFeqThreeTimes = false, bool referenceAcquisition = false)
		=> RunOwnedTestAsync(_ => RunTestCoreAsync(playbackDevice, recordingDevice, runFeqThreeTimes, referenceAcquisition, CaptureSession ?? _audioEngine.CaptureSession));

	private async Task RunOwnedTestAsync(Func<CancellationToken, Task> run)
	{
		if (System.Threading.Interlocked.CompareExchange(ref _testRunActive, 1, 0) != 0)
			throw new InvalidOperationException("Bài test trước chưa kết thúc; không thể chạy đo line và Auto Test đồng thời.");
		var runTimer = Stopwatch.StartNew();
		using var cancellation = new CancellationTokenSource();
		_runCancellation = cancellation;
		_isCancelled = false;
		_currentRunTimer = runTimer;
		try
		{
			_audioEngine.StopAllAudio();
			await run(cancellation.Token);
		}
		finally
		{
			try
			{
				_audioEngine.StopAllAudio();
				OnLogMessage?.Invoke("Thời gian đo", $"Tổng quy trình: {runTimer.Elapsed.TotalSeconds:F2}s (gồm chuẩn bị, phát/thu, phân tích và cập nhật kết quả).");
			}
			finally
			{
				_currentRunTimer = null;
				System.Threading.Interlocked.CompareExchange(ref _runCancellation, null, cancellation);
				System.Threading.Volatile.Write(ref _testRunActive, 0);
			}
		}
	}

	private int _testRunActive;
	private Stopwatch? _currentRunTimer;
	private void NotifyTestCompleted(bool success)
	{
		if (_currentRunTimer != null)
			OnLogMessage?.Invoke("Thời gian kết quả", $"Kết quả sẵn sàng sau {_currentRunTimer.Elapsed.TotalSeconds:F2}s.");
		var uiTimer = Stopwatch.StartNew();
		OnTestCompleted?.Invoke(success);
		OnLogMessage?.Invoke("Thời gian giao diện", $"Cập nhật kết quả / hộp thông báo: {uiTimer.Elapsed.TotalSeconds:F2}s.");
	}
	// Optional externally owned diagnostic session; normal operation uses the engine session.
	public SharedCaptureSession? CaptureSession { get; set; }

	private static bool MustStopAtNoisePreflight(double rmsDbFs, double peak, bool strict) =>
		!double.IsFinite(rmsDbFs) || !double.IsFinite(peak) || peak >= 0.995
		|| (strict && (rmsDbFs > -55 || peak >= 0.05));

	private async Task RunTestCoreAsync(MMDevice playbackDevice, MMDevice recordingDevice, bool runFeqThreeTimes, bool referenceAcquisition, SharedCaptureSession captureSession)
	{
		var preparationTimer = Stopwatch.StartNew();
		bool useLogSweep = UseLogSweepFrequencyResponse && !AudioEngine.flagGenerateSeperateSine;
		bool requireQuietPreflight = AutoTestOneKilohertzOnly || referenceAcquisition || useLogSweep;
		InitializeSteps();
		if (referenceAcquisition)
		{
			_steps[2].Status = "Skipped";
			_steps[2].Details = "Không áp dụng khi tìm line chuẩn.";
		}
		OnLogMessage?.Invoke("Hệ thống", "Bắt đầu quy trình đo tự động...");
		LastMaxDevPercent = 0.0;
		LastAvgDevPercent = 0.0;
		HasComparedToStandard = false;
		BassPassed = true;
		MidPassed = true;
		TreblePassed = true;
		ThdPassed = true;
		ThdAcquisitionInvalid = false;
		LastFrequencyMaxDeviationDb = 0.0;
		LastMeasuredThdPercent = double.NaN;
		LastPreflightWarning = "";
		LastToneQuality = null;
		LastToneQualities = new Dictionary<double, ToneQualityMetrics>();
		LastSweepResult = null;
		LastSweepHarmonics = null;
		LastSweepDistortion = null;
		LastUnsmoothedFrequencyResponse = new Dictionary<double, double>();
		OnUnsmoothedResponseReady?.Invoke(LastUnsmoothedFrequencyResponse);
		LastRubBuzzMetrics = null;
		LastSignalLevelDbFs = double.NaN;
		LastOneKilohertzLevelDbFs = double.NaN;
		LastFeqPeakSample = 0.0;
		InputClippingDetected = false;
		FeqRepeatabilityInvalid = false;
		ComplexPhaseAlignmentInvalid = false;
		MissingFrequencyLimits = false;
		ReferenceAcquisitionValid = false;
		LastFeqInvalidReason = "";
		LastFeqInvalidIsRetryableNoise = false;
		SilentInputDetected = false;
		if (!ReuseSuiteNoiseFloor || SuiteSharedNoiseAssessment == null)
		{
			LastNoiseAssessment = null;
			_dutNoiseFloorByFrequencyDbFs.Clear();
		}
		else
		{
			LastNoiseAssessment = SuiteSharedNoiseAssessment;
			_dutNoiseFloorByFrequencyDbFs.Clear();
			foreach (KeyValuePair<double, double> kv in SuiteSharedNoiseFloorByFrequency)
			{
				_dutNoiseFloorByFrequencyDbFs[kv.Key] = kv.Value;
			}
		}
		_steps[0].Status = "Running";
		OnStepsChanged?.Invoke(_steps);
		OnLogMessage?.Invoke("Bước 1", "Đang kiểm tra thiết bị phát và thu âm...");
		await Task.Delay(50, RunCancellation);
		if (playbackDevice == null || recordingDevice == null)
		{
			_steps[0].Status = "Fail";
			_steps[0].Details = "Thiếu thiết bị phát hoặc thu âm.";
			OnStepsChanged?.Invoke(_steps);
			OnLogMessage?.Invoke("Lỗi bước 1", "Chưa chọn thiết bị âm thanh hoặc thiết bị đã mất kết nối.");
			NotifyTestCompleted(false);
			return;
		}
		try
		{
			captureSession.Ensure(recordingDevice, _audioEngine.UseExclusivePlayback);
			if (captureSession.IsActive)
				OnLogMessage?.Invoke("Audio", "Giữ kết nối thu FastTrack Pro giữa các bài; mọi nguồn phát vẫn dừng/reset sau mỗi bài. Đóng ứng dụng sẽ giải phóng kết nối thu.");
		}
		catch (Exception ex)
		{
			ThdAcquisitionInvalid = true;
			LastFeqInvalidReason = AudioSessionDiagnostics.IsDeviceInUse(ex)
				? "Ngõ thu đang bị chiếm quyền (WASAPI DEVICE_IN_USE). Dừng ứng dụng âm thanh khác trước khi đo lại."
				: "Không mở được phiên thu: " + ex.Message;
			_steps[0].Status = "Fail";
			_steps[0].Details = LastFeqInvalidReason;
			OnStepsChanged?.Invoke(_steps);
			OnLogMessage?.Invoke("Lỗi thiết bị", LastFeqInvalidReason);
			NotifyTestCompleted(false);
			return;
		}
		_steps[0].Status = "Pass";
		_steps[0].Details = "Out: " + playbackDevice.FriendlyName + "\nIn: " + recordingDevice.FriendlyName;
		OnStepsChanged?.Invoke(_steps);
		OnLogMessage?.Invoke("Bước 1", "Ngõ phát: " + playbackDevice.FriendlyName);
		OnLogMessage?.Invoke("Bước 1", "Ngõ thu: " + recordingDevice.FriendlyName);
		if (requireQuietPreflight && EnableNoiseDiagnostics)
		{
			if (AutoTestOneKilohertzOnly && ReuseSuiteNoiseFloor && SuiteSharedNoiseAssessment != null)
			{
				OnLogMessage?.Invoke("Noise", $"Gộp noise floor từ bài test Left ({SuiteSharedNoiseAssessment.DutMicrophone?.TotalRmsDb:F1} dBFS) vào sweep cho bài test này.");
			}
			else
			{
				OnLogMessage?.Invoke("Noise", referenceAcquisition
					? "Capturing noise floor trước khi tìm line chuẩn..."
					: "Capturing noise floor trước khi phát tín hiệu Auto Test...");
				await RunFastNoiseDiagnosticsAsync(recordingDevice, referenceAcquisition);
				if (AutoTestOneKilohertzOnly && ReuseSuiteNoiseFloor && LastNoiseAssessment != null)
				{
					SuiteSharedNoiseAssessment = LastNoiseAssessment;
					SuiteSharedNoiseFloorByFrequency = new Dictionary<double, double>(_dutNoiseFloorByFrequencyDbFs);
					SuiteSharedNoiseFloorDbFs = LastNoiseAssessment.DutMicrophone?.TotalRmsDb;
					OnLogMessage?.Invoke("Noise", $"Đã lưu noise floor từ bài test Left ({SuiteSharedNoiseFloorDbFs:F1} dBFS) làm chuẩn sweep cho toàn bộ bài test.");
				}
				var inputNoise = LastNoiseAssessment?.DutMicrophone;
				bool elevatedNoise = inputNoise != null && (inputNoise.TotalRmsDb > -55.0 || inputNoise.PeakSample >= 0.05);
				if (MustStopAtNoisePreflight(inputNoise?.TotalRmsDb ?? double.NaN, inputNoise?.PeakSample ?? double.NaN,
					AutoTestOneKilohertzOnly || referenceAcquisition))
				{
					ThdAcquisitionInvalid = !referenceAcquisition;
					LastFeqInvalidReason = $"Nền ngõ thu {inputNoise?.TotalRmsDb:F1} dBFS, peak {inputNoise?.PeakSample:F4} vượt ngưỡng trước khi phát; không dùng lượt đo này.";
					_steps[0].Status = "Fail";
					_steps[0].Details = $"Nền ngõ thu {inputNoise?.TotalRmsDb:F1} dBFS, peak {inputNoise?.PeakSample:F4} không hợp lệ hoặc vượt -55 dBFS / 0.05 trước khi phát; chờ tín hiệu dư tắt rồi đo lại.";
					OnStepsChanged?.Invoke(_steps);
					OnLogMessage?.Invoke("Auto Test", _steps[0].Details);
					NotifyTestCompleted(false);
					return;
				}
				if (elevatedNoise)
					OnLogMessage?.Invoke("Nền ngõ thu", $"Nền trước phát: {inputNoise!.TotalRmsDb:F1} dBFS. Tiếp tục đo và đánh giá tín hiệu sweep thực thu.");
			}
		}
		if (!useLogSweep || IsRubBuzzTest)
		{
			OnLogMessage?.Invoke("Bước 1", "Đang kiểm tra tín hiệu ngõ thu...");
			double probeFreq = (IsRubBuzzTest ? RubBuzzTestFreq : 1000.0);
			float[] samples;
			try
			{
				samples = await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, ScalePlaybackFrequency(probeFreq), 0.35,
					cancellationToken: RunCancellation,
					signalSampleScale: PlaybackAmplitude * ((AutoTestOneKilohertzOnly || referenceAcquisition) ? AutoTestToneSampleScale : 1.0));
			}
			catch (OperationCanceledException) when (RunCancellation.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				_steps[0].Status = "Fail";
				_steps[0].Details = "Lỗi thiết bị âm thanh: " + ex.Message;
				OnStepsChanged?.Invoke(_steps);
				OnLogMessage?.Invoke("Lỗi bước 1", ex.Message);
				NotifyTestCompleted(false);
				return;
			}
			if (_isCancelled)
			{
				return;
			}
			float[] samples2 = RemoveDigitalRecordingGain(samples);
			int probeSampleRate = _audioEngine.RecordingSampleRate;
			(int, int) centeredAnalysisRange = GetCenteredAnalysisRange(samples2, probeSampleRate, 0.2);
			double num = DspProcessor.CalculateRms(samples2, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
			double probeDbFs = 20.0 * Math.Log10(num + 1E-09);
			double probePeak = GetPeak(samples2, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
			double probeClippedPercent = GetClippedPercent(samples2, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
			if (probeDbFs < MinimumInputSignalDbFs)
			{
				try
				{
					samples = await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, ScalePlaybackFrequency(probeFreq), 0.4,
						cancellationToken: RunCancellation,
						signalSampleScale: PlaybackAmplitude * ((AutoTestOneKilohertzOnly || referenceAcquisition) ? AutoTestToneSampleScale : 1.0));
					if (_isCancelled)
					{
						return;
					}
					float[] samples3 = RemoveDigitalRecordingGain(samples);
					centeredAnalysisRange = GetCenteredAnalysisRange(samples3, probeSampleRate, 0.2);
					num = DspProcessor.CalculateRms(samples3, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
					probeDbFs = 20.0 * Math.Log10(num + 1E-09);
					probePeak = GetPeak(samples3, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
					probeClippedPercent = GetClippedPercent(samples3, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
				}
				catch
				{
				}
			}
			if (probeDbFs < MinimumInputSignalDbFs)
			{
				SilentInputDetected = true;
				ThdPassed = false;
				LastSignalLevelDbFs = probeDbFs;
				_steps[0].Status = "Fail";
				_steps[0].Details = $"Không đủ tín hiệu ({probeDbFs:F1} dBFS; cần ≥ {MinimumInputSignalDbFs:F1} dBFS).";
				_steps[1].Status = "Pending";
				_steps[1].Details = "Bỏ qua: Không phát hiện tín hiệu ở bước 1.";
				_steps[2].Status = "Pending";
				_steps[2].Details = "Bỏ qua: Không phát hiện tín hiệu ở bước 1.";
				_steps[3].Status = "Fail";
				_steps[3].Details = "Dừng sớm: Không phát hiện tín hiệu từ ngõ thu.";
				OnStepsChanged?.Invoke(_steps);
				OnLogMessage?.Invoke("Bước 1 Error", $"KHÔNG ĐỦ TÍN HIỆU: Mức thu {probeDbFs:F1} dBFS (< {MinimumInputSignalDbFs:F1} dBFS). Đã dừng trước FEQ/THD để không tạo đồ thị giả.");
				OnLogMessage?.Invoke("Gợi ý khắc phục", "• Kiểm tra volume loa và đảm bảo ngõ phát đang phát âm thanh bình thường.\n• Kiểm tra micro: đảm bảo không bị Mute hoặc âm lượng micro trong Windows đang ở mức 0%.\n• Nếu dùng loa & micro laptop: Windows hoặc Realtek Audio Console thường tự động bật Echo Cancellation (khử vọng) hoặc AI Noise Suppression triệt tiêu âm loa vào mic.\n• Kiểm tra quyền Microphone trong Windows Settings (Allow desktop apps to access microphone).");
				NotifyTestCompleted(false);
				return;
			}
			if (probePeak >= 0.995 || probeClippedPercent > MaximumClippedSamplePercent)
			{
				InputClippingDetected = true;
				ThdPassed = false;
				LastSignalLevelDbFs = probeDbFs;
				LastFeqPeakSample = probePeak;
				_steps[0].Status = "Fail";
				_steps[0].Details = $"Tín hiệu quá mạnh/clipping (peak {probePeak:F4}; rail {probeClippedPercent:F4}%).";
				_steps[1].Status = "Pending";
				_steps[1].Details = "Bỏ qua: Input clipping ở bước kiểm tra tín hiệu.";
				_steps[2].Status = "Pending";
				_steps[2].Details = "Bỏ qua: Input clipping ở bước kiểm tra tín hiệu.";
				_steps[3].Status = "Fail";
				_steps[3].Details = "Dừng sớm: tín hiệu input quá mạnh; không tính FEQ/THD.";
				OnStepsChanged?.Invoke(_steps);
				OnLogMessage?.Invoke("Bước 1 Error", $"INPUT QUÁ MẠNH: peak {probePeak:F4}, rail {probeClippedPercent:F4}% (> {MaximumClippedSamplePercent:F4}%). Đã dừng trước sweep/THD để tránh báo méo giả.");
				OnLogMessage?.Invoke("Gợi ý khắc phục", "Giảm gain phần cứng/ngõ IN trước. Nếu vẫn clipping, giảm Windows Master Volume/ngõ OUT từng 5–10%. Sau đó mở Sine Check và chỉ đo lại khi báo ĐẠT.");
				NotifyTestCompleted(false);
				return;
			}
			_steps[0].Details = $"Out: {playbackDevice.FriendlyName}\nIn: {recordingDevice.FriendlyName} ({probeDbFs:F1} dBFS)";
			OnStepsChanged?.Invoke(_steps);
			OnLogMessage?.Invoke("Bước 1", $"Tín hiệu hợp lệ: {probeDbFs:F1} dBFS.");
			if (AutoTestOneKilohertzOnly || referenceAcquisition)
				await Task.Delay(AutoTestOneKilohertzOnly ? 250 : 1000, RunCancellation);
		}
		else
		{
			OnLogMessage?.Invoke("Bước 1", "Kiểm tra mức, clipping, SNR và đường âm trực tiếp từ chính bản thu sweep; không phát thêm tone 1 kHz.");
		}
		if (EnableNoiseDiagnostics && !requireQuietPreflight)
		{
			await RunFastNoiseDiagnosticsAsync(recordingDevice);
		}
		if (_isCancelled)
		{
			return;
		}
		if (IsRubBuzzTest)
		{
			await RunRubBuzzTestInternalAsync(playbackDevice, recordingDevice);
			return;
		}
		_steps[1].Status = "Running";
		OnStepsChanged?.Invoke(_steps);
		OnLogMessage?.Invoke("Step 2", RestrictFeqToEvaluationRange
			? $"Bắt đầu đo đáp tuyến; chỉ đánh giá 50 Hz–{EvaluationMaxFrequencyHz / 1000.0:0.#} kHz."
			: useLogSweep ? $"Bắt đầu logarithmic sine sweep 20 Hz - 20 kHz ({LogSweepPointsPerOctave} điểm/octave)..." : "Starting frequency sweep from 20 Hz to 20 kHz...");
		OnTestSubstatusChanged?.Invoke("Freq", "Initializing...");
		double[] sweepFrequencies = useLogSweep
			? GenerateLogSweepEvaluationFrequencies(RestrictFeqToEvaluationRange ? 50.0 : 20.0,
				RestrictFeqToEvaluationRange ? EvaluationMaxFrequencyHz : 20000.0, LogSweepPointsPerOctave)
			: (RestrictFeqToEvaluationRange
				? TestFrequencies.Where(f => f >= 50.0 && f <= EvaluationMaxFrequencyHz)
					.Append(EvaluationMaxFrequencyHz).Distinct().OrderBy(f => f).ToArray()
				: TestFrequencies);
		double[] array = FindMissingFrequencyLimits();
		if (array.Length != 0 && !referenceAcquisition)
		{
			MissingFrequencyLimits = true;
			_steps[1].Details = "ĐANG ĐO THAM KHẢO: thiếu golden/limit cho " + string.Join(", ", array.Select((double value16) => $"{value16:F0} Hz"));
			OnStepsChanged?.Invoke(_steps);
			OnLogMessage?.Invoke("INVALID_MISSING_LIMITS", _steps[1].Details + ". App vẫn thu và vẽ đồ thị nhưng không được kết luận PASS/FAIL.");
		}
		(string Name, double[] Frequencies)[] multitoneBands = ((string Name, double[] Frequencies)[])((!UseCombinedMultitoneFrequencyResponse) ? ((Array)TestFrequencyBands) : ((Array)new(string, double[])[1] { ("Combined", TestFrequencies) }));
		if (RestrictFeqToEvaluationRange)
		{
			multitoneBands = multitoneBands.Select(band =>
				(band.Name, band.Frequencies.Where(f => f >= 50.0 && f <= EvaluationMaxFrequencyHz)
					.Append(band.Name == "Treble" || band.Name == "Combined" ? EvaluationMaxFrequencyHz : 0.0)
					.Where(f => f >= 50.0).Distinct().OrderBy(f => f).ToArray())).ToArray();
		}
		(string Name, double[] MeasuredFrequencies, double[] PlaybackFrequencies)[] playbackBands = multitoneBands.Select(((string Name, double[] Frequencies) tuple2) => (Name: tuple2.Name, MeasuredFrequencies: tuple2.Frequencies, PlaybackFrequencies: (from f in tuple2.Frequencies.Append(1100.0).Distinct()
			orderby f
			select f).ToArray())).ToArray();
		double commonMultitoneScale = useLogSweep ? 1.0 : SignalSampleProvider.CalculateCommonMultitoneSampleScale(_audioEngine.PlaybackSampleRate, playbackBands.Select(((string Name, double[] MeasuredFrequencies, double[] PlaybackFrequencies) tuple2) => tuple2.PlaybackFrequencies));
		OnLogMessage?.Invoke("Thời gian chuẩn bị", $"Thiết bị + noise floor + chuẩn bị trước sweep: {preparationTimer.Elapsed.TotalSeconds:F2}s.");
		// Native-library initialization can overlap the capture instead of
		// extending the first post-capture analysis. No audio runs on this task.
		Task<string>? sweepFftBackend = useLogSweep ? Task.Run(() => SweepFourier.BackendName) : null;
		bool freqResponsePass = true;
		double maxFreqDev = 0.0;
		string compareMsg = "";
		bool isSilent = false;
		int targetValidRuns = (runFeqThreeTimes ? 3 : ((!useLogSweep) ? 1 : Math.Clamp(LogSweepVerificationRuns, 1, 3)));
		bool verifyFeqRepeatability = targetValidRuns > 1;
		int maxRunAttempts = (verifyFeqRepeatability ? (targetValidRuns + 1) : 1);
		int passedRuns = 0;
		int failedRuns = 0;
		int runAttemptsUsed = 0;
		List<Dictionary<double, double>> measuredRunResults = new List<Dictionary<double, double>>();
		List<IReadOnlyDictionary<double, double>> rawMeasuredRunResults = new();
		List<Complex[]> alignedComplexTransfers = new List<Complex[]>();
		List<Complex[][]> harmonicTransfersByRun = new List<Complex[][]>();
		LogSweepSettings? lastSweepSettings = null;
		(double DbFs, double Peak)? sweepTailNoise = null;
		int complexTransferSampleRate = 0;
		bool anyFeqSilentAttempt = false;
		bool anyFeqLowSnrAttempt = false;
		bool anySweepAcquisitionInvalid = false;
		string lastSweepInvalidReason = "";
		int lastLowSnrPointCount = 0;
		int lastSnrCheckedPointCount = 0;
		string lastLowSnrPointDetails = "";
		bool insufficientValidFeqRuns = false;
		double lowerDb;
		double value6;
		FrequencyLimitPoint point;
		for (int run = 1; run <= maxRunAttempts; run++)
		{
			if (_isCancelled)
			{
				return;
			}
			runAttemptsUsed = run;
			OnLogMessage?.Invoke("Step 2", $"--- FEQ attempt {run}/{maxRunAttempts}; valid {measuredRunResults.Count}/{targetValidRuns} ---");
			OnTestSubstatusChanged?.Invoke("Freq", $"Run {run}/{maxRunAttempts} (valid {measuredRunResults.Count}/{targetValidRuns})...");
			Dictionary<double, double> rawDbResults = new Dictionary<double, double>();
			List<double> pilotLevelsDbFs = new List<double>();
			List<double> multitoneBroadbandLevelsDbFs = new List<double>();
			List<(double Frequency, double SnrDb)> lowSnrPoints = new List<(double, double)>();
			int snrCheckedPoints = 0;
			bool sweepAcquisitionInvalid = false;
			bool complexPhaseLockInvalid = false;
			Complex[] alignedComplexTransfer = null;
			StandardAcousticResult? capturedSweepResult = null;
			double sweepSignalLevelDbFs = -180.0;
			if (useLogSweep)
			{
				int playbackSampleRate = _audioEngine.PlaybackSampleRate;
				double num2 = Math.Min(22000.0, (double)playbackSampleRate * 0.47);
				ImpulseEffectsSettings.Validate();
				LogSweepSettings logSweepSettings = new LogSweepSettings(playbackSampleRate, 18.0, num2, LogSweepDurationSeconds, PlaybackAmplitude,
					ResponseSmoothing.SourcePointsPerOctave, ImpulseEffectsSettings.CaptureTailSeconds, _audioEngine.RecordingGain);
				LogSweepSettings generatedSettings = logSweepSettings with
				{
					ApplyDistortionCalibration = ApplyDistortionCalibration,
					UseHarmonicFrequencyAsDistortionReference = UseHarmonicFrequencyAsDistortionReference,
					EffectsSettings = ImpulseEffectsSettings,
					StartFrequencyHz = ScalePlaybackFrequency(18.0),
					EndFrequencyHz = Math.Min(ScalePlaybackFrequency(num2), (double)playbackSampleRate * 0.47),
					DistortionEvaluationFrequenciesHz = new[]
					{
						ScalePlaybackFrequency(BassThdFrequencyHz),
						ScalePlaybackFrequency(MidThdFrequencyHz),
						ScalePlaybackFrequency(TrebleThdFrequencyHz)
					}
				};
				float[] generatedExcitation = StandardAcousticMeasurement.GenerateLogSweep(generatedSettings);
				float[] array2 = generatedExcitation;
				OnLogMessage?.Invoke("Step 2", $"Run {run} - phát log sweep {generatedSettings.StartFrequencyHz:F1} Hz - {generatedSettings.EndFrequencyHz / 1000.0:F2} kHz ({(double)array2.Length / playbackSampleRate:F2}s, {PlaybackLevelDbFs:F1} dBFS), so sánh 1/24 octave 50 Hz–18 kHz và thu thêm {ImpulseEffectsSettings.CaptureTailSeconds:F1}s...");
				OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - Log sweep guard 18 Hz → 22 kHz...");
				AudioEngine audioEngine = _audioEngine;
				double durationSeconds = (double)array2.Length / (double)playbackSampleRate + ImpulseEffectsSettings.CaptureTailSeconds;
				float[] customSweepSamples = array2;
				var captureTimer = Stopwatch.StartNew();
				float[] array3 = await audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sweep, 0.0, durationSeconds, samples => OnRealTimeRecordedSamples?.Invoke(samples), forceSaveFiles: false, null, null, 1.0, RunCancellation, null, customSweepSamples);
				captureTimer.Stop();
				var analysisTimer = Stopwatch.StartNew();
				int tailRate = _audioEngine.RecordingSampleRate;
				sweepTailNoise = null;
				if (array3.Length >= generatedExcitation.Length * tailRate / (double)playbackSampleRate + tailRate)
				{
					int tailStart = array3.Length - tailRate;
					double rms = DspProcessor.CalculateRms(array3, tailStart, tailRate);
					sweepTailNoise = (20 * Math.Log10(Math.Max(rms, 1e-9)), GetPeak(array3, tailStart, tailRate));
				}
				if (array3.Length > 0)
				{
					double sweepPeakAbs = array3.Max(s => Math.Abs((double)s));
					double sweepPeakDb = 20.0 * Math.Log10(Math.Max(1e-9, sweepPeakAbs));
					double sweepRmsVal = Math.Sqrt(array3.Select(s => (double)s * s).Average());
					double sweepRmsDb = 20.0 * Math.Log10(Math.Max(1e-9, sweepRmsVal));
					double noiseFloorDb = LastNoiseAssessment?.DutMicrophone != null ? LastNoiseAssessment.DutMicrophone.TotalRmsDb : -80.0;
					double sweepSnrDb = Math.Max(0.0, sweepRmsDb - noiseFloorDb);
					bool sweepIsClipped = sweepPeakAbs >= 0.995;
					OnMeasurementCaptured?.Invoke(array3, sweepPeakDb, sweepRmsDb, sweepSnrDb, sweepIsClipped);
				}
				if (_isCancelled)
				{
					return;
				}
				int recordingSampleRate = _audioEngine.RecordingSampleRate;
				if (array3.Length <= 1024)
				{
					throw new InvalidOperationException("Bản thu log sweep thiếu dữ liệu.");
				}
				float[] samples4 = RemoveDigitalRecordingGain(array3);
				if (CheckFeqInputClipping(samples4, "Log sweep"))
				{
					InputClippingDetected = true;
				}
				array3 = StandardAcousticMeasurement.ApplySubsonicConditioning(array3, recordingSampleRate);
				LogSweepSettings settings = generatedSettings with
				{
					SampleRate = recordingSampleRate,
					RecordingGain = _audioEngine.RecordingGain
				};
				lastSweepSettings = settings;
				float[] excitation = ((recordingSampleRate == playbackSampleRate) ? generatedExcitation : StandardAcousticMeasurement.GenerateLogSweep(settings));
				double? preSweepFloor = SuiteSharedNoiseFloorDbFs ?? LastNoiseAssessment?.DutMicrophone?.TotalRmsDb;
				var sweepCalibration = FeqMicrophoneCalibration;
				var stageTimings = new List<string>();
				string fftBackend = await sweepFftBackend!;
				capturedSweepResult = await Task.Run(() => StandardAcousticMeasurement.AnalyzeLogSweep(
					excitation, array3, settings, sweepCalibration, preSweepFloor,
					(stage, ms) => stageTimings.Add($"{stage}={ms:F0}ms"), RunCancellation), RunCancellation);
				analysisTimer.Stop();
				OnLogMessage?.Invoke("Thời gian sweep", $"Run {run}, {generatedExcitation.Length} mẫu: phát/thu {captureTimer.Elapsed.TotalSeconds:F2}s; tiền xử lý + phân tích {analysisTimer.Elapsed.TotalSeconds:F2}s. " + string.Join(", ", stageTimings));
				if (_isCancelled) return;
				StandardAcousticResult sweepResult = capturedSweepResult;
				double clockScale = sweepResult.ClockDriftCorrectionApplied ? 1 + sweepResult.EstimatedClockDriftPpm / 1e6 : 1;
				if (array3.Length - clockScale * (sweepResult.DirectArrivalSample + excitation.Length) < recordingSampleRate * 1.05)
					sweepTailNoise = null;
				OnLogMessage?.Invoke("Thời gian FFT", fftBackend);
				string clockState = sweepResult.ClockDriftCorrectionApplied
					? $"đã bù {sweepResult.EstimatedClockDriftPpm:+0.0;-0.0;0.0} ppm"
					: double.IsFinite(sweepResult.ClockFitRmsSamples)
						? $"ổn định {sweepResult.EstimatedClockDriftPpm:+0.0;-0.0;0.0} ppm"
						: "không đủ tin cậy để tự bù";
				OnLogMessage?.Invoke("FEQ Clock", $"Run {run}: {clockState}; sai số fit {sweepResult.ClockFitRmsSamples:F2} mẫu.");
				OnLogMessage?.Invoke("FEQ Window",
					$"Run {run}: {sweepResult.ImpulseWindowKind}, L −{sweepResult.ImpulseWindowLeftMs:F3} ms / R +{sweepResult.ImpulseWindowRightMs:F3} ms; "
                    + (sweepResult.FrequencyDependentWindowing ? $"FDW {sweepResult.FrequencyDependentWindowCycles:F0} chu kỳ." : "FDW tắt."));
				OnLogMessage?.Invoke("FEQ Grid",
					$"Run {run}: {sweepFrequencies.Length} điểm, {LogSweepPointsPerOctave} điểm/octave.");
				if (RequireLogSweepPhaseAlignment && sweepResult.AlignedTransferFunction.Length != 0)
				{
					alignedComplexTransfer = sweepResult.AlignedTransferFunction.ToArray();
					if (alignedComplexTransfers.Count > 0)
					{
						ComplexPhaseAlignment complexPhaseAlignment = StandardAcousticMeasurement.AlignComplexTransferToReference(alignedComplexTransfers[0], alignedComplexTransfer, recordingSampleRate);
						OnLogMessage?.Invoke("FEQ Phase Lock", $"Run {run}: bù trễ dư {complexPhaseAlignment.ResidualDelaySamples:F3} mẫu, pha hằng {complexPhaseAlignment.ConstantPhaseDegrees:F1}°, RMS pha {complexPhaseAlignment.FitRmsDegrees:F1}°; {(complexPhaseAlignment.IsValid ? "LOCKED" : "UNRELIABLE")}.");
						if (complexPhaseAlignment.IsValid)
						{
							alignedComplexTransfer = complexPhaseAlignment.Transfer;
						}
						else
						{
							complexPhaseLockInvalid = true;
						}
					}
					complexTransferSampleRate = recordingSampleRate;
				}
				rawDbResults = BuildSweepComparisonResponse(sweepResult.FrequencyResponse, sweepFrequencies);
				double num3 = 20.0 * Math.Log10(Math.Max(0.01, Math.Abs(_audioEngine.RecordingGain)));
				sweepSignalLevelDbFs = sweepResult.SignalLevelDbFs - num3;
				LastSweepResult = sweepResult;
				OnSweepResultReady?.Invoke(sweepResult);
				LastSweepHarmonics = sweepResult.SweepHarmonics;
				LastSweepDistortion = BuildSweepDistortionSummary(sweepResult);
				OnSweepDistortionReady?.Invoke(BuildSweepDistortionSummary(sweepResult, MidThdFrequencyHz));
				sweepAcquisitionInvalid = (sweepResult.Validity == "INVALID" || rawDbResults.Count != sweepFrequencies.Length) | complexPhaseLockInvalid;
				if (sweepResult.IsClipped)
				{
					InputClippingDetected = true;
				}
				if (sweepAcquisitionInvalid)
				{
					anySweepAcquisitionInvalid = true;
					lastSweepInvalidReason = string.Join(" ", from measurementDiagnostic in sweepResult.Diagnostics
						where measurementDiagnostic.Severity == "INVALID"
						select measurementDiagnostic.Code + ": " + measurementDiagnostic.Message);
					if (complexPhaseLockInvalid)
					{
						lastSweepInvalidReason = "INVALID_PHASE_LOCK: không căn được pha/trễ của lượt sweep này với lượt đầu; app sẽ tự đo bù.";
					}
					else if (string.IsNullOrWhiteSpace(lastSweepInvalidReason))
					{
						lastSweepInvalidReason = "Không giải mã đủ điểm đáp tuyến từ log sweep.";
					}
					OnLogMessage?.Invoke("Log Sweep INVALID", lastSweepInvalidReason);
				}
				if (sweepResult.EstimatedSnrDb < MinimumFeqSnrDb)
				{
					lowSnrPoints.AddRange(from frequency in sweepFrequencies
						where frequency >= 50.0 && frequency <= EvaluationMaxFrequencyHz
						select (frequency: frequency, EstimatedSnrDb: sweepResult.EstimatedSnrDb));
				}
				snrCheckedPoints = sweepFrequencies.Count((double frequency) => frequency >= 50.0 && frequency <= EvaluationMaxFrequencyHz);
				OnLogMessage?.Invoke("Log Sweep", $"Run {run}: giải mã {rawDbResults.Count} điểm, mức {sweepSignalLevelDbFs:F1} dBFS, SNR {sweepResult.EstimatedSnrDb:F1} dB, tương quan {sweepResult.SweepCorrelation:F3}.");
				OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - Đã giải mã {rawDbResults.Count} điểm...");
			}
			else if (AudioEngine.flagGenerateSeperateSine)
			{
				double[] array4 = sweepFrequencies;
				double[] array5 = array4;
				foreach (double freq in array5)
				{
					if (_isCancelled)
					{
						return;
					}
					double durationSeconds2 = ((freq < 100.0 || freq > 10000.0) ? 1.0 : 0.5);
					OnLogMessage?.Invoke("Step 2", $"Run {run} - Testing: {freq} Hz");
					OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - {freq} Hz");
					float[] samples5 = RemoveDigitalRecordingGain(await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, ScalePlaybackFrequency(freq), durationSeconds2, cancellationToken: RunCancellation));
					if (CheckFeqInputClipping(samples5, $"{freq:F0} Hz"))
					{
						InputClippingDetected = true;
					}
					int recordingSampleRate2 = _audioEngine.RecordingSampleRate;
					double durationSeconds3 = Math.Clamp(20.0 / freq, 0.2, 0.8);
					(int, int) centeredAnalysisRange2 = GetCenteredAnalysisRange(samples5, recordingSampleRate2, durationSeconds3);
					double num4 = DspProcessor.CalculateRms(samples5, centeredAnalysisRange2.Item1, centeredAnalysisRange2.Item2);
					double value = ApplyFeqMicrophoneCalibration(freq, 20.0 * Math.Log10(num4 + 1E-09));
					rawDbResults[freq] = value;
				}
			}
			else
			{
				for (int playbackSampleRate = 0; playbackSampleRate < playbackBands.Length; playbackSampleRate++)
				{
					if (_isCancelled)
					{
						return;
					}
					(string Name, double[] MeasuredFrequencies, double[] PlaybackFrequencies) band = playbackBands[playbackSampleRate];
					double num5 = Math.Clamp(MultitoneDurationSeconds, 3.0, 60.0);
					double freq = num5 - 2.0;
					OnLogMessage?.Invoke("Step 2", $"Run {run} - {band.Name} multitone {playbackSampleRate + 1}/{playbackBands.Length}: {band.MeasuredFrequencies.Length} points + pilot {1100.0:F0} Hz, thu {num5:F1}s / phân tích {freq:F1}s giữa...");
					OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - {band.Name} ({playbackSampleRate + 1}/{playbackBands.Length})...");
					double[] array5 = ScalePlaybackFrequencies(band.PlaybackFrequencies);
					float[] samples6 = await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Multitone, ScalePlaybackFrequency(1100.0), num5, samplesChunk => OnRealTimeRecordedSamples?.Invoke(samplesChunk), forceSaveFiles: false, array5, null, commonMultitoneScale, cancellationToken: RunCancellation);
					if (samples6.Length > 0)
					{
						double multiPeakAbs = samples6.Max(s => Math.Abs((double)s));
						double multiPeakDb = 20.0 * Math.Log10(Math.Max(1e-9, multiPeakAbs));
						double multiRmsVal = Math.Sqrt(samples6.Select(s => (double)s * s).Average());
						double multiRmsDb = 20.0 * Math.Log10(Math.Max(1e-9, multiRmsVal));
						double noiseFloorDb = LastNoiseAssessment?.DutMicrophone != null ? LastNoiseAssessment.DutMicrophone.TotalRmsDb : -80.0;
						double multiSnrDb = Math.Max(0.0, multiRmsDb - noiseFloorDb);
						bool multiIsClipped = multiPeakAbs >= 0.995;
						OnMeasurementCaptured?.Invoke(samples6, multiPeakDb, multiRmsDb, multiSnrDb, multiIsClipped);
					}
					if (_isCancelled)
					{
						return;
					}
					int recordingSampleRate3 = _audioEngine.RecordingSampleRate;
					float[] array6 = ExtractCenteredAnalysisWindow(samples6, recordingSampleRate3, freq);
					if ((double)array6.Length < (double)recordingSampleRate3 * freq)
					{
						throw new InvalidOperationException("Bản thu FEQ bị thiếu mẫu; kiểm tra thiết bị thu rồi đo lại.");
					}
					array6 = RemoveDigitalRecordingGain(array6);
					double num6 = DspProcessor.CalculateRms(array6, 0, array6.Length);
					multitoneBroadbandLevelsDbFs.Add(20.0 * Math.Log10(num6 + 1E-09));
					if (CheckFeqInputClipping(array6, band.Name))
					{
						InputClippingDetected = true;
					}
					Dictionary<double, double> dictionary = DspProcessor.CalculateMultitoneResponse(array6, recordingSampleRate3, array5);
					double num7 = dictionary[ScalePlaybackFrequency(1100.0)];
					pilotLevelsDbFs.Add(num7);
					OnLogMessage?.Invoke("FEQ Pilot", $"Run {run} - {band.Name}: {1100.0:F0} Hz = {num7:F2} dBFS.");
					double[] item = band.MeasuredFrequencies;
					foreach (double num9 in item)
					{
						rawDbResults[num9] = ApplyFeqMicrophoneCalibration(num9, dictionary[ScalePlaybackFrequency(num9)]);
						if (num9 >= 50.0 && num9 <= EvaluationMaxFrequencyHz && _dutNoiseFloorByFrequencyDbFs.TryGetValue(num9, out var value3))
						{
							snrCheckedPoints++;
							double num10 = dictionary[ScalePlaybackFrequency(num9)] - value3;
							if (num10 < MinimumFeqSnrDb)
							{
								lowSnrPoints.Add((num9, num10));
							}
						}
					}
				}
				OnLogMessage?.Invoke("Step 2", UseCombinedMultitoneFrequencyResponse ? $"Combined {sweepFrequencies.Length} points from one simultaneous multitone capture; each point uses the median of 5 FFT windows." : $"Combined {sweepFrequencies.Length} points from Bass/Mid/Treble segments; each point uses the median of 5 FFT windows.");
				OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - Analyzing combined curve...");
			}
			bool flag = ((!useLogSweep) ? (snrCheckedPoints > 0 && multitoneBands.Any(delegate((string Name, double[] Frequencies) tuple2)
			{
				int num42 = tuple2.Frequencies.Count((double f) => f >= 50.0 && f <= EvaluationMaxFrequencyHz && _dutNoiseFloorByFrequencyDbFs.ContainsKey(f));
				int num43 = lowSnrPoints.Count(((double Frequency, double SnrDb) p) => Enumerable.Contains(tuple2.Frequencies, p.Frequency));
				return num42 > 0 && (double)num43 / (double)num42 > MaxLowSnrPointRatio;
			})) : (sweepAcquisitionInvalid || lowSnrPoints.Count > 0));
			if (flag)
			{
				string text = string.Join(", ", (from tuple2 in lowSnrPoints
					orderby tuple2.Frequency
					select $"{tuple2.Frequency:F0}Hz={tuple2.SnrDb:F1}dB").Take(12));
				anyFeqLowSnrAttempt |= lowSnrPoints.Count > 0;
				lastLowSnrPointCount = lowSnrPoints.Count;
				lastSnrCheckedPointCount = snrCheckedPoints;
				lastLowSnrPointDetails = text;
				if (lowSnrPoints.Count > 0)
				{
					OnLogMessage?.Invoke("Noise", $"FEQ attempt {run} INVALID/RETRY: {lowSnrPoints.Count}/{snrCheckedPoints} điểm có SNR < {MinimumFeqSnrDb:F0} dB. Điểm mẫu: {text}");
				}
			}
			Dictionary<double, double> dictionary2 = useLogSweep ? new(rawDbResults) : ApplyThreePointDbSmoothing(rawDbResults);
			if (NormalizeFrequencyResponseToOneKilohertz)
			{
				dictionary2 = NormalizeAtOneKilohertz(dictionary2, out double oneKilohertzDbFs);
				LastOneKilohertzLevelDbFs = oneKilohertzDbFs;
				OnOneKilohertzLevelMeasured?.Invoke(oneKilohertzDbFs);
			}
			else if (TryGetStandardValue(dictionary2, 1000.0, out double oneKilohertzDbFs))
			{
				LastOneKilohertzLevelDbFs = oneKilohertzDbFs;
				OnOneKilohertzLevelMeasured?.Invoke(oneKilohertzDbFs);
			}
			double num11 = (useLogSweep ? sweepSignalLevelDbFs : ((!AudioEngine.flagGenerateSeperateSine) ? ((multitoneBroadbandLevelsDbFs.Count == 0) ? (-180.0) : CalculateMedian(multitoneBroadbandLevelsDbFs)) : ((!rawDbResults.ContainsKey(1000.0)) ? rawDbResults.Values.Max() : rawDbResults[1000.0])));
			// A sweep spreads energy over the full band and is quieter in broadband
			// RMS than the 1 kHz pilot. Its correlation/SNR guard still rejects noise.
			double feqSignalThresholdDbFs = useLogSweep
				? Math.Min(-55.0, MinimumInputSignalDbFs - 12.0)
				: MinimumInputSignalDbFs;
			isSilent = num11 < feqSignalThresholdDbFs;
			LastSignalLevelDbFs = num11;
			if (!useLogSweep && !AudioEngine.flagGenerateSeperateSine)
			{
				double value4 = ((pilotLevelsDbFs.Count == 0) ? (-180.0) : CalculateMedian(pilotLevelsDbFs));
				OnLogMessage?.Invoke("FEQ Signal", $"Run {run}: mức băng rộng {num11:F1} dBFS; pilot {1100.0:F0} Hz {value4:F1} dBFS.");
			}
			anyFeqSilentAttempt |= isSilent;
			if (isSilent)
			{
				OnLogMessage?.Invoke("Step 2 Error", $"Run {run} - Silent input detected ({num11:F1} dBFS).");
			}
			Dictionary<double, double> dictionary3 = new Dictionary<double, double>();
			double num12 = 0.0;
			int num13 = 0;
			int num14 = 0;
			int num15 = 0;
			int num16 = 0;
			int num17 = 0;
			int num18 = 0;
			bool criticalFailure = false;
			bool criticalFailure2 = false;
			bool criticalFailure3 = false;
			foreach (KeyValuePair<double, double> item2 in dictionary2)
			{
				double num20 = item2.Value;
				dictionary3[item2.Key] = num20;
				OnFrequencyResponsePoint?.Invoke(item2.Key, num20);
				if (!(item2.Key >= 50.0) || !(item2.Key <= EvaluationMaxFrequencyHz))
				{
					continue;
				}
				bool flag2 = EvaluateFrequencyPoint(item2.Key, num20, out var targetDb, out lowerDb, out value6, out var critical);
				if (flag2 && item2.Key >= 4000.0)
				{
					OnLogMessage?.Invoke("Treble", $"{item2.Key:0} Hz: đo {num20:F2} {(NormalizeFrequencyResponseToOneKilohertz ? "dBr" : "dBFS")}; chuẩn {targetDb:F2}; giới hạn [{lowerDb:F2}, {value6:F2}].");
				}
				bool num21 = ProductionMeasurement.TryGetFrequencyLimit(FrequencyLimits, item2.Key, out point) || TryGetStandardValue(StandardCurve, item2.Key, out targetDb);
				double num22 = (num21 ? Math.Abs(num20 - targetDb) : 0.0);
				if (num21 && num22 > num12)
				{
					num12 = num22;
				}
				bool flag3 = isSilent | flag2;
				if (item2.Key < 250.0)
				{
					num13++;
					if (flag3)
					{
						num14++;
					}
					if (critical & flag3)
					{
						criticalFailure = true;
					}
				}
				else if (item2.Key < 4000.0)
				{
					num15++;
					if (flag3)
					{
						num16++;
					}
					if (critical & flag3)
					{
						criticalFailure2 = true;
					}
				}
				else
				{
					num17++;
					if (flag3)
					{
						num18++;
					}
					if (critical & flag3)
					{
						criticalFailure3 = true;
					}
				}
			}
			if (!isSilent && !flag && !sweepAcquisitionInvalid)
			{
				measuredRunResults.Add(dictionary3);
				if (useLogSweep) rawMeasuredRunResults.Add(new Dictionary<double, double>(LastUnsmoothedFrequencyResponse));
				if (useLogSweep && capturedSweepResult?.HarmonicTransferFunctions.Count >= 10)
				{
					harmonicTransfersByRun.Add(capturedSweepResult.HarmonicTransferFunctions.ToArray());
				}
				if (useLogSweep && alignedComplexTransfer != null)
				{
					alignedComplexTransfers.Add(alignedComplexTransfer);
				}
			}
			bool flag4 = IsBandPassed(isSilent, num14, num13, criticalFailure);
			bool flag5 = IsBandPassed(isSilent, num16, num15, criticalFailure2);
			bool flag6 = IsBandPassed(isSilent, num18, num17, criticalFailure3);
			bool flag7 = flag4 & flag5 & flag6;
			if (isSilent)
			{
				SilentInputDetected = true;
				OnLogMessage?.Invoke("Step 2 Error", $"Run {run} phát hiện không có tín hiệu ({num11:F1} dBFS). Dừng đo ngay lập tức.");
				insufficientValidFeqRuns = true;
				break;
			}
			if (flag)
			{
				OnLogMessage?.Invoke("Step 2", $"Run {run} không được tính PASS/FAIL vì bản thu không hợp lệ; vẫn giữ các điểm để chẩn đoán. "
					+ (run < maxRunAttempts ? "Tiếp tục lượt xác nhận đã cấu hình." : "Đã hết số lượt đã cấu hình; không tự phát lại."));
			}
			else if (flag7)
			{
				passedRuns++;
				OnLogMessage?.Invoke("Step 2", $"Run {run} PASSED. Bass: {num14}/{num13}, Mid: {num16}/{num15}, Treble: {num18}/{num17}, Max Dev: {num12:F2} dB");
			}
			else
			{
				failedRuns++;
				OnLogMessage?.Invoke("Step 2", $"Run {run} FAILED. Bass: {num14}/{num13}{(flag4 ? "" : " (FAIL)")}, Mid: {num16}/{num15}{(flag5 ? "" : " (FAIL)")}, Treble: {num18}/{num17}{(flag6 ? "" : " (FAIL)")}, Max Dev: {num12:F2} dB");
			}
			maxFreqDev = num12;
			BassPassed = flag4;
			MidPassed = flag5;
			TreblePassed = flag6;
			if (!verifyFeqRepeatability & flag)
			{
				freqResponsePass = false;
			}
			double num23 = 0.0;
			double num24 = 0.0;
			int num25 = 0;
			if (StandardCurve != null && StandardCurve.Count > 0)
			{
				foreach (KeyValuePair<double, double> item3 in dictionary3)
				{
					double key = item3.Key;
					if (key >= 50.0 && key <= EvaluationMaxFrequencyHz && TryGetStandardValue(StandardCurve, key, out var value7))
					{
						double num26 = item3.Value - value7;
						double num27 = Math.Abs(Math.Pow(10.0, num26 / 20.0) - 1.0) * 100.0;
						num24 += num27;
						if (num27 > num23)
						{
							num23 = num27;
						}
						num25++;
					}
				}
			}
			if (num25 > 0 && !flag && !isSilent && !sweepAcquisitionInvalid)
			{
				double num28 = num24 / (double)num25;
				LastMaxDevPercent = num23;
				LastAvgDevPercent = num28;
				HasComparedToStandard = true;
				compareMsg = $" | Dev to Std: Max {num23:F1}%, Avg {num28:F1}%";
			}
			if (AudioEngine.flagSaveData)
			{
				try
				{
					string value8 = DateTime.Now.ToString("yyyyMMdd_HHmmss");
					string path = $"feq_results_run{run}_{value8}.csv";
					using StreamWriter streamWriter = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
					streamWriter.WriteLine($"Sonca Audio Inspector - FEQ Test Results (Run {run})");
					streamWriter.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
					streamWriter.WriteLine("Test Mode: " + (useLogSweep ? "Logarithmic Sine Sweep" : (AudioEngine.flagGenerateSeperateSine ? "Sine Sweep" : "Multitone")));
					streamWriter.WriteLine($"Captured Signal Level: {num11:F2} dBFS; 1 kHz: {LastOneKilohertzLevelDbFs:F2} dBFS; LevelBasis={CurrentFrequencyResponseLevelBasis}");
					streamWriter.WriteLine($"Tolerance Limit: ±{FreqResponseToleranceDb:F1} dB");
					streamWriter.WriteLine("Overall Run Result: " + ((!flag) ? (flag7 ? "PASS" : "FAIL") : (sweepAcquisitionInvalid ? "INVALID_SWEEP_ACQUISITION" : "INVALID_NOISE_LOW_SNR")));
					streamWriter.WriteLine($"Max Deviation: {num12:F2} dB");
					streamWriter.WriteLine();
					string responseUnit = NormalizeFrequencyResponseToOneKilohertz ? "dBr" : "dBFS";
					streamWriter.WriteLine($"Frequency (Hz),Raw Level (dBFS),Measured Level ({responseUnit}),Lower Limit ({responseUnit}),Upper Limit ({responseUnit}),Critical,Limit Status");
					foreach (KeyValuePair<double, double> item4 in rawDbResults)
					{
						double freq4 = item4.Key;
						double value9 = item4.Value;
						double num29 = dictionary3[freq4];
						string value10 = "N/A";
						string value11 = "";
						string value12 = "";
						bool critical2 = CriticalZones.Any((CriticalFrequencyZone zone) => zone.Contains(freq4));
						if (freq4 >= 50.0 && freq4 <= EvaluationMaxFrequencyHz)
						{
							bool flag8 = EvaluateFrequencyPoint(freq4, num29, out value6, out var lowerDb2, out var upperDb, out critical2);
							if (ProductionMeasurement.TryGetFrequencyLimit(FrequencyLimits, freq4, out point) || TryGetStandardValue(StandardCurve, freq4, out value6))
							{
								value11 = lowerDb2.ToString("F2", CultureInfo.InvariantCulture);
								value12 = upperDb.ToString("F2", CultureInfo.InvariantCulture);
								value10 = (flag8 ? "FAIL" : "PASS");
							}
						}
						streamWriter.WriteLine($"{freq4},{value9:F2},{num29:F2},{value11},{value12},{critical2},{value10}");
					}
				}
				catch (Exception ex2)
				{
					OnLogMessage?.Invoke("Step 2 Error", $"Failed to save FEQ CSV for Run {run}: {ex2.Message}");
				}
			}
			if (!verifyFeqRepeatability && !flag && !isSilent && passedRuns >= 1)
			{
				freqResponsePass = true;
				OnLogMessage?.Invoke("Step 2", $"FEQ Decided: PASS ({passedRuns} Passes, {failedRuns} Fails)");
				break;
			}
			if (!verifyFeqRepeatability && !flag && !isSilent && failedRuns >= 1)
			{
				freqResponsePass = false;
				OnLogMessage?.Invoke("Step 2", $"FEQ Decided: FAIL ({passedRuns} Passes, {failedRuns} Fails)");
				break;
			}
			if (measuredRunResults.Count >= targetValidRuns)
			{
				break;
			}
			await Task.Delay(300, RunCancellation);
		}
		if (verifyFeqRepeatability)
		{
			compareMsg = "";
			HasComparedToStandard = false;
			LastMaxDevPercent = 0.0;
			LastAvgDevPercent = 0.0;
			Dictionary<double, double> dictionary4 = new Dictionary<double, double>();
			bool flag9 = false;
			if (useLogSweep && alignedComplexTransfers.Count == measuredRunResults.Count && alignedComplexTransfers.Count > 0 && complexTransferSampleRate > 0)
			{
				var finalizeTimer = Stopwatch.StartNew();
				var transfersToAverage = alignedComplexTransfers.ToArray();
				double startFrequency = ScalePlaybackFrequency(18.0), endFrequency = ScalePlaybackFrequency(22000.0);
				var calibration = FeqMicrophoneCalibration;
				var (complexAverage, response) = await Task.Run(() =>
				{
					RunCancellation.ThrowIfCancellationRequested();
					var average = StandardAcousticMeasurement.AverageComplexTransfers(transfersToAverage);
					var curve = StandardAcousticMeasurement.BuildFrequencyResponseFromAlignedTransfer(average.Transfer,
						complexTransferSampleRate, startFrequency, endFrequency, ResponseSmoothing.SourcePointsPerOctave,
						calibration, lastSweepSettings);
					return (average, curve);
				}, RunCancellation);
				if (_isCancelled) return;
				OnLogMessage?.Invoke("Thời gian tổng hợp", $"Coherent average + đáp tuyến cuối: {finalizeTimer.Elapsed.TotalSeconds:F2}s.");
				dictionary4 = BuildSweepComparisonResponse(response, sweepFrequencies);
				if (NormalizeFrequencyResponseToOneKilohertz)
				{
					dictionary4 = NormalizeAtOneKilohertz(dictionary4, out double coherentOneKilohertzDbFs);
					LastOneKilohertzLevelDbFs = coherentOneKilohertzDbFs;
					OnOneKilohertzLevelMeasured?.Invoke(coherentOneKilohertzDbFs);
				}
				double[] array7 = sweepFrequencies.Where((double frequency) => frequency >= 50.0 && frequency <= EvaluationMaxFrequencyHz).Select(delegate(double frequency)
				{
					int num42 = Math.Clamp((int)Math.Round(ScalePlaybackFrequency(frequency) * (double)complexAverage.Transfer.Length / (double)complexTransferSampleRate), 1, complexAverage.Transfer.Length / 2 - 1);
					return complexAverage.VectorStrength[num42];
				}).ToArray();
				int num30 = array7.Count((double num42) => num42 < 0.85);
				flag9 = array7.Length != 0 && (double)num30 / (double)array7.Length > 0.1;
				OnLogMessage?.Invoke("FEQ Complex Average", $"Đã cộng vector phức {alignedComplexTransfers.Count} lần sweep sau căn trễ phân số; vector strength thấp < 0,85 tại {num30}/{array7.Length} điểm." + (flag9 ? " INVALID_PHASE_ALIGNMENT: pha giữa các lần đo chưa ổn định." : " Pha đủ ổn định để dùng kết quả coherent average."));
				if (!flag9
					&& harmonicTransfersByRun.Count == measuredRunResults.Count
					&& harmonicTransfersByRun.Count > 1
					&& lastSweepSettings != null
					&& LastSweepResult != null)
				{
					Complex[][]? averagedHarmonicTransfers = AverageHarmonicTransfers(harmonicTransfersByRun);
					if (averagedHarmonicTransfers != null)
					{
						IReadOnlyList<SweepHarmonicPoint> averagedHarmonics =
							StandardAcousticMeasurement.BuildSweepHarmonicPointsFromTransfers(
								averagedHarmonicTransfers, lastSweepSettings, FeqMicrophoneCalibration);
						if (averagedHarmonics.Count > 0)
						{
							LastSweepResult = LastSweepResult with
							{
								SweepHarmonics = averagedHarmonics,
								HarmonicTransferFunctions = averagedHarmonicTransfers,
								RewDistortion = StandardAcousticMeasurement.CalculateRewDistortionStats(averagedHarmonics)
							};
							LastSweepHarmonics = averagedHarmonics;
							LastSweepDistortion = BuildSweepDistortionSummary(LastSweepResult);
							OnSweepDistortionReady?.Invoke(BuildSweepDistortionSummary(LastSweepResult, MidThdFrequencyHz));
							OnLogMessage?.Invoke("THD Coherent Average", $"Đã cộng phức H1-H9 của {harmonicTransfersByRun.Count} log-sweep đồng bộ; không dùng median cho THD.");
						}
					}
				}
			}
			else if (measuredRunResults.Count > 0)
			{
				if (useLogSweep && rawMeasuredRunResults.Count > 0)
				{
					LastUnsmoothedFrequencyResponse = rawMeasuredRunResults[0].Keys.Where(f => rawMeasuredRunResults.All(r => r.ContainsKey(f)))
						.ToDictionary(f => f, f => CalculateMedian(rawMeasuredRunResults.Select(r => r[f])));
					OnUnsmoothedResponseReady?.Invoke(LastUnsmoothedFrequencyResponse);
				}
				double[] item = sweepFrequencies;
				foreach (double freq5 in item)
				{
					List<double> list = (from dictionary5 in measuredRunResults
						where dictionary5.ContainsKey(freq5)
						select dictionary5[freq5]).ToList();
					if (list.Count > 0)
					{
						dictionary4[freq5] = CalculateMedian(list);
					}
				}
			}
		FeqRepeatabilityAssessment feqRepeatabilityAssessment = ((measuredRunResults.Count < targetValidRuns) ? new FeqRepeatabilityAssessment(IsInvalid: false, 0, Array.Empty<FeqRepeatabilityPoint>(), 0, 0, 0, 0, 0, 0) : AssessFeqRepeatabilityInRange(measuredRunResults, useLogSweep, FreqResponseToleranceDb, CriticalZones, EvaluationMaxFrequencyHz));
			ComplexPhaseAlignmentInvalid = flag9;
			FeqRepeatabilityInvalid = feqRepeatabilityAssessment.IsInvalid || ComplexPhaseAlignmentInvalid;
			bool flag10 = measuredRunResults.Count >= targetValidRuns && !FeqRepeatabilityInvalid;
			insufficientValidFeqRuns = measuredRunResults.Count < targetValidRuns;
			if (ComplexPhaseAlignmentInvalid)
			{
				OnLogMessage?.Invoke("FEQ Phase Alignment", "INVALID_PHASE_ALIGNMENT: biên độ các lần đo có thể ổn định nhưng pha không đồng bộ; không dùng coherent average để lưu chuẩn hoặc kết luận.");
			}
			else if (FeqRepeatabilityInvalid)
			{
				OnLogMessage?.Invoke("FEQ Repeatability", $"INVALID_UNSTABLE_RESPONSE: {DescribeFeqRepeatability(feqRepeatabilityAssessment)} Ngưỡng spread {FreqResponseToleranceDb:F2} dB; invalid khi một dải vượt 10% hoặc có điểm CRITICAL.");
			}
			else if (feqRepeatabilityAssessment.UnstablePoints.Count > 0)
			{
				OnLogMessage?.Invoke("FEQ Repeatability", "ACCEPTED_WITH_LOCAL_VARIATION: " + DescribeFeqRepeatability(feqRepeatabilityAssessment, 8) + " Vẫn trong tỷ lệ cho phép 10% mỗi dải.");
			}
			bool flag11 = (SilentInputDetected = (dictionary4.Count == 0) & anyFeqSilentAttempt);
			isSilent = flag11;
			int num31 = 0;
			int num32 = 0;
			int num33 = 0;
			int num34 = 0;
			int num35 = 0;
			int num36 = 0;
			bool criticalFailure4 = false;
			bool criticalFailure5 = false;
			bool criticalFailure6 = false;
			maxFreqDev = 0.0;
			double num37 = 0.0;
			double num38 = 0.0;
			int num39 = 0;
			foreach (KeyValuePair<double, double> item5 in dictionary4.OrderBy((KeyValuePair<double, double> keyValuePair) => keyValuePair.Key))
			{
				double key2 = item5.Key;
				double value13 = item5.Value;
				OnFrequencyResponsePoint?.Invoke(key2, value13);
				if (key2 < 50.0 || key2 > EvaluationMaxFrequencyHz)
				{
					continue;
				}
				bool flag13 = EvaluateFrequencyPoint(key2, value13, out var targetDb2, out value6, out lowerDb, out var critical3);
				bool num40 = ProductionMeasurement.TryGetFrequencyLimit(FrequencyLimits, key2, out point) || TryGetStandardValue(StandardCurve, key2, out targetDb2);
				double val = (num40 ? Math.Abs(value13 - targetDb2) : 0.0);
				if (num40)
				{
					maxFreqDev = Math.Max(maxFreqDev, val);
				}
				bool flag14 = isSilent | flag13;
				if (key2 < 250.0)
				{
					num34++;
					if (flag14)
					{
						num31++;
					}
					if (critical3 & flag14)
					{
						criticalFailure4 = true;
					}
				}
				else if (key2 < 4000.0)
				{
					num35++;
					if (flag14)
					{
						num32++;
					}
					if (critical3 & flag14)
					{
						criticalFailure5 = true;
					}
				}
				else
				{
					num36++;
					if (flag14)
					{
						num33++;
					}
					if (critical3 & flag14)
					{
						criticalFailure6 = true;
					}
				}
				if (num40)
				{
					double num41 = Math.Abs(Math.Pow(10.0, (value13 - targetDb2) / 20.0) - 1.0) * 100.0;
					num38 += num41;
					num37 = Math.Max(num37, num41);
					num39++;
				}
			}
			BassPassed = IsBandPassed(isSilent, num31, num34, criticalFailure4);
			MidPassed = IsBandPassed(isSilent, num32, num35, criticalFailure5);
			TreblePassed = IsBandPassed(isSilent, num33, num36, criticalFailure6);
			freqResponsePass = flag10 && BassPassed && MidPassed && TreblePassed;
			if (num39 > 0)
			{
				LastMaxDevPercent = num37;
				LastAvgDevPercent = num38 / (double)num39;
				HasComparedToStandard = true;
				compareMsg = $" | Dev to Std: Max {LastMaxDevPercent:F1}%, Avg {LastAvgDevPercent:F1}%";
			}
			OnLogMessage?.Invoke("Step 2", $"FEQ Median (valid runs: {measuredRunResults.Count}/{targetValidRuns}, attempts: {runAttemptsUsed}): {(freqResponsePass ? "PASS" : "FAIL")}. Bass: {num31}/{num34}, Mid: {num32}/{num35}, Treble: {num33}/{num36}, Max Dev: {maxFreqDev:F2} dB");
			if (insufficientValidFeqRuns)
			{
				OnLogMessage?.Invoke("Step 2", $"INVALID_INSUFFICIENT_VALID_RUNS: chỉ có {measuredRunResults.Count}/{targetValidRuns} lần FEQ hợp lệ sau {runAttemptsUsed} lần thử{(anyFeqSilentAttempt ? "; có lần không nhận đủ tín hiệu" : "")}.");
			}
		}
		else if (measuredRunResults.Count < targetValidRuns)
		{
			freqResponsePass = false;
			SilentInputDetected = anyFeqSilentAttempt;
			insufficientValidFeqRuns = true;
		}
		LastFrequencyMaxDeviationDb = maxFreqDev;
		if ((InputClippingDetected | insufficientValidFeqRuns) || FeqRepeatabilityInvalid)
		{
			freqResponsePass = false;
			_steps[1].Status = "Invalid";
			_steps[1].Details = (InputClippingDetected ? "Bản thu FEQ quá tải; giảm gain phần cứng/volume rồi đo lại." : ((!FeqRepeatabilityInvalid) ? ((anySweepAcquisitionInvalid && !string.IsNullOrWhiteSpace(lastSweepInvalidReason)) ? lastSweepInvalidReason : "Không đủ lượt thu FEQ hợp lệ; kiểm tra SNR từng dải rồi đo lại.") : (ComplexPhaseAlignmentInvalid ? "Biên độ ổn định nhưng pha log-sweep chưa đồng bộ giữa các lượt; không dùng coherent average sai. Kiểm tra clock/timing của ngõ phát-thu." : "Đáp tuyến không ổn định giữa các lượt; không dùng median để che sai lệch. Kiểm tra DSP/AGC, vị trí mic và gá loa.")));
		}
		else if (MissingFrequencyLimits)
		{
			_steps[1].Status = "Invalid";
			_steps[1].Details = "Đã thu đáp tuyến nhưng chưa có đủ line chuẩn/giới hạn; chỉ dùng để kiểm tra đường tín hiệu.";
			OnLogMessage?.Invoke("CHƯA ĐỦ CHUẨN", _steps[1].Details);
		}
		else if (freqResponsePass)
		{
			_steps[1].Status = "Pass";
			_steps[1].Details = $"Max Deviation: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB)";
			OnLogMessage?.Invoke("Step 2", $"Frequency response PASSED. Max deviation: {maxFreqDev:F2} dB{compareMsg}");
		}
		else
		{
			_steps[1].Status = "Fail";
			string text2 = "";
			if (!BassPassed)
			{
				text2 += "Bass ";
			}
			if (!MidPassed)
			{
				text2 += "Middle ";
			}
			if (!TreblePassed)
			{
				text2 += "Treble ";
			}
			string value14 = (string.IsNullOrEmpty(text2) ? "" : (" | Failed: " + text2.Trim()));
			_steps[1].Details = (insufficientValidFeqRuns ? $"Không đủ lần FEQ hợp lệ ({measuredRunResults.Count}/{targetValidRuns}); cần đo lại." : (isSilent ? "No signal detected (silent input)." : $"Max Dev: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB){value14}"));
			OnLogMessage?.Invoke("Step 2", insufficientValidFeqRuns ? $"Frequency response INVALID: chỉ có {measuredRunResults.Count}/{targetValidRuns} lần hợp lệ." : (isSilent ? "Frequency response FAILED: Silent input." : $"Frequency response FAILED. Max dev: {maxFreqDev:F2} dB{value14}{compareMsg}"));
		}
		OnStepsChanged?.Invoke(_steps);
		// Log-sweep THD is derived from the sweep already captured. No new
		// playback starts here, so the transition needs no acoustic settle.
		if (!useLogSweep)
			await Task.Delay(500, RunCancellation);
		if (_isCancelled)
		{
			return;
		}
		if (SilentInputDetected | isSilent)
		{
			SilentInputDetected = true;
			LastFeqInvalidReason = $"Không nhận đủ tín hiệu từ micro (mức pilot {LastSignalLevelDbFs:F1} dBFS).";
			LastFeqInvalidIsRetryableNoise = false;
			_steps[1].Status = "Fail";
			_steps[1].Details = "Không có tín hiệu (silent input).";
			_steps[2].Status = "Pending";
			_steps[2].Details = "Bỏ qua: Không có tín hiệu ở bước FEQ.";
			_steps[3].Status = "Fail";
			_steps[3].Details = "Đo không đạt do không nhận được tín hiệu âm thanh.";
			OnStepsChanged?.Invoke(_steps);
			OnLogMessage?.Invoke("Step 2", "Frequency response FAILED: Silent input. Dừng quy trình đo do không có tín hiệu.");
			NotifyTestCompleted(false);
			return;
		}
		if (referenceAcquisition)
		{
			bool flag15 = measuredRunResults.All((Dictionary<double, double> curve) => curve.Count == sweepFrequencies.Length && curve.Values.All(double.IsFinite));
			ReferenceAcquisitionValid = (measuredRunResults.Count >= targetValidRuns && !insufficientValidFeqRuns && !InputClippingDetected && !SilentInputDetected && !FeqRepeatabilityInvalid) & flag15;
			LastFeqInvalidIsRetryableNoise = ((!ReferenceAcquisitionValid & insufficientValidFeqRuns & anyFeqLowSnrAttempt) && !InputClippingDetected && !SilentInputDetected && !FeqRepeatabilityInvalid) & flag15;
			LastFeqInvalidReason = (ReferenceAcquisitionValid ? "" : (InputClippingDetected ? $"Tín hiệu micro bị clipping/quá tải (peak {LastFeqPeakSample:F4}); giảm Playback Volume hoặc gain phần cứng đầu vào rồi đo lại." : (SilentInputDetected ? $"Không nhận đủ tín hiệu từ micro (mức pilot {LastSignalLevelDbFs:F1} dBFS)." : ((!FeqRepeatabilityInvalid) ? ((anySweepAcquisitionInvalid && !string.IsNullOrWhiteSpace(lastSweepInvalidReason) && !LastFeqInvalidIsRetryableNoise) ? lastSweepInvalidReason : (LastFeqInvalidIsRetryableNoise ? $"Nhiễu/SNR thấp: {lastLowSnrPointCount}/{lastSnrCheckedPointCount} điểm dưới {MinimumFeqSnrDb:F0} dB. {lastLowSnrPointDetails}" : ((!flag15) ? "Dữ liệu đáp tuyến thiếu điểm hoặc có giá trị không hữu hạn." : $"Không đủ lượt FEQ hợp lệ ({measuredRunResults.Count}/{targetValidRuns})."))) : (ComplexPhaseAlignmentInvalid ? "Pha giữa các lượt log-sweep chưa đồng bộ (INVALID_PHASE_ALIGNMENT); biên độ có thể ổn định nhưng chưa được lưu line chuẩn." : $"Đáp tuyến chưa ổn định giữa các lượt (dao động vượt {FreqResponseToleranceDb:F2} dB); không được lưu line chuẩn.")))));
			if (EnableNoiseDiagnostics && useLogSweep && sweepTailNoise is { DbFs: <= -55, Peak: < .05 } quietTail)
			{
				OnLogMessage?.Invoke("Headroom", $"Dùng lại 1 s nền cuối bản thu sweep: {quietTail.DbFs:F1} dBFS, peak {quietTail.Peak:F4}; không mở thêm phiên thu nền.");
			}
			else if (EnableNoiseDiagnostics)
			{
				await Task.Delay(1000, RunCancellation);
				try
				{
					AudioEngine.DualCaptureResult postSweep = await _audioEngine.CaptureSilenceAsync(recordingDevice, null, 1.0, RunCancellation);
					double postRms = DspProcessor.CalculateRms(postSweep.DutSamples, 0, postSweep.DutSamples.Length);
					double postDbFs = 20.0 * Math.Log10(Math.Max(postRms, 1E-09));
					double postPeak = postSweep.DutSamples.Select(sample => Math.Abs((double)sample)).DefaultIfEmpty().Max();
					OnLogMessage?.Invoke("Headroom", $"Sau sweep tìm line: nền {postDbFs:F1} dBFS, peak {postPeak:F4}.");
					if (postDbFs > -55.0 || postPeak >= 0.05)
					{
						ReferenceAcquisitionValid = false;
						LastFeqInvalidIsRetryableNoise = false;
						LastFeqInvalidReason = $"Nền sau sweep tăng lên {postDbFs:F1} dBFS (peak {postPeak:F4}); không lưu line chuẩn từ lượt này.";
					}
				}
				catch (Exception ex)
				{
					ReferenceAcquisitionValid = false;
					LastFeqInvalidIsRetryableNoise = false;
					LastFeqInvalidReason = "Không kiểm tra được nền sau sweep; không lưu line chuẩn: " + ex.Message;
				}
			}
			_steps[1].Status = (ReferenceAcquisitionValid ? "Diagnostic" : "Invalid");
			_steps[1].Details = (ReferenceAcquisitionValid ? "Đã thu đáp tuyến để tạo line chuẩn." : LastFeqInvalidReason);
			_steps[2].Status = "Skipped";
			_steps[2].Details = "Không đo THD khi tìm line chuẩn. THD là bước kiểm tra méo riêng.";
			_steps[3].Status = (ReferenceAcquisitionValid ? "Diagnostic" : "Invalid");
			_steps[3].Details = _steps[1].Details;
			OnTestSubstatusChanged?.Invoke("THD", "KHÔNG ÁP DỤNG · TÌM LINE CHUẨN");
			OnStepsChanged?.Invoke(_steps);
			NotifyTestCompleted(ReferenceAcquisitionValid);
			return;
		}
		_steps[2].Status = "Running";
		OnStepsChanged?.Invoke(_steps);
		OnLogMessage?.Invoke("Bước 3", useLogSweep
			? "Tính THD từ chính bản thu log-sweep vừa đo đáp tuyến; không phát thêm tone distortion."
			: AutoTestOneKilohertzOnly
			? $"Auto Test: đo THD H2-H9 chỉ tại {MidThdFrequencyHz:F0} Hz bằng {(_audioEngine.UseExclusivePlayback ? "EXCL" : "Shared")}..."
			: $"Đang đo THD tại {BassThdFrequencyHz:F0} Hz, {MidThdFrequencyHz:F0} Hz và {TrebleThdFrequencyHz:F0} Hz bằng {(_audioEngine.UseExclusivePlayback ? "EXCL" : "Shared")}; mỗi điểm đo 1 lần...");
		OnTestSubstatusChanged?.Invoke("Freq", "");
		(double FrequencyHz, double LimitPercent)[] distortionPoints = AutoTestOneKilohertzOnly
			? new[] { (MidThdFrequencyHz, MidThdLimitPercent ?? ThdLimitPercent) }
			: new(double, double)[3]
		{
			(BassThdFrequencyHz, BassThdLimitPercent ?? ThdLimitPercent),
			(MidThdFrequencyHz, MidThdLimitPercent ?? ThdLimitPercent),
			(TrebleThdFrequencyHz, TrebleThdLimitPercent ?? ThdLimitPercent)
		};
		if (!useLogSweep && AutoTestOneKilohertzOnly && EnableNoiseDiagnostics)
		{
			OnLogMessage?.Invoke("Noise", "Capturing noise floor lại sau FEQ, trước tone THD 1 kHz...");
			await RunFastNoiseDiagnosticsAsync(recordingDevice);
			var thdNoise = LastNoiseAssessment?.DutMicrophone;
			if (thdNoise == null || !double.IsFinite(thdNoise.TotalRmsDb) || thdNoise.TotalRmsDb > -55.0)
			{
				ThdAcquisitionInvalid = true;
				_steps[2].Status = "Fail";
				_steps[2].Details = $"Chưa đo THD: nền ngõ thu {thdNoise?.TotalRmsDb:F1} dBFS không hợp lệ hoặc vượt -55 dBFS sau FEQ.";
				OnStepsChanged?.Invoke(_steps);
				OnLogMessage?.Invoke("Auto Test", _steps[2].Details);
				NotifyTestCompleted(false);
				return;
			}
		}
		List<DistortionPointResult> distortionResults = new List<DistortionPointResult>();
		if (useLogSweep)
		{
			bool sweepCaptureValid = LastSweepResult != null
				&& LastSweepResult.Validity != "INVALID"
				&& !LastSweepResult.IsClipped
				&& double.IsFinite(LastSweepResult.SignalLevelDbFs)
				&& LastSweepResult.SignalLevelDbFs >= MinimumInputSignalDbFs
				&& LastSweepHarmonics != null
				&& LastSweepHarmonics.Count > 0;
			OnLogMessage?.Invoke("Bước 3", sweepCaptureValid
				? "Đang trích xuất THD & H2-H9 trực tiếp từ log-sweep bằng tách impulse harmonic Farina..."
				: "WARNING · Log-sweep không đủ điều kiện tính THD. Không chạy thêm bài sine THD và không dùng giá trị méo giả.");
			double sweepNoiseFloor = (LastSweepResult != null && double.IsFinite(LastSweepResult.NoiseFloorDbFs)) ? LastSweepResult.NoiseFloorDbFs : -75.0;
			double sweepSigLevel = (LastSweepResult != null && double.IsFinite(LastSweepResult.SignalLevelDbFs)) ? LastSweepResult.SignalLevelDbFs : -20.0;
			double sweepSnr = Math.Max(0.0, sweepSigLevel - sweepNoiseFloor);

			foreach (var (frequencyHz, limitPercent) in distortionPoints)
			{
				SweepHarmonicPoint? pt = sweepCaptureValid ? FindSweepDistortionPoint(LastSweepHarmonics, frequencyHz) : null;
				if (sweepCaptureValid && pt != null && double.IsFinite(pt.ThdPercent)
					&& pt.ThdPercent >= 0.0 && pt.ThdPercent <= 100.0
					&& double.IsFinite(pt.FundamentalLevelDb)
					&& pt.FundamentalLevelDb >= sweepNoiseFloor + 10.0)
				{
					var harmonicsList = new List<HarmonicMeasurement>();
					for (int h = 2; h <= 9; h++)
					{
						double dbc = pt.GetHarmonicDbc(h);
						if (double.IsFinite(dbc))
						{
							harmonicsList.Add(new HarmonicMeasurement(h, h * frequencyHz, dbc));
						}
					}
					var quality = new ToneQualityMetrics(
						IsValid: true,
						FundamentalFrequencyHz: pt.FundamentalFrequencyHz,
						FundamentalRms: Math.Pow(10.0, pt.FundamentalLevelDb / 20.0),
						SignalLevelDbFs: pt.FundamentalLevelDb,
						ThdPercent: pt.ThdPercent,
						ThdNPercent: double.NaN,
						SinadDb: double.NaN,
						SnrDb: sweepSnr,
						DcOffset: 0.0,
						CrestFactor: 1.414,
						PeakSample: (float)(LastSweepResult?.PeakSample ?? 0.0),
						ClippedSamplePercent: (LastSweepResult?.IsClipped ?? false) ? 1.0 : 0.0,
						LargestSpurFrequencyHz: 0.0,
						SfdrDb: sweepSnr,
						Harmonics: harmonicsList) { AnalysisSource = "LOG_SWEEP_FDW" };

					distortionResults.Add(new DistortionPointResult(frequencyHz, limitPercent, quality, 1, false, false, false, false, pt.ThdPercent, sweepSnr));
					int highestHarmonic = harmonicsList.Select(h => h.Order).DefaultIfEmpty(1).Max();
					OnLogMessage?.Invoke("Bước 3", $"• {frequencyHz:0} Hz (đọc từ sweep, không phát thêm): THD H2-H{highestHarmonic} = {pt.ThdPercent:F3}% (Giới hạn: {limitPercent:F3}%) · {(pt.ThdPercent <= limitPercent ? "ĐẠT" : "KHÔNG ĐẠT")}");
					if (PlaybackLevelDbFs > -6 && pt.ThdPercent > limitPercent)
						OnLogMessage?.Invoke("Headroom nguồn phát", $"Đang phát {PlaybackLevelDbFs:F1} dBFS peak. Limiter/DSP có thể méo trước khi ADC báo clipping; đối chiếu REW phải cùng mức phát. Kiểm tra lại ở -12 dBFS và lấy line chuẩn tại cùng mức khi cần.");
				}
				else
				{
					if (pt != null)
						OnLogMessage?.Invoke("THD Acquisition", $"{frequencyHz:0} Hz không hợp lệ: H1={pt.FundamentalLevelDb:F1} dB, nền={sweepNoiseFloor:F1} dBFS, THD thô={pt.ThdPercent:F3}%. Không đưa vào kết quả.");
					var invalidQuality = new ToneQualityMetrics(false, 0.0, 0.0, sweepSigLevel, double.NaN, double.NaN, double.NaN, sweepSnr, 0.0, 0.0, LastSweepResult?.PeakSample ?? 0.0, (LastSweepResult?.IsClipped ?? false) ? 1.0 : 0.0, 0.0, double.NaN, Array.Empty<HarmonicMeasurement>());
					distortionResults.Add(new DistortionPointResult(
						frequencyHz,
						limitPercent,
						invalidQuality,
						0,
						LastSweepResult == null || LastSweepResult.SignalLevelDbFs < MinimumInputSignalDbFs,
						LastSweepResult?.IsClipped ?? false,
						false,
						false,
						double.NaN,
						sweepSnr));
				}
			}
		}
		else
		{
			(double FrequencyHz, double LimitPercent)[] array8 = distortionPoints;
			for (int run = 0; run < array8.Length; run++)
			{
				var (frequencyHz, limitPercent) = array8[run];
				if (_isCancelled)
				{
					return;
				}
				if (run > 0)
				{
					OnTestSubstatusChanged?.Invoke("THD", $"Ổn định thiết bị trước khi đo {frequencyHz:0} Hz...");
					await Task.Delay(DistortionInterPointSettleMilliseconds, RunCancellation);
				}
				distortionResults.Add(await MeasureDistortionPointAsync(playbackDevice, recordingDevice, frequencyHz, limitPercent));
			}
		}
		LastToneQualities = distortionResults.ToDictionary((DistortionPointResult distortionPointResult) => distortionPointResult.FrequencyHz, (DistortionPointResult distortionPointResult) => distortionPointResult.Quality);
		LastToneQuality = (LastToneQualities.TryGetValue(MidThdFrequencyHz, out ToneQualityMetrics value15) ? value15 : distortionResults.Select((DistortionPointResult distortionPointResult) => distortionPointResult.Quality).FirstOrDefault());
		LastMeasuredThdPercent = useLogSweep
			? (LastToneQuality is { IsValid: true } ? LastToneQuality.ThdPercent : double.NaN)
			: distortionResults
			.Where(result => result.ValidRuns >= 1 && result.Quality.IsValid && double.IsFinite(result.Quality.ThdPercent))
			.Select(result => result.Quality.ThdPercent)
			.DefaultIfEmpty(double.NaN).Max();
		LastSignalLevelDbFs = distortionResults.Select((DistortionPointResult distortionPointResult) => distortionPointResult.Quality.SignalLevelDbFs).DefaultIfEmpty(-180.0).Min();
		bool flag16 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.ValidRuns < 1);
		bool flag17 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.AnySilent);
		bool flag18 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.AnyClipping);
		bool flag19 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.AnyWrongFrequency);
		bool flag20 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.AnyLowSnr);
		bool flag21 = distortionResults.Any((DistortionPointResult distortionPointResult) => double.IsFinite(distortionPointResult.Quality.ThdPercent) && distortionPointResult.Quality.ThdPercent > distortionPointResult.LimitPercent);
		bool flag22 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.Quality.IsValid && ThdNLimitPercent.HasValue && distortionPointResult.Quality.ThdNPercent > ThdNLimitPercent.Value);
		bool flag23 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.Quality.IsValid && MinimumSinadDb.HasValue && distortionPointResult.Quality.SinadDb < MinimumSinadDb.Value);
		bool flag24 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.Quality.IsValid && MinimumSnrDb.HasValue && distortionPointResult.Quality.SnrDb < MinimumSnrDb.Value);
		bool flag25 = distortionResults.Any((DistortionPointResult distortionPointResult) => distortionPointResult.Quality.IsValid && MaximumDcOffset.HasValue && Math.Abs(distortionPointResult.Quality.DcOffset) > MaximumDcOffset.Value);
		bool flag26 = distortionResults.Count == distortionPoints.Length && distortionResults.All((DistortionPointResult distortionPointResult) => distortionPointResult.Quality.IsValid && double.IsFinite(distortionPointResult.Quality.ThdPercent));
		ThdAcquisitionInvalid = flag16 || !flag26;
		SilentInputDetected |= flag16 & flag17;
		if (double.IsFinite(LastSignalLevelDbFs) && LastSignalLevelDbFs >= MinimumInputSignalDbFs)
		{
			SilentInputDetected = false;
		}
		InputClippingDetected |= flag16 & flag18;
		bool flag27 = (ThdPassed = (!flag16 & flag26) && !flag21 && !flag22 && !flag23 && !flag24 && !flag25);
		bool thdPass = flag27;
		string text3 = string.Join(" · ", distortionResults.Select((DistortionPointResult distortionPointResult) => $"{distortionPointResult.FrequencyHz:0}Hz: {((distortionPointResult.ValidRuns >= 1 && double.IsFinite(distortionPointResult.Quality.ThdPercent)) ? (distortionPointResult.Quality.ThdPercent.ToString("F3", CultureInfo.InvariantCulture) + "%") : (double.IsFinite(distortionPointResult.DiagnosticThdPercent) ? (distortionPointResult.DiagnosticThdPercent.ToString("F3", CultureInfo.InvariantCulture) + "% tham khảo") : "N/A"))}/{distortionPointResult.LimitPercent:F3}% ({distortionPointResult.ValidRuns}/{1})"));
		if (thdPass)
		{
			_steps[2].Status = "Pass";
			string thdHeader = double.IsFinite(LastMeasuredThdPercent) ? $"THD{(useLogSweep ? $" tại {MidThdFrequencyHz:0} Hz" : "")} {LastMeasuredThdPercent:F3}% (ĐẠT)" : "ĐẠT";
			_steps[2].Details = $"{thdHeader} · " + text3;
			OnLogMessage?.Invoke("Bước 3", _steps[2].Details);
		}
		else
		{
			List<string> list2 = new List<string>();
			if (flag16)
			{
				list2.Add("lần đo THD không hợp lệ tại một hoặc nhiều tần số");
			}
			if (distortionResults.Any(result => !result.Quality.IsValid && !double.IsFinite(result.DiagnosticThdPercent)))
			{
				list2.Add("không khóa/giải tích được tone");
			}
			if (flag21)
			{
				list2.Add("THD vượt ngưỡng riêng theo dải");
			}
			if (flag22)
			{
				list2.Add("THD+N vượt ngưỡng");
			}
			if (flag23)
			{
				list2.Add("SINAD thấp");
			}
			if (flag24)
			{
				list2.Add("SNR thấp");
			}
			if (flag25)
			{
				list2.Add("DC offset cao");
			}
			if (flag16 & flag19)
			{
				list2.Add("sai tần số");
			}
			if (flag16 & flag17)
			{
				list2.Add("mức thu dưới ngưỡng");
			}
			if (flag16 & flag20)
			{
				list2.Add("SNR chưa đủ: " + string.Join("; ", from distortionPointResult in distortionResults
					where distortionPointResult.AnyLowSnr
					select $"{distortionPointResult.FrequencyHz:0}Hz {distortionPointResult.AcquisitionSnrDb:F1} dB < {MinimumSnrDb ?? 0:F1} dB"));
			}
			if (flag16 & flag18)
			{
				list2.Add("clipping đầu vào");
			}
			_steps[2].Status = (ThdAcquisitionInvalid ? "Invalid" : "Fail");
			string verdictLabel = ThdAcquisitionInvalid ? "tham khảo" : "KHÔNG ĐẠT";
			string thdHeader = double.IsFinite(LastMeasuredThdPercent)
				? $"THD {LastMeasuredThdPercent:F3}% ({verdictLabel})"
				: (ThdAcquisitionInvalid ? "ĐO KHÔNG HỢP LỆ" : "KHÔNG ĐẠT");
			string issueReason = list2.Count > 0 ? $" ({string.Join(", ", list2)})" : "";
			_steps[2].Details = $"{thdHeader} · {text3}{issueReason}";
			OnLogMessage?.Invoke("Bước 3", _steps[2].Details);
		}
		OnTestSubstatusChanged?.Invoke("THD", _steps[2].Details);
		OnStepsChanged?.Invoke(_steps);
		if (!AutoTestOneKilohertzOnly && !useLogSweep)
			await Task.Delay(500, RunCancellation);
		if (_isCancelled)
		{
			return;
		}
		_steps[3].Status = "Running";
		OnStepsChanged?.Invoke(_steps);
		OnTestSubstatusChanged?.Invoke("Freq", "");
		OnTestSubstatusChanged?.Invoke("THD", "");
		bool flag29;
		if (referenceAcquisition)
		{
			ReferenceAcquisitionValid = measuredRunResults.Count > 0 && !anyFeqSilentAttempt;
			flag29 = ReferenceAcquisitionValid;
			_steps[3].Status = (ReferenceAcquisitionValid ? "Diagnostic" : "Invalid");
			_steps[3].Details = (ReferenceAcquisitionValid ? "Đã thu dữ liệu tạo chuẩn; chưa phải kết luận PASS sản phẩm." : "Dữ liệu tạo chuẩn không hợp lệ; không được lưu.");
		}
		else
		{
			ReferenceAcquisitionValid = false;
			flag29 = (freqResponsePass & thdPass) && !MissingFrequencyLimits;
			if (MissingFrequencyLimits)
			{
				_steps[3].Status = "Invalid";
				_steps[3].Details = "Đã đo được tín hiệu nhưng chưa có đủ line chuẩn/giới hạn; không kết luận sản phẩm.";
				OnLogMessage?.Invoke("Kết luận", "CHƯA ĐỦ CHUẨN · đã có dữ liệu đo, không kết luận PASS/FAIL.");
			}
			else if (ThdAcquisitionInvalid)
			{
				_steps[3].Status = "Invalid";
				_steps[3].Details = "Bản thu THD chưa đủ chất lượng (tone/SNR/level/clock/clipping); không kết luận thiết bị lỗi méo.";
				OnLogMessage?.Invoke("Kết luận", "THD INVALID · kiểm tra kênh thu, gain phần cứng, mức phát và clock trước khi đo lại.");
			}
			else if (flag29)
			{
				_steps[3].Status = "Pass";
				_steps[3].Details = "Thiết bị đạt cả đáp tuyến và THD.";
				OnLogMessage?.Invoke("Kết luận", "ĐẠT · thiết bị đáp ứng cả tiêu chí đáp tuyến và THD.");
			}
			else
			{
				_steps[3].Status = "Fail";
				string text5 = ((!freqResponsePass && !thdPass) ? "Cả đáp tuyến và THD đều không đạt" : ((!freqResponsePass) ? "Đáp tuyến không đạt" : "THD không đạt"));
				_steps[3].Details = "Thiết bị không đạt (" + text5 + ").";
				OnLogMessage?.Invoke("Kết luận", "KHÔNG ĐẠT · " + text5 + ".");
			}
		}
		OnStepsChanged?.Invoke(_steps);
		NotifyTestCompleted(flag29);
	}

	private static double GetPeak(float[] samples, int start, int count)
	{
		if (samples == null || samples.Length == 0 || count <= 0) return 0.0;
		int first = Math.Clamp(start, 0, samples.Length);
		int end = Math.Clamp(first + count, first, samples.Length);
		double peak = 0.0;
		for (int index = first; index < end; index++)
			peak = Math.Max(peak, Math.Abs((double)samples[index]));
		return peak;
	}

	private static double GetClippedPercent(float[] samples, int start, int count)
	{
		if (samples == null || samples.Length == 0 || count <= 0) return 0.0;
		int first = Math.Clamp(start, 0, samples.Length);
		int end = Math.Clamp(first + count, first, samples.Length);
		if (end <= first) return 0.0;
		int clipped = 0;
		for (int index = first; index < end; index++)
			if (Math.Abs(samples[index]) >= 0.995f) clipped++;
		return clipped * 100.0 / (end - first);
	}

	private async Task<DistortionPointResult> MeasureDistortionPointAsync(MMDevice playbackDevice, MMDevice recordingDevice, double frequencyHz, double limitPercent)
	{
		List<ToneQualityMetrics> validRuns = new List<ToneQualityMetrics>();
		bool anySilent = false;
		bool anyClipping = false;
		bool anyWrongFrequency = false;
		bool anyLowSnr = false;
		ToneQualityMetrics lastAnalysis = null;
		ToneQualityMetrics bestDiagnosticAnalysis = null;
		double lastAcquisitionSnrDb = double.NaN;
		for (int attempt = 1; attempt <= 1; attempt++)
		{
			if (validRuns.Count >= 1)
			{
				break;
			}
			if (_isCancelled)
			{
				break;
			}
			OnTestSubstatusChanged?.Invoke("THD", $"{frequencyHz:0} Hz · lần {attempt}/{1} · hợp lệ {validRuns.Count}/{1}...");
			Stopwatch timer = Stopwatch.StartNew();
			double playbackFrequencyHz = ScalePlaybackFrequency(frequencyHz);
			AudioEngine audioEngine = _audioEngine;
			double frequency = playbackFrequencyHz;
			double signalSampleScale = AutoTestOneKilohertzOnly ? AutoTestToneSampleScale
				: ((frequencyHz < 250.0) ? BassDistortionSampleScale : 1.0);
			float[] samples = await audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, frequency, 3.0, samplesChunk => OnRealTimeRecordedSamples?.Invoke(samplesChunk), forceSaveFiles: true, null, null, 1.0, RunCancellation, null, null, signalSampleScale);
			if (samples.Length > 0)
			{
				double thdPeakAbs = samples.Max(s => Math.Abs((double)s));
				double thdPeakDb = 20.0 * Math.Log10(Math.Max(1e-9, thdPeakAbs));
				double thdRmsVal = Math.Sqrt(samples.Select(s => (double)s * s).Average());
				double thdRmsDb = 20.0 * Math.Log10(Math.Max(1e-9, thdRmsVal));
				double noiseFloorDb = LastNoiseAssessment?.DutMicrophone != null ? LastNoiseAssessment.DutMicrophone.TotalRmsDb : -80.0;
				double thdSnrDb = Math.Max(0.0, thdRmsDb - noiseFloorDb);
				bool thdIsClipped = thdPeakAbs >= 0.995;
				OnMeasurementCaptured?.Invoke(samples, thdPeakDb, thdRmsDb, thdSnrDb, thdIsClipped);
			}
			if (_isCancelled)
			{
				break;
			}
			int sampleRate = _audioEngine.RecordingSampleRate;
			float[] analysisWindow = RemoveDigitalRecordingGain(ExtractTrailingAnalysisWindow(samples, sampleRate, 2.0));
			long captureMs = timer.ElapsedMilliseconds;
			ToneQualityMetrics toneQualityMetrics = await Task.Run(() => AnalyzeStableToneCapture(analysisWindow, sampleRate, playbackFrequencyHz));
			lastAnalysis = toneQualityMetrics;
			double num = 20.0 * Math.Log10(DspProcessor.CalculateRms(analysisWindow, 0, analysisWindow.Length) + 1E-09);
			bool flag = num < MinimumInputSignalDbFs;
			bool flag2 = toneQualityMetrics.PeakSample >= 0.995 || toneQualityMetrics.ClippedSamplePercent > MaximumClippedSamplePercent;
			if (Math.Abs(frequencyHz - 1000.0) <= 1.0)
				OnOneKilohertzToneMeasured?.Invoke(num, toneQualityMetrics.PeakSample);
			double num2 = Math.Max(MaximumToneFrequencyErrorHz, playbackFrequencyHz * 0.002);
			bool flag3 = toneQualityMetrics.IsValid && Math.Abs(toneQualityMetrics.FundamentalFrequencyHz - playbackFrequencyHz) > num2;
			double num3 = MinimumSnrDb ?? 0.0;
			// Separate the pre-stimulus noise floor from the sine-fit residual:
			// independent playback/capture clocks can inflate the latter.
			double acquisitionSnrDb = toneQualityMetrics.SnrDb;
			if (AutoTestOneKilohertzOnly && LastNoiseAssessment?.DutMicrophone is { } dutNoise
				&& double.IsFinite(dutNoise.TotalRmsDb))
				acquisitionSnrDb = 20.0 * Math.Log10(Math.Max(toneQualityMetrics.FundamentalRms, 1E-12)) - dutNoise.TotalRmsDb;
			bool flag4 = !double.IsFinite(acquisitionSnrDb) || acquisitionSnrDb < num3;
			lastAcquisitionSnrDb = acquisitionSnrDb;
			bool flag5 = double.IsFinite(toneQualityMetrics.ThdPercent) && toneQualityMetrics.ThdPercent <= limitPercent;
			bool flag6 = toneQualityMetrics.IsValid && !flag && !flag2 && !flag3
				&& (!AutoTestOneKilohertzOnly || !flag4)
				&& double.IsFinite(toneQualityMetrics.ThdPercent)
				&& toneQualityMetrics.ThdPercent >= 0.0 && toneQualityMetrics.ThdPercent <= 100.0;
			if (toneQualityMetrics.IsValid && !flag && !flag2 && !flag3 && double.IsFinite(toneQualityMetrics.ThdPercent) && (bestDiagnosticAnalysis == null || toneQualityMetrics.SnrDb > bestDiagnosticAnalysis.SnrDb))
			{
				bestDiagnosticAnalysis = toneQualityMetrics;
			}
			anySilent |= flag;
			anyClipping |= flag2;
			anyWrongFrequency |= flag3;
			anyLowSnr |= flag4;
			OnLogMessage?.Invoke("THD Timing", $"{frequencyHz:0} Hz lần {attempt}: thu/mở thiết bị {captureMs} ms; AnalyzeTone {timer.ElapsedMilliseconds - captureMs} ms; {(flag6 ? "VALID" : "RETRY")}.");
			Action<string, string>? action = OnLogMessage;
			if (action != null)
			{
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(131, 13);
				defaultInterpolatedStringHandler.AppendFormatted(frequencyHz, "0");
				defaultInterpolatedStringHandler.AppendLiteral(" Hz lần ");
				defaultInterpolatedStringHandler.AppendFormatted(attempt);
				defaultInterpolatedStringHandler.AppendLiteral(": f0=");
				defaultInterpolatedStringHandler.AppendFormatted(toneQualityMetrics.FundamentalFrequencyHz, "F2");
				defaultInterpolatedStringHandler.AppendLiteral(" Hz, fundamental=");
				defaultInterpolatedStringHandler.AppendFormatted(20.0 * Math.Log10(Math.Max(toneQualityMetrics.FundamentalRms, 1E-12)), "F1");
				defaultInterpolatedStringHandler.AppendLiteral(" dBFS, broadband=");
				defaultInterpolatedStringHandler.AppendFormatted(num, "F1");
				defaultInterpolatedStringHandler.AppendLiteral(" dBFS, peak=");
				defaultInterpolatedStringHandler.AppendFormatted(toneQualityMetrics.PeakSample, "F4");
				defaultInterpolatedStringHandler.AppendLiteral(", noise-floor SNR=");
				defaultInterpolatedStringHandler.AppendFormatted(acquisitionSnrDb, "F1");
				defaultInterpolatedStringHandler.AppendLiteral(" dB, fit residual SNR=");
				defaultInterpolatedStringHandler.AppendFormatted(toneQualityMetrics.SnrDb, "F1");
				defaultInterpolatedStringHandler.AppendLiteral(" dB (cần ≥ ");
				defaultInterpolatedStringHandler.AppendFormatted(num3, "F1");
				defaultInterpolatedStringHandler.AppendLiteral(" dB), THD=");
				defaultInterpolatedStringHandler.AppendFormatted(toneQualityMetrics.ThdPercent, "F4");
				defaultInterpolatedStringHandler.AppendLiteral("%, THD+N=");
				defaultInterpolatedStringHandler.AppendFormatted(toneQualityMetrics.ThdNPercent, "F4");
				defaultInterpolatedStringHandler.AppendLiteral("%, kênh thu=");
				int? recordingChannel = _audioEngine.RecordingChannel;
				object value;
				if (recordingChannel.HasValue)
				{
					int valueOrDefault = recordingChannel.GetValueOrDefault();
					value = (valueOrDefault + 1).ToString();
				}
				else
				{
					value = "mix";
				}
				defaultInterpolatedStringHandler.AppendFormatted((string?)value);
				defaultInterpolatedStringHandler.AppendLiteral(", phát=");
				defaultInterpolatedStringHandler.AppendFormatted(playbackFrequencyHz, "F2");
				defaultInterpolatedStringHandler.AppendLiteral(" Hz. Harmonics: ");
				defaultInterpolatedStringHandler.AppendFormatted(string.Join(", ", from h in toneQualityMetrics.Harmonics.Take(5)
					select $"H{h.Order}={h.LevelDbc:F1}dBc"));
				defaultInterpolatedStringHandler.AppendLiteral(".");
				action("THD Acquisition", defaultInterpolatedStringHandler.ToStringAndClear());
			}
			if (flag6)
			{
				validRuns.Add(toneQualityMetrics);
			}
			else
			{
				OnLogMessage?.Invoke("Bước 3", $"{frequencyHz:0} Hz không hợp lệ: mức {num:F1} dBFS (ngưỡng {MinimumInputSignalDbFs:F1}), clipping={flag2} (giới hạn {MaximumClippedSamplePercent:F4}%), khóa tone={toneQualityMetrics.IsValid}, f0={toneQualityMetrics.FundamentalFrequencyHz:F2} Hz, phát={playbackFrequencyHz:F2} Hz, sai tần số={flag3} (±{num2:F1} Hz), SNR nền={acquisitionSnrDb:F1} dB (cần ≥ {num3:F1} dB).");
			}
			if (attempt < 1 && validRuns.Count < 1)
			{
				await Task.Delay(50, RunCancellation);
			}
		}
		ToneQualityMetrics toneQualityMetrics2;
		if (validRuns.Count > 0)
		{
			double medianThd = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.ThdPercent));
			toneQualityMetrics2 = validRuns.OrderBy((ToneQualityMetrics item) => Math.Abs(item.ThdPercent - medianThd)).First()with
			{
				FundamentalFrequencyHz = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.FundamentalFrequencyHz)),
				FundamentalRms = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.FundamentalRms)),
				SignalLevelDbFs = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.SignalLevelDbFs)),
				ThdPercent = medianThd,
				ThdNPercent = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.ThdNPercent)),
				SinadDb = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.SinadDb)),
				SnrDb = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.SnrDb)),
				DcOffset = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.DcOffset)),
				CrestFactor = CalculateMedian(validRuns.Select((ToneQualityMetrics item) => item.CrestFactor)),
				PeakSample = validRuns.Max((ToneQualityMetrics item) => item.PeakSample),
				ClippedSamplePercent = validRuns.Max((ToneQualityMetrics item) => item.ClippedSamplePercent)
			};
		}
		else
		{
			ToneQualityMetrics obj = bestDiagnosticAnalysis ?? lastAnalysis ?? new ToneQualityMetrics(IsValid: false, 0.0, 0.0, -180.0, double.NaN, double.NaN, double.NaN, double.NaN, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, Array.Empty<HarmonicMeasurement>());
			toneQualityMetrics2 = obj with
			{
				IsValid = false,
				ThdPercent = double.NaN,
				ThdNPercent = double.NaN
			};
		}
		double num4 = validRuns.Count > 0 ? toneQualityMetrics2.ThdPercent : double.NaN;
		ToneQualityMetrics toneQualityMetrics3 = ((validRuns.Count > 0) ? toneQualityMetrics2 : (bestDiagnosticAnalysis ?? toneQualityMetrics2));
		OnThdSpectrumReady?.Invoke(frequencyHz, toneQualityMetrics3.SpectrumFrequenciesHz, toneQualityMetrics3.SpectrumMagnitudes, num4, validRuns.Count >= 1);
		return new DistortionPointResult(frequencyHz, limitPercent, toneQualityMetrics2, validRuns.Count, anySilent, anyClipping, anyWrongFrequency, anyLowSnr, num4, lastAcquisitionSnrDb);
	}

	public async Task RunNoiseTestAsync(MMDevice playbackDevice, MMDevice recordingDevice)
	{
		_isCancelled = false;
		OnLogMessage?.Invoke("Đo nhiễu", "Bắt đầu đo nền nhiễu và hum...");
		OnTestSubstatusChanged?.Invoke("THD", $"Đang thu tín hiệu im lặng ({2.5:F1} giây)...");
		if (recordingDevice == null || playbackDevice == null)
		{
			OnLogMessage?.Invoke("Lỗi đo nhiễu", "Thiếu thiết bị phát hoặc thu âm.");
			return;
		}
		float[] samples = await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, 0.0, 2.5);
		if (!_isCancelled)
		{
			OnTestSubstatusChanged?.Invoke("THD", "Đang phân tích phổ nhiễu...");
			samples = RemoveDigitalRecordingGain(samples);
			int recordingSampleRate = _audioEngine.RecordingSampleRate;
			(int, int) centeredAnalysisRange = GetCenteredAnalysisRange(samples, recordingSampleRate, 1.0);
			float[] samples2 = ExtractCenteredAnalysisWindow(samples, recordingSampleRate, 1.0);
			double num = DspProcessor.CalculateRms(samples, centeredAnalysisRange.Item1, centeredAnalysisRange.Item2);
			double num2 = 20.0 * Math.Log10(num + 1E-09);
			FrequencySpectrum frequencySpectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(samples2, _audioEngine.RecordingSampleRate);
			OnThdSpectrumReady?.Invoke(0.0, frequencySpectrum.FrequenciesHz, frequencySpectrum.Magnitudes, 0.0, arg5: true);
			NoiseSpectrumMetrics noiseSpectrumMetrics = ProductionMeasurement.AnalyzeNoise(samples2, recordingSampleRate, null);
			OnLogMessage?.Invoke("Đo nhiễu", $"Nền nhiễu {num2:F2} dBFS · hum {noiseSpectrumMetrics.DominantHumHz:F0} Hz {noiseSpectrumMetrics.DominantHumDb:F1} dBFS · đỉnh phổ {noiseSpectrumMetrics.DominantSpectralPeakHz:F0} Hz {noiseSpectrumMetrics.DominantSpectralPeakDb:F1} dBFS");
			if (num2 > -55.0)
			{
				OnLogMessage?.Invoke("Cảnh báo nhiễu", "Nền nhiễu cao hơn -55 dBFS; hãy kiểm tra ground loop, cáp và cách ly USB.");
			}
			else
			{
				OnLogMessage?.Invoke("Đo nhiễu", "Nền nhiễu tốt; đường tín hiệu sạch.");
			}
			OnTestSubstatusChanged?.Invoke("THD", "Hoàn tất");
		}
	}

	private async Task RunRubBuzzTestInternalAsync(MMDevice playbackDevice, MMDevice recordingDevice)
	{
		_steps[1].Status = "Running";
		OnStepsChanged?.Invoke(_steps);
		OnLogMessage?.Invoke("Bước 2", $"Đang đo tiếng rè/rung bất thường tại {RubBuzzTestFreq} Hz...");
		OnTestSubstatusChanged?.Invoke("Freq", $"Đang đo rè/rung ({RubBuzzTestFreq} Hz)...");
		float[] samples = await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, ScalePlaybackFrequency(RubBuzzTestFreq), 2.5);
		if (!_isCancelled)
		{
			OnTestSubstatusChanged?.Invoke("Freq", "Đang tách harmonic và nhiễu không điều hòa...");
			int recordingSampleRate = _audioEngine.RecordingSampleRate;
			float[] samples2 = ExtractCenteredAnalysisWindow(samples, recordingSampleRate, 1.0);
			samples2 = RemoveDigitalRecordingGain(samples2);
			(double, double[], double) tuple = DspProcessor.CalculateRubBuzz(samples2, recordingSampleRate, RubBuzzTestFreq, out double[] frequencies);
			RubBuzzMetrics rubBuzzMetrics = (LastRubBuzzMetrics = AdvancedAudioMeasurement.AnalyzeRubBuzz(samples2, recordingSampleRate, RubBuzzTestFreq));
			RubBuzzMetrics rubBuzzMetrics3 = rubBuzzMetrics;
			InputClippingDetected |= rubBuzzMetrics3.PeakSample >= 0.995 || rubBuzzMetrics3.ClippedSamplePercent > MaximumClippedSamplePercent;
			OnThdSpectrumReady?.Invoke(RubBuzzTestFreq, frequencies, tuple.Item2, rubBuzzMetrics3.RubBuzzPercent, arg5: true);
			double num = DspProcessor.CalculateRms(samples2, 0, samples2.Length);
			double num2 = 20.0 * Math.Log10(num + 1E-09);
			bool flag = num2 < -70.0;
			LastSignalLevelDbFs = num2;
			SilentInputDetected = flag;
			bool flag2 = !flag && rubBuzzMetrics3.IsValid && rubBuzzMetrics3.RubBuzzPercent <= RubBuzzLimit && rubBuzzMetrics3.PeakSample < 0.995 && rubBuzzMetrics3.ClippedSamplePercent <= MaximumClippedSamplePercent;
			LastRubBuzzValue = rubBuzzMetrics3.RubBuzzPercent;
			ThdPassed = flag2;
			BassPassed = true;
			MidPassed = true;
			TreblePassed = true;
			if (flag2)
			{
				_steps[1].Status = "Pass";
				_steps[1].Details = $"Rè/rung: {rubBuzzMetrics3.RubBuzzPercent:F3}% · đỉnh bất thường {rubBuzzMetrics3.ResidualPeakFrequencyHz:F0} Hz ({rubBuzzMetrics3.ResidualPeakDbc:F1} dBc) · giới hạn < {RubBuzzLimit}%";
				OnLogMessage?.Invoke("Bước 2", $"ĐẠT · rè/rung {rubBuzzMetrics3.RubBuzzPercent:F3}%");
			}
			else
			{
				_steps[1].Status = "Fail";
				string value = (flag ? "không có tín hiệu" : ((!rubBuzzMetrics3.IsValid) ? "không khóa được tone" : ((rubBuzzMetrics3.PeakSample >= 0.995 || rubBuzzMetrics3.ClippedSamplePercent > MaximumClippedSamplePercent) ? "clipping đầu vào" : "nhiễu không điều hòa vượt ngưỡng")));
				_steps[1].Details = $"KHÔNG ĐẠT ({value}) · rè/rung {rubBuzzMetrics3.RubBuzzPercent:F3}% · đỉnh {rubBuzzMetrics3.ResidualPeakFrequencyHz:F0} Hz ({rubBuzzMetrics3.ResidualPeakDbc:F1} dBc)";
				OnLogMessage?.Invoke("Bước 2", _steps[1].Details);
			}
			_steps[2].Status = "Pass";
			_steps[2].Details = "Không áp dụng cho bài đo rè/rung";
			_steps[3].Status = (flag2 ? "Pass" : "Fail");
			_steps[3].Details = (flag2 ? "Thiết bị đạt giới hạn rè/rung." : "Thiết bị có rè/rung hoặc hở khí vượt giới hạn.");
			OnLogMessage?.Invoke("Kết luận", flag2 ? "ĐẠT · tiếng rè/rung trong giới hạn." : "KHÔNG ĐẠT · phát hiện rè/rung hoặc hở khí.");
			OnStepsChanged?.Invoke(_steps);
			OnTestCompleted?.Invoke(flag2);
		}
	}
}
