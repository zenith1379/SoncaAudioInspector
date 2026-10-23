using System.ComponentModel;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed record AudioQaAcquisitionSnapshot(double PlaybackLevelDbfs, int PlaybackSampleRate, int RecordingSampleRate,
	bool ExclusivePlayback, int? PlaybackChannel, int? RecordingChannel, string PlaybackDeviceId, string RecordingDeviceId,
	double ThdLimitPercent, DateTimeOffset CapturedUtc);

public class AutoTestCaseItem : INotifyPropertyChanged
{
	private string _status = "WAITING";

	private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(113, 113, 122));

	private string _freqStatus = "ĐANG CHỜ";

	private Brush _freqBrush = new SolidColorBrush(Color.FromRgb(113, 113, 122));

	private string _thdStatus = "ĐANG CHỜ";

	private Brush _thdBrush = new SolidColorBrush(Color.FromRgb(113, 113, 122));

	public string Id { get; set; } = "";

	public string Name { get; set; } = "";
	public string QualityLabel => Config.SpeakerStage == "MicEffects" ? "• Effect: " : Config.SpeakerStage == "MicFrequencyShift" ? "• Di tần: " : "• Distortion sweep: ";

	public TestConfig Config { get; set; } = new TestConfig();

	public string NoiseStatus { get; set; } = "CHƯA ĐO";

	private IReadOnlyList<ConfiguredTestResult> _additionalResults = Array.Empty<ConfiguredTestResult>();
	public IReadOnlyList<ConfiguredTestResult> AdditionalResults
	{
		get => _additionalResults;
		set { _additionalResults = value; OnPropertyChanged(nameof(AdditionalResults)); OnPropertyChanged(nameof(AdditionalStatus)); OnPropertyChanged(nameof(AdditionalBrush)); }
	}
	public string AdditionalStatus => Config.SpeakerStage is "MicEffects" or "MicFrequencyShift" ? "" : AdditionalResults.Count > 0 ? string.Join(Environment.NewLine, AdditionalResults.Select(r => r.Display))
		: string.Join(Environment.NewLine, new[] { (Config.TestEffect, "Effect"), (Config.TestAirLeak, "Rò khí"), (Config.TestLooseParticle, "Hạt rác"), (Config.TestSoundLevel, "Mức âm") }
			.Where(p => p.Item1).Select(p => p.Item2 + ": CHƯA ĐO"));
	public Brush AdditionalBrush => AdditionalResults.Any(r => r.Status == "FAIL") ? Brushes.Salmon
		: AdditionalResults.Count == 0 || AdditionalResults.Any(r => !r.Passed) ? Brushes.Gold : Brushes.MediumSpringGreen;

	public string DistortionText { get; set; } = "CHƯA CÓ DỮ LIỆU DISTORTION";

	public string ResponseLevelUnit { get; set; } = "dBFS";

	public AudioQaAcquisitionSnapshot? Acquisition { get; set; }

	public double[] ResponseFrequencies { get; set; } = Array.Empty<double>();

	public double[] ResponseLevels { get; set; } = Array.Empty<double>();
	public IReadOnlyDictionary<double, double> UnsmoothedResponse { get; set; } = new Dictionary<double, double>();
	public IReadOnlyDictionary<double, double> UnsmoothedStandardCurve { get; set; } = new Dictionary<double, double>();
	[System.Text.Json.Serialization.JsonIgnore]
	public StandardAcousticResult? ImpulseResult { get; set; }
	[System.Text.Json.Serialization.JsonIgnore]
	public FrequencyShiftResult? FrequencyShift { get; set; }
	[System.Text.Json.Serialization.JsonIgnore]
	public IReadOnlyList<MeasurementValue> MeasurementValues { get; set; } = Array.Empty<MeasurementValue>();

	public IReadOnlyDictionary<double, double> StandardCurve { get; set; } = new Dictionary<double, double>();

	public IReadOnlyDictionary<double, FrequencyLimitPoint> FrequencyLimits { get; set; }
		= new Dictionary<double, FrequencyLimitPoint>();

	public CriticalFrequencyZone[] CriticalZones { get; set; } = Array.Empty<CriticalFrequencyZone>();

	public bool BassPassed { get; set; }

	public bool MidPassed { get; set; }

	public bool TreblePassed { get; set; }

	public bool SilentInputDetected { get; set; }

	public bool FeqRepeatabilityInvalid { get; set; }

	public IReadOnlyDictionary<double, ToneQualityMetrics> DistortionMeasurements { get; set; }
		= new Dictionary<double, ToneQualityMetrics>();

	public string Status
	{
		get
		{
			return _status;
		}
		set
		{
			_status = value;
			OnPropertyChanged("Status");
			OnPropertyChanged("StatusDisplay");
		}
	}

	public string StatusDisplay => _status switch
	{
		"WAITING" => "ĐANG CHỜ", 
		"RUNNING" => "ĐANG ĐO", 
		"PASS" => "ĐẠT", 
		"FAIL" => "KHÔNG ĐẠT", 
		"RETRY" => "TÍN HIỆU CHƯA ỔN ĐỊNH",
		"INVALID" => "ĐO KHÔNG HỢP LỆ",
		_ => _status, 
	};

	public Brush StatusBrush
	{
		get
		{
			return _statusBrush;
		}
		set
		{
			_statusBrush = value;
			OnPropertyChanged("StatusBrush");
		}
	}

	public string FreqStatus
	{
		get
		{
			return _freqStatus;
		}
		set
		{
			_freqStatus = value;
			OnPropertyChanged("FreqStatus");
		}
	}

	public Brush FreqBrush
	{
		get
		{
			return _freqBrush;
		}
		set
		{
			_freqBrush = value;
			OnPropertyChanged("FreqBrush");
		}
	}

	public string ThdStatus
	{
		get
		{
			return _thdStatus;
		}
		set
		{
			_thdStatus = value;
			OnPropertyChanged("ThdStatus");
		}
	}

	public Brush ThdBrush
	{
		get
		{
			return _thdBrush;
		}
		set
		{
			_thdBrush = value;
			OnPropertyChanged("ThdBrush");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged(string name)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
