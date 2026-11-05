#define DEBUG
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.TickGenerators;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

public partial class AudioRouting : UserControl
{
	private AudioEngine _audioEngine;

	private TestRunner _testRunner;

	private List<double> _freqs;
	private bool _showUnsmoothedResponse;
	private IReadOnlyDictionary<double, double> _rawResponse = new Dictionary<double, double>();
	private IReadOnlyDictionary<double, double> _rawStandardCurve = new Dictionary<double, double>();
	private ImpulseEffectReference? _standardImpulseEffects;
	private bool _savingCompositeReference;
	private ImpulseEffectReference? _pendingEffectsReference;

	private List<double> _dbValues;

	private double[] _sessionLineFrequencies;

	private double[] _sessionLineValues;

	private string _referenceRouteKey;

	private InOutConfig _activeInOutConfig;

	private List<AutoTestCaseItem> _autoTestCases;

	private bool _isExecutingAutoSuite;
	private const int DefaultLogSweepSamples = 65536;
	private int _autoTestAttempt;
	private bool IsFirstAutoTestAttempt => _isExecutingAutoSuite && _autoTestAttempt == 1;
	private bool AutoTestAcquisitionInvalid => !string.IsNullOrWhiteSpace(_lastTrackedTestError)
		|| !string.IsNullOrWhiteSpace(_testRunner.LastFeqInvalidReason)
		|| _testRunner.Steps.ElementAtOrDefault(0)?.Status == "Fail"
		|| _testRunner.SilentInputDetected || _testRunner.InputClippingDetected
		|| _testRunner.FeqRepeatabilityInvalid || _testRunner.LastSweepResult?.Validity == "INVALID"
		|| _testRunner.ThdAcquisitionInvalid || _testRunner.RubBuzzAcquisitionInvalid;
	private bool AutoTestDistortionPassed => _testRunner.Steps.ElementAtOrDefault(2)?.Status == "Pass";

	private bool _isFindingStandardReference;
	private bool _routingWorkflowActive;
	private bool _suppressChartUpdates;

	private IDisposable? TryBeginRoutingWorkflow()
	{
		if (_routingWorkflowActive || _activeMeasurementTask != null || _isExecutingAutoSuite
			|| _isFindingStandardReference || ((Application.Current.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false))
		{
			AppendLog("Thiết bị đang bận", "Chờ quy trình trước kết thúc và tắt hết tín hiệu phát trước khi mở bài mới.");
			return null;
		}
		_sampleRateConsent.Reset();
		_autoTestInterrupted = false;
		_routingWorkflowActive = true;
		SetRoutingControlsLocked(true);
		return new RoutingWorkflowLease(this);
	}

	private sealed class RoutingWorkflowLease(AudioRouting owner) : IDisposable
	{
		private AudioRouting? _owner = owner;
		public void Dispose()
		{
			AudioRouting? current = System.Threading.Interlocked.Exchange(ref _owner, null);
			if (current == null) return;
			try { current._audioEngine.StopAllAudio(); }
			finally
			{
				current._routingWorkflowActive = false;
				current._routingMeterBuffer.Stop();
				current.SetRoutingControlsLocked(false);
				current.UpdateAutoTestStartButton();
				if (current.IsLoaded) current.MaintainRoutingHeadroomMonitor();
				if (current.IsLoaded) _ = current.RefreshPendingQaAsync();
			}
		}
	}

	private bool _discardCurrentMeasurementRequested;

	private Task? _activeMeasurementTask;
	private string _lastTrackedTestError = "";

	private string? _autoTestSessionFolder;

	private readonly List<string> _autoTestGraphPaths;
	private AutoTestResumeSession? _autoTestResumeSession;

	private sealed record AutoTestResumeSession(
		string Model, string Serial, string Folder, AutoTestCaseItem[] Cases,
		double FrequencyToleranceDb);

	private const int ServerGraphWidth = 800;

	private const int ServerGraphHeight = 450;

	private const int FullscreenGraphWidth = 1920;

	private const int FullscreenGraphHeight = 1080;
	private bool _frequencyViewAdjustedByUser;

	private AutoTestCaseItem? _currentRunningTestCase;
	private AutoTestCaseItem? _reviewedAutoTestCase;

	private TestConfig? _appliedTestConfig;

	private bool? _currentTestSuccess;

	private Dictionary<double, double>? _standardCurve;
	private double? _standardOneKilohertzLevelDbFs;

	private Dictionary<double, FrequencyLimitPoint>? _frequencyLimits;

	private string _loadedStandardDeviceKey;

	private string _preferredUsbPlaybackDeviceId;

	private string _preferredRecordingDeviceId;
	private string _preferredPlaybackDeviceName = "";
	private string _preferredRecordingDeviceName = "";
	private string _fastTrackPlayback12Identity = "";

	private bool _isAutoSelectingDevices;

	private bool _isLoadingServerPreference;

	private bool _lastSyncedSendToServer;

	private bool _isConfigLoaded;

	private bool _isApplyingMeasurementMethod;

	private bool _areChartsInteractionsSetup;

	private readonly DispatcherTimer _frequencyChartRefreshTimer;

	private const string IconExpandData = "M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z";

	private const string IconRestoreData = "M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z";

	private bool _isFreqExpanded;

	private bool _isThdExpanded;

	private GridLength _savedSetupWidth;

	private Visibility _savedSetupVisibility;

	private Visibility _savedAutoTestVisibility;

	private Visibility _savedFooterVisibility;

	private HwndSource? _hwndSource;

	private const int WM_MOUSEHWHEEL = 526;

	private const int AutoTestRouteSettleMilliseconds = 1000;

	private const int AutoTestPostWarmupSettleMilliseconds = 1000;

	private const double AutoTestStableHeadroomDbFs = -55.0;
	private double? _autoTestMicFloorReferenceDbFs;
	private string _autoTestMicContinuityFailureStatus = "";


	private RoutingScopeWindow? _routingScopeWindow;
	private bool _leaveAudioReleasedAfterAutoTest;

	private bool _isUpdatingChannelSelectors;
	private int _playbackRouteSettleVersion;

	private double _selectedThdInspectFrequencyHz;

	private bool _isRoutingHeadroomRunning;

	private string _routingHeadroomRecordingId;
	private bool _routingHeadroomEnabled = true;
	private readonly LiveLevelBuffer _routingMeterBuffer = new();
	private long _routingMeterSession;
	private readonly DispatcherTimer _routingMeterTimer;
	private DateTime _headroomRetryAtUtc;
	private int _headroomFailureCount;
	private Dictionary<UIElement, bool>? _lockedRoutingControls;
	private bool _configSaveWarningShown;


	public bool IsFreqExpanded => _isFreqExpanded;

	public bool IsThdExpanded => _isThdExpanded;

	public bool IsSetupVisible => PanelSetup.Visibility == Visibility.Visible;

	private double _playbackLevelDbfs = 0.0;

	public double PlaybackLevelDbfs
	{
		get => _playbackLevelDbfs;
		set
		{
			double clamped = Math.Clamp(double.IsFinite(value) ? value : 0.0, -60.0, 0.0);
			_playbackLevelDbfs = clamped;
			if (TxtPlaybackLevelDbfs != null && TxtPlaybackLevelDbfs.Text != $"{clamped:0.#}")
			{
				TxtPlaybackLevelDbfs.Text = $"{clamped:0.#}";
			}
			if (SliderPlaybackLevelDbfs != null && Math.Abs(SliderPlaybackLevelDbfs.Value - clamped) > 0.01)
			{
				SliderPlaybackLevelDbfs.Value = clamped;
			}
			if (_testRunner != null)
			{
				_testRunner.PlaybackLevelDbFs = clamped;
			}
		}
	}

	public double PlaybackAmplitude => Math.Pow(10.0, _playbackLevelDbfs / 20.0);

	public bool IsTestingBusy
	{
		get
		{
			if (_routingScopeWindow != null)
			{
				return true;
			}
			if (!_routingWorkflowActive && !_isExecutingAutoSuite && !_isFindingStandardReference && _activeMeasurementTask == null)
			{
				if (BtnStart != null)
				{
					return !BtnStart.IsEnabled;
				}
				return false;
			}
			return true;
		}
	}

	public MMDevice? SelectedPlaybackDevice => (ComboPlayback?.SelectedItem as DeviceItem)?.Device;

	public MMDevice? SelectedRecordingDevice => (ComboRecording?.SelectedItem as DeviceItem)?.Device;

	public AudioRouting()
	{
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Expected O, but got Unknown
		_freqs = new List<double>();
		_dbValues = new List<double>();
		_sessionLineFrequencies = Array.Empty<double>();
		_sessionLineValues = Array.Empty<double>();
		_referenceRouteKey = "manual";
		_autoTestCases = new List<AutoTestCaseItem>();
		_autoTestGraphPaths = new List<string>();
		_loadedStandardDeviceKey = "";
		_preferredUsbPlaybackDeviceId = "";
		_preferredRecordingDeviceId = "";
		_lastSyncedSendToServer = true;
		_savedSetupWidth = new GridLength(380.0);
		_savedSetupVisibility = Visibility.Collapsed;
		_savedAutoTestVisibility = Visibility.Collapsed;
		_selectedThdInspectFrequencyHz = 1000.0;
		_isRoutingHeadroomRunning = false;
		_routingHeadroomRecordingId = "";
		InitializeComponent();
		_routingMeterTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(100)
		};
		_routingMeterTimer.Tick += (_, _) =>
		{
			if (IsLoaded && DateTime.UtcNow - _lastSampleRateWarningPollUtc >= TimeSpan.FromSeconds(2))
			{
				_lastSampleRateWarningPollUtc = DateTime.UtcNow;
				RefreshSelectedDeviceFormats();
			}
			if (_routingMeterBuffer.TryRead(out double peak, out double rms, out bool clipped, out bool invalid))
			{
				if (invalid) RewMeterRouting.ShowUnavailable("DỮ LIỆU THU KHÔNG HỢP LỆ");
				else RewMeterRouting.PushLevel(peak, rms, clipped);
			}
			if (!_isRoutingHeadroomRunning && _headroomRetryAtUtc != DateTime.MinValue
				&& DateTime.UtcNow >= _headroomRetryAtUtc) MaintainRoutingHeadroomMonitor();
		};
		_frequencyChartRefreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(80L, 0L)
		};
		_frequencyChartRefreshTimer.Tick += delegate
		{
			_frequencyChartRefreshTimer.Stop();
			if (!_discardCurrentMeasurementRequested && !_isFindingStandardReference)
			{
				UpdateFreqResponseChart();
			}
		};
		base.PreviewKeyDown += HandlePreviewKeyDown;
		base.Loaded += AudioRouting_Loaded;
		base.Unloaded += AudioRouting_Unloaded;
	}

	public void InitializeRouting(AudioEngine audioEngine, TestRunner testRunner)
	{
		_audioEngine = audioEngine;
		_audioEngine.MeasurementSampleRateGuard = ConfirmMeasurementSampleRatesAsync;
		_testRunner = testRunner;
		BtnStart.IsEnabledChanged += (_, _) => RefreshMicrophoneCalibrationSelection();
		_testRunner.OnRealTimeRecordedSamples += delegate(float[] samples)
		{
			_routingMeterBuffer.Push(samples, System.Threading.Volatile.Read(ref _routingMeterSession));
		};
		_testRunner.OnMeasurementCaptured += delegate(float[] samples, double peakDb, double rmsDb, double snrDb, bool isClipped)
		{
			long session = System.Threading.Volatile.Read(ref _routingMeterSession);
			((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
			{
				if (_routingMeterBuffer.IsCurrent(session) && !_discardCurrentMeasurementRequested)
					RewMeterRouting?.DisplayCapturedResult(samples, peakDb, rmsDb, snrDb, isClipped);
			}, (DispatcherPriority)7);
		};
		_testRunner.OnStepsChanged += delegate(List<TestStep> Steps)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested)
				{
					ListSteps.ItemsSource = IsFirstAutoTestAttempt
						? Steps.Select(step => step.Status == "Fail" ? new TestStep { Name = step.Name, Status = "Waiting", Details = "Tín hiệu chưa ổn định — cần xác nhận bằng lần đo lại." } : step).ToList()
						: Steps.ToList();
					if (!_isFindingStandardReference)
					{
						SidebarScrollViewer.ScrollToEnd();
					}
					TestStep testStep = Steps.ElementAtOrDefault(1);
					if (testStep != null && (testStep.Status == "Pass" || testStep.Status == "Fail"))
					{
						UpdateFreqResponseChart();
					}
					if (_isExecutingAutoSuite && _currentRunningTestCase != null)
					{
						TestStep testStep2 = Steps.ElementAtOrDefault(1);
						if (testStep2 != null)
						{
							if (testStep2.Status == "Waiting" || testStep2.Status == "Running")
							{
								_currentRunningTestCase.FreqStatus = "ĐANG ĐO";
								_currentRunningTestCase.FreqBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
							}
							else if (testStep2.Status == "Pass" || testStep2.Status == "Fail")
							{
								string text = testStep2.Details;
								if (text.Contains("Max Deviation:"))
								{
									int num = text.IndexOf("(Limit:");
									if (num > 0)
									{
										text = text.Substring(0, num).Trim();
									}
								}
								_currentRunningTestCase.FreqStatus = ((testStep2.Status == "Pass") ? "ĐẠT" : (IsFirstAutoTestAttempt && AutoTestAcquisitionInvalid ? "CHƯA ỔN ĐỊNH" : "KHÔNG ĐẠT")) + " (" + text + ")";
								_currentRunningTestCase.FreqBrush = ((testStep2.Status == "Pass") ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113)));
							}
						}
						TestStep testStep3 = Steps.ElementAtOrDefault(2);
						if (testStep3 != null)
						{
							if (testStep3.Status == "Waiting" || testStep3.Status == "Running")
							{
								_currentRunningTestCase.ThdStatus = "ĐANG ĐO";
								_currentRunningTestCase.ThdBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
							}
							else if (testStep3.Status == "Pass" || testStep3.Status == "Fail" || testStep3.Status == "Invalid")
							{
								double thdVal = _testRunner.LastMeasuredThdPercent;
								if (!double.IsFinite(thdVal) && _testRunner.LastToneQualities != null)
								{
									var finitePoint = _testRunner.LastToneQualities.Values.FirstOrDefault(q => double.IsFinite(q.ThdPercent));
									if (finitePoint != null) thdVal = finitePoint.ThdPercent;
								}

                                    if (testStep3.Status != "Invalid" && !_testRunner.ThdAcquisitionInvalid && double.IsFinite(thdVal))
								{
									string verdict = (testStep3.Status == "Pass") ? "ĐẠT" : ((testStep3.Status == "Fail") ? (IsFirstAutoTestAttempt && AutoTestAcquisitionInvalid ? "CHƯA ỔN ĐỊNH" : "KHÔNG ĐẠT") : "tham khảo");
									_currentRunningTestCase.ThdStatus = $"{thdVal:F3}% ({verdict})";
								}
								else
								{
									_currentRunningTestCase.ThdStatus = (testStep3.Status == "Invalid") ? "ĐO KHÔNG HỢP LỆ" : "KHÔNG ĐẠT";
								}
								_currentRunningTestCase.ThdBrush = new SolidColorBrush((testStep3.Status == "Pass")
									? System.Windows.Media.Color.FromRgb(52, 211, 153)
									: ((testStep3.Status == "Invalid") ? System.Windows.Media.Color.FromRgb(250, 204, 21) : System.Windows.Media.Color.FromRgb(248, 113, 113)));
							}
						}
					}
				}
			});
		};
		_testRunner.OnLogMessage += delegate(string source, string msg)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				AppendLog(source, msg);
			});
		};
		_testRunner.OnFrequencyResponsePoint += delegate(double freq, double db)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested)
				{
					lock (_freqs)
					{
						if (_freqs.Count > 0 && freq <= _freqs[^1])
						{
							_freqs.Clear();
							_dbValues.Clear();
						}
						_freqs.Add(freq);
						_dbValues.Add(db);
					}
					BtnSaveStandard.IsEnabled = _freqs.Count >= 2;
					if (!_isFindingStandardReference)
					{
						QueueFrequencyChartRefresh();
					}
				}
			});
		};
		_testRunner.OnUnsmoothedResponseReady += raw => Dispatcher.Invoke(() =>
		{
			if (!_discardCurrentMeasurementRequested) _rawResponse = new Dictionary<double, double>(raw);
		});
		_testRunner.OnSweepResultReady += result => Dispatcher.Invoke(() =>
		{
			if (_discardCurrentMeasurementRequested) return;
			RememberSweepPolarity(result);
			UpdateSampleRateWarning(result.SampleRate);
			UpdateMeasurementDetails();
		});
		_testRunner.OnOneKilohertzLevelMeasured += levelDbFs =>
		{
			Dispatcher.Invoke(() => { if (!_discardCurrentMeasurementRequested) UpdateOneKilohertzLevel(levelDbFs); });
		};
		_testRunner.OnOneKilohertzToneMeasured += (levelDbFs, peakSample) =>
		{
			Dispatcher.Invoke(() => { if (!_discardCurrentMeasurementRequested) UpdateOneKilohertzToneLevel(levelDbFs, peakSample); });
		};
		_testRunner.OnThdSpectrumReady += delegate(double toneFrequency, double[] frequencies, double[] magnitudes, double thdPercent, bool validated)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested && !_isFindingStandardReference && _reviewedAutoTestCase == null)
				{
					UpdateThdFftChart(toneFrequency, frequencies, magnitudes, thdPercent, validated);
				}
			});
		};
		_testRunner.OnSweepDistortionReady += delegate(SweepDistortionSummary summary)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested && !_isFindingStandardReference && _reviewedAutoTestCase == null)
				{
					UpdateSweepDistortionMetrics(summary);
				}
			});
		};
		_testRunner.OnNoiseAssessmentReady += delegate(NoiseAssessment assessment)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested && _isExecutingAutoSuite && _currentRunningTestCase != null)
				{
					string value = ((assessment.AmbientMicrophone == null) ? "không có mic môi trường" : $"{assessment.AmbientMicrophone.BroadbandDb:F1} {assessment.AmbientMicrophone.Unit}");
					_currentRunningTestCase.NoiseStatus = $"DUT {assessment.DutMicrophone.BroadbandDb:F1} {assessment.DutMicrophone.Unit}; Ambient {value}; {assessment.Classification}";
				}
			});
		};
		_testRunner.OnTestCompleted += delegate(bool Success)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested)
				{
					UpdateMeasurementDetails();
					if (_isExecutingAutoSuite && _currentRunningTestCase != null)
					{
						_currentTestSuccess = Success;
						bool thdAcquisitionInvalid = AutoTestAcquisitionInvalid;
						bool missingLimits = _testRunner.MissingFrequencyLimits;
						bool nothingToEvaluate = _testRunner.LastResponseLimitAssessment?.InvalidKind == ResponseLimitInvalidKind.NothingToEvaluate;
						bool awaitingRetry = !Success && IsFirstAutoTestAttempt && AutoTestAcquisitionInvalid;
						_currentRunningTestCase.Status = awaitingRetry ? "RETRY" : (thdAcquisitionInvalid || missingLimits || nothingToEvaluate ? "INVALID" : (Success ? "PASS" : "FAIL"));
						_currentRunningTestCase.StatusBrush = awaitingRetry || thdAcquisitionInvalid || missingLimits || nothingToEvaluate ? Brushes.Gold : (Success ? Brushes.MediumSpringGreen : Brushes.Salmon);
						if (_reviewedAutoTestCase == null) LblVerdict.Text = _currentRunningTestCase.Id + " — " + (awaitingRetry ? "TÍN HIỆU CHƯA ỔN ĐỊNH" : (missingLimits ? "CHƯA CÓ LINE CHUẨN" : (nothingToEvaluate ? "CẤU HÌNH HÕM LOẠI HẾT ĐIỂM XÉT" : (thdAcquisitionInvalid ? "ĐO KHÔNG HỢP LỆ" : (Success ? "ĐẠT" : "KHÔNG ĐẠT")))));
					}
					else if (_isFindingStandardReference)
					{
						_currentTestSuccess = Success;
					}
					else
					{
						SetFinalVerdict(Success);
						if (!Success && AudioEngine.flagExportImageSingleLine && _standardCurve != null && _standardCurve.Count > 0)
						{
							SaveFailureScreenshots("SingleLine");
						}
					}
					if (!_isFindingStandardReference)
					{
						RefreshThdVerdictAfterMeasurement();
					}
					if (!_isFindingStandardReference)
						RenderLocalMeasurementDiagnosis();
					ShowSignalLevelPopupIfNeeded();
				}
			});
		};
		_testRunner.OnTestSubstatusChanged += delegate(string type, string details)
		{
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				if (!_discardCurrentMeasurementRequested)
				{
					if (type == "Freq")
					{
						TxtFreqStatus.Text = details;
						if (!string.IsNullOrEmpty(details) && details != "Finished" && details != "Hoàn tất")
						{
							BorderFreqChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
							BorderThdChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
						}
						else
						{
							BorderFreqChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
						}
					}
					else if (type == "THD")
					{
						if (details.EndsWith("...") || details.StartsWith("Đang "))
						{
							TxtThdStatus.Text = details;
						}
						else if (double.IsFinite(_testRunner.LastMeasuredThdPercent))
						{
							string verdict = (_testRunner.Steps.ElementAtOrDefault(2)?.Status == "Pass") ? " (ĐẠT)" : ((_testRunner.Steps.ElementAtOrDefault(2)?.Status == "Fail") ? " (KHÔNG ĐẠT)" : " (tham khảo)");
							TxtThdStatus.Text = $"{_selectedThdInspectFrequencyHz:0} Hz · THD {_testRunner.LastMeasuredThdPercent:F3}%{verdict}";
						}
						else
						{
							TxtThdStatus.Text = details;
						}
						TxtThdStatus.ToolTip = details;
						if (_isFindingStandardReference)
						{
							BorderThdChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
							BorderFreqChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
						}
						else if (!string.IsNullOrEmpty(details) && details != "Finished" && details != "Hoàn tất")
						{
							BorderThdChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
							BorderFreqChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
						}
						else
						{
							BorderThdChart.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
						}
					}
				}
			});
		};
		LoadConfig();
		AutoDetectDevices();
		SyncPlaybackVolumeFromWindows();
		InitCharts();
		InitThdInspectFrequencyCombo();
		TxtFreqTolerance.TextChanged += delegate
		{
			CheckAndLoadStandardDevice();
			UpdateAutoTestStartButton();
		};
		TxtThdLimit.TextChanged += delegate
		{
			CheckAndLoadStandardDevice();
			UpdateThdInspection(_selectedThdInspectFrequencyHz);
		};
		CheckAndLoadStandardDevice();
		SetCurrentProduct(ServerEngine.CurrentProduct);
		_ = LoadSendToServerPreferenceAsync();
	}

	private async Task LoadSendToServerPreferenceAsync()
	{
		if (!File.Exists(GetRoutingConfigPath()))
		{
			_isLoadingServerPreference = true;
			try
			{
				_lastSyncedSendToServer = true;
				ChkSendToServer.IsChecked = true;
				if (ServerEngine.IsAuthenticated)
					await ServerEngine.SaveAudioQaUploadPreferenceAsync(true);
				return;
			}
			catch
			{
				return;
			}
			finally
			{
				_isLoadingServerPreference = false;
			}
		}
		try
		{
			if (ServerEngine.IsAuthenticated)
			{
				await ServerEngine.SaveAudioQaUploadPreferenceAsync(ChkSendToServer.IsChecked == true);
			}
		}
		catch
		{
		}
	}

	private async void ChkSendToServer_Changed(object sender, RoutedEventArgs e)
	{
		if (!_isConfigLoaded || _isLoadingServerPreference)
		{
			return;
		}
		bool enabled = (_lastSyncedSendToServer = ChkSendToServer.IsChecked == true);
		SaveConfig();
		try
		{
			if (ServerEngine.IsAuthenticated)
			{
				await ServerEngine.SaveAudioQaUploadPreferenceAsync(enabled);
			}
		}
		catch
		{
		}
	}

	public void SetCurrentProduct(ProductInfo? product)
	{
		ServerEngine.CurrentProduct = product;
		if (product != null && !string.IsNullOrEmpty(product.SerialNumber))
		{
			TxtProductSerial.Text = product.SerialNumber;
			TxtProductSerial.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
		}
		else
		{
			TxtProductSerial.Text = "TỰ ĐỘNG TẠO KHI BẮT ĐẦU ĐO";
			TxtProductSerial.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
		}
	}

	private void LoadConfig()
	{
		try
		{
			string path = GetRoutingConfigPath();
			if (File.Exists(path) || File.Exists(path + ".bak"))
			{
				AppConfig appConfig = AtomicFile.ReadJson<AppConfig>(path);
				if (appConfig != null)
				{
					_routingHeadroomEnabled = appConfig.HeadroomEnabled;
					PlaybackLevelDbfs = appConfig.PlaybackLevelDbfs;
					SelectPlaybackSampleRate(appConfig.PlaybackSampleRate);
					SelectPlaybackMode(appConfig.PlaybackMode);
					TxtFreqTolerance.Text = RepairStoredTolerance(appConfig.FreqTolerance, 3.0, 12.0, 10.0).ToString("F1", CultureInfo.InvariantCulture);
					TxtThdLimit.Text = RepairStoredTolerance(appConfig.ThdLimit, 0.5, 5.0, 100.0).ToString("F2", CultureInfo.InvariantCulture);
					_preferredUsbPlaybackDeviceId = (appConfig.UseUsbPlayback ? (appConfig.UsbPlaybackDeviceId ?? "") : (appConfig.BluetoothPlaybackDeviceId ?? ""));
					ChkSendToServer.IsChecked = appConfig.SendToServer;
					_isApplyingMeasurementMethod = true;
					ChkUseLogSweep.IsChecked = appConfig.UseLogSweepFrequencyResponse;
					appConfig.NormalizeFrequencyResponseToOneKilohertz = false;
					ChkDistortionCalibration.IsChecked = appConfig.ApplyDistortionCalibration;
					ChkDistortionHarmonicReference.IsChecked = appConfig.UseHarmonicFrequencyAsDistortionReference;
					SelectLogSweepDuration(appConfig.LogSweepDurationSeconds);
					SelectMultitoneDuration(appConfig.MultitoneDurationSeconds);
					_isApplyingMeasurementMethod = false;
					_testRunner.UseLogSweepFrequencyResponse = appConfig.UseLogSweepFrequencyResponse;
					_testRunner.NormalizeFrequencyResponseToOneKilohertz = false;
					_testRunner.LogSweepDurationSeconds = GetSelectedLogSweepDuration();
					_testRunner.MultitoneDurationSeconds = GetSelectedMultitoneDuration();
					_lastSyncedSendToServer = appConfig.SendToServer;
					_preferredRecordingDeviceId = appConfig.RecordingDeviceId ?? "";
					_preferredPlaybackDeviceName = appConfig.UsbPlaybackDeviceName ?? "";
					_preferredRecordingDeviceName = appConfig.RecordingDeviceName ?? "";
					_fastTrackPlayback12Identity = appConfig.FastTrackPlayback12Identity ?? "";
					_audioEngine.PlaybackVolume = appConfig.PlaybackVolume / 100.0;
					_audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
					_audioEngine.UseExclusivePlayback = IsExclusivePlaybackSelected();
					if (appConfig.LastPlaybackChannel.HasValue)
					{
						_audioEngine.PlaybackChannel = appConfig.LastPlaybackChannel;
					}
					if (appConfig.LastRecordingChannel.HasValue)
					{
						_audioEngine.RecordingChannel = appConfig.LastRecordingChannel;
					}
				}
			}
			else
			{
				ChkSendToServer.IsChecked = true;
				_lastSyncedSendToServer = true;
			}
		}
		catch
		{
		}
		finally
		{
			_isApplyingMeasurementMethod = false;
			_audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
			_testRunner.NormalizeFrequencyResponseToOneKilohertz = false;
			_testRunner.ApplyDistortionCalibration = ChkDistortionCalibration.IsChecked == true;
			_testRunner.UseHarmonicFrequencyAsDistortionReference = ChkDistortionHarmonicReference.IsChecked == true;
			_isConfigLoaded = true;
		}
	}

	private void FrequencyResponseMethod_Changed(object sender, RoutedEventArgs e)
	{
		if (!_isApplyingMeasurementMethod && _testRunner != null)
		{
			_testRunner.UseLogSweepFrequencyResponse = ChkUseLogSweep.IsChecked == true;
			_testRunner.UseCombinedMultitoneFrequencyResponse = !_testRunner.UseLogSweepFrequencyResponse;
			if (_isConfigLoaded)
			{
				SaveConfig();
				CheckAndLoadStandardDevice();
			}
		}
	}

	public bool IsModelTransitionBusy => _routingWorkflowActive || _isExecutingAutoSuite || _activeMeasurementTask != null;

	public void ClearModelSelection()
	{
        ClearPendingQaCompletion();
		_reviewedAutoTestCase = null;
		_activeInOutConfig = null;
		_appliedTestConfig = null;
		_autoTestResumeSession = null;
		_c360AutoTestResumeSession = null;
		UpdateAutoTestStartButton();
		_referenceRouteKey = "manual";
		_autoTestCases.Clear();
		ListAutoTestCases.ItemsSource = null;
		BtnStartAutoTest.IsEnabled = false;
	}

	public bool IsAutoTestRunning => _isExecutingAutoSuite;

	private void DistortionOptions_Changed(object sender, RoutedEventArgs e)
	{
		if (_testRunner == null || !_isConfigLoaded) return;
		_testRunner.ApplyDistortionCalibration = ChkDistortionCalibration.IsChecked == true;
		_testRunner.UseHarmonicFrequencyAsDistortionReference = ChkDistortionHarmonicReference.IsChecked == true;
		SaveConfig();
	}

	private void UpdateOneKilohertzLevel(double levelDbFs)
	{
		if (TxtOneKilohertzLevel == null || !double.IsFinite(levelDbFs)) return;
		string warning = _testRunner.InputClippingDetected || _testRunner.LastFeqPeakSample >= 0.995
			? " · QUÁ TẢI / CLIPPING"
			: levelDbFs < _testRunner.MinimumInputSignalDbFs ? " · TÍN HIỆU NHỎ"
			: levelDbFs >= -6.0 ? " · GẦN MỨC CAO" : "";
		string comparison = _standardOneKilohertzLevelDbFs is double referenceLevel && double.IsFinite(referenceLevel)
			? $" · chuẩn {referenceLevel:F1} dBFS · lệch {levelDbFs - referenceLevel:+0.0;-0.0;0.0} dB"
			: "";
		TxtOneKilohertzLevel.Text = $"Mức thu 1 kHz: {levelDbFs:F1} dBFS{comparison}{warning}";
		TxtOneKilohertzLevel.Foreground = string.IsNullOrEmpty(warning) ? Brushes.MediumSpringGreen : Brushes.Gold;
	}

	private void UpdateOneKilohertzToneLevel(double levelDbFs, double peakSample)
	{
		if (TxtOneKilohertzToneLevel == null || !double.IsFinite(levelDbFs)) return;
		string warning = peakSample >= 0.995 ? " · CLIPPING"
			: levelDbFs < _testRunner.MinimumInputSignalDbFs ? " · DƯỚI NGƯỠNG"
			: levelDbFs >= -6.0 ? " · GẦN MỨC CAO" : "";
		TxtOneKilohertzToneLevel.Text = $"Tone 1 kHz: RMS {levelDbFs:F1} dBFS · peak {peakSample:F3}{warning}";
		TxtOneKilohertzToneLevel.Foreground = string.IsNullOrEmpty(warning) ? Brushes.MediumSpringGreen : Brushes.Gold;
	}

	private double GetSelectedLogSweepDuration()
	{
		// The sequence length is authoritative; do not round-trip it through
		// formatted seconds (especially for 512K and 1M).
		if (ComboSweepLength?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int length))
			return length / (double)GetSelectedPlaybackSampleRate();
		if (!TryReadMeasurementDuration(TxtLogSweepDuration?.Text, out var duration, 0.5, 30.0))
		{
			return _testRunner?.LogSweepDurationSeconds ?? DefaultLogSweepSamples / (double)GetSelectedPlaybackSampleRate();
		}
		return duration;
	}

	private void SelectLogSweepDuration(double duration)
	{
		if (TxtLogSweepDuration != null)
		{
			UpdateSweepLengthLabels();
			TxtLogSweepDuration.Text = FormatMeasurementDuration(duration, DefaultLogSweepSamples / (double)GetSelectedPlaybackSampleRate(), 0.5, 30.0, "0.#####");
			if (ComboSweepLength != null)
			{
				bool wasApplying = _isApplyingMeasurementMethod;
				_isApplyingMeasurementMethod = true;
				int sampleCount = (int)Math.Round(duration * GetSelectedPlaybackSampleRate());
				ComboSweepLength.SelectedItem = ComboSweepLength.Items.OfType<ComboBoxItem>()
					.FirstOrDefault(item => int.TryParse(item.Tag?.ToString(), out int length) && Math.Abs(length - sampleCount) <= 1);
				_isApplyingMeasurementMethod = wasApplying;
			}
		}
	}

	private void SweepLength_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isApplyingMeasurementMethod || !_isConfigLoaded || _testRunner == null ||
			ComboSweepLength?.SelectedItem is not ComboBoxItem item ||
			!int.TryParse(item.Tag?.ToString(), out int sampleCount))
			return;
		double duration = sampleCount / (double)GetSelectedPlaybackSampleRate();
		TxtLogSweepDuration.Text = duration.ToString("0.#####", CultureInfo.InvariantCulture);
		_testRunner.LogSweepDurationSeconds = duration;
		SaveConfig();
		CheckAndLoadStandardDevice();
	}

	private double GetSelectedMultitoneDuration()
	{
		if (!TryReadMeasurementDuration(TxtMultitoneDuration?.Text, out var duration))
		{
			return _testRunner?.MultitoneDurationSeconds ?? 10.0;
		}
		return duration;
	}

	private void SelectMultitoneDuration(double duration)
	{
		if (TxtMultitoneDuration != null)
		{
			TxtMultitoneDuration.Text = FormatMeasurementDuration(duration, 10.0);
		}
	}

	private static bool TryReadMeasurementDuration(string? text, out double duration, double minimum = 3.0, double maximum = 60.0)
	{
		if (double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) && double.IsFinite(duration) && duration >= minimum)
		{
			return duration <= maximum;
		}
		return false;
	}

	private static string FormatMeasurementDuration(double duration, double fallback, double minimum = 3.0, double maximum = 60.0, string format = "0.##")
	{
		return ((double.IsFinite(duration) && duration >= minimum && duration <= maximum) ? duration : fallback).ToString(format, CultureInfo.InvariantCulture);
	}

	private int GetSelectedPlaybackSampleRate()
	{
		if (ComboPlaybackSampleRate?.SelectedItem is ComboBoxItem { Tag: var tag } && int.TryParse(tag?.ToString(), out var result) && result == 44100)
		{
			return 44100;
		}
		return 48000;
	}

	private void SelectPlaybackSampleRate(int sampleRate)
	{
		if (ComboPlaybackSampleRate != null)
		{
			int target = ((sampleRate == 44100) ? 44100 : 48000);
			ComboPlaybackSampleRate.SelectedItem = ComboPlaybackSampleRate.Items.OfType<ComboBoxItem>().FirstOrDefault((ComboBoxItem item) => item.Tag?.ToString() == target.ToString(CultureInfo.InvariantCulture));
		}
	}

	private bool IsExclusivePlaybackSelected()
	{
		return false;
	}

	private void SelectPlaybackMode(string? playbackMode)
	{
		if (ComboPlaybackMode != null)
		{
			ComboPlaybackMode.SelectedItem = ComboPlaybackMode.Items.OfType<ComboBoxItem>().FirstOrDefault();
		}
	}

	private void PlaybackMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_audioEngine != null)
		{
			_audioEngine.StopContinuousPlayback();
			_audioEngine.UseExclusivePlayback = false;
			UpdateSelectedDeviceFormats();
			if (_isConfigLoaded)
			{
				SaveConfig();
				CheckAndLoadStandardDevice();
				AppendLog("Audio Routing", "Chế độ phát: " + (_audioEngine.UseExclusivePlayback ? "EXCL (WASAPI Exclusive)" : "Shared (Windows)") + ".");
			}
		}
	}

	private void PlaybackSampleRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_audioEngine != null)
		{
			_audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
			UpdateSweepLengthLabels();
			if (_testRunner != null && ComboSweepLength?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int length))
			{
				SelectLogSweepDuration(length / (double)_audioEngine.PlaybackSampleRate);
				_testRunner.LogSweepDurationSeconds = GetSelectedLogSweepDuration();
			}
			EnsureSelectedStandardSampleRates();
			UpdateSelectedDeviceFormats();
			if (_isConfigLoaded)
			{
				SaveConfig();
				CheckAndLoadStandardDevice();
			}
		}
	}

	private void MeasurementDuration_LostFocus(object sender, RoutedEventArgs e)
	{
		if (_testRunner == null || !_isConfigLoaded || _isApplyingMeasurementMethod)
		{
			return;
		}
		bool flag = sender == TxtLogSweepDuration;
		TextBox textBox = (flag ? TxtLogSweepDuration : TxtMultitoneDuration);
		if (!TryReadMeasurementDuration(textBox.Text, out var duration, flag ? 0.5 : 3.0, flag ? 30.0 : 60.0))
		{
			if (flag) SelectLogSweepDuration(_testRunner.LogSweepDurationSeconds);
			else textBox.Text = FormatMeasurementDuration(_testRunner.MultitoneDurationSeconds, 10.0);
			TxtFreqStatus.Text = flag ? "Sweep phải từ 0,5 đến 30 giây." : "Thời gian multitone phải từ 3 đến 60 giây.";
			return;
		}
		if (flag)
		{
			_testRunner.LogSweepDurationSeconds = duration;
			SelectLogSweepDuration(duration);
		}
		else
		{
			_testRunner.MultitoneDurationSeconds = duration;
		}
		SaveConfig();
		CheckAndLoadStandardDevice();
	}

	private void ApplyEnteredMeasurementDurations()
	{
		if (_testRunner != null)
		{
			_testRunner.LogSweepDurationSeconds = GetSelectedLogSweepDuration();
			_testRunner.MultitoneDurationSeconds = GetSelectedMultitoneDuration();
			if (_isConfigLoaded)
			{
				SelectLogSweepDuration(_testRunner.LogSweepDurationSeconds);
				SelectMultitoneDuration(_testRunner.MultitoneDurationSeconds);
			}
		}
	}

	private string DescribeDeviceFormat(MMDevice? device, bool playback)
	{
		if (device == null)
		{
			return "Định dạng: chưa chọn thiết bị";
		}
		try
		{
			WaveFormat defaultFormat = AudioSampleRateSetup.ReadDefaultFormat(device.ID);
			WaveFormat mixFormat = ReadEndpointFormat(device)!;
			return DescribeAudioFormats(defaultFormat, mixFormat, playback);
		}
		catch (Exception ex)
		{
			return "Không đọc được định dạng: " + ex.Message;
		}
	}

	private void RefreshSelectedDeviceFormats()
	{
		if (TxtPlaybackFormat != null)
		{
			TxtPlaybackFormat.Text = DescribeDeviceFormat(SelectedPlaybackDevice, playback: true);
		}
		if (TxtRecordingFormat != null)
		{
			TxtRecordingFormat.Text = DescribeDeviceFormat(SelectedRecordingDevice, playback: false);
		}
		UpdateSampleRateWarning();
	}

	private void UpdateSelectedDeviceFormats()
	{
        RefreshMicrophoneCalibrationSelection();
		RefreshSelectedDeviceFormats();
		UpdateChannelSelectorAvailability();
		SyncChannelSelectionsFromEngine();
	}

	private static int GetDeviceChannelCount(MMDevice? device)
	{
		if (device == null)
		{
			return 0;
		}
		try
		{
			using AudioClient formatClient = device.AudioClient;
			return Math.Max(0, formatClient.MixFormat.Channels);
		}
		catch
		{
			return 0;
		}
	}

	private void UpdateChannelSelectorAvailability()
	{
		if (ComboRoutingPlaybackChannel == null || ComboRoutingRecordingChannel == null)
		{
			return;
		}
		if (_audioEngine == null)
		{
			GridRoutingPlaybackChannel.Visibility = Visibility.Collapsed;
			GridRoutingRecordingChannel.Visibility = Visibility.Collapsed;
			return;
		}
		bool flag = SelectedPlaybackDevice != null;
		bool flag2 = SelectedRecordingDevice != null;
		bool flag3 = flag & flag2;
		bool flag4 = GetDeviceChannelCount(SelectedPlaybackDevice) >= 2;
		bool flag5 = GetDeviceChannelCount(SelectedRecordingDevice) >= 2;
		GridRoutingPlaybackChannel.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
		GridRoutingRecordingChannel.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
		ComboRoutingPlaybackChannel.Visibility = ((!flag4) ? Visibility.Collapsed : Visibility.Visible);
		ComboRoutingRecordingChannel.Visibility = ((!flag5) ? Visibility.Collapsed : Visibility.Visible);
		TxtPlaybackChannelUnavailable.Visibility = ((!flag | flag4) ? Visibility.Collapsed : Visibility.Visible);
		TxtRecordingChannelUnavailable.Visibility = ((!flag2 | flag5) ? Visibility.Collapsed : Visibility.Visible);
		_isUpdatingChannelSelectors = true;
		try
		{
			if (!flag4)
			{
				_audioEngine.PlaybackChannel = null;
				ComboRoutingPlaybackChannel.SelectedIndex = 0;
			}
			if (!flag5)
			{
				_audioEngine.RecordingChannel = null;
				ComboRoutingRecordingChannel.SelectedIndex = 0;
			}
		}
		finally
		{
			_isUpdatingChannelSelectors = false;
		}
	}

	private void SaveConfig()
	{
		try
		{
			AppConfig appConfig = new AppConfig();
			appConfig.HeadroomEnabled = _routingHeadroomEnabled;
			appConfig.PlaybackVolume = 60.0;
			appConfig.PlaybackLevelDbfs = PlaybackLevelDbfs;
			appConfig.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
			appConfig.PlaybackMode = (IsExclusivePlaybackSelected() ? "Exclusive" : "Shared");
			appConfig.FreqTolerance = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
			appConfig.ThdLimit = ParseDoubleSafe(TxtThdLimit.Text, 0.5);
			appConfig.UseUsbPlayback = true;
			appConfig.LastSerialNumber = (Application.Current.MainWindow as MainWindow)?.TxtSerialNumber?.Text?.Trim() ?? "";
			appConfig.SendToServer = ChkSendToServer.IsChecked == true;
			appConfig.UseLogSweepFrequencyResponse = ChkUseLogSweep.IsChecked == true;
			appConfig.NormalizeFrequencyResponseToOneKilohertz = false;
			appConfig.ApplyDistortionCalibration = ChkDistortionCalibration.IsChecked == true;
			appConfig.UseHarmonicFrequencyAsDistortionReference = ChkDistortionHarmonicReference.IsChecked == true;
			appConfig.LogSweepDurationSeconds = GetSelectedLogSweepDuration();
			appConfig.MultitoneDurationSeconds = GetSelectedMultitoneDuration();
			appConfig.AmbientRecordingDeviceId = "";
			appConfig.UsbPlaybackDeviceId = _preferredUsbPlaybackDeviceId;
			appConfig.BluetoothPlaybackDeviceId = "";
			appConfig.RecordingDeviceId = _preferredRecordingDeviceId;
			appConfig.UsbPlaybackDeviceName = _preferredPlaybackDeviceName;
			appConfig.RecordingDeviceName = _preferredRecordingDeviceName;
			appConfig.FastTrackPlayback12Identity = _fastTrackPlayback12Identity;
			appConfig.LastPlaybackChannel = _audioEngine?.PlaybackChannel;
			appConfig.LastRecordingChannel = _audioEngine?.RecordingChannel;
			string contents = JsonSerializer.Serialize(appConfig);
			AtomicFile.WriteAllText(GetRoutingConfigPath(), contents);
			_configSaveWarningShown = false;
		}
		catch (Exception ex)
		{
			AppendLog("Lưu cấu hình", ex.Message);
			if (IsLoaded && !_configSaveWarningShown)
			{
				_configSaveWarningShown = true;
				ModernMessageBox.Show(Window.GetWindow(this), "Chưa lưu được cấu hình: " + ex.Message,
					"Lỗi lưu cấu hình", ModernMessageBox.MessageBoxType.Warning);
			}
		}
	}

	private void AutoDetectDevices()
	{
		List<MMDevice> playbackDevices = new();
		List<MMDevice> recordingDevices = new();
		try
		{
			_isAutoSelectingDevices = true;
			StopRoutingHeadroomMonitor();
			DisposeRoutingDeviceItems();
			playbackDevices = _audioEngine.GetPlaybackDevices();
			recordingDevices = _audioEngine.GetRecordingDevices();
			if (!IsTestingBusy) PrepareFastTrackEndpoints(playbackDevices, recordingDevices);
			AppendLog("System", $"Found {playbackDevices.Count} playback and {recordingDevices.Count} recording devices.");
			foreach (MMDevice item in playbackDevices)
			{
				bool flag = item.FriendlyName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 || item.FriendlyName.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 || item.FriendlyName.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 || item.FriendlyName.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0;
				string displayName = (flag ? ("[BT] " + item.FriendlyName) : ("[USB/Wired] " + item.FriendlyName));
				ComboPlayback.Items.Add(new DeviceItem(item, displayName));
				AppendLog("Device", "Playback Out Found: " + item.FriendlyName + " " + (flag ? "[Bluetooth]" : "[Wired/USB]"));
			}
			foreach (MMDevice item2 in recordingDevices)
			{
				string displayName2 = ((item2.FriendlyName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 || item2.FriendlyName.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 || item2.FriendlyName.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 || item2.FriendlyName.IndexOf("Stereo", StringComparison.OrdinalIgnoreCase) >= 0 || item2.FriendlyName.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0) ? ("[BT] " + item2.FriendlyName) : ("[USB/Wired] " + item2.FriendlyName));
				ComboRecording.Items.Add(new DeviceItem(item2, displayName2));
				AppendLog("Device", "Recording In Found: " + item2.FriendlyName);
			}
			MMDevice? playback = FastTrackDeviceSetup.SelectUnique(playbackDevices, _preferredUsbPlaybackDeviceId, d =>
				!string.IsNullOrEmpty(_preferredPlaybackDeviceName) && !FastTrackDeviceSetup.IsUnnamedAnalog(_preferredPlaybackDeviceName)
					? FastTrackDeviceSetup.NormalizeFriendlyName(d.FriendlyName).Equals(FastTrackDeviceSetup.NormalizeFriendlyName(_preferredPlaybackDeviceName), StringComparison.OrdinalIgnoreCase)
					: IsFastTrackPlayback12(d));
			playback ??= FastTrackDeviceSetup.SelectUnique(playbackDevices, "", IsFastTrackPlayback12);
			if (playback == null && string.IsNullOrEmpty(_preferredUsbPlaybackDeviceId))
				playback = playbackDevices.FirstOrDefault(d => d.FriendlyName.Contains("MI_LCD", StringComparison.OrdinalIgnoreCase)
					|| d.FriendlyName.Contains("MI LCD", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("MI_SAM", StringComparison.OrdinalIgnoreCase));
			MMDevice? recording = FastTrackDeviceSetup.SelectUnique(recordingDevices, _preferredRecordingDeviceId, d =>
				!string.IsNullOrEmpty(_preferredRecordingDeviceName)
					? FastTrackDeviceSetup.NormalizeFriendlyName(d.FriendlyName).Equals(FastTrackDeviceSetup.NormalizeFriendlyName(_preferredRecordingDeviceName), StringComparison.OrdinalIgnoreCase)
					: FastTrackDeviceSetup.IsFastTrack(d));
			recording ??= FastTrackDeviceSetup.SelectUnique(recordingDevices, "", FastTrackDeviceSetup.IsFastTrack);
			if (recording == null && string.IsNullOrEmpty(_preferredRecordingDeviceId))
				recording = recordingDevices.FirstOrDefault(d => d.FriendlyName.Contains("SONCA", StringComparison.OrdinalIgnoreCase));
			DeviceItem? deviceItem = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device == playback);
			DeviceItem? deviceItem2 = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device == recording);
			if (deviceItem != null)
			{
				ComboPlayback.SelectedItem = deviceItem;
				AppendLog("AutoSelect", "Matched Output: " + deviceItem.Device.FriendlyName);
			}
			else if (ComboPlayback.Items.Count > 0 && string.IsNullOrEmpty(_preferredUsbPlaybackDeviceId)
				&& !playbackDevices.Any(FastTrackDeviceSetup.IsFastTrack))
			{
				ComboPlayback.SelectedIndex = 0;
				AppendLog("AutoSelect", "Fallback Output (No MI_LCD/MI_SAM found): " + ((DeviceItem)ComboPlayback.SelectedItem).Device.FriendlyName);
			}
			if (deviceItem2 != null)
			{
				ComboRecording.SelectedItem = deviceItem2;
				AppendLog("AutoSelect", "Matched Input: " + deviceItem2.Device.FriendlyName);
			}
			else if (ComboRecording.Items.Count > 0 && string.IsNullOrEmpty(_preferredRecordingDeviceId))
			{
				ComboRecording.SelectedIndex = 0;
				AppendLog("AutoSelect", "Fallback Input (No SONCA found): " + ((DeviceItem)ComboRecording.SelectedItem).Device.FriendlyName);
			}
			AppendLog("System", "Device discovery finished.");
			RememberSelectedDeviceNames();
			if (_isConfigLoaded) SaveConfig();
			EnsureSelectedStandardSampleRates();
			UpdateSelectedDeviceFormats();
			TxtFreqStatus.Text = ((ComboRecording.SelectedItem == null || ComboPlayback.SelectedItem == null) ? "Chưa chọn đủ ngõ phát/thu" : "");
		}
		catch (Exception ex)
		{
			AppendLog("Error", "Failed to list audio devices: " + ex.Message);
			DisposeRoutingDeviceItems(playbackDevices.Concat(recordingDevices));
			TxtFreqStatus.Text = "Không cập nhật được thiết bị: " + ex.Message;
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), TxtFreqStatus.Text, "Lỗi cập nhật thiết bị", ModernMessageBox.MessageBoxType.Error);
		}
		finally
		{
			_isAutoSelectingDevices = false;
		}
	}

	private bool IsFastTrackPlayback12(MMDevice device) => FastTrackDeviceSetup.IsFastTrack(device)
		&& (device.FriendlyName.Contains(FastTrackDeviceSetup.Playback12Name, StringComparison.OrdinalIgnoreCase)
			|| (!string.IsNullOrEmpty(_fastTrackPlayback12Identity)
				&& FastTrackDeviceSetup.GetPlaybackIdentity(device) == _fastTrackPlayback12Identity));

	private void RememberSelectedDeviceNames()
	{
		if (SelectedPlaybackDevice is { } playback)
		{
			_preferredUsbPlaybackDeviceId = playback.ID;
			_preferredPlaybackDeviceName = playback.FriendlyName;
		}
		if (SelectedRecordingDevice is { } recording)
		{
			_preferredRecordingDeviceId = recording.ID;
			_preferredRecordingDeviceName = recording.FriendlyName;
		}
	}

	private void PrepareFastTrackEndpoints(List<MMDevice> playback, List<MMDevice> recording)
	{
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			using var history = new DeviceEnumerationLease(enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList());
			_fastTrackPlayback12Identity = FastTrackDeviceSetup.LearnPlayback12Identity(history.Devices
				.Where(FastTrackDeviceSetup.IsFastTrack).Select(d => (d.FriendlyName, FastTrackDeviceSetup.GetPlaybackIdentity(d))), _fastTrackPlayback12Identity);
		}
		catch (Exception ex) { AppendLog("FastTrack", "Chưa đọc được nhận diện ngõ 1/2: " + ex.Message); }
		foreach (MMDevice device in playback.Where(FastTrackDeviceSetup.IsFastTrack))
		{
			FastTrackDeviceSetup.TryRenamePlayback12(device, _fastTrackPlayback12Identity, out string message);
			if (!string.IsNullOrEmpty(message)) AppendLog("FastTrack", message);
		}
	}

	public bool ApplyModelDevices(string modelName, InOutConfig inOutConfig, out string? missingMessage)
	{
		return ApplyModelDevices(inOutConfig, out missingMessage);
	}

	public bool ApplyModelDevices(InOutConfig inOutConfig, out string? missingMessage)
	{
        ClearPendingQaCompletion();
		BtnStartAutoTest.IsEnabled = true;
		_activeInOutConfig = inOutConfig;
		_autoTestResumeSession = null;
		_c360AutoTestResumeSession = null;
		UpdateAutoTestStartButton();
		_appliedTestConfig = null;
		_referenceRouteKey = "manual";
		bool devicesReady = CheckModelDevices(inOutConfig, out missingMessage);
		_autoTestCases.Clear();
		if (inOutConfig?.Tests != null)
		{
			foreach (TestConfig test in inOutConfig.Tests)
			{
				if (test.IsEnabled)
				{
					_autoTestCases.Add(new AutoTestCaseItem
					{
						Id = test.id,
						Name = test.name,
						Status = "WAITING",
						StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)),
						Config = test
					});
				}
			}
		}
		ListAutoTestCases.ItemsSource = null;
		ListAutoTestCases.ItemsSource = _autoTestCases;
		if (_autoTestCases.FirstOrDefault()?.Config is { } firstTest) ApplyTestCaseConfig(firstTest);
		if (_isFreqExpanded || _isThdExpanded)
		{
			_savedAutoTestVisibility = Visibility.Visible;
		}
		else
		{
			PanelAutoTestList.Visibility = Visibility.Visible;
			ColAutoTestList.Width = new GridLength(1.0, GridUnitType.Star);
			ColChartsPlot.Width = new GridLength(3.0, GridUnitType.Star);
		}
		return devicesReady;
	}

	public bool CheckModelDevices(InOutConfig inOutConfig, out string? missingMessage)
	{
		if (_audioEngine == null)
		{
			missingMessage = "Audio Routing chưa sẵn sàng.";
			return false;
		}
		if (C360ModelDefinition.Matches(inOutConfig)) return CheckC360ModelDevices(inOutConfig, out missingMessage);
		Dictionary<string, string> dictionary = inOutConfig?.Devices?.Input ?? new Dictionary<string, string>();
		Dictionary<string, string> dictionary2 = inOutConfig?.Devices?.Output ?? new Dictionary<string, string>();
		using var playbackDevices = new DeviceEnumerationLease(_audioEngine.GetPlaybackDevices());
		using var recordingDevices = new DeviceEnumerationLease(_audioEngine.GetRecordingDevices());
		List<string> list = new List<string>();
		foreach (KeyValuePair<string, string> item in dictionary)
		{
			string key = item.Key;
			string targetName = item.Value;
			if (ResolvePlaybackDevice(key, targetName, playbackDevices.Devices, requireConfiguredMatch: true) == null)
			{
				list.Add($"- Ngõ vào thiết bị (Playback Out): {key} (yêu cầu chứa \"{targetName}\")");
			}
		}
		foreach (KeyValuePair<string, string> item2 in dictionary2)
		{
			string key2 = item2.Key;
			string targetName2 = item2.Value;
			if (!recordingDevices.Devices.Any(d => d.FriendlyName.IndexOf(targetName2, StringComparison.OrdinalIgnoreCase) >= 0))
			{
				list.Add($"- Ngõ ra thiết bị (Recording In): {key2} (yêu cầu chứa \"{targetName2}\")");
			}
		}
		missingMessage = ((list.Count == 0) ? null : string.Join(Environment.NewLine, list));
		return list.Count == 0;
	}

	public HashSet<string> GetModelDeviceIds(InOutConfig config)
	{
		var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		using var playback = new DeviceEnumerationLease(_audioEngine.GetPlaybackDevices());
		using var recording = new DeviceEnumerationLease(_audioEngine.GetRecordingDevices());
		foreach (var entry in config.Devices?.Input ?? new Dictionary<string, string>())
		{
			var device = ResolvePlaybackDevice(entry.Key, entry.Value, playback.Devices, requireConfiguredMatch: true);
			if (device != null) ids.Add(device.ID);
		}
		foreach (var entry in config.Devices?.Output ?? new Dictionary<string, string>())
		{
			var device = ResolveRecordingDevice(entry.Key, entry.Value, recording.Devices, requireConfiguredMatch: true);
			if (device != null) ids.Add(device.ID);
		}
		return ids;
	}

	private void InitCharts()
	{
		ApplyDarkThemeToPlot(PlotFreqResponse.Plot);
		WpfPlot[] thdPlots = GetThdPlots();
		WpfPlot[] array = thdPlots;
		WpfPlot[] array2 = array;
		WpfPlot[] array3 = array2;
		foreach (WpfPlot wpfPlot in array3)
		{
			ApplyDarkThemeToPlot(wpfPlot.Plot);
		}
		PlotFreqResponse.Plot.Title(_testRunner.NormalizeFrequencyResponseToOneKilohertz ? "Đáp tuyến chuẩn hóa theo 1 kHz" : "Đáp tuyến mức thu — không chuẩn hóa");
		PlotFreqResponse.Plot.Axes.Left.Label.Text = _testRunner.NormalizeFrequencyResponseToOneKilohertz ? "Mức tương đối 1 kHz (dBr)" : "Mức thu RMS (dBFS)";
		PlotFreqResponse.Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
		PlotFreqResponse.Plot.Axes.Left.TickGenerator = new NumericManual(
			Enumerable.Range(-20, 31).Select(index => new Tick(index * 10.0, (index * 10).ToString())).ToArray());
		ConfigureLogarithmicXAxis(PlotFreqResponse.Plot);
		ApplyFrequencyChartView(fitVerticalData: false);
		PlotFreqResponse.Refresh();
		InitializeConfiguredThdPlots();
		if (!_isThdExpanded)
			ApplyHorizontalThdLayout();
		if (!_areChartsInteractionsSetup)
		{
			_areChartsInteractionsSetup = true;
			SetupPlotInteractions(PlotFreqResponse, isFreqLog: true);
			SetupResponseContextMenu();
			thdPlots = GetThdPlots();
			WpfPlot[] array4 = thdPlots;
			WpfPlot[] array5 = array4;
			WpfPlot[] array6 = array5;
			foreach (WpfPlot wpfPlot2 in array6)
			{
				SetupPlotInteractions(wpfPlot2, isFreqLog: false);
			}
		}
	}

	private WpfPlot[] GetThdPlots()
	{
		return new WpfPlot[3] { PlotThdFft, PlotThdFft1k, PlotThdFft4k };
	}

	private void InitializeConfiguredThdPlots()
	{
		InitializeThdPlot(PlotThdFft, FormatThdToneTitle(_testRunner?.BassThdFrequencyHz ?? 80.0));
		InitializeThdPlot(PlotThdFft1k, FormatThdToneTitle(_testRunner?.MidThdFrequencyHz ?? 1000.0));
		InitializeThdPlot(PlotThdFft4k, FormatThdToneTitle(_testRunner?.TrebleThdFrequencyHz ?? 4000.0));
	}

	private static void InitializeThdPlot(WpfPlot plot, string title)
	{
		plot.Plot.Title(title);
		plot.Plot.Axes.Left.Label.Text = "dBFS";
		plot.Plot.Axes.Bottom.Label.Text = "Hz";
		plot.Plot.Axes.SetLimits(0.0, 10000.0, -90.0, 0.0);
		plot.Refresh();
	}

	private void ClearThdPlots()
	{
		if (_reviewedAutoTestCase == null && TxtQualityChartHeading != null && PanelThdInspection != null)
		{ TxtQualityChartHeading.Text = "DISTORTION"; PanelThdInspection.Visibility = Visibility.Visible; }
		WpfPlot[] thdPlots = GetThdPlots();
		for (int i = 0; i < thdPlots.Length; i++)
		{
			thdPlots[i].Plot.Clear();
		}
	}

	private void ApplyHorizontalThdLayout()
	{
		if (GridThdPlots == null) return;
		for (int i = 0; i < GridThdPlots.ColumnDefinitions.Count; i++)
		{
			GridThdPlots.ColumnDefinitions[i].Width = new GridLength(1.0, GridUnitType.Star);
		}
		if (GridThdPlots.RowDefinitions.Count > 0)
		{
			GridThdPlots.RowDefinitions[0].Height = new GridLength(1.0, GridUnitType.Star);
		}
		for (int i = 1; i < GridThdPlots.RowDefinitions.Count; i++)
		{
			GridThdPlots.RowDefinitions[i].Height = new GridLength(0.0);
		}
		WpfPlot[] thdPlots = GetThdPlots();
		for (int j = 0; j < thdPlots.Length; j++)
		{
			if (thdPlots[j] != null)
			{
				Grid.SetRow(thdPlots[j], 0);
				Grid.SetColumn(thdPlots[j], Math.Min(j, Math.Max(0, GridThdPlots.ColumnDefinitions.Count - 1)));
			}
		}
		if (thdPlots.Length > 0 && thdPlots[0] != null) thdPlots[0].Margin = new Thickness(0.0, 0.0, 3.0, 0.0);
		if (thdPlots.Length > 1 && thdPlots[1] != null) thdPlots[1].Margin = new Thickness(3.0, 0.0, 3.0, 0.0);
		if (thdPlots.Length > 2 && thdPlots[2] != null) thdPlots[2].Margin = new Thickness(3.0, 0.0, 0.0, 0.0);
	}

	private void ApplyVerticalThdLayout()
	{
		if (GridThdPlots == null) return;
		if (GridThdPlots.ColumnDefinitions.Count > 0)
		{
			GridThdPlots.ColumnDefinitions[0].Width = new GridLength(1.0, GridUnitType.Star);
		}
		for (int i = 1; i < GridThdPlots.ColumnDefinitions.Count; i++)
		{
			GridThdPlots.ColumnDefinitions[i].Width = new GridLength(0.0);
		}
		for (int i = 0; i < GridThdPlots.RowDefinitions.Count; i++)
		{
			GridThdPlots.RowDefinitions[i].Height = new GridLength(1.0, GridUnitType.Star);
		}
		WpfPlot[] thdPlots = GetThdPlots();
		for (int j = 0; j < thdPlots.Length; j++)
		{
			if (thdPlots[j] != null)
			{
				Grid.SetRow(thdPlots[j], Math.Min(j, Math.Max(0, GridThdPlots.RowDefinitions.Count - 1)));
				Grid.SetColumn(thdPlots[j], 0);
			}
		}
		if (thdPlots.Length > 0 && thdPlots[0] != null) thdPlots[0].Margin = new Thickness(0.0, 0.0, 0.0, 3.0);
		if (thdPlots.Length > 1 && thdPlots[1] != null) thdPlots[1].Margin = new Thickness(0.0, 3.0, 0.0, 3.0);
		if (thdPlots.Length > 2 && thdPlots[2] != null) thdPlots[2].Margin = new Thickness(0.0, 3.0, 0.0, 0.0);
	}

	private void SetupPlotInteractions(WpfPlot wpfPlot, bool isFreqLog)
	{
		Point panStartPoint = default(Point);
		AxisLimits panStartLimits = default(AxisLimits);
		bool isPanning = false;
		wpfPlot.PreviewMouseDown += delegate(object s, MouseButtonEventArgs e)
		{
			try
			{
				if (e.LeftButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed)
				{
					if (isFreqLog)
					{
						HideFrequencyHover();
					}
					panStartPoint = e.GetPosition(wpfPlot);
					panStartLimits = wpfPlot.Plot.Axes.GetLimits();
					isPanning = true;
					wpfPlot.CaptureMouse();
					Mouse.OverrideCursor = Cursors.Hand;
					wpfPlot.Cursor = Cursors.Hand;
					e.Handled = true;
				}
			}
			catch { }
		};
		wpfPlot.PreviewMouseMove += delegate(object s, MouseEventArgs e)
		{
			try
			{
				if (isPanning && (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed || e.MiddleButton == MouseButtonState.Pressed))
				{
					Point position = e.GetPosition(wpfPlot);
					double num = position.X - panStartPoint.X;
					double num2 = position.Y - panStartPoint.Y;
					double num3 = ((wpfPlot.ActualWidth > 80.0) ? (wpfPlot.ActualWidth - 80.0) : 400.0);
					double num4 = ((wpfPlot.ActualHeight > 60.0) ? (wpfPlot.ActualHeight - 60.0) : 250.0);
					double num5 = panStartLimits.Right - panStartLimits.Left;
					double num6 = panStartLimits.Top - panStartLimits.Bottom;
					if (num5 > 0.0 && num6 > 0.0 && double.IsFinite(num5) && double.IsFinite(num6))
					{
						double num7 = (0.0 - num) * (num5 / num3);
						double num8 = num2 * (num6 / num4);
						double left = panStartLimits.Left + num7;
						double right = panStartLimits.Right + num7;
						double bottom = panStartLimits.Bottom + num8;
						double top = panStartLimits.Top + num8;
						if (double.IsFinite(left) && double.IsFinite(right) && double.IsFinite(bottom) && double.IsFinite(top))
						{
							wpfPlot.Plot.Axes.SetLimits(left, right, bottom, top);
							if (isFreqLog) _frequencyViewAdjustedByUser = true;
							wpfPlot.Refresh();
						}
					}
					Mouse.OverrideCursor = Cursors.Hand;
					wpfPlot.Cursor = Cursors.Hand;
					e.Handled = true;
				}
				else
				{
					if (isPanning)
					{
						isPanning = false;
						wpfPlot.ReleaseMouseCapture();
					}
					if (Mouse.OverrideCursor == Cursors.Hand)
					{
						Mouse.OverrideCursor = null;
					}
					if (wpfPlot.Cursor != Cursors.Arrow)
					{
						wpfPlot.Cursor = Cursors.Arrow;
					}
					if (isFreqLog)
					{
						UpdateFrequencyHover(e.GetPosition(wpfPlot));
					}
				}
			}
			catch { }
		};
		if (isFreqLog)
		{
			wpfPlot.MouseLeave += delegate
			{
				HideFrequencyHover();
			};
		}
		wpfPlot.PreviewMouseUp += delegate(object s, MouseButtonEventArgs e)
		{
			try
			{
				if (isPanning)
				{
					isPanning = false;
					wpfPlot.ReleaseMouseCapture();
					Mouse.OverrideCursor = null;
					wpfPlot.Cursor = Cursors.Arrow;
					e.Handled = true;
				}
			}
			catch { }
		};
		wpfPlot.LostMouseCapture += delegate
		{
			try
			{
				isPanning = false;
				Mouse.OverrideCursor = null;
				wpfPlot.Cursor = Cursors.Arrow;
			}
			catch { }
		};
		wpfPlot.Focusable = true;
		wpfPlot.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
		{
			try
			{
				if (isFreqLog)
				{
					HideFrequencyHover();
				}
				Point position = e.GetPosition(wpfPlot);
				double factor = ((e.Delta > 0) ? 1.25 : 0.8);
				ZoomPlot(wpfPlot, factor, position);
				if (isFreqLog)
				{
					UpdateFrequencyHover(position);
				}
				e.Handled = true;
			}
			catch { }
		};
		wpfPlot.PreviewKeyDown += delegate(object s, KeyEventArgs e)
		{
			try
			{
				if ((int)e.Key == 13 && (_isFreqExpanded || _isThdExpanded))
				{
					RestoreChartsLayout();
					e.Handled = true;
				}
				else if (((Enum)Keyboard.Modifiers).HasFlag((Enum)(object)(ModifierKeys)2))
				{
					if ((int)e.Key == 141 || (int)e.Key == 85)
					{
						if (isFreqLog)
						{
							HideFrequencyHover();
						}
						ZoomPlot(wpfPlot, 1.25);
						if (isFreqLog)
						{
							UpdateFrequencyHover(Mouse.GetPosition(wpfPlot));
						}
						e.Handled = true;
					}
					else if ((int)e.Key == 143 || (int)e.Key == 87)
					{
						if (isFreqLog)
						{
							HideFrequencyHover();
						}
						ZoomPlot(wpfPlot, 0.8);
						if (isFreqLog)
						{
							UpdateFrequencyHover(Mouse.GetPosition(wpfPlot));
						}
						e.Handled = true;
					}
					else if ((int)e.Key == 34 || (int)e.Key == 74)
					{
						if (isFreqLog)
						{
							HideFrequencyHover();
							ApplyFrequencyChartView(fitVerticalData: true);
							_frequencyViewAdjustedByUser = false;
						}
						else
						{
							wpfPlot.Plot.Axes.SetLimits(0.0, 10000.0, -90.0, 0.0);
						}
						wpfPlot.Refresh();
						if (isFreqLog)
						{
							UpdateFrequencyHover(Mouse.GetPosition(wpfPlot));
						}
						e.Handled = true;
					}
				}
			}
			catch { }
		};
		if (isFreqLog)
		{
			wpfPlot.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
			{
				try
				{
					HideFrequencyHover();
					ApplyFrequencyChartView(fitVerticalData: true);
					_frequencyViewAdjustedByUser = false;
					wpfPlot.Refresh();
					UpdateFrequencyHover(e.GetPosition(wpfPlot));
					e.Handled = true;
				}
				catch { }
			};
		}
		else
		{
			wpfPlot.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
			{
				try
				{
					wpfPlot.Plot.Axes.SetLimits(0.0, 10000.0, -90.0, 0.0);
					wpfPlot.Refresh();
					e.Handled = true;
				}
				catch { }
			};
		}
	}

	private void GetSafeFrequencySnapshots(out double[] freqs, out double[] dbValues)
	{
		IReadOnlyDictionary<double, double> raw = _reviewedAutoTestCase?.UnsmoothedResponse ?? _rawResponse;
		if (_showUnsmoothedResponse && raw.Count > 0)
		{
			var points = raw.Where(p => p.Key >= 20 && p.Key <= 20000).OrderBy(p => p.Key).ToArray();
			freqs = points.Select(p => p.Key).ToArray(); dbValues = points.Select(p => p.Value).ToArray();
			return;
		}
        if (_reviewedAutoTestCase is { } review)
        {
            int count = Math.Min(review.ResponseFrequencies.Length, review.ResponseLevels.Length);
            freqs = review.ResponseFrequencies.Take(count).ToArray();
            dbValues = review.ResponseLevels.Take(count).ToArray();
            return;
        }
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				lock (_freqs)
				{
					freqs = _freqs?.ToArray() ?? Array.Empty<double>();
					dbValues = _dbValues?.ToArray() ?? Array.Empty<double>();
					int min = Math.Min(freqs.Length, dbValues.Length);
					if (freqs.Length != min) Array.Resize(ref freqs, min);
					if (dbValues.Length != min) Array.Resize(ref dbValues, min);
					return;
				}
			}
			catch
			{
			}
		}
		freqs = Array.Empty<double>();
		dbValues = Array.Empty<double>();
	}

	private static bool TryInterpolateCurve(IReadOnlyDictionary<double, double>? curve, double frequency, out double value)
	{
		value = 0.0;
		try
		{
			if (curve == null || curve.Count == 0 || frequency <= 0.0 || !double.IsFinite(frequency))
			{
				return false;
			}
			if (curve.TryGetValue(frequency, out value))
			{
				return double.IsFinite(value);
			}
			var pairs = curve.ToArray();
			if (pairs.Length == 0) return false;

			KeyValuePair<double, double>? lowerPair = null;
			KeyValuePair<double, double>? upperPair = null;

			foreach (var pair in pairs)
			{
				if (pair.Key < frequency)
				{
					if (!lowerPair.HasValue || pair.Key > lowerPair.Value.Key)
					{
						lowerPair = pair;
					}
				}
				else if (pair.Key > frequency)
				{
					if (!upperPair.HasValue || pair.Key < upperPair.Value.Key)
					{
						upperPair = pair;
					}
				}
			}

			if (!lowerPair.HasValue || !upperPair.HasValue)
			{
				return false;
			}
			double logFreq = Math.Log10(frequency);
			double logLower = Math.Log10(lowerPair.Value.Key);
			double logUpper = Math.Log10(upperPair.Value.Key);
			double denom = logUpper - logLower;
			if (Math.Abs(denom) < 1e-9) return false;

			double num = (logFreq - logLower) / denom;
			value = lowerPair.Value.Value + (upperPair.Value.Value - lowerPair.Value.Value) * num;
			return double.IsFinite(value);
		}
		catch
		{
			value = 0.0;
			return false;
		}
	}

	private void UpdateFrequencyHover(Point mousePosition)
	{
		try
		{
			GetSafeFrequencySnapshots(out double[] freqsSnapshot, out double[] dbValuesSnapshot);
			int num = freqsSnapshot.Length;
			if (PlotFreqResponse.ActualWidth <= 0.0 || PlotFreqResponse.ActualHeight <= 0.0)
			{
				HideFrequencyHover();
				return;
			}
			float num2 = ((PlotFreqResponse.DisplayScale <= 0f) ? 1f : PlotFreqResponse.DisplayScale);
			if (num == 0)
			{
				UpdateStandardOnlyHover(mousePosition, num2);
				return;
			}
			int num3 = -1;
			double num4 = double.PositiveInfinity;
			for (int i = 0; i < num; i++)
			{
				double f = freqsSnapshot[i];
				double db = dbValuesSnapshot[i];
				if (double.IsFinite(f) && f > 0.0 && double.IsFinite(db))
				{
					Pixel pixel = PlotFreqResponse.Plot.GetPixel(new Coordinates(Math.Log10(f), db));
					double num5 = pixel.X / num2;
					double num6 = pixel.Y / num2;
					double num7 = num5 - mousePosition.X;
					double num8 = num6 - mousePosition.Y;
					double num9 = num7 * num7 + num8 * num8;
					if (num9 < num4)
					{
						num4 = num9;
						num3 = i;
					}
				}
			}
			if (num3 < 0 || num4 > 121.0 || num3 >= freqsSnapshot.Length || num3 >= dbValuesSnapshot.Length)
			{
				HideFrequencyHover();
				return;
			}
			double num10 = freqsSnapshot[num3];
			double num11 = dbValuesSnapshot[num3];
			Pixel pixel2 = PlotFreqResponse.Plot.GetPixel(new Coordinates(Math.Log10(num10), num11));
			double num12 = pixel2.X / num2;
			double num13 = pixel2.Y / num2;
			string levelUnit = _testRunner.NormalizeFrequencyResponseToOneKilohertz ? "dBr" : "dBFS";
			string text = $"{num10:0.#} Hz — | — Do: {num11:+0.00;-0.00;0.00} {levelUnit}";
			if (_showUnsmoothedResponse && TryInterpolateCurve(GetDisplayedStandardCurve(), num10, out double rawTarget))
			{
				text += $"\nLine gốc: {rawTarget:+0.00;-0.00;0.00} {levelUnit} | Δ {num11 - rawTarget:+0.00;-0.00;0.00} dB (xem dữ liệu; đánh giá theo 1/24)";
			}
			else if (!_showUnsmoothedResponse)
			{
				var limit = DisplayedResponseLimitPolicy().Evaluate(num10, num11);
				text += FormatFrequencyHoverLimitLine(limit, num11, levelUnit);
			}
			else
			{
				text += "\nChưa có line chuẩn/giới hạn tại điểm này";
			}
			TxtFreqHover.Text = text;
			double left = Math.Clamp(mousePosition.X + 14.0, 4.0, Math.Max(4.0, PlotFreqResponse.ActualWidth - 300.0));
			double top = Math.Clamp(mousePosition.Y + 14.0, 4.0, Math.Max(4.0, PlotFreqResponse.ActualHeight - 70.0));
			BorderFreqHover.Margin = new Thickness(left, top, 0.0, 0.0);
			BorderFreqHover.Visibility = Visibility.Visible;
			bool flag = IsFrequencyPointOverThreeDb(num10, num11);
			EllipseFreqHoverPoint.Fill = new SolidColorBrush(flag ? System.Windows.Media.Color.FromRgb(239, 68, 68) : System.Windows.Media.Color.FromRgb(34, 197, 94));
			EllipseFreqHoverPoint.Stroke = new SolidColorBrush(flag ? System.Windows.Media.Color.FromRgb(254, 202, 202) : System.Windows.Media.Color.FromRgb(220, 252, 231));
			EllipseFreqHoverPoint.Margin = new Thickness(num12 - 8.0, num13 - 8.0, 0.0, 0.0);
			EllipseFreqHoverPoint.Visibility = Visibility.Visible;
			if (flag && TryGetComparableStandardTarget(num10, out var target))
			{
				Pixel pixel3 = PlotFreqResponse.Plot.GetPixel(new Coordinates(Math.Log10(num10), target));
				double num17 = pixel3.X / num2;
				double num18 = pixel3.Y / num2;
				EllipseFreqHoverStandardPoint.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				EllipseFreqHoverStandardPoint.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 249, 195));
				EllipseFreqHoverStandardPoint.Margin = new Thickness(num17 - 7.0, num18 - 7.0, 0.0, 0.0);
				EllipseFreqHoverStandardPoint.Visibility = Visibility.Visible;
			}
			else
			{
				EllipseFreqHoverStandardPoint.Visibility = Visibility.Collapsed;
			}
		}
		catch
		{
			HideFrequencyHover();
		}
	}

	private void UpdateStandardOnlyHover(Point mousePosition, float displayScale)
	{
		try
		{
			if (!HasComparableStandardCurve())
			{
				HideFrequencyHover();
				return;
			}
			var standardSnapshot = GetDisplayedStandardCurve()?.ToArray();
			if (standardSnapshot == null || standardSnapshot.Length == 0)
			{
				HideFrequencyHover();
				return;
			}
			double value = 0.0;
			double value2 = 0.0;
			double num = 0.0;
			double num2 = 0.0;
			double num3 = double.PositiveInfinity;
			foreach (KeyValuePair<double, double> item in standardSnapshot)
			{
				if (double.IsFinite(item.Key) && !(item.Key <= 0.0) && double.IsFinite(item.Value))
				{
					Pixel pixel = PlotFreqResponse.Plot.GetPixel(new Coordinates(Math.Log10(item.Key), item.Value));
					double num4 = pixel.X / displayScale;
					double num5 = pixel.Y / displayScale;
					double num6 = num4 - mousePosition.X;
					double num7 = num5 - mousePosition.Y;
					double num8 = num6 * num6 + num7 * num7;
					if (num8 < num3)
					{
						num3 = num8;
						value = item.Key;
						value2 = item.Value;
						num = num4;
						num2 = num5;
					}
				}
			}
			if (!double.IsFinite(num3) || num3 > 121.0)
			{
				HideFrequencyHover();
				return;
			}
			TxtFreqHover.Text = $"{value:0.#} Hz — | — Line chuẩn: {value2:+0.00;-0.00;0.00} {(_testRunner.NormalizeFrequencyResponseToOneKilohertz ? "dBr" : "dBFS")}";
			double left = Math.Clamp(mousePosition.X + 14.0, 4.0, Math.Max(4.0, PlotFreqResponse.ActualWidth - 300.0));
			double top = Math.Clamp(mousePosition.Y + 14.0, 4.0, Math.Max(4.0, PlotFreqResponse.ActualHeight - 70.0));
			BorderFreqHover.Margin = new Thickness(left, top, 0.0, 0.0);
			BorderFreqHover.Visibility = Visibility.Visible;
			EllipseFreqHoverPoint.Visibility = Visibility.Collapsed;
			EllipseFreqHoverStandardPoint.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			EllipseFreqHoverStandardPoint.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(254, 249, 195));
			EllipseFreqHoverStandardPoint.Margin = new Thickness(num - 7.0, num2 - 7.0, 0.0, 0.0);
			EllipseFreqHoverStandardPoint.Visibility = Visibility.Visible;
		}
		catch
		{
			HideFrequencyHover();
		}
	}

	private void HideFrequencyHover()
	{
		BorderFreqHover.Visibility = Visibility.Collapsed;
		EllipseFreqHoverPoint.Visibility = Visibility.Collapsed;
		EllipseFreqHoverStandardPoint.Visibility = Visibility.Collapsed;
	}

	private bool HasComparableStandardCurve()
	{
		try
		{
			if (_standardCurve != null && _standardCurve.Count > 0 && !string.IsNullOrEmpty(_loadedStandardDeviceKey))
			{
				if (string.Equals(_loadedStandardDeviceKey, GetStandardDeviceKey(), StringComparison.Ordinal))
					return true;
				if (HasDistinctDisplayIdFallback && string.Equals(_loadedStandardDeviceKey, GetStandardDeviceKey(useCurrentDisplayId: true), StringComparison.Ordinal))
					return true;
			}
		}
		catch { }
		return false;
	}

	private bool TryGetComparableStandardTarget(double frequency, out double target)
	{
		target = 0.0;
		try
		{
			IReadOnlyDictionary<double, double>? standardCurve = _reviewedAutoTestCase?.StandardCurve ?? _standardCurve;
			IReadOnlyDictionary<double, FrequencyLimitPoint>? frequencyLimits = _reviewedAutoTestCase?.FrequencyLimits ?? _frequencyLimits;
			if (_reviewedAutoTestCase == null && !HasComparableStandardCurve())
			{
				return false;
			}
			if (standardCurve == null || standardCurve.Count == 0)
			{
				return false;
			}
			if (ProductionMeasurement.TryGetFrequencyLimit(frequencyLimits, frequency, out FrequencyLimitPoint point))
			{
				target = point.TargetDb;
				return true;
			}
			return TryInterpolateCurve(standardCurve, frequency, out target);
		}
		catch
		{
			return false;
		}
	}

	private bool IsFrequencyPointOverThreeDb(double frequency, double measured)
	{
		return DisplayedResponseLimitPolicy().Evaluate(frequency, measured).Outside;
	}

	private void ApplyFrequencyChartView(bool fitVerticalData)
	{
		if (_reviewedAutoTestCase is { Config.SpeakerStage: "MicFrequencyShift" } shift) { ShowC360FrequencyShiftPlot(shift); return; }
		try
		{
			double left = Math.Log10(20.0);
			double right = Math.Log10(20000.0);
			if (!fitVerticalData)
			{
				PlotFreqResponse.Plot.Axes.SetLimits(left, right, _testRunner.NormalizeFrequencyResponseToOneKilohertz ? -30.0 : -100.0, _testRunner.NormalizeFrequencyResponseToOneKilohertz ? 30.0 : 0.0);
				return;
			}
			var list = new List<double>();
			GetSafeFrequencySnapshots(out _, out double[] dbSnap);
			list.AddRange(dbSnap.Where(double.IsFinite));
			if (_sessionLineValues != null)
			{
				list.AddRange(_sessionLineValues.Where(double.IsFinite));
			}
			var stdSnap = GetDisplayedStandardCurve()?.Values.ToArray();
			if (stdSnap != null)
			{
				list.AddRange(stdSnap.Where(double.IsFinite));
			}
			var limSnap = _frequencyLimits?.Values.ToArray();
			if (limSnap != null)
			{
				list.AddRange(limSnap.Select((FrequencyLimitPoint point) => point.LowerDb).Where(double.IsFinite));
				list.AddRange(limSnap.Select((FrequencyLimitPoint point) => point.UpperDb).Where(double.IsFinite));
			}
			if (list.Count == 0)
			{
				PlotFreqResponse.Plot.Axes.SetLimits(left, right, _testRunner.NormalizeFrequencyResponseToOneKilohertz ? -30.0 : -100.0, _testRunner.NormalizeFrequencyResponseToOneKilohertz ? 30.0 : 0.0);
				return;
			}
			double num = list.Min();
			double num2 = list.Max();
			double num3 = num2 - num;
			if (num3 < 8.0)
			{
				double num4 = (num + num2) / 2.0;
				num = num4 - 4.0;
				num2 = num4 + 4.0;
				num3 = 8.0;
			}
			double num5 = Math.Max(0.75, num3 * 0.08);
			PlotFreqResponse.Plot.Axes.SetLimits(left, right, num - num5, num2 + num5);
		}
		catch { }
	}

	public void ZoomPlot(WpfPlot wpfPlot, double factor, Point? mousePos = null)
	{
		try
		{
			if (wpfPlot?.Plot == null) return;
			AxisLimits limits = wpfPlot.Plot.Axes.GetLimits();
			if (!double.IsFinite(limits.Left) || !double.IsFinite(limits.Right) ||
			    !double.IsFinite(limits.Bottom) || !double.IsFinite(limits.Top))
			{
				return;
			}
			double num = limits.Right - limits.Left;
			double num2 = limits.Top - limits.Bottom;
			if (num <= 0.0 || num2 <= 0.0 || !double.IsFinite(num) || !double.IsFinite(num2))
			{
				return;
			}
			double num5;
			double num6;
			if (mousePos.HasValue && wpfPlot.ActualWidth > 0.0 && wpfPlot.ActualHeight > 0.0)
			{
				Point value = mousePos.Value;
				double num3 = Math.Clamp(value.X / wpfPlot.ActualWidth, 0.05, 0.95);
				double num4 = Math.Clamp(1.0 - value.Y / wpfPlot.ActualHeight, 0.05, 0.95);
				num5 = limits.Left + num3 * num;
				num6 = limits.Bottom + num4 * num2;
			}
			else
			{
				num5 = (limits.Left + limits.Right) / 2.0;
				num6 = (limits.Bottom + limits.Top) / 2.0;
			}
			bool flag = wpfPlot == PlotFreqResponse;
			double num7 = (flag ? Math.Pow(factor, 1.35) : factor);
			double num8 = (flag ? Math.Pow(factor, 0.55) : factor);
			if (num7 <= 0.0 || num8 <= 0.0 || !double.IsFinite(num7) || !double.IsFinite(num8)) return;
			double num9 = num / num7;
			double num10 = num2 / num8;
			if (!(num9 < 0.001) && !(num9 > 100000.0) && !(num10 < 0.01) && !(num10 > 1000.0))
			{
				double num11 = (num5 - limits.Left) / num;
				double num12 = (num6 - limits.Bottom) / num2;
				double num13 = num5 - num9 * num11;
				double right = num13 + num9;
				double num14 = num6 - num10 * num12;
				double top = num14 + num10;
				if (double.IsFinite(num13) && double.IsFinite(right) && double.IsFinite(num14) && double.IsFinite(top))
				{
					wpfPlot.Plot.Axes.SetLimits(num13, right, num14, top);
					if (flag) _frequencyViewAdjustedByUser = true;
					wpfPlot.Refresh();
				}
			}
		}
		catch
		{
		}
	}

	public void ZoomActivePlot(double factor)
	{
		WpfPlot wpfPlot = GetThdPlots().FirstOrDefault((WpfPlot plot) => plot.IsMouseOver) ?? PlotFreqResponse;
		ZoomPlot(wpfPlot, factor);
	}

	public void ResetActivePlotZoom()
	{
		WpfPlot wpfPlot = GetThdPlots().FirstOrDefault((WpfPlot plot) => plot.IsMouseOver) ?? PlotFreqResponse;
		if (wpfPlot == PlotFreqResponse)
		{
			_frequencyViewAdjustedByUser = false;
			ApplyFrequencyChartView(true);
		}
		else
		{
			wpfPlot.Plot.Axes.SetLimits(0.0, 10000.0, -90.0, 0.0);
		}
		wpfPlot.Refresh();
	}

	private void HandlePreviewKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Invalid comparison between Unknown and I4
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Invalid comparison between Unknown and I4
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Invalid comparison between Unknown and I4
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Invalid comparison between Unknown and I4
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Invalid comparison between Unknown and I4
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Invalid comparison between Unknown and I4
		if (TryHandleMeasurementShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, () => ShowMeasurementDetails()))
		{
			e.Handled = true;
			return;
		}
		if ((int)e.Key == 13 && (_isFreqExpanded || _isThdExpanded))
		{
			RestoreChartsLayout();
			e.Handled = true;
		}
		else if (((Enum)Keyboard.Modifiers).HasFlag((Enum)(object)(ModifierKeys)2))
		{
			if ((int)e.Key == 141 || (int)e.Key == 85)
			{
				ZoomActivePlot(1.25);
				e.Handled = true;
			}
			else if ((int)e.Key == 143 || (int)e.Key == 87)
			{
				ZoomActivePlot(0.8);
				e.Handled = true;
			}
			else if ((int)e.Key == 34 || (int)e.Key == 74)
			{
				ResetActivePlotZoom();
				e.Handled = true;
			}
		}
	}

	private void ConfigureLogarithmicXAxis(Plot plot)
	{
		List<Tick> list = new List<Tick>();
		double[] array = new double[11]
		{
			20.0, 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 15000.0,
			20000.0
		};
		string[] array2 = new string[11]
		{
			"20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "15k",
			"20k"
		};
		for (int i = 0; i < array.Length; i++)
		{
			list.Add(new Tick(Math.Log10(array[i]), array2[i]));
		}
		plot.Axes.Bottom.TickGenerator = new NumericManual(list.ToArray());
	}

	private void ApplyDarkThemeToPlot(Plot plot)
	{
		plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
		plot.DataBackground.Color = ScottPlot.Color.FromHex("#0E0E10");
		plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#27272A");
		plot.Axes.Color(ScottPlot.Color.FromHex("#A1A1AA"));
		plot.Axes.Title.Label.ForeColor = ScottPlot.Color.FromHex("#F4F4F5");
	}

	private void UpdateFreqResponseChart()
	{
        if (ShowInlineImpulse(_reviewedAutoTestCase)) return;
		if (_reviewedAutoTestCase is { Config.SpeakerStage: "MicFrequencyShift" } shift) { ShowC360FrequencyShiftPlot(shift); return; }
        var displayedRange = MeasurementCriteria.Range(_reviewedAutoTestCase?.Config ?? _appliedTestConfig);
        TxtFrequencyChartHeading.Text = $"ĐÁP TUYẾN 1/24 · VẼ 20–20000 Hz · XÉT {displayedRange.MinimumHz:0}–{displayedRange.MaximumHz:0} Hz";
		if (_suppressChartUpdates)
		{
			return;
		}
		try
		{
			_frequencyChartRefreshTimer.Stop();
			HideFrequencyHover();
			PlotFreqResponse.Plot.Clear();
            PlotFreqResponse.Plot.Axes.Left.TickGenerator = new ResponseDbTickGenerator();
            PlotFreqResponse.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
            PlotFreqResponse.Plot.Axes.Left.Label.Text = _testRunner.NormalizeFrequencyResponseToOneKilohertz ? "Mức tương đối 1 kHz (dBr)" : "Mức thu RMS (dBFS)";
			PlotFreqResponse.Plot.Title((_reviewedAutoTestCase is { } chartRow ? chartRow.Id + " · " + chartRow.Name + " · " : "")
                + (_testRunner.NormalizeFrequencyResponseToOneKilohertz ? "Đáp tuyến dBr" : "Đáp tuyến dBFS")
				+ (_showUnsmoothedResponse ? " — No smoothing (trước làm mượt)" : " — 1/24 octave · toàn dải 20 Hz–20 kHz"));
			bool showBandVerdict = _reviewedAutoTestCase != null ? _reviewedAutoTestCase.Config.SpeakerStage == null : (!_isFindingStandardReference && HasComparableStandardCurve()
				&& _testRunner?.Steps.ElementAtOrDefault(1)?.Status is "Pass" or "Fail");
			bool bassPassed = _reviewedAutoTestCase?.BassPassed ?? _testRunner.BassPassed;
			bool midPassed = _reviewedAutoTestCase?.MidPassed ?? _testRunner.MidPassed;
			bool treblePassed = _reviewedAutoTestCase?.TreblePassed ?? _testRunner.TreblePassed;
			HorizontalSpan horizontalSpan = PlotFreqResponse.Plot.Add.HorizontalSpan(Math.Log10(TestRunner.BassMin), Math.Log10(TestRunner.BassMax));
			horizontalSpan.LineStyle.Width = 0f;
			if (showBandVerdict && !bassPassed)
			{
				horizontalSpan.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.12);
			}
			else
			{
				horizontalSpan.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(0.08);
			}
			HorizontalSpan horizontalSpan2 = PlotFreqResponse.Plot.Add.HorizontalSpan(Math.Log10(TestRunner.BassMax), Math.Log10(TestRunner.MidMax));
			horizontalSpan2.LineStyle.Width = 0f;
			if (showBandVerdict && !midPassed)
			{
				horizontalSpan2.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.12);
			}
			else
			{
				horizontalSpan2.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(0.04);
			}
			HorizontalSpan horizontalSpan3 = PlotFreqResponse.Plot.Add.HorizontalSpan(Math.Log10(TestRunner.MidMax), Math.Log10(TestRunner.TrebleMax));
			horizontalSpan3.LineStyle.Width = 0f;
			if (showBandVerdict && !treblePassed)
			{
				horizontalSpan3.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.12);
			}
			else
			{
				horizontalSpan3.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(0.08);
			}
			VerticalLine verticalLine = PlotFreqResponse.Plot.Add.VerticalLine(Math.Log10(TestRunner.BassMax));
			verticalLine.Color = ScottPlot.Color.FromHex("#3F3F46");
			verticalLine.LineStyle.Width = 1f;
			verticalLine.LineStyle.Pattern = LinePattern.Dashed;
			VerticalLine verticalLine2 = PlotFreqResponse.Plot.Add.VerticalLine(Math.Log10(TestRunner.MidMax));
			verticalLine2.Color = ScottPlot.Color.FromHex("#3F3F46");
			verticalLine2.LineStyle.Width = 1f;
			verticalLine2.LineStyle.Pattern = LinePattern.Dashed;
			double x = (Math.Log10(TestRunner.BassMin) + Math.Log10(TestRunner.BassMax)) / 2.0;
			double x2 = (Math.Log10(TestRunner.BassMax) + Math.Log10(TestRunner.MidMax)) / 2.0;
			double x3 = (Math.Log10(TestRunner.MidMax) + Math.Log10(TestRunner.TrebleMax)) / 2.0;
			Text text = PlotFreqResponse.Plot.Add.Text((showBandVerdict && !bassPassed) ? "BASS (FAIL)" : "BASS", x, 13.5);
			text.LabelFontColor = ((showBandVerdict && !bassPassed) ? ScottPlot.Colors.Red : ScottPlot.Color.FromHex("#A1A1AA"));
			text.LabelFontSize = 10f;
			text.LabelBold = true;
			text.LabelAlignment = Alignment.UpperCenter;
			Text text2 = PlotFreqResponse.Plot.Add.Text((showBandVerdict && !midPassed) ? "MID (FAIL)" : "MIDDLE", x2, 13.5);
			text2.LabelFontColor = ((showBandVerdict && !midPassed) ? ScottPlot.Colors.Red : ScottPlot.Color.FromHex("#A1A1AA"));
			text2.LabelFontSize = 10f;
			text2.LabelBold = true;
			text2.LabelAlignment = Alignment.UpperCenter;
			Text text3 = PlotFreqResponse.Plot.Add.Text((showBandVerdict && !treblePassed) ? "TREBLE (FAIL)" : "TREBLE", x3, 13.5);
			text3.LabelFontColor = ((showBandVerdict && !treblePassed) ? ScottPlot.Colors.Red : ScottPlot.Color.FromHex("#A1A1AA"));
			text3.LabelFontSize = 10f;
			text3.LabelBold = true;
			text3.LabelAlignment = Alignment.UpperCenter;

			IReadOnlyDictionary<double, double>? standardSnapshot = GetDisplayedStandardCurve();
			if (standardSnapshot != null && standardSnapshot.Count > 0)
			{
				var orderedStandard = standardSnapshot.OrderBy(p => p.Key).ToArray();
				double[] xs = orderedStandard.Select(kv => Math.Log10(kv.Key)).ToArray();
				double[] ys = orderedStandard.Select(kv => kv.Value).ToArray();
				Scatter scatter = PlotFreqResponse.Plot.Add.Scatter(xs, ys);
				scatter.LineWidth = 2f;
				scatter.Color = ScottPlot.Color.FromHex("#EAB308");
				scatter.MarkerSize = 5f;
			}
			if (!_showUnsmoothedResponse)
				ResponseLimitPlot.Draw(PlotFreqResponse.Plot, DisplayedResponseLimitPolicy(),
					(GetDisplayedStandardCurve()?.Keys ?? Enumerable.Empty<double>()).Concat((_reviewedAutoTestCase?.FrequencyLimits ?? _frequencyLimits)?.Keys ?? Enumerable.Empty<double>()));
			IEnumerable<CriticalFrequencyZone> enumerable = _reviewedAutoTestCase != null
				? _reviewedAutoTestCase.CriticalZones
				: (_testRunner?.CriticalZones ?? Enumerable.Empty<CriticalFrequencyZone>());
			foreach (CriticalFrequencyZone item in enumerable ?? Enumerable.Empty<CriticalFrequencyZone>())
			{
				if (!(item.MinHz <= 0.0) && !(item.MaxHz <= 0.0))
				{
					HorizontalSpan horizontalSpan4 = PlotFreqResponse.Plot.Add.HorizontalSpan(Math.Log10(Math.Min(item.MinHz, item.MaxHz)), Math.Log10(Math.Max(item.MinHz, item.MaxHz)));
					horizontalSpan4.LineStyle.Width = 0f;
					horizontalSpan4.FillStyle.Color = ScottPlot.Color.FromHex("#F59E0B").WithAlpha(0.09);
				}
			}
			bool flag = _reviewedAutoTestCase?.SilentInputDetected ?? (_testRunner != null && _testRunner.SilentInputDetected);
			bool flag2 = _reviewedAutoTestCase?.FeqRepeatabilityInvalid ?? (_testRunner?.FeqRepeatabilityInvalid ?? false);

			GetSafeFrequencySnapshots(out double[] chartFreqs, out double[] chartDbValues);
			if (chartFreqs.Length > 0 && !flag2)
			{
				double[] array = chartFreqs.Select(f => Math.Log10(f)).ToArray();
				double[] array2 = chartDbValues;
				Scatter scatter4 = PlotFreqResponse.Plot.Add.Scatter(array, array2);
				scatter4.LineWidth = 3f;
				scatter4.Color = ScottPlot.Color.FromHex("#10B981");
				scatter4.MarkerSize = 6f;
				int safeCount = Math.Min(chartFreqs.Length, chartDbValues.Length);
				for (int num = 0; num < safeCount; num++)
				{
					double num2 = chartFreqs[num];
					if (num2 > 0.0 && double.IsFinite(num2))
					{
						double num3 = array2[num];
						if (!_showUnsmoothedResponse && IsFrequencyPointOverThreeDb(num2, num3))
						{
							Marker marker = PlotFreqResponse.Plot.Add.Marker(array[num], num3);
							marker.Color = ScottPlot.Colors.Red;
							marker.Size = 9f;
							marker.LineWidth = 1f;
						}
					}
				}
			}
			if (!_showUnsmoothedResponse && _sessionLineFrequencies != null && _sessionLineValues != null && _sessionLineFrequencies.Length > 0 && _sessionLineFrequencies.Length == _sessionLineValues.Length)
			{
				Scatter scatter5 = PlotFreqResponse.Plot.Add.Scatter(_sessionLineFrequencies.Select(Math.Log10).ToArray(), _sessionLineValues);
				scatter5.Color = ScottPlot.Color.FromHex("#A855F7");
				scatter5.LineWidth = 2.5f;
				scatter5.LinePattern = LinePattern.Dashed;
				scatter5.MarkerSize = 4f;
			}
			if (flag)
			{
				Text text4 = PlotFreqResponse.Plot.Add.Text("NO SIGNAL DETECTED", 2.8, 0.0);
				text4.LabelFontColor = ScottPlot.Colors.Red;
				text4.LabelFontSize = 20f;
				text4.LabelBold = true;
				text4.LabelAlignment = Alignment.MiddleCenter;
			}
			if (flag2)
			{
				Text text5 = PlotFreqResponse.Plot.Add.Text("SWEEP KHÔNG ỔN ĐỊNH", 2.8, 2.0);
				text5.LabelFontColor = ScottPlot.Colors.Red;
				text5.LabelFontSize = 18f;
				text5.LabelBold = true;
				text5.LabelAlignment = Alignment.MiddleCenter;
			}
			PlotFreqResponse.Plot.Legend.IsVisible = false;
			ConfigureLogarithmicXAxis(PlotFreqResponse.Plot);
			if (!_frequencyViewAdjustedByUser)
				ApplyFrequencyChartView(fitVerticalData: true);
			PlotFreqResponse.Refresh();
		}
		catch
		{
		}
	}

	private void QueueFrequencyChartRefresh()
	{
		_frequencyChartRefreshTimer.Stop();
		_frequencyChartRefreshTimer.Start();
	}

	private void UpdateThdFftChart(double toneFrequency, double[] frequencies, double[] magnitudes, double thdPercent, bool validated)
		=> RenderThdFftChart(toneFrequency, frequencies, magnitudes, thdPercent, validated, _testRunner != null && _testRunner.IsRubBuzzTest);

	private void RenderThdFftChart(double toneFrequency, double[] frequencies, double[] magnitudes, double thdPercent, bool validated, bool isRubBuzz)
	{
		WpfPlot wpfPlot = ((toneFrequency <= 0.0 || toneFrequency < 500.0) ? PlotThdFft : ((toneFrequency < 2500.0) ? PlotThdFft1k : PlotThdFft4k));
		if (toneFrequency <= 0.0)
		{
			ClearThdPlots();
		}
		wpfPlot.Plot.Clear();
		wpfPlot.Plot.Title(isRubBuzz ? $"Rub & Buzz — {toneFrequency:0} Hz" : ((toneFrequency <= 0.0) ? "Nhiễu" : FormatThdToneTitle(toneFrequency)));
		bool flag = true;
		if (frequencies != null && frequencies.Length != 0)
		{
			double[] array = magnitudes.Select((double m) => Math.Max(-100.0, 20.0 * Math.Log10(m + 1E-09))).ToArray();
			if (array.Max() > -70.0)
			{
				flag = false;
			}
			Scatter scatter = wpfPlot.Plot.Add.Scatter(frequencies, array);
			scatter.LineWidth = 1.5f;
			scatter.Color = ScottPlot.Color.FromHex("#3B82F6");
			scatter.MarkerSize = 0f;
		}
		if (double.IsFinite(thdPercent) && toneFrequency > 0.0)
		{
			string text = FormatThdValueLabel(thdPercent, validated, isRubBuzz);
			Text text2 = wpfPlot.Plot.Add.Text(text, 5000.0, -15.0);
			text2.LabelFontColor = ScottPlot.Color.FromHex("#F4F4F5");
			text2.LabelFontSize = 14f;
			text2.LabelBold = true;
		}
		if (toneFrequency > 0.0 && !double.IsFinite(thdPercent))
		{
			Text text3 = wpfPlot.Plot.Add.Text(flag ? "NO SIGNAL — THD: N/A" : "THD: N/A", 5000.0, -15.0);
			text3.LabelFontColor = ScottPlot.Color.FromHex("#F4F4F5");
			text3.LabelFontSize = 14f;
			text3.LabelBold = true;
		}
		if (isRubBuzz)
		{
			HorizontalSpan horizontalSpan = wpfPlot.Plot.Add.HorizontalSpan(1000.0, 8000.0);
			horizontalSpan.LineStyle.Width = 0f;
			horizontalSpan.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.07999999821186066);
			Text text4 = wpfPlot.Plot.Add.Text("Dải rè/rung (1–8 kHz)", 4500.0, -75.0);
			text4.LabelFontColor = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.6000000238418579);
			text4.LabelFontSize = 11f;
			text4.LabelBold = true;
			text4.LabelAlignment = Alignment.MiddleCenter;
		}
		double right = ((toneFrequency > 0.0) ? Math.Min(24000.0, Math.Max(10000.0, toneFrequency * 6.0)) : 10000.0);
		wpfPlot.Plot.Axes.SetLimits(0.0, right, -90.0, 0.0);
		wpfPlot.Refresh();
	}

	private static string FormatThdToneTitle(double toneFrequency)
	{
		if (!(toneFrequency >= 1000.0))
		{
			return $"{toneFrequency:0} Hz";
		}
		return $"{toneFrequency / 1000.0:0.#} kHz";
	}

	private static string FormatThdValueLabel(double percent, bool validated, bool rubBuzz)
	{
		if (!rubBuzz)
		{
			return $"THD: {percent:F3}%";
		}
		return $"Rub & Buzz: {percent:F3}%";
	}

	private void InitThdInspectFrequencyCombo()
	{
		if (ComboThdInspectFreq != null)
		{
			ComboThdInspectFreq.Items.Clear();
			string[] array = new string[14]
			{
				"1000 Hz (Mặc định)", "80 Hz", "100 Hz", "200 Hz", "250 Hz", "500 Hz", "1000 Hz", "2000 Hz", "3000 Hz", "4000 Hz",
				"5000 Hz", "6300 Hz", "8000 Hz", "10000 Hz"
			};
			string[] array2 = array;
			string[] array3 = array2;
			string[] array4 = array3;
			foreach (string newItem in array4)
			{
				ComboThdInspectFreq.Items.Add(newItem);
			}
			ComboThdInspectFreq.SelectedIndex = 0;
		}
	}

	private void ComboThdInspectFreq_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (ComboThdInspectFreq?.SelectedItem == null)
		{
			return;
		}
		string text = ComboThdInspectFreq.SelectedItem.ToString() ?? "";
		double num = ParseInspectFrequency(text);
		if (num > 0.0)
		{
			_selectedThdInspectFrequencyHz = num;
            // Inspecting another frequency never changes the 1000 Hz acceptance tone.
			UpdateThdInspection(num);
		}
	}

	private static double ParseInspectFrequency(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return 1000.0;
		}
		string s = text.Replace("Hz", "").Replace("(Mặc định)", "").Replace("k", "000")
			.Replace("K", "000")
			.Trim();
		if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0.0)
		{
			return result;
		}
		if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out result) && result > 0.0)
		{
			return result;
		}
		return 1000.0;
	}

	private void RefreshThdVerdictAfterMeasurement()
	{
		if (_testRunner.IsRubBuzzTest)
		{
			TxtThdStatus.Text = _testRunner.Steps.ElementAtOrDefault(2)?.Details ?? "Không áp dụng cho bài đo rè/rung";
			TxtSweepDistortionMetrics.Text = _testRunner.Steps.ElementAtOrDefault(1)?.Details ?? "Chưa có bản thu rè/rung.";
			TxtThdVerdictBadge.Text = _testRunner.RubBuzzAcquisitionInvalid ? "RÈ/RUNG CHƯA HỢP LỆ" : "RÈ/RUNG · XEM CHI TIẾT";
			return;
		}
		if (_testRunner.LastToneQualities.Count > 0 || _testRunner.LastSweepResult != null)
		{
			UpdateThdInspection(_selectedThdInspectFrequencyHz);
			ToneQualityMetrics? measuredTone = _testRunner.GetToneQualityAtFrequency(_selectedThdInspectFrequencyHz);
			if (measuredTone != null && double.IsFinite(measuredTone.ThdPercent))
			{
				TxtThdStatus.Text = (_testRunner.ThdAcquisitionInvalid || !measuredTone.IsValid)
					? $"{_selectedThdInspectFrequencyHz:F0} Hz · THD chưa xác định — cần đo lại"
					: $"{_selectedThdInspectFrequencyHz:F0} Hz · THD {measuredTone.ThdPercent:F3}%";
				TxtThdStatus.ToolTip = _testRunner.Steps.ElementAtOrDefault(2)?.Details;
			}
			return;
		}
		TestStep inputStep = _testRunner.Steps.ElementAtOrDefault(0);
		TxtSweepDistortionMetrics.Text = ((inputStep?.Status == "Fail") ? inputStep.Details : (_testRunner.Steps.ElementAtOrDefault(2)?.Details ?? "Chưa có bản thu THD."));
		TxtThdVerdictBadge.Text = (_testRunner.SilentInputDetected ? "KHÔNG ĐỦ TÍN HIỆU" : "THD CHƯA ĐO");
		BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
		BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
		TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
	}

	private void UpdateThdInspection(double frequencyHz)
	{
		if (_reviewedAutoTestCase is { Config.SpeakerStage: not null } electronic) { ShowC360Review(electronic, frequencyHz); return; }
		double num = TxtThdLimit == null ? 0.5 : ParseDoubleSafe(TxtThdLimit.Text, 0.5);
		if (_testRunner?.LastSweepResult != null && _testRunner.GetToneQualityAtFrequency(frequencyHz) == null)
		{
			SweepDistortionSummary summary = _testRunner.BuildSweepDistortionSummary(_testRunner.LastSweepResult, frequencyHz);
			UpdateSweepDistortionMetrics(summary);
		}
		else
		{
			if (_testRunner?.LastToneQualities == null || _testRunner.LastToneQualities.Count <= 0)
			{
				return;
			}
			ToneQualityMetrics toneQualityAtFrequency = _testRunner.GetToneQualityAtFrequency(frequencyHz);
			if (toneQualityAtFrequency != null && (!toneQualityAtFrequency.IsValid || !double.IsFinite(toneQualityAtFrequency.ThdPercent)))
			{
				TxtSweepDistortionMetrics.Text = _testRunner.Steps.ElementAtOrDefault(2)?.Details ?? "Bản thu THD không hợp lệ.";
				TxtThdVerdictBadge.Text = "THD KHÔNG HỢP LỆ";
				PlotThdFft1k.Plot.Clear(); PlotThdFft1k.Refresh();
				BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
				BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			}
			else if (toneQualityAtFrequency != null)
			{
				bool diagnostic = !toneQualityAtFrequency.IsValid;
				bool flag = !diagnostic && toneQualityAtFrequency.ThdPercent <= num;
				string verdict = (diagnostic ? "THAM KHẢO · CHƯA HỢP LỆ" : (flag ? "✓ ĐẠT (PASS)" : (IsFirstAutoTestAttempt ? "CHƯA ỔN ĐỊNH — CẦN ĐO LẠI" : "✗ VƯỢT (FAIL)")));
				StringBuilder stringBuilder = new StringBuilder();
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder3 = stringBuilder2;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(37, 1, stringBuilder2);
				handler.AppendLiteral("Tại tần số cơ bản: ");
				handler.AppendFormatted(toneQualityAtFrequency.FundamentalFrequencyHz, "F1");
					handler.AppendLiteral(toneQualityAtFrequency.AnalysisSource == "LOG_SWEEP_FDW"
						? " Hz (Log sweep · cửa sổ hài theo tần số)" : " Hz (Tone rời rạc)");
				stringBuilder3.AppendLine(ref handler);
				stringBuilder.AppendLine();
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder2);
				handler.AppendLiteral("Fundamental —  —  —  ");
				handler.AppendFormatted(toneQualityAtFrequency.SignalLevelDbFs, "F1");
				handler.AppendLiteral(" dBFS");
				stringBuilder4.AppendLine(ref handler);
				if (toneQualityAtFrequency.Harmonics != null)
				{
					foreach (HarmonicMeasurement item in toneQualityAtFrequency.Harmonics.OrderBy((HarmonicMeasurement h) => h.Order))
					{
						double value = 100.0 * Math.Pow(10.0, item.LevelDbc / 20.0);
						stringBuilder2 = stringBuilder;
						StringBuilder stringBuilder5 = stringBuilder2;
						handler = new StringBuilder.AppendInterpolatedStringHandler(26, 3, stringBuilder2);
						handler.AppendFormatted(item.Order);
						handler.AppendLiteral("th Harmonic —  —  — ");
						handler.AppendFormatted(value, "F4");
						handler.AppendLiteral(" % (");
						handler.AppendFormatted(item.LevelDbc, "F1");
						handler.AppendLiteral(" dBc)");
						stringBuilder5.AppendLine(ref handler);
					}
				}
				stringBuilder.AppendLine();
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder6 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(38, 3, stringBuilder2);
				handler.AppendLiteral("THD —  —  —  —  —  —  —  ");
				handler.AppendFormatted(toneQualityAtFrequency.ThdPercent, "F4");
				handler.AppendLiteral(" % — [Giới hạn <= ");
				handler.AppendFormatted(num, "F3");
				handler.AppendLiteral("%] ");
				handler.AppendFormatted(verdict);
				stringBuilder6.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
				handler.AppendLiteral("THD+N —  —  —  —  —  —  ");
				handler.AppendFormatted(toneQualityAtFrequency.ThdNPercent, "F4");
				handler.AppendLiteral(" %");
				stringBuilder7.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder8 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(32, 2, stringBuilder2);
				handler.AppendLiteral("SINAD —  —  —  —  —  —  ");
				handler.AppendFormatted(toneQualityAtFrequency.SinadDb, "F1");
				handler.AppendLiteral(" dB | SNR: ");
				handler.AppendFormatted(toneQualityAtFrequency.SnrDb, "F1");
				handler.AppendLiteral(" dB");
				stringBuilder8.AppendLine(ref handler);
				stringBuilder.AppendLine();
				stringBuilder.AppendLine(verdict);
				TxtSweepDistortionMetrics.Text = stringBuilder.ToString();
				TxtSweepDistortionMetrics.Foreground = (diagnostic ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21)) : (flag ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(228, 228, 231)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113))));
				if (diagnostic)
				{
					TxtThdVerdictBadge.Text = $"{toneQualityAtFrequency.ThdPercent:F3}% (THAM KHẢO)";
					BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
					BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
					TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				}
				else if (flag)
				{
					TxtThdVerdictBadge.Text = $"{toneQualityAtFrequency.ThdPercent:F3}% (PASS)";
					BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 83, 45));
					BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
					TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
				}
				else
				{
					TxtThdVerdictBadge.Text = $"{toneQualityAtFrequency.ThdPercent:F3}% ({(IsFirstAutoTestAttempt ? "CẦN ĐO LẠI" : "FAIL")})";
					BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 10, 10));
					BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
					TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
				}
				if (toneQualityAtFrequency.SpectrumFrequenciesHz != null && toneQualityAtFrequency.SpectrumFrequenciesHz.Length != 0)
				{
					UpdateThdFftChart(toneQualityAtFrequency.FundamentalFrequencyHz, toneQualityAtFrequency.SpectrumFrequenciesHz, toneQualityAtFrequency.SpectrumMagnitudes, toneQualityAtFrequency.ThdPercent, !diagnostic);
				}
			}
			else
			{
				string value2 = string.Join(", ", _testRunner.LastToneQualities.Keys.Select((double k) => $"{k:0} Hz"));
				TxtSweepDistortionMetrics.Text = $"Chưa có dữ liệu tone rời rạc tại {frequencyHz:0} Hz.\nCác tần số tone đã đo: {value2}\nHoặc đo Log Sweep để kiểm tra toàn bộ dải tần 20 Hz - 20 kHz.";
				TxtThdVerdictBadge.Text = "CHƯA ĐO";
				BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 25, 23));
				BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 53, 15));
				TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
			}
		}
	}

	private void UpdateSweepDistortionMetrics(SweepDistortionSummary summary) => UpdateSweepDistortionMetricsWithLimit(summary, null, false);

	private void UpdateSweepDistortionMetricsWithLimit(SweepDistortionSummary summary, double? limitOverride, bool finalVerdict)
	{
		var inspectedConfig = _reviewedAutoTestCase?.Config ?? _appliedTestConfig;
        double num = limitOverride ?? inspectedConfig?.ThdLimitAt(summary.ReferenceFrequencyHz) ?? (TxtThdLimit == null ? 0.5 : ParseDoubleSafe(TxtThdLimit.Text, 0.5));
		bool firstAttempt = !finalVerdict && IsFirstAutoTestAttempt;
		if (!summary.IsValid) TxtThdStatus.Text = "THD chưa xác định — cần đo lại";
		TxtSweepDistortionMetrics.Text = FormatSweepDistortionMetrics(summary, num);
        bool diagnosticOnly = summary.IsSweepWide || !(inspectedConfig?.EvaluatesThdAt(summary.ReferenceFrequencyHz) ?? Math.Abs(summary.ReferenceFrequencyHz - 1000) < .01);
        if (diagnosticOnly)
        {
            TxtSweepDistortionMetrics.Text = TxtSweepDistortionMetrics.Text.Replace("✓ ĐẠT (PASS)", "CHỈ GHI CHI TIẾT").Replace("✗ VƯỢT (FAIL)", "CHỈ GHI CHI TIẾT");
            TxtThdVerdictBadge.Text = double.IsFinite(summary.ThdPercent) ? $"{summary.ThdPercent:F3}% · CHI TIẾT" : "CHƯA CÓ DỮ LIỆU";
            TxtThdVerdictBadge.Foreground = Brushes.LightGray; BorderThdVerdictBadge.BorderBrush = Brushes.Gray;
            BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
            if (double.IsFinite(summary.ThdPercent)) PlotHarmonicBars(summary);
            return;
        }
		if (firstAttempt) TxtSweepDistortionMetrics.Text = TxtSweepDistortionMetrics.Text.Replace("FAIL", "CẦN ĐO LẠI").Replace("KHÔNG ĐẠT", "CHƯA ỔN ĐỊNH");
		bool flag = summary.IsValid && double.IsFinite(summary.ThdPercent) && summary.ThdPercent <= num;
		TxtSweepDistortionMetrics.Foreground = (summary.IsValid ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(228, 228, 231)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21)));
		if (double.IsFinite(summary.ThdPercent))
		{
			if (!summary.IsValid)
			{
				TxtThdVerdictBadge.Text = "THD CHƯA XÁC ĐỊNH";
				BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
				BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			}
			else if (flag)
			{
				TxtThdVerdictBadge.Text = $"{summary.ThdPercent:F3}% (PASS)";
				BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 83, 45));
				BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
				TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
			}
			else
			{
				TxtThdVerdictBadge.Text = $"{summary.ThdPercent:F3}% ({(firstAttempt ? "CẦN ĐO LẠI" : "FAIL")})";
				BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 10, 10));
				BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
				TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			}
			if (summary.IsValid) PlotHarmonicBars(summary);
			else { PlotThdFft1k.Plot.Clear(); PlotThdFft1k.Refresh(); }
		}
		else
		{
			TxtThdVerdictBadge.Text = "THD KHÔNG HỢP LỆ";
			PlotThdFft1k.Plot.Clear(); PlotThdFft1k.Refresh();
			BorderThdVerdictBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 25, 23));
			BorderThdVerdictBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 53, 15));
			TxtThdVerdictBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
		}
	}

	private void PlotHarmonicBars(SweepDistortionSummary summary)
	{
		PlotThdFft1k.Plot.Clear();
		PlotThdFft1k.Plot.Title(summary.IsSweepWide ? $"Năng lượng méo H2-H9 toàn Log-Sweep — THD: {summary.ThdPercent:F3}%" : $"Phổ méo hài Log-Sweep tại {summary.ReferenceFrequencyHz:0} Hz — THD: {summary.ThdPercent:F3}%");
		PlotThdFft1k.Plot.Axes.Left.Label.Text = "Mức tương đối (dBc)";
		PlotThdFft1k.Plot.Axes.Bottom.Label.Text = (summary.IsSweepWide ? "Bậc hài" : "Tần số hài (Hz)");
		List<double> list = new List<double> { summary.IsSweepWide ? 1.0 : summary.ReferenceFrequencyHz };
		List<double> list2 = new List<double> { 0.0 };
		for (int i = 2; i <= 9; i++)
		{
			if (summary.HarmonicPercent.TryGetValue(i, out var value) && double.IsFinite(value) && value > 0.0)
			{
				list.Add(summary.IsSweepWide ? ((double)i) : (summary.ReferenceFrequencyHz * (double)i));
				list2.Add(20.0 * Math.Log10(value / 100.0));
			}
		}
		if (list.Count > 0)
		{
			Scatter scatter = PlotThdFft1k.Plot.Add.Scatter(list.ToArray(), list2.ToArray());
			scatter.LineWidth = 2f;
			scatter.Color = ScottPlot.Color.FromHex("#10B981");
			scatter.MarkerSize = 7f;
		}
		double right = (summary.IsSweepWide ? 10.0 : Math.Min(24000.0, Math.Max(10000.0, summary.ReferenceFrequencyHz * 10.0)));
		PlotThdFft1k.Plot.Axes.SetLimits(0.0, right, -100.0, 10.0);
		PlotThdFft1k.Refresh();
	}

	private static string FormatSweepDistortionMetrics(SweepDistortionSummary summary, double limit = 0.5)
	{
		if (!summary.IsValid)
			return $"THD chưa xác định — bản thu chưa đủ điều kiện.\n{summary.Status.Replace("THD thô chỉ để tham khảo; ", "")}";
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(summary.IsSweepWide ? $"Toàn Log-Sweep: {summary.StartFrequencyHz:0}-{summary.EndFrequencyHz:0} Hz — {summary.FrequencyPointCount} điểm" : $"Tại tần số cơ bản: {summary.ReferenceFrequencyHz:0} Hz (Tách từ Log-Sweep)");
		stringBuilder.AppendLine();
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(21, 2, stringBuilder2);
		handler.AppendLiteral("Fundamental —  —  —  ");
		handler.AppendFormatted(Level(summary.FundamentalLevelDb));
		handler.AppendLiteral(" (");
		handler.AppendFormatted(summary.IsSweepWide ? "RMS năng lượng toàn sweep" : "transfer tương đối");
		handler.AppendLiteral(")");
		stringBuilder3.AppendLine(ref handler);
		for (int i = 2; i <= 9; i++)
		{
			bool flag = false;
			bool flag2 = false;
			if (1 == 0)
			{
			}
			string text = i switch
			{
				2 => "2nd Harmonic", 
				3 => "3rd Harmonic", 
				_ => $"{i}th Harmonic", 
			};
			if (1 == 0)
			{
			}
			string text2 = text;
			bool flag3 = false;
			string text3 = text2;
			bool flag4 = false;
			string value = text3;
			double num = (summary.HarmonicPercent.TryGetValue(i, out var value2) ? value2 : double.NaN);
			double num2 = ((double.IsFinite(num) && num > 0.0) ? (20.0 * Math.Log10(num / 100.0)) : double.NaN);
			string value3 = (double.IsFinite(num2) ? $"({num2:F1} dBc)" : "");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(2, 3, stringBuilder2);
			handler.AppendFormatted<string>(value, -17);
			handler.AppendLiteral(" ");
			handler.AppendFormatted(Percent(num));
			handler.AppendLiteral(" ");
			handler.AppendFormatted(value3);
			stringBuilder4.AppendLine(ref handler);
		}
		stringBuilder.AppendLine();
		bool flag5 = double.IsFinite(summary.ThdPercent) && summary.ThdPercent <= limit;
		string value4 = !summary.IsValid ? "THAM KHẢO · CẦN ĐO LẠI" : !double.IsFinite(summary.ThdPercent) ? "" : flag5 ? "✓ ĐẠT (PASS)" : "✗ VƯỢT (FAIL)";
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(23, 4, stringBuilder2);
		handler.AppendLiteral("THD ");
		handler.AppendFormatted<string>(summary.IncludedHarmonics, -11);
		handler.AppendLiteral(" ");
		handler.AppendFormatted(Percent(summary.ThdPercent));
		handler.AppendLiteral(" — [Giới hạn <= ");
		handler.AppendFormatted(limit, "F2");
		handler.AppendLiteral("%] ");
		handler.AppendFormatted(value4);
		stringBuilder5.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(26, 2, stringBuilder2);
		handler.AppendLiteral("Noise Floor —  —  —  ");
		handler.AppendFormatted(Percent(summary.NoiseFloorPercent));
		handler.AppendLiteral(" (");
		handler.AppendFormatted(summary.NoiseFloorDbFs, "F1");
		handler.AppendLiteral(" dBFS)");
		stringBuilder6.AppendLine(ref handler);
		stringBuilder.AppendLine();
		stringBuilder.Append(summary.Status);
		return stringBuilder.ToString();
		static string Level(double num3)
		{
			return double.IsFinite(num3) ? $"{num3:F1} dB" : "N/A";
		}
		static string Percent(double num3)
		{
			return double.IsFinite(num3) ? $"{num3:F4} %" : "N/A";
		}
	}

	private void ClearLocalMeasurementDiagnosis()
	{
		TxtFailureDiagnosis.Text = "";
		TxtFailureDiagnosis.Visibility = Visibility.Collapsed;
	}

    public static bool ShouldDeferAuxiliaryViews(bool executingSuite, bool detailsOpen) => executingSuite && !detailsOpen;

	private void RenderLocalMeasurementDiagnosis()
	{
		if (ShouldDeferAuxiliaryViews(_isExecutingAutoSuite, _measurementDetailsWindow != null)) { ClearLocalMeasurementDiagnosis(); return; }
		// This text lives only in the routing view: do not attach it to a test
		// case, report, chart export or server payload.
		if (_testRunner.IsRubBuzzTest)
		{
			ClearLocalMeasurementDiagnosis();
			return;
		}
		var sweep = _testRunner.LastSweepResult;
		var response = new List<DiagnosisResponsePoint>();
		for (int i = 0; i < Math.Min(_freqs.Count, _dbValues.Count); i++)
		{
			if (TryGetComparableStandardTarget(_freqs[i], out double target))
			{
				double tolerance = _testRunner.FreqResponseToleranceDb;
				if (ProductionMeasurement.TryGetFrequencyLimit(_frequencyLimits, _freqs[i], out FrequencyLimitPoint limit))
					tolerance = _dbValues[i] < target ? target - limit.LowerDb : limit.UpperDb - target;
				response.Add(new DiagnosisResponsePoint(_freqs[i], _dbValues[i], target, tolerance));
			}
		}
		double DistortionLimit(double frequency) => frequency == _testRunner.BassThdFrequencyHz
			? _testRunner.BassThdLimitPercent ?? _testRunner.ThdLimitPercent
			: frequency == _testRunner.TrebleThdFrequencyHz
			? _testRunner.TrebleThdLimitPercent ?? _testRunner.ThdLimitPercent
			: _testRunner.MidThdLimitPercent ?? _testRunner.ThdLimitPercent;
		var evidence = new MeasurementDiagnosisEvidence
		{
			SignalTooWeak = _testRunner.SilentInputDetected,
			Clipped = _testRunner.InputClippingDetected || (sweep?.IsClipped ?? false),
			SignalDbFs = _testRunner.LastSignalLevelDbFs,
			NoiseDbFs = _testRunner.LastNoiseAssessment?.DutMicrophone?.TotalRmsDb ?? sweep?.NoiseFloorDbFs ?? double.NaN,
			SnrDb = sweep?.EstimatedSnrDb ?? _testRunner.LastToneQuality?.SnrDb ?? double.NaN,
			ResponseValid = _freqs.Count >= 3 && !_testRunner.FeqRepeatabilityInvalid
				&& string.IsNullOrWhiteSpace(_testRunner.LastFeqInvalidReason) && sweep?.Validity != "INVALID",
			AcquisitionIssue = _testRunner.LastFeqInvalidReason,
			HasComparableReference = HasComparableStandardCurve(),
			NormalizedAtOneKilohertz = _testRunner.NormalizeFrequencyResponseToOneKilohertz,
			MeasuredOneKilohertzDbFs = _testRunner.LastOneKilohertzLevelDbFs,
			ReferenceOneKilohertzDbFs = _standardOneKilohertzLevelDbFs ?? double.NaN,
			HasEffectEvidence = sweep?.ImpulseEffects is { IsValid: true },
			EffectClassificationReliable = sweep?.ImpulseEffects?.CanClassify(_testRunner.ImpulseEffectsSettings) == true,
			EchoDetected = sweep?.ImpulseEffects?.EchoDetected == true,
			ReverbDetected = sweep?.ImpulseEffects?.ReverbDetected == true,
			EchoDelayMs = sweep?.ImpulseEffects?.EchoDelayMs ?? double.NaN,
			EffectObservation = sweep?.ImpulseEffects?.Describe(_testRunner.ImpulseEffectsSettings) ?? "",
			ReliableImpulse = sweep is { HasPlausibleDirectArrival: true, Validity: not "INVALID", IsClipped: false },
			Polarity = sweep?.Polarity ?? "UNKNOWN",
			DistortionValid = !_testRunner.ThdAcquisitionInvalid && _testRunner.LastToneQualities.Count > 0,
			Response = response,
			ResponseLimits = DisplayedResponseLimitSettings(),
			ResponseAssessment = DisplayedResponseLimitPolicy().Assess(response.GroupBy(p => p.FrequencyHz).ToDictionary(g => g.Key, g => g.Last().MeasuredDbFs), _testRunner.AllowedFrequencyOutlierPercent),
			Distortion = _testRunner.LastToneQualities.Select(p => new DiagnosisDistortionPoint(p.Key, p.Value.ThdPercent, DistortionLimit(p.Key))).ToArray(),
			// Sweep harmonic extraction does not measure THD+N. Only use tone data here.
			ToneThdPercent = sweep == null ? _testRunner.LastToneQuality?.ThdPercent ?? double.NaN : double.NaN,
			ToneThdNPercent = sweep == null ? _testRunner.LastToneQuality?.ThdNPercent ?? double.NaN : double.NaN
		};
		var notes = SpeakerMeasurementDiagnosis.DescribeMeasurement(evidence).ToList();
		if (!string.IsNullOrEmpty(_testRunner.LastPreflightWarning)) notes.Insert(0, _testRunner.LastPreflightWarning);
		TxtFailureDiagnosis.Text = "Tổng quan bản thu:\n• " + string.Join("\n• ", notes);
		TxtFailureDiagnosis.Visibility = Visibility.Visible;
	}

	private void SetFinalVerdict(bool success)
	{
		BtnStart.IsEnabled = true;
		BtnSaveStandardReference.IsEnabled = true;
		BtnNoiseTest.IsEnabled = true;
		BtnSaveStandard.IsEnabled = _freqs.Count > 0 && !_testRunner.FeqRepeatabilityInvalid
			&& string.IsNullOrWhiteSpace(_testRunner.LastFeqInvalidReason) && !_testRunner.InputClippingDetected;
		if ((_testRunner.IsRubBuzzTest && (AutoTestAcquisitionInvalid || _testRunner.Steps.ElementAtOrDefault(0)?.Status != "Pass"))
			|| (!_testRunner.IsRubBuzzTest && (_testRunner.Steps.ElementAtOrDefault(0)?.Status != "Pass"
			|| !string.IsNullOrWhiteSpace(_testRunner.LastFeqInvalidReason)
			|| _testRunner.FeqRepeatabilityInvalid || _testRunner.InputClippingDetected
			|| _testRunner.ThdAcquisitionInvalid || _freqs.Count == 0)))
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
			BorderVerdict.BorderBrush = Brushes.Gold;
			LblVerdict.Foreground = Brushes.Gold;
			LblVerdict.Text = "CHƯA CÓ PHÉP ĐO HỢP LỆ — KHÔNG KẾT LUẬN";
			TxtFreqStatus.Text = _testRunner.RubBuzzAcquisitionInvalid ? _testRunner.Steps[1].Details : !string.IsNullOrWhiteSpace(_testRunner.LastFeqInvalidReason)
				? "CHƯA ĐO — " + _testRunner.LastFeqInvalidReason
				: "CHƯA ĐO — " + (_testRunner.Steps.ElementAtOrDefault(0)?.Details ?? "Chưa thu được dữ liệu.");
			return;
		}
		if (_testRunner.MissingFrequencyLimits)
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			LblVerdict.Text = "CHƯA CÓ LINE CHUẨN — KHÔNG KẾT LUẬN";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			return;
		}
		if (_testRunner.LastResponseLimitAssessment?.InvalidKind == ResponseLimitInvalidKind.NothingToEvaluate)
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			LblVerdict.Text = "CẤU HÌNH KHOẢNG HÕM LOẠI HẾT ĐIỂM XÉT — KHÔNG KẾT LUẬN";
			TxtFreqStatus.Text = _testRunner.Steps.ElementAtOrDefault(1)?.Details ?? "Cấu hình khoảng xét hõm đã loại hết điểm đánh giá.";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			return;
		}
		if (_testRunner != null && _testRunner.SilentInputDetected)
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 27, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
			LblVerdict.Text = "KHÔNG CÓ TÍN HIỆU (NO SIGNAL)";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			TxtFreqStatus.Text = "KHÔNG CÓ TÍN HIỆU";
			UpdateFreqResponseChart();
			ClearThdPlots();
			InitializeConfiguredThdPlots();
			return;
		}
		TestRunner testRunner = _testRunner;
		if (testRunner != null && testRunner.ThdAcquisitionInvalid && !double.IsFinite(testRunner.LastMeasuredThdPercent))
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			LblVerdict.Text = "THD CHƯA HỢP LỆ — KHÔNG KẾT LUẬN";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
		}
		else if (success)
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 95, 70));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
			LblVerdict.Text = "ĐẠT";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153));
		}
		else
		{
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 27, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
			LblVerdict.Text = "KHÔNG ĐẠT";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
		}
	}

	private void AppendLog(string source, string message)
	{
		string value = DateTime.Now.ToString("HH:mm:ss.fff");
		Debug.WriteLine($"[{value}] [{source}] {message}");
		if (source == "Thời gian chuẩn bị") TxtMeasurementTiming.Text = "";
		if (source == "Thời gian kết quả") TxtMeasurementTiming.Text = message;
		if (source.StartsWith("Thời gian", StringComparison.Ordinal))
		{
			try
			{
				string entry = System.Text.Json.JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, source, message });
				File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "measurement-timing.jsonl"), entry + Environment.NewLine);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
		}
	}

	private async void BtnStart_Click(object sender, RoutedEventArgs e)
	{
		if (IsTestingBusy) return;
		_allowInlineEffectImpulse = false;
		_reviewedAutoTestCase = null;
		ShowInlineImpulse(null);
		if (C360ModelDefinition.Matches(_activeInOutConfig)) { await RunC360ModelAutoTestAsync(singleTest: _appliedTestConfig ?? _autoTestCases.FirstOrDefault()?.Config); return; }
		CloseRoutingScope();
		using IDisposable? workflow = TryBeginRoutingWorkflow();
		if (workflow == null) return;
		_leaveAudioReleasedAfterAutoTest = false;
		BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
		BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
		LblVerdict.Text = "ĐANG ĐO...";
		LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
		if ((Application.Current.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Bộ đo âm học nâng cao đang thực hiện phép đo. Vui lòng chờ hoàn tất trước khi bắt đầu đo line.", "Thiết bị đang bận", ModernMessageBox.MessageBoxType.Warning);
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
			LblVerdict.Text = "ĐANG CHỜ...";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
			return;
		}
		if (!_audioEngine.UseExclusivePlayback && AudioEngine.IsWindowsMonoAudioEnabled())
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Windows đang bật Mono audio nên shared mode sẽ trộn Left và Right. Hãy tắt Settings > Accessibility > Audio > Mono audio trước khi đo.", "Không thể đo riêng Left / Right", ModernMessageBox.MessageBoxType.Error);
			AppendLog("Đo line", "Đã chặn phép đo vì Windows Accessibility Mono audio đang bật; kết quả Left/Right sẽ bị trộn.");
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
			LblVerdict.Text = "ĐANG CHỜ...";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
			return;
		}
		_testRunner.PlaybackLevelDbFs = PlaybackLevelDbfs;
		AppendLog("Auto Test", $"Thiết lập mức phát chuẩn: {PlaybackLevelDbfs:F1} dBFS cho toàn bộ bài test.");

		_testRunner.ReuseSuiteNoiseFloor = false;
		_testRunner.SuiteSharedNoiseAssessment = null;
		_testRunner.SuiteSharedNoiseFloorByFrequency = new Dictionary<double, double>();
		_testRunner.SuiteSharedNoiseFloorDbFs = null;

		ApplyEnteredMeasurementDurations();
		SaveConfig();
		_discardCurrentMeasurementRequested = false;
		lock (_freqs)
		{
			_freqs.Clear();
			_dbValues.Clear();
		}
		PlotFreqResponse.Plot.Clear();
		ClearThdPlots();
		InitCharts();
		TxtFreqStatus.Text = "";
		TxtOneKilohertzLevel.Text = "Mức thu 1 kHz: đang đo...";
		TxtOneKilohertzToneLevel.Text = "Tone 1 kHz: chưa đo";
		TxtThdStatus.Text = "";
		BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
		BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
		LblVerdict.Text = "ĐANG ĐO...";
		LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
		BtnStart.IsEnabled = false;
		BtnStartAutoTest.IsEnabled = false;
		BtnSaveStandardReference.IsEnabled = false;
		BtnNoiseTest.IsEnabled = false;
		BtnSaveStandard.IsEnabled = false;
		_testRunner.FreqResponseToleranceDb = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
		_testRunner.ThdLimitPercent = ParseDoubleSafe(TxtThdLimit.Text, 0.5);
		_testRunner.IsRubBuzzTest = false;
		try
		{
			ConfigureProductionMeasurement(_appliedTestConfig);
		}
		catch (Exception ex)
		{
			TxtFreqStatus.Text = ex.Message;
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 10, 10));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
			LblVerdict.Text = "LỖI CẤU HÌNH — CHƯA ĐO";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			BtnStart.IsEnabled = true;
			UpdateAutoTestStartButton();
			BtnSaveStandardReference.IsEnabled = true;
			BtnNoiseTest.IsEnabled = true;
			BtnSaveStandard.IsEnabled = _freqs.Count >= 2;
			AppendLog("Đo line", "Lỗi cấu hình: " + ex.Message);
			return;
		}
		CheckAndLoadStandardDevice();
		_testRunner.StandardCurve = _standardCurve;
		MMDevice selectedPlaybackDevice = SelectedPlaybackDevice;
		MMDevice mMDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;
		if (selectedPlaybackDevice == null || mMDevice == null)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Chưa chọn đủ thiết bị phát và thu âm.", "Thiếu thiết bị", ModernMessageBox.MessageBoxType.Warning);
			BtnStart.IsEnabled = true;
			BtnSaveStandardReference.IsEnabled = true;
			BtnNoiseTest.IsEnabled = true;
		}
		else
		{
			await RunTrackedTestAsync(selectedPlaybackDevice, mMDevice);
		}
	}

	private void BtnCancel_Click(object sender, RoutedEventArgs e)
	{
		CancelAndDiscardCurrentMeasurement();
	}

	public void CancelAndDiscardCurrentMeasurement()
	{
        ClearPendingQaCompletion();
		if (_measurementDetailsWindow?.Impedance?.IsBusy == true) { _measurementDetailsWindow?.Impedance.Cancel(); return; }
		_c360ModelCancellation?.Cancel();
		_autoDiagnosticsCancellation?.Cancel();
		if (_measurementDetailsWindow?.IsDiagnosticRunning == true)
		{
			_measurementDetailsWindow.CancelDiagnostic();
			AppendLog("Chẩn đoán riêng", "Dừng bài tone riêng; giữ kết quả sweep/line đã đo.");
			return;
		}
		bool isTestingBusy = IsTestingBusy;
		_c360AutoTestResumeSession = null;
		_discardCurrentMeasurementRequested = true;
		_reviewedAutoTestCase = null;
		_lineAcquisitionValues = null;
		_rawResponse = new Dictionary<double, double>();
		_routingMeterBuffer.Stop();
		ClearLocalMeasurementDiagnosis();
		_isExecutingAutoSuite = false;
		_testRunner?.Cancel();
		if (_currentRunningTestCase != null)
		{
			_currentRunningTestCase.Status = "WAITING";
			_currentRunningTestCase.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
			_currentRunningTestCase.FreqStatus = "CHƯA ĐO";
			_currentRunningTestCase.FreqBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
			_currentRunningTestCase.ThdStatus = "CHƯA ĐO";
			_currentRunningTestCase.ThdBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
			_currentRunningTestCase.NoiseStatus = "CHƯA ĐO";
		}
		lock (_freqs)
		{
			_freqs.Clear();
			_dbValues.Clear();
		}
		PlotFreqResponse.Plot.Clear();
		ClearThdPlots();
		InitCharts();
		TxtFreqStatus.Text = "";
		TxtThdStatus.Text = "";
		ListSteps.ItemsSource = null;
		_currentTestSuccess = null;
		RewMeterRouting?.Reset();
		_autoTestResumeSession = null;
		if (_c360ModelCancellation == null) DeleteDiscardedAutoTestArtifacts();
		ClearC360ModelResults();
		ShowInlineImpulse(null);
		UpdateAutoTestStartButton();
		BtnSaveStandard.IsEnabled = false;
		BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
		BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
		LblVerdict.Text = isTestingBusy ? "ĐÃ DỪNG VÀ XÓA KẾT QUẢ" : "ĐÃ XÓA KẾT QUẢ VỪA ĐO";
		LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
		AppendLog("Hệ thống", LblVerdict.Text);
		UpdateMeasurementDetails();
	}

	private void DeleteDiscardedAutoTestArtifacts()
	{
		string autoTestSessionFolder = _autoTestSessionFolder;
		_autoTestGraphPaths.Clear();
		if (string.IsNullOrWhiteSpace(autoTestSessionFolder))
		{
			return;
		}
		try
		{
			string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail data"));
			string fullPath2 = System.IO.Path.GetFullPath(autoTestSessionFolder);
			string value = fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
			if (fullPath2.StartsWith(value, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath2))
			{
				Directory.Delete(fullPath2, recursive: true);
				AppendLog("Hệ thống", "Đã xóa dữ liệu phiên đo sai: " + fullPath2);
			}
		}
		catch (Exception ex)
		{
			AppendLog("Cảnh báo", "Không thể xóa thư mục kết quả bị hủy: " + ex.Message);
		}
	}

	private async Task RunTrackedTestAsync(MMDevice playbackDevice, MMDevice recordingDevice, bool runFeqThreeTimes = false, bool? referenceAcquisitionOverride = null)
	{
		ClearLocalMeasurementDiagnosis();
		_lastTrackedTestError = "";
		if (_activeMeasurementTask != null)
			throw new InvalidOperationException("Bài đo trước chưa giải phóng xong; không mở bài đo chồng lên nhau.");
		CloseRoutingScope();
		StopRoutingHeadroomMonitor();
		_audioEngine.StopAllAudio();
		EnsureSelectedStandardSampleRates(beforeMeasurement: true);
		if (_appliedTestConfig != null) _appliedTestConfig.ModelVolume = _activeInOutConfig?.TestVolume;
        await ConfirmGenericModelVolumeAsync();
		using var windowsLevels = new WindowsAudioLevels(playbackDevice, recordingDevice, _appliedTestConfig);
		AppendLog("Audio", "Đã tắt mọi nguồn phát của app và đóng/xóa buffer bài cũ; bắt đầu bài mới.");
		RewMeterRouting?.PrepareForLiveCapture();
		System.Threading.Interlocked.Exchange(ref _routingMeterSession, _routingMeterBuffer.Start());
		_testRunner.AutoTestOneKilohertzOnly = _isExecutingAutoSuite;
		bool referenceAcquisition = referenceAcquisitionOverride ?? _isFindingStandardReference;
		if (!_isExecutingAutoSuite) _reviewedAutoTestCase = null;
		_lastObservedCaptureRate = null;
		UpdateSampleRateWarning();
		if (_sampleRateWarningText.Length > 0) AppendLog("Cảnh báo 44.1 kHz", _sampleRateWarningText);
		_lineAcquisitionValues = BuildAcquisitionValues();
		_captureDiagnosticContext = DiagnosticContextKey();
		_capturePolarityReference = HasComparableStandardCurve() ? _standardPolarityReference : null;
		// Both the comparison grid and default display use 1/24 octave, 50–18k.
		var evaluationRange = MeasurementCriteria.Range(_appliedTestConfig);
        _testRunner.EvaluationMinFrequencyHz = evaluationRange.MinimumHz;
        _testRunner.EvaluationMaxFrequencyHz = evaluationRange.MaximumHz;
		_testRunner.RestrictFeqToEvaluationRange = true;
		UpdateMeasurementDetails(pending: true);
		Task measurementTask = (_activeMeasurementTask = _testRunner.RunTestAsync(playbackDevice, recordingDevice, runFeqThreeTimes, referenceAcquisition));
		await ObserveMeasurementTaskAsync(measurementTask);
	}

	private async Task ObserveMeasurementTaskAsync(Task measurementTask)
	{
		try
		{
			await measurementTask;
			if (!_discardCurrentMeasurementRequested && _testRunner.LastSweepResult is { } finalSweep)
				RememberSweepPolarity(finalSweep);
		}
		catch (OperationCanceledException) when (_discardCurrentMeasurementRequested)
		{
			AppendLog("Hệ thống", "Phép đo đã được hủy; kết quả không được sử dụng.");
		}
		catch (OperationCanceledException) when (_autoTestInterrupted)
		{
			AppendLog("Auto Test", "Bài đang đo bị ngắt; chưa sử dụng dữ liệu dở dang.");
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			Exception exception = ex4;
			_currentTestSuccess = false;
			string measurementErrorMessage = GetMeasurementErrorMessage(exception);
			_lastTrackedTestError = measurementErrorMessage;
			AppendLog("Lỗi đo line", measurementErrorMessage);
			AppendLog("Chi tiết kỹ thuật", exception.ToString());
			TxtFreqStatus.Text = "CHƯA ĐO — " + measurementErrorMessage;
			TxtThdStatus.Text = "CHƯA ĐO";
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 10, 10));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
			LblVerdict.Text = "KHÔNG THỂ ĐO — CHƯA CÓ KẾT QUẢ";
			TxtFailureDiagnosis.Text = "Chưa thu được dữ liệu hợp lệ; kiểm tra kết nối và ngõ phát/thu.";
			TxtFailureDiagnosis.Visibility = Visibility.Visible;
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			if (!_isExecutingAutoSuite && !_autoTestInterrupted)
				ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), measurementErrorMessage, "Không thể đo line", ModernMessageBox.MessageBoxType.Error);
		}
		finally
		{
			try { _audioEngine.StopAllAudio(); }
			finally
			{
				if (_activeMeasurementTask == measurementTask) _activeMeasurementTask = null;
				if (_discardCurrentMeasurementRequested) UpdateMeasurementDetails();
				if (!_isExecutingAutoSuite && !_isFindingStandardReference)
				{
					BtnStart.IsEnabled = true;
					BtnStartAutoTest.IsEnabled = true;
					BtnSaveStandardReference.IsEnabled = true;
					BtnNoiseTest.IsEnabled = true;
					BtnSaveStandard.IsEnabled = !_discardCurrentMeasurementRequested && _freqs.Count >= 2;
				}
			}
		}
	}

	private static string GetMeasurementErrorMessage(Exception exception)
	{
		return AudioMeasurementErrors.Describe(exception);
	}

	private async void BtnNoiseTest_Click(object sender, RoutedEventArgs e)
	{
		CloseRoutingScope();
		using IDisposable? workflow = TryBeginRoutingWorkflow();
		if (workflow == null) return;
		if ((Application.Current.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Bộ đo âm học nâng cao đang thực hiện phép đo. Vui lòng chờ hoàn tất trước khi đo nền nhiễu.", "Thiết bị đang bận", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		ClearThdPlots();
		WpfPlot[] thdPlots = GetThdPlots();
		for (int i = 0; i < thdPlots.Length; i++)
		{
			thdPlots[i].Refresh();
		}
		BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
		BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
		LblVerdict.Text = "ĐANG PHÂN TÍCH NHIỄU...";
		LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
		BtnStart.IsEnabled = false;
		BtnSaveStandardReference.IsEnabled = false;
		BtnNoiseTest.IsEnabled = false;
		MMDevice selectedPlaybackDevice = SelectedPlaybackDevice;
		MMDevice mMDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;
		if (selectedPlaybackDevice == null || mMDevice == null)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Chưa chọn đủ thiết bị phát và thu âm.", "Thiếu thiết bị", ModernMessageBox.MessageBoxType.Warning);
			BtnStart.IsEnabled = true;
			BtnSaveStandardReference.IsEnabled = true;
			BtnNoiseTest.IsEnabled = true;
			return;
		}
		StopRoutingHeadroomMonitor();
		int? previousRecordingChannel = _audioEngine.RecordingChannel;
		EnsureSelectedStandardSampleRates(beforeMeasurement: true);
		try
		{
			_audioEngine.RecordingChannel = null;
			_discardCurrentMeasurementRequested = false;
			System.Threading.Interlocked.Exchange(ref _routingMeterSession, _routingMeterBuffer.Start());
			await _testRunner.RunNoiseTestAsync(selectedPlaybackDevice, mMDevice);
			LblVerdict.Text = _discardCurrentMeasurementRequested ? "ĐÃ HỦY ĐO NHIỄU" : "ĐÃ ĐO NHIỄU";
			LblVerdict.Foreground = Brushes.MediumSpringGreen;
		}
		catch (Exception ex)
		{
			LblVerdict.Text = "CHƯA ĐO ĐƯỢC NHIỄU";
			LblVerdict.Foreground = Brushes.Salmon;
			AppendLog("Lỗi đo nhiễu", ex.ToString());
			ModernMessageBox.Show(Window.GetWindow(this), GetMeasurementErrorMessage(ex),
				"Không thể đo nhiễu", ModernMessageBox.MessageBoxType.Error);
		}
		finally
		{
			try { _audioEngine.StopAllAudio(); }
			finally
			{
				_audioEngine.RecordingChannel = previousRecordingChannel;
				BtnStart.IsEnabled = true;
				BtnSaveStandardReference.IsEnabled = true;
				BtnNoiseTest.IsEnabled = true;
			}
		}
	}

	private void BtnDecreaseLevelDbfs_Click(object sender, RoutedEventArgs e)
	{
		PlaybackLevelDbfs = Math.Max(-60.0, PlaybackLevelDbfs - 1.0);
	}

	private void BtnIncreaseLevelDbfs_Click(object sender, RoutedEventArgs e)
	{
		PlaybackLevelDbfs = Math.Min(0.0, PlaybackLevelDbfs + 1.0);
	}

	private void SliderPlaybackLevelDbfs_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		PlaybackLevelDbfs = e.NewValue;
	}

	private void SliderPlaybackLevelDbfs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (SliderPlaybackLevelDbfs != null)
		{
			double step = (e.Delta > 0) ? 1.0 : -1.0;
			PlaybackLevelDbfs = Math.Clamp(SliderPlaybackLevelDbfs.Value + step, SliderPlaybackLevelDbfs.Minimum, SliderPlaybackLevelDbfs.Maximum);
			e.Handled = true;
		}
	}

	private void TxtPlaybackLevelDbfs_LostFocus(object sender, RoutedEventArgs e)
	{
		ApplyPlaybackLevelFromTextBox();
	}

	private void TxtPlaybackLevelDbfs_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			ApplyPlaybackLevelFromTextBox();
			e.Handled = true;
		}
	}

	private void ApplyPlaybackLevelFromTextBox()
	{
		if (TxtPlaybackLevelDbfs != null && double.TryParse(TxtPlaybackLevelDbfs.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
		{
			PlaybackLevelDbfs = Math.Clamp(val, -60.0, 0.0);
		}
		else if (TxtPlaybackLevelDbfs != null)
		{
			TxtPlaybackLevelDbfs.Text = $"{PlaybackLevelDbfs:0.#}";
		}
	}

	private void SyncPlaybackVolumeFromWindows()
	{
	}

	private void SyncRecordingVolumeFromWindows()
	{
	}

	private void AudioRouting_Loaded(object sender, RoutedEventArgs e)
	{
		_measurementShortcutHost = Window.GetWindow(this);
		if (_measurementShortcutHost != null) _measurementShortcutHost.PreviewKeyDown += HandleMeasurementShortcut;
		_hwndSource = PresentationSource.FromVisual(this) as HwndSource;
		_hwndSource?.AddHook(HwndHorizontalWheelHook);
		SyncChannelSelectionsFromEngine();
		EnsureSelectedStandardSampleRates();
		UpdateSelectedDeviceFormats();
		_routingMeterTimer.Start();
		MaintainRoutingHeadroomMonitor();
		_ = RefreshPendingQaAsync();
	}

	private void AudioRouting_Unloaded(object sender, RoutedEventArgs e)
	{
		_measurementDetailsWindow?.Close();
		if (_measurementShortcutHost != null) _measurementShortcutHost.PreviewKeyDown -= HandleMeasurementShortcut;
		_measurementShortcutHost = null;
		CloseRoutingScope();
		_frequencyChartRefreshTimer.Stop();
		_hwndSource?.RemoveHook(HwndHorizontalWheelHook);
		_hwndSource = null;
		StopRoutingHeadroomMonitor();
		_routingMeterTimer.Stop();
		_routingMeterBuffer.Stop();
	}

	internal Action<string, string>? SignalWarningPresenter;

	private void ShowSignalLevelPopupIfNeeded()
	{
		if (_discardCurrentMeasurementRequested || _testRunner == null || _isExecutingAutoSuite)
		{
			return;
		}
		double value = PlaybackLevelDbfs;
		double? num = null;
		try
		{
			MMDevice mMDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;
			if (mMDevice != null)
			{
				num = (double)mMDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0;
			}
		}
		catch
		{
		}
		string value2 = (num.HasValue ? $"{num.Value:F0}%" : "không đọc được");
		if (_testRunner.InputClippingDetected)
		{
			string text = $"TÍN HIỆU INPUT QUÁ MẠNH / CLIPPING\n\nPeak thu: {_testRunner.LastFeqPeakSample:F4} (phải < 0.995)\nWindows IN: {value2} — OUT: {value:F1} dBFS\n\n" + "Cách thao tác:\n1. Giảm gain vật lý/ngõ IN trước.\n2. Nếu vẫn clipping, giảm mức OUT dBFS.\n3. Mở Scope → Sine Check 1 kHz và thử lại.\n4. Chỉ bắt đầu sweep khi Scope báo SINE CHECK ĐẠT.";
			AppendLog("Popup mức tín hiệu", text.Replace(Environment.NewLine, " "));
			ModernMessageBox.ShowPersistentWarning(Window.GetWindow((DependencyObject)(object)this), text, "Input quá mạnh — Chưa đo THD");
		}
			else if (MeasurementSignalStatus.MissingStimulus(_testRunner.LastSweepResult, _testRunner.SilentInputDetected))
		{
				string text2 = MeasurementSignalStatus.MissingStimulusMessage(SelectedPlaybackDevice?.FriendlyName ?? "Chưa chọn",
					SelectedRecordingDevice?.FriendlyName ?? "Chưa chọn", _testRunner.LastSweepResult);
			AppendLog("Popup mức tín hiệu", text2.Replace(Environment.NewLine, " "));
				TxtThdStatus.Text = "CHƯA ĐO — KHÔNG NHẬN ĐƯỢC TÍN HIỆU THỬ";
				if (SignalWarningPresenter is { } present) present(text2, "Kiểm tra đường phát / thu");
				else ModernMessageBox.ShowPersistentWarning(Window.GetWindow((DependencyObject)(object)this), text2, "Kiểm tra đường phát / thu");
		}
		else if (MeasurementSignalStatus.LowSignalWarning(_testRunner.LastSweepResult) is string lowSignal)
		{
			AppendLog("Mức tín hiệu đo", lowSignal);
			if (SignalWarningPresenter is { } present) present(lowSignal, "Tín hiệu đo thấp");
			else ModernMessageBox.ShowPersistentWarning(Window.GetWindow(this), lowSignal, "Tín hiệu đo thấp");
		}
	}

	private void BtnOpenRoutingScope_Click(object sender, RoutedEventArgs e)
	{
		if (_routingScopeWindow != null)
		{
			_routingScopeWindow.Activate();
			return;
		}
		if (IsTestingBusy)
		{
			AppendLog("Thiết bị đang bận", "Chờ toàn bộ quy trình đo kết thúc rồi mở Scope / Sine Check.");
			return;
		}
		if (Application.Current.MainWindow is MainWindow { IsStandardMeasurementBusy: not false })
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Bộ đo âm học nâng cao đang dùng thiết bị. Hãy đợi phép đo kết thúc rồi mở Scope.", "Thiết bị đang bận", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		MMDevice selectedPlaybackDevice = SelectedPlaybackDevice;
		MMDevice selectedRecordingDevice = SelectedRecordingDevice;
		if (selectedPlaybackDevice == null || selectedRecordingDevice == null)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Hãy chọn đủ ngõ phát và ngõ thu trước khi mở Scope.", "Thiếu thiết bị", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		StopRoutingHeadroomMonitor();
		try
		{
			RoutingScopeWindow scope = new RoutingScopeWindow(_audioEngine, selectedPlaybackDevice, selectedRecordingDevice, _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel)
			{
				Owner = Window.GetWindow((DependencyObject)(object)this)
			};
			_routingScopeWindow = scope;
			scope.Closed += delegate
			{
				if (_routingScopeWindow == scope)
				{
					_routingScopeWindow = null;
				}
				if (base.IsLoaded)
				{
					MaintainRoutingHeadroomMonitor();
				}
			};
			scope.Show();
		}
		catch (Exception exception)
		{
			_routingScopeWindow = null;
			MaintainRoutingHeadroomMonitor();
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Không thể mở Scope: " + GetMeasurementErrorMessage(exception), "Lỗi Scope", ModernMessageBox.MessageBoxType.Error);
		}
	}

	private void CloseRoutingScope()
	{
		RoutingScopeWindow routingScopeWindow = _routingScopeWindow;
		if (routingScopeWindow == null)
		{
			return;
		}
		_routingScopeWindow = null;
		try
		{
			routingScopeWindow.Close();
		}
		catch
		{
		}
	}

	private nint HwndHorizontalWheelHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		if (msg == 526)
		{
			nint num = wParam;
			nint num2 = num;
			nint num3 = num2;
			nint num4 = num3;
			short num5 = (short)((((IntPtr)num4).ToInt64() >> 16) & 0xFFFF);
			if (num5 != 0)
			{
				num = lParam;
				num2 = num;
				num3 = num2;
				num4 = num3;
				int num6 = (short)(((IntPtr)num4).ToInt64() & 0xFFFF);
				num = lParam;
				num2 = num;
				num3 = num2;
				num4 = num3;
				int num7 = (short)((((IntPtr)num4).ToInt64() >> 16) & 0xFFFF);
				Point screenPoint = default(Point);
				screenPoint = new Point(num6, num7);
				if (IsMouseOverElement(SliderPlaybackLevelDbfs, screenPoint))
				{
					double num8 = ((Math.Abs(num5) >= 120) ? 2.0 : 1.0);
					if (num5 > 0)
					{
						PlaybackLevelDbfs = Math.Min(0.0, PlaybackLevelDbfs + num8);
					}
					else
					{
						PlaybackLevelDbfs = Math.Max(-60.0, PlaybackLevelDbfs - num8);
					}
					handled = true;
					return IntPtr.Zero;
				}
			}
		}
		return IntPtr.Zero;
	}

	private static bool IsMouseOverElement(UIElement? element, Point screenPoint)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		if (element == null || !element.IsVisible)
		{
			return false;
		}
		try
		{
			Point val = element.PointFromScreen(screenPoint);
			Rect val2 = default(Rect);
			val2 = new Rect(0.0, 0.0, ((FrameworkElement)element).ActualWidth, ((FrameworkElement)element).ActualHeight);
			return val2.Contains(val);
		}
		catch
		{
			return element.IsMouseOver;
		}
	}


	private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
	{
		if (IsTestingBusy)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Dừng phép đo trước khi cập nhật thiết bị.", "Đang đo", ModernMessageBox.MessageBoxType.Warning);
		}
		else
		{
			AutoDetectDevices();
		}
	}

	public void RefreshDevicesAfterConnection()
	{
		if (!IsTestingBusy)
		{
			AutoDetectDevices();
			_leaveAudioReleasedAfterAutoTest = false;
			CheckAndLoadStandardDevice();
			MaintainRoutingHeadroomMonitor();
		}
	}

	private async void BtnRemoveBluetooth_Click(object sender, RoutedEventArgs e)
	{
		if (IsTestingBusy)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)this), "Dừng phép đo trước khi xóa thiết bị Bluetooth.", "Đang đo", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		string modelName = (Application.Current.MainWindow as MainWindow)?.ComboModels?.SelectedItem?.ToString()?.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(modelName))
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)this), "Chọn model trước khi xóa thiết bị Bluetooth đã ghép.", "Thiết bị Bluetooth", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		using var workflow = TryBeginRoutingWorkflow();
		if (workflow == null) return;
		bool selectedPlaybackMatches = SelectedPlaybackDevice is { } playback
			&& BluetoothDeviceRemoval.MatchesModel(playback.FriendlyName, modelName);
		StopRoutingHeadroomMonitor();
		BtnRemoveBluetooth.IsEnabled = false;
		try
		{
			var result = await BluetoothDeviceRemoval.RemovePairedDevicesForModelAsync(modelName);
			AppendLog("Bluetooth", result.Message);
			if (result.Removed > 0)
			{
				if (selectedPlaybackMatches) _preferredUsbPlaybackDeviceId = "";
				AutoDetectDevices();
				SaveConfig();
			}
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)this), result.Message, "Xóa Bluetooth của model", (!result.Success) ? ModernMessageBox.MessageBoxType.Warning : ModernMessageBox.MessageBoxType.Info);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			AppendLog("Bluetooth", ex3.Message);
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)this), ex3.Message, "Không xóa được thiết bị Bluetooth", ModernMessageBox.MessageBoxType.Error);
		}
		finally
		{
			BtnRemoveBluetooth.IsEnabled = true;
		}
	}

	private void BtnToggleFreqFullscreen_Click(object sender, RoutedEventArgs e)
	{
		if (_isFreqExpanded)
		{
			RestoreChartsLayout();
		}
		else
		{
			ExpandFreqChart();
		}
	}

	private void BtnToggleThdFullscreen_Click(object sender, RoutedEventArgs e)
	{
		if (_isThdExpanded)
		{
			RestoreChartsLayout();
		}
		else
		{
			ExpandThdChart();
		}
	}

	public void ExpandFreqChart()
	{
		if (!_isFreqExpanded && !_isThdExpanded)
		{
			_savedSetupWidth = SetupColumn.Width;
			_savedSetupVisibility = PanelSetup.Visibility;
			_savedAutoTestVisibility = PanelAutoTestList.Visibility;
			_savedFooterVisibility = BorderFooterArea.Visibility;
		}
		_isFreqExpanded = true;
		_frequencyViewAdjustedByUser = false;
		_isThdExpanded = false;
		SetupColumn.MinWidth = 0.0;
		SetupColumn.Width = new GridLength(0.0);
		PanelSetup.Visibility = Visibility.Collapsed;
		if (SetupSplitter != null)
		{
			SetupSplitter.Visibility = Visibility.Collapsed;
		}
		PanelAutoTestList.Visibility = Visibility.Collapsed;
		ColAutoTestList.Width = new GridLength(0.0);
		ColChartsPlot.Width = new GridLength(1.0, GridUnitType.Star);
		BorderFooterArea.Visibility = Visibility.Collapsed;
		RowFooterArea.Height = new GridLength(0.0);
		BorderThdChart.Visibility = Visibility.Collapsed;
		RowThdChart.Height = new GridLength(0.0);
		BorderFreqChart.Visibility = Visibility.Visible;
		BorderFreqChart.Margin = new Thickness(0.0);
		BorderFreqChart.Padding = new Thickness(4.0);
		GridChartsArea.Margin = new Thickness(4.0, 8.0, 4.0, 4.0);
		RowFreqChart.Height = new GridLength(1.0, GridUnitType.Star);
		IconFreqFullscreen.Data = Geometry.Parse("M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z");
		IconFreqFullscreen.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
		BtnToggleFreqFullscreen.ToolTip = "Thu nhỏ về kích thước mặc định (Esc)";
		IconThdFullscreen.Data = Geometry.Parse("M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z");
		IconThdFullscreen.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
		BtnToggleThdFullscreen.ToolTip = "Phóng to đồ thị THD (ẩn các phần khác)";
		(Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(isVisible: false);
		ApplyFrequencyChartView(fitVerticalData: true);
		PlotFreqResponse.Refresh();
	}

	public void ExpandThdChart()
	{
		if (!_isFreqExpanded && !_isThdExpanded)
		{
			_savedSetupWidth = SetupColumn.Width;
			_savedSetupVisibility = PanelSetup.Visibility;
			_savedAutoTestVisibility = PanelAutoTestList.Visibility;
			_savedFooterVisibility = BorderFooterArea.Visibility;
		}
		_isThdExpanded = true;
		_isFreqExpanded = false;
		_frequencyViewAdjustedByUser = false;
		SetupColumn.MinWidth = 0.0;
		SetupColumn.Width = new GridLength(0.0);
		PanelSetup.Visibility = Visibility.Collapsed;
		if (SetupSplitter != null)
		{
			SetupSplitter.Visibility = Visibility.Collapsed;
		}
		PanelAutoTestList.Visibility = Visibility.Collapsed;
		ColAutoTestList.Width = new GridLength(0.0);
		ColChartsPlot.Width = new GridLength(1.0, GridUnitType.Star);
		BorderFooterArea.Visibility = Visibility.Collapsed;
		RowFooterArea.Height = new GridLength(0.0);
		BorderFreqChart.Visibility = Visibility.Collapsed;
		RowFreqChart.Height = new GridLength(0.0);
		BorderThdChart.Visibility = Visibility.Visible;
		BorderThdChart.Padding = new Thickness(4.0);
		GridChartsArea.Margin = new Thickness(4.0, 8.0, 4.0, 4.0);
		RowThdChart.Height = new GridLength(1.0, GridUnitType.Star);
		ApplyVerticalThdLayout();
		IconThdFullscreen.Data = Geometry.Parse("M5 16h3v3h2v-5H5v2zm3-8H5v2h5V5H8v3zm6 11h2v-3h3v-2h-5v5zm2-11V5h-2v5h5V8h-3z");
		IconThdFullscreen.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
		BtnToggleThdFullscreen.ToolTip = "Thu nhỏ về kích thước mặc định (Esc)";
		IconFreqFullscreen.Data = Geometry.Parse("M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z");
		IconFreqFullscreen.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
		BtnToggleFreqFullscreen.ToolTip = "Phóng to đồ thị tần số (ẩn các phần khác)";
		(Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(isVisible: false);
		WpfPlot[] thdPlots = GetThdPlots();
		for (int i = 0; i < thdPlots.Length; i++)
		{
			thdPlots[i].Refresh();
		}
	}

	public void RestoreChartsLayout()
	{
		_isFreqExpanded = false;
		_frequencyViewAdjustedByUser = false;
		_isThdExpanded = false;
		if (_savedSetupVisibility == Visibility.Visible)
		{
			SetupColumn.MinWidth = 280.0;
			SetupColumn.Width = ((_savedSetupWidth.Value >= 260.0) ? _savedSetupWidth : new GridLength(380.0));
		}
		else
		{
			SetupColumn.MinWidth = 0.0;
			SetupColumn.Width = new GridLength(0.0);
		}
		PanelSetup.Visibility = _savedSetupVisibility;
		if (SetupSplitter != null)
		{
			SetupSplitter.Visibility = ((_savedSetupVisibility != Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible);
		}
		PanelAutoTestList.Visibility = _savedAutoTestVisibility;
		ColAutoTestList.Width = ((_savedAutoTestVisibility == Visibility.Visible) ? new GridLength(1.0, GridUnitType.Star) : new GridLength(0.0));
		ColChartsPlot.Width = ((_savedAutoTestVisibility == Visibility.Visible) ? new GridLength(3.0, GridUnitType.Star) : new GridLength(1.0, GridUnitType.Star));
		BorderFooterArea.Visibility = _savedFooterVisibility;
		RowFooterArea.Height = GridLength.Auto;
		BorderFreqChart.Visibility = Visibility.Visible;
		BorderFreqChart.Margin = new Thickness(0.0, 0.0, 0.0, 15.0);
		BorderFreqChart.Padding = new Thickness(10.0);
		GridChartsArea.Margin = new Thickness(15.0, 15.0, 15.0, 5.0);
		RowFreqChart.Height = new GridLength(13.0, GridUnitType.Star);
		BorderThdChart.Visibility = Visibility.Visible;
		BorderThdChart.Padding = new Thickness(10.0);
		RowThdChart.Height = new GridLength(7.0, GridUnitType.Star);
		ApplyHorizontalThdLayout();
		IconFreqFullscreen.Data = Geometry.Parse("M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z");
		IconFreqFullscreen.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
		BtnToggleFreqFullscreen.ToolTip = "Phóng to đồ thị tần số (ẩn các phần khác để nhìn rõ tần số và dB)";
		IconThdFullscreen.Data = Geometry.Parse("M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z");
		IconThdFullscreen.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
		BtnToggleThdFullscreen.ToolTip = "Phóng to đồ thị THD (ẩn các phần khác để nhìn rõ phổ và THD)";
		(Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(_savedSetupVisibility == Visibility.Visible);
		ApplyFrequencyChartView(fitVerticalData: false);
		PlotFreqResponse.Refresh();
		WpfPlot[] thdPlots = GetThdPlots();
		for (int i = 0; i < thdPlots.Length; i++)
		{
			thdPlots[i].Refresh();
		}
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		if ((int)e.Key == 13 && (_isFreqExpanded || _isThdExpanded))
		{
			RestoreChartsLayout();
			e.Handled = true;
		}
		else
		{
			base.OnPreviewKeyDown(e);
		}
	}

	public bool ToggleSetupVisibility()
	{
		bool flag = PanelSetup.Visibility != Visibility.Visible;
		SetSetupVisibility(flag);
		return flag;
	}

	public void SetSetupVisibility(bool visible)
	{
		if (_isFreqExpanded || _isThdExpanded)
		{
			RestoreChartsLayout();
		}
		if (visible)
		{
			SetupColumn.MinWidth = 280.0;
			SetupColumn.Width = ((_savedSetupWidth.Value >= 260.0) ? _savedSetupWidth : new GridLength(380.0));
			PanelSetup.Visibility = Visibility.Visible;
			_savedSetupVisibility = Visibility.Visible;
			if (SetupSplitter != null)
			{
				SetupSplitter.Visibility = Visibility.Visible;
			}
			if (ChkSendToServer != null)
			{
				ChkSendToServer.Visibility = Visibility.Visible;
			}
			(Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(isVisible: true);
			return;
		}
		if (SetupColumn.Width.Value >= 260.0)
		{
			_savedSetupWidth = SetupColumn.Width;
		}
		_savedSetupVisibility = Visibility.Collapsed;
		PanelSetup.Visibility = Visibility.Collapsed;
		SetupColumn.MinWidth = 0.0;
		SetupColumn.Width = new GridLength(0.0);
		if (SetupSplitter != null)
		{
			SetupSplitter.Visibility = Visibility.Collapsed;
		}
		if (ChkSendToServer != null)
		{
			ChkSendToServer.Visibility = Visibility.Collapsed;
		}
		(Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(isVisible: false);
	}

	private void BtnSidebarWidth_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: not null } button && double.TryParse(button.Tag.ToString(), out var result))
		{
			SetupColumn.Width = new GridLength(result);
			_savedSetupWidth = SetupColumn.Width;
		}
	}

	private void BtnHeadroomHeight_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: not null } button && double.TryParse(button.Tag.ToString(), out var result) && RewMeterRouting != null)
		{
			RewMeterRouting.Height = result;
		}
	}

	private static double RepairStoredTolerance(double value, double fallback, double maximum, double legacyMultiplier)
	{
		if (!double.IsFinite(value) || value <= 0.0) return fallback;
		// Earlier versions parsed a decimal comma as a thousands separator on
		// every restart (3,0 -> 30 -> 300; 0,50 -> 50 -> 5000).
		while (value > maximum && value >= legacyMultiplier)
			value /= legacyMultiplier;
		return value > maximum ? fallback : value;
	}

	private void ConfigureSelectedSweep()
	{
		_audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
		_testRunner.LogSweepDurationSeconds = GetSelectedLogSweepDuration();
	}

	private static string GetUserDataDirectory()
	{
		string? overridePath = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
		if (!string.IsNullOrWhiteSpace(overridePath))
		{
			Directory.CreateDirectory(overridePath);
			return overridePath;
		}
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string directory = System.IO.Path.Combine(
			string.IsNullOrWhiteSpace(local) ? AppDomain.CurrentDomain.BaseDirectory : local,
			"SoncaAudioInspector");
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static string GetRoutingConfigPath()
	{
		string persistentPath = System.IO.Path.Combine(GetUserDataDirectory(), "routing_value.json");
		string legacyPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "routing_value.json");
		if (!File.Exists(persistentPath) && File.Exists(legacyPath))
			File.Copy(legacyPath, persistentPath);
		return persistentPath;
	}

	private double ParseDoubleSafe(string text, double defaultValue = 0.0)
	{
		if (string.IsNullOrWhiteSpace(text)) return defaultValue;
		return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
			CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
			? result : defaultValue;
	}

	private MMDevice? ResolvePlaybackDevice(string? configKey, string? targetKeyword, IEnumerable<MMDevice> devices, bool requireConfiguredMatch = false)
	{
		List<MMDevice> list = devices.ToList();
		if ((targetKeyword ?? configKey ?? "").Contains(FastTrackDeviceSetup.Playback12Name, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(configKey, "Analog 1/2", StringComparison.OrdinalIgnoreCase))
		{
			var matches = list.Where(IsFastTrackPlayback12).ToList();
			return FastTrackDeviceSetup.SelectUnique(matches, _preferredUsbPlaybackDeviceId, _ => true);
		}
		if (requireConfiguredMatch)
		{
			string expected = !string.IsNullOrWhiteSpace(targetKeyword) ? targetKeyword : configKey ?? "";
			return !string.IsNullOrWhiteSpace(expected) ? list.FirstOrDefault(device => device.FriendlyName.Contains(expected, StringComparison.OrdinalIgnoreCase)) : null;
		}
		if (list.Count == 0)
		{
			return null;
		}
		if (!string.IsNullOrWhiteSpace(targetKeyword))
		{
			List<MMDevice> exactMatches = list.Where((MMDevice d) => d.FriendlyName.IndexOf(targetKeyword, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
			MMDevice mMDevice = null;
			if (exactMatches.Count > 1)
			{
				string defaultDeviceId = _audioEngine.TryGetWindowsDefaultPlaybackVolume()?.DeviceId ?? "";
				mMDevice = exactMatches.FirstOrDefault((MMDevice d) => string.Equals(d.ID, defaultDeviceId, StringComparison.OrdinalIgnoreCase)) ?? exactMatches.FirstOrDefault((MMDevice d) => string.Equals(d.ID, _preferredUsbPlaybackDeviceId, StringComparison.OrdinalIgnoreCase));
			}
			if (mMDevice == null)
			{
				mMDevice = exactMatches.FirstOrDefault();
			}
			if (mMDevice != null)
			{
				return mMDevice;
			}
			string[] array = targetKeyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			string[] array2 = array;
			string[] array3 = array2;
			string[] array4 = array3;
			foreach (string token in array4)
			{
				if (token.Length >= 3)
				{
					mMDevice = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
					if (mMDevice != null)
					{
						return mMDevice;
					}
				}
			}
		}
		if (!string.IsNullOrWhiteSpace(configKey))
		{
			MMDevice mMDevice2 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(configKey, StringComparison.OrdinalIgnoreCase) >= 0 || configKey.IndexOf(d.FriendlyName, StringComparison.OrdinalIgnoreCase) >= 0);
			if (mMDevice2 != null)
			{
				return mMDevice2;
			}
			string[] array5 = configKey.Split(new char[4] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
			string[] array6 = array5;
			string[] array7 = array6;
			string[] array8 = array7;
			foreach (string token2 in array8)
			{
				if (token2.Length >= 3 && !string.Equals(token2, "Line", StringComparison.OrdinalIgnoreCase))
				{
					mMDevice2 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(token2, StringComparison.OrdinalIgnoreCase) >= 0);
					if (mMDevice2 != null)
					{
						return mMDevice2;
					}
				}
			}
		}
		if ((configKey ?? "").IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 || (targetKeyword ?? "").IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			MMDevice btSelected = SelectedPlaybackDevice;
			if (btSelected != null && list.Any((MMDevice d) => d.ID == btSelected.ID))
			{
				return btSelected;
			}
			MMDevice mMDevice3 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0);
			if (mMDevice3 != null)
			{
				return mMDevice3;
			}
		}
		else
		{
			MMDevice usbSelected = (ComboPlayback?.SelectedItem as DeviceItem)?.Device;
			if (usbSelected != null && list.Any((MMDevice d) => d.ID == usbSelected.ID))
			{
				return usbSelected;
			}
			MMDevice mMDevice4 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0);
			if (mMDevice4 != null)
			{
				return mMDevice4;
			}
		}
		return list.FirstOrDefault();
	}

	private MMDevice? ResolveRecordingDevice(string? configKey, string? targetKeyword, IEnumerable<MMDevice> devices, bool requireConfiguredMatch = false)
	{
		List<MMDevice> list = devices.ToList();
		if (requireConfiguredMatch)
		{
			string expected = !string.IsNullOrWhiteSpace(targetKeyword) ? targetKeyword : configKey ?? "";
			return !string.IsNullOrWhiteSpace(expected) ? list.FirstOrDefault(device => device.FriendlyName.Contains(expected, StringComparison.OrdinalIgnoreCase)) : null;
		}
		if (list.Count == 0)
		{
			return null;
		}
		MMDevice recSelected = (ComboRecording?.SelectedItem as DeviceItem)?.Device;
		bool flag = (configKey ?? "").IndexOf("Mic", StringComparison.OrdinalIgnoreCase) >= 0 || (configKey ?? "").IndexOf("Physical", StringComparison.OrdinalIgnoreCase) >= 0;
		if (flag && recSelected != null && list.Any((MMDevice d) => d.ID == recSelected.ID))
		{
			return recSelected;
		}
		if (!string.IsNullOrWhiteSpace(targetKeyword))
		{
			MMDevice mMDevice = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(targetKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
			if (mMDevice != null)
			{
				return mMDevice;
			}
			string[] array = targetKeyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			string[] array2 = array;
			string[] array3 = array2;
			string[] array4 = array3;
			foreach (string token in array4)
			{
				if (token.Length >= 3)
				{
					mMDevice = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
					if (mMDevice != null)
					{
						return mMDevice;
					}
				}
			}
		}
		if (!string.IsNullOrWhiteSpace(configKey))
		{
			MMDevice mMDevice2 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(configKey, StringComparison.OrdinalIgnoreCase) >= 0 || configKey.IndexOf(d.FriendlyName, StringComparison.OrdinalIgnoreCase) >= 0);
			if (mMDevice2 != null)
			{
				return mMDevice2;
			}
			string[] array5 = configKey.Split(new char[4] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
			string[] array6 = array5;
			string[] array7 = array6;
			string[] array8 = array7;
			foreach (string token2 in array8)
			{
				if (token2.Length >= 3 && !string.Equals(token2, "Line", StringComparison.OrdinalIgnoreCase))
				{
					mMDevice2 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf(token2, StringComparison.OrdinalIgnoreCase) >= 0);
					if (mMDevice2 != null)
					{
						return mMDevice2;
					}
				}
			}
		}
		if (flag)
		{
			MMDevice mMDevice3 = list.FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf("KT USB", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("Measurement", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("Calib", StringComparison.OrdinalIgnoreCase) >= 0);
			if (mMDevice3 != null)
			{
				return mMDevice3;
			}
		}
		if (recSelected != null && list.Any((MMDevice d) => d.ID == recSelected.ID))
		{
			return recSelected;
		}
		return list.FirstOrDefault();
	}

	public void ApplyTestCaseConfig(TestConfig? testConfig)
	{
		ApplyTestCaseConfigCore(testConfig, selectDevices: true);
	}

	private void ApplyTestCaseConfigCore(TestConfig? testConfig, bool selectDevices)
	{
		if (testConfig == null)
		{
			return;
		}
		_appliedTestConfig = testConfig;
		_testRunner.ImpulseEffectsSettings = testConfig.ImpulseEffects ?? new();
		_referenceRouteKey = MakeSafeFileToken(testConfig.id + "_" + testConfig.PlaybackOut + "_TO_" + testConfig.RecordingIn);
		_audioEngine.RecordingChannel = ResolveRecordingChannel(testConfig);
		_audioEngine.PlaybackChannel = ResolvePlaybackChannel(testConfig);
		SyncChannelSelectionsFromEngine();
		_testRunner.PlaybackFrequencyScale = ResolvePlaybackFrequencyScale(testConfig);
		SelectLogSweepDuration(testConfig.ResolveSweepSeconds(GetSelectedPlaybackSampleRate()));
		if (testConfig.MultitoneDurationSeconds is double multitoneSeconds) SelectMultitoneDuration(multitoneSeconds);
		if (testConfig.PlaybackLevelDbfs.HasValue)
		{
			PlaybackLevelDbfs = testConfig.PlaybackLevelDbfs.Value;
		}
        testConfig.ValidateMeasurementCriteria();
        TxtThdLimit.Text = testConfig.Thd1000Hz.ToString("0.00", CultureInfo.InvariantCulture);
        _testRunner.ThdLimitPercent = testConfig.Thd1000Hz;
		if (!string.IsNullOrWhiteSpace(testConfig.FrequencyResponseMethod))
		{
			_testRunner.UseCombinedMultitoneFrequencyResponse = IsCombinedMultitoneMethod(testConfig.FrequencyResponseMethod);
			bool flag = testConfig.FrequencyResponseMethod.Equals("LogSweep", StringComparison.OrdinalIgnoreCase) || testConfig.FrequencyResponseMethod.Equals("REW", StringComparison.OrdinalIgnoreCase);
			_isApplyingMeasurementMethod = true;
			ChkUseLogSweep.IsChecked = flag;
			_isApplyingMeasurementMethod = false;
			_testRunner.UseLogSweepFrequencyResponse = flag;
		}
		if (selectDevices && _activeInOutConfig?.Devices != null)
		{
			string text = testConfig.PlaybackOut ?? "";
			string value = "";
			if (!string.IsNullOrEmpty(text))
			{
				_activeInOutConfig.Devices.Input?.TryGetValue(text, out value);
			}
			string text2 = testConfig.RecordingIn ?? "";
			string value2 = "";
			if (!string.IsNullOrEmpty(text2))
			{
				_activeInOutConfig.Devices.Output?.TryGetValue(text2, out value2);
			}
			using var playbackLease = new DeviceEnumerationLease(_audioEngine.GetPlaybackDevices());
			List<MMDevice> playbackDevices = playbackLease.Devices;
			using var recordingLease = new DeviceEnumerationLease(_audioEngine.GetRecordingDevices());
			List<MMDevice> recordingDevices = recordingLease.Devices;
			MMDevice matchedP = ResolvePlaybackDevice(text, value, playbackDevices);
			MMDevice matchedR = ResolveRecordingDevice(text2, value2, recordingDevices);
			_isAutoSelectingDevices = true;
			try
			{
			if (matchedP != null)
			{
				DeviceItem deviceItem = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault((DeviceItem i) => i.Device.ID == matchedP.ID);
				if (deviceItem != null)
				{
					ComboPlayback.SelectedItem = deviceItem;
				}
			}
			if (matchedR != null)
			{
				DeviceItem deviceItem2 = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault((DeviceItem i) => i.Device.ID == matchedR.ID);
				if (deviceItem2 != null)
				{
					ComboRecording.SelectedItem = deviceItem2;
				}
			}
			}
			finally { _isAutoSelectingDevices = false; }
		}
		if (selectDevices) CheckAndLoadStandardDevice();
	}

	private void TestCaseItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: AutoTestCaseItem { Config: not null } dataContext }) return;
		_allowInlineEffectImpulse = true;
		if ((dataContext.Status is "PASS" or "FAIL" && (dataContext.ResponseFrequencies.Length > 0 || dataContext.FrequencyShift != null))
			|| C360ModelDefinition.Matches(_activeInOutConfig) && dataContext.Status == "INVALID")
		{
			if (!IsTestingBusy) ApplyTestCaseConfig(dataContext.Config);
			DisplayAutoTestResult(dataContext);
			AppendLog("TestReview", $"Đang xem lại đáp tuyến và distortion của {dataContext.Id} trong khung đồ thị chính.");
			e.Handled = true;
			return;
		}
		if (dataContext.Status == "RUNNING")
		{
			_reviewedAutoTestCase = null;
			PlotFreqResponse.Plot.Title(_testRunner.NormalizeFrequencyResponseToOneKilohertz ? "Đáp tuyến chuẩn hóa theo 1 kHz" : "Đáp tuyến mức thu — không chuẩn hóa");
			PlotFreqResponse.Plot.Axes.Left.Label.Text = _testRunner.NormalizeFrequencyResponseToOneKilohertz ? "Mức tương đối 1 kHz (dBr)" : "Mức thu RMS (dBFS)";
			UpdateFreqResponseChart();
			e.Handled = true;
			return;
		}
		if (!IsTestingBusy)
		{
			ApplyTestCaseConfig(dataContext.Config);
			AppendLog("TestSelect", $"Đã nạp cấu hình {dataContext.Id} ({dataContext.Name}); mức phát {PlaybackLevelDbfs:F1} dBFS, THD_1000Hz={dataContext.Config.Thd1000Hz}%.");
		}
	}

	private void DisplayAutoTestResult(AutoTestCaseItem test)
	{
        SetC360Verdict(test.Id + " — " + test.Name + " · " + test.StatusDisplay, test.StatusBrush);
        bool changedTest = !ReferenceEquals(_reviewedAutoTestCase, test);
        if (changedTest) _frequencyViewAdjustedByUser = false;
        _reviewedAutoTestCase = test;
        UpdateSampleRateWarning();
        ShowInlineImpulse(test);
		TxtQualityChartHeading.Text = test.Config.SpeakerStage == "MicFrequencyShift" ? "DI TẦN MIC" : test.Config.SpeakerStage == "MicEffects" ? "ECHO / REVERB" : "DISTORTION";
		PanelThdInspection.Visibility = test.Config.SpeakerStage is "MicFrequencyShift" or "MicEffects" ? Visibility.Collapsed : Visibility.Visible;
		if (test.Config.SpeakerStage == "MicFrequencyShift")
		{
			ShowC360FrequencyShiftPlot(test); ClearThdPlots(); ShowC360Review(test, _selectedThdInspectFrequencyHz); UpdateMeasurementDetails(); return;
		}
		if (test.Config.RubBuzzTestFreq is double rubBuzzFrequency)
		{
			PlotFreqResponse.Plot.Clear();
			PlotFreqResponse.Plot.Title($"{test.Id} — Rè/rung: xem phổ bên dưới, không đo đáp tuyến");
			PlotFreqResponse.Refresh();
			ClearThdPlots();
			TxtQualityChartHeading.Text = "RÈ / RUNG";
			var spectrum = test.RubBuzzSpectrum;
			RenderThdFftChart(rubBuzzFrequency, spectrum?.FrequenciesHz ?? Array.Empty<double>(), spectrum?.Magnitudes ?? Array.Empty<double>(),
				test.RubBuzzMetrics?.RubBuzzPercent ?? double.NaN, test.RubBuzzMetrics?.IsValid == true && test.Status is "PASS" or "FAIL", isRubBuzz: true);
			TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(test, _activeInOutConfig) + $"\n{test.FreqStatus}\n{test.ThdStatus}";
			UpdateMeasurementDetails();
			return;
		}
		PlotFreqResponse.Plot.Title($"{test.Id} — {test.Name}");
		PlotFreqResponse.Plot.Axes.Left.Label.Text = test.ResponseLevelUnit == "dBr"
			? "Mức tương đối 1 kHz (dBr)" : "Mức thu RMS (dBFS)";
		UpdateFreqResponseChart();
        if (ShouldDeferAuxiliaryViews(_isExecutingAutoSuite, _measurementDetailsWindow != null))
        {
            TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(test, _activeInOutConfig) + "\n" + test.ThdStatus;
            UpdateMeasurementDetails();
            return;
        }
		ClearThdPlots();
		foreach (ToneQualityMetrics quality in test.DistortionMeasurements.Values.OrderBy(value => value.FundamentalFrequencyHz))
			RenderThdFftChart(quality.FundamentalFrequencyHz, quality.SpectrumFrequenciesHz,
				quality.SpectrumMagnitudes, quality.ThdPercent, quality.IsValid, isRubBuzz: false);
		if (test.Config.SpeakerStage != null) ShowC360Review(test, _selectedThdInspectFrequencyHz);
		else TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(test, _activeInOutConfig) + $"\n{test.ThdStatus}";
		UpdateMeasurementDetails();
	}

	private void ComboDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isAutoSelectingDevices)
		{
			return;
		}
		if (IsModelTransitionBusy)
		{
			_isAutoSelectingDevices = true;
			try { ((ComboBox)sender).SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null; }
			finally { _isAutoSelectingDevices = false; }
			return;
		}
		// An explicit device selection means the operator wants the live input
		// headroom back, including after an Auto Test released all audio handles.
		_leaveAudioReleasedAfterAutoTest = false;
		_lastObservedCaptureRate = null;
		_sampleRateSyncFailure = "";
		if (!_isAutoSelectingDevices && _isConfigLoaded)
		{
			if (sender == ComboPlayback)
			{
				_preferredUsbPlaybackDeviceId = (ComboPlayback.SelectedItem as DeviceItem)?.Device.ID ?? _preferredUsbPlaybackDeviceId;
			}
			else if (sender == ComboRecording)
			{
				_preferredRecordingDeviceId = (ComboRecording.SelectedItem as DeviceItem)?.Device.ID ?? _preferredRecordingDeviceId;
				RewMeterRouting?.Reset();
				RewMeterRouting?.PrepareForLiveCapture();
			}
			RememberSelectedDeviceNames();
			SaveConfig();
		}
		EnsureSelectedStandardSampleRates();
		UpdateSelectedDeviceFormats();
		if (TxtFreqTolerance != null && TxtThdLimit != null && !_isExecutingAutoSuite)
		{
			CheckAndLoadStandardDevice();
		}
		MaintainRoutingHeadroomMonitor();
	}

	private void PlaybackSource_Checked(object sender, RoutedEventArgs e)
	{
		if (_isConfigLoaded && !_isAutoSelectingDevices)
		{
			SaveConfig();
		}
	}

	private async Task<bool> CheckAutoTestMicContinuityAsync(MMDevice recordingDevice, string testId)
	{
		if (_discardCurrentMeasurementRequested || !_isExecutingAutoSuite)
			return true;
		_autoTestMicContinuityFailureStatus = "";
		await Task.Delay(300);
		AudioEngine.DualCaptureResult capture = await _audioEngine.CaptureSilenceAsync(recordingDevice, null, 0.7);
		(double floorDbFs, double peak) = AssessCapture(capture.DutSamples);
		// A short acoustic tail after the last stimulus must not be mistaken for
		// persistent noise. Recheck only when the quick capture is suspicious.
		if (floorDbFs > AutoTestStableHeadroomDbFs || peak >= 0.05)
		{
			await Task.Delay(500);
			capture = await _audioEngine.CaptureSilenceAsync(recordingDevice, null, 0.7);
			(floorDbFs, peak) = AssessCapture(capture.DutSamples);
		}
		if (_discardCurrentMeasurementRequested || !_isExecutingAutoSuite)
			return true;
		AppendLog("Auto Test", $"[{testId}] Nền Mic 2: {floorDbFs:F1} dBFS, peak {peak:F4}; nền đầu phiên {_autoTestMicFloorReferenceDbFs?.ToString("F1") ?? "N/A"} dBFS.");
		if (!double.IsFinite(floorDbFs) || floorDbFs > AutoTestStableHeadroomDbFs || peak >= 0.05)
		{
			_autoTestMicContinuityFailureStatus = "NHIỄU NỀN QUÁ CAO";
			string message = $"[{testId}] Nền Mic 2 hiện tại là {floorDbFs:F1} dBFS, peak {peak:F4}; không dùng dữ liệu của lượt này để kết luận THD. Auto Test đã dừng. Kiểm tra mic, dây và FastTrack rồi đo lại.";
			AppendLog("Auto Test", message);
			// The Auto Test retry flow owns the reconnect prompt.
			return false;
		}
		return true;
	}

	private static (double FloorDbFs, double Peak) AssessCapture(float[] samples)
	{
		if (samples.Length == 0 || samples.Any(sample => !float.IsFinite(sample)))
			return (double.NaN, double.NaN);
		double rms = DspProcessor.CalculateRms(samples, 0, samples.Length);
		return (20.0 * Math.Log10(Math.Max(rms, 1E-09)), samples.Max(sample => Math.Abs((double)sample)));
	}

	private async Task RestoreUserRoutingAfterAutoTestAsync(string playbackDeviceId, string recordingDeviceId, int? playbackChannel, int? recordingChannel)
	{
		_audioEngine.Stop();
		await Task.Delay(250);
		_isAutoSelectingDevices = true;
		try
		{
			DeviceItem playbackItem = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault((DeviceItem item) => string.Equals(item.Device.ID, playbackDeviceId, StringComparison.OrdinalIgnoreCase));
			DeviceItem recordingItem = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault((DeviceItem item) => string.Equals(item.Device.ID, recordingDeviceId, StringComparison.OrdinalIgnoreCase));
			if (playbackItem != null)
			{
				ComboPlayback.SelectedItem = playbackItem;
			}
			if (recordingItem != null)
			{
				ComboRecording.SelectedItem = recordingItem;
			}
			_audioEngine.PlaybackChannel = playbackChannel;
			_audioEngine.RecordingChannel = recordingChannel;
			_preferredUsbPlaybackDeviceId = SelectedPlaybackDevice?.ID ?? _preferredUsbPlaybackDeviceId;
			_preferredRecordingDeviceId = SelectedRecordingDevice?.ID ?? _preferredRecordingDeviceId;
		}
		finally
		{
			_isAutoSelectingDevices = false;
		}
		SyncChannelSelectionsFromEngine();
		SaveConfig();
		string playbackSide = ((_audioEngine.PlaybackChannel == 0) ? "Left" : ((_audioEngine.PlaybackChannel == 1) ? "Right" : "Stereo"));
		AppendLog("Auto Test", $"Đã đóng WASAPI và giữ lựa chọn của người dùng: {SelectedPlaybackDevice?.FriendlyName ?? "chưa có thiết bị phát"} ({playbackSide}).");
	}

	private sealed class DeviceEnumerationLease(List<MMDevice> devices) : IDisposable
	{
		public List<MMDevice> Devices { get; } = devices;
		public void Dispose()
		{
			foreach (var device in Devices)
				try { device.Dispose(); } catch { }
		}
	}

	private bool AreAllAutoTestDevicesConnected(out string missingDevices)
	{
		using var playbackLease = new DeviceEnumerationLease(_audioEngine.GetPlaybackDevices());
		List<MMDevice> playbackDevices = playbackLease.Devices;
		using var recordingLease = new DeviceEnumerationLease(_audioEngine.GetRecordingDevices());
		List<MMDevice> recordingDevices = recordingLease.Devices;
		List<string> missing = new List<string>();
		foreach (AutoTestCaseItem testCase in _autoTestCases)
		{
			string playbackKey = testCase.Config?.PlaybackOut ?? "";
			string recordingKey = testCase.Config?.RecordingIn ?? "";
			string playbackName = "";
			string recordingName = "";
			_activeInOutConfig?.Devices?.Input?.TryGetValue(playbackKey, out playbackName);
			_activeInOutConfig?.Devices?.Output?.TryGetValue(recordingKey, out recordingName);
			if (ResolvePlaybackDevice(playbackKey, playbackName, playbackDevices, requireConfiguredMatch: true) == null)
				missing.Add($"{testCase.Name}: ngõ phát {playbackName ?? playbackKey}");
			if (ResolveRecordingDevice(recordingKey, recordingName, recordingDevices, requireConfiguredMatch: true) == null)
				missing.Add($"{testCase.Name}: ngõ thu {recordingName ?? recordingKey}");
		}
		missingDevices = string.Join(Environment.NewLine, missing.Distinct(StringComparer.OrdinalIgnoreCase));
		return missing.Count == 0;
	}

	private async Task<string?> VerifyAutoTestSignalPathsAsync()
	{
		int? previousPlaybackChannel = _audioEngine.PlaybackChannel;
		int? previousRecordingChannel = _audioEngine.RecordingChannel;
		int previousSampleRate = _audioEngine.PlaybackSampleRate;
		try
		{
			foreach (AutoTestCaseItem testCase in _autoTestCases)
			{
				string playbackKey = testCase.Config?.PlaybackOut ?? "";
				string recordingKey = testCase.Config?.RecordingIn ?? "";
				string playbackName = "";
				string recordingName = "";
				_activeInOutConfig?.Devices?.Input?.TryGetValue(playbackKey, out playbackName);
				_activeInOutConfig?.Devices?.Output?.TryGetValue(recordingKey, out recordingName);
				MMDevice? playbackDevice = ResolvePlaybackDevice(playbackKey, playbackName, _audioEngine.GetPlaybackDevices(), requireConfiguredMatch: true);
				MMDevice? recordingDevice = ResolveRecordingDevice(recordingKey, recordingName, _audioEngine.GetRecordingDevices(), requireConfiguredMatch: true);
				if (playbackDevice == null || recordingDevice == null)
					return $"{testCase.Name}: thiết bị phát hoặc thu đã ngắt kết nối.";
				_audioEngine.Stop();
				_audioEngine.PlaybackSampleRate = 44100;
				_audioEngine.PlaybackChannel = ResolvePlaybackChannel(testCase.Config);
				_audioEngine.RecordingChannel = ResolveRecordingChannel(testCase.Config);
				await Task.Delay(750);
				AudioEngine.DualCaptureResult background = await _audioEngine.CaptureSilenceAsync(recordingDevice, null, 2.0);
				float[] backgroundStableWindow = background.DutSamples.TakeLast(Math.Min(background.DutSamples.Length, background.DutSampleRate)).ToArray();
				double backgroundRms = DspProcessor.CalculateRms(backgroundStableWindow, 0, backgroundStableWindow.Length);
				double backgroundDbFs = 20.0 * Math.Log10(Math.Max(backgroundRms, 1E-09));
				double backgroundPeak = backgroundStableWindow.Select(sample => Math.Abs((double)sample)).DefaultIfEmpty().Max();
				AppendLog("Auto Test", $"Preflight noise {testCase.Name}: {backgroundDbFs:F1} dBFS, peak {backgroundPeak:F4}.");
				if (!_autoTestMicFloorReferenceDbFs.HasValue)
					_autoTestMicFloorReferenceDbFs = backgroundDbFs;
				else if (backgroundDbFs < _autoTestMicFloorReferenceDbFs.Value - 8.0)
					AppendLog("Auto Test", $"{testCase.Name}: nền Mic 2 giảm xuống {backgroundDbFs:F1} dBFS; xác minh bằng tone 1 kHz trước khi kết luận có tín hiệu.");
				if (backgroundDbFs > AutoTestStableHeadroomDbFs || backgroundPeak >= 0.05)
					return $"{testCase.Name}: nền Mic 2 {backgroundDbFs:F1} dBFS còn cao trước khi phát tone. Chờ tín hiệu dư/nhiễu tắt rồi thử lại.";
				float[] samples = await _audioEngine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, 1000.0, 3.0);
				int count = Math.Min(samples.Length, _audioEngine.RecordingSampleRate * 2);
				float[] stableSamples = samples.Skip(samples.Length - count).ToArray();
				ToneQualityMetrics quality = AdvancedAudioMeasurement.AnalyzeTone(stableSamples, _audioEngine.RecordingSampleRate, 1000.0, maxHarmonic: 9);
				UpdateOneKilohertzToneLevel(quality.SignalLevelDbFs, quality.PeakSample);
				AppendLog("Auto Test", $"Preflight {testCase.Name}: 1 kHz {quality.SignalLevelDbFs:F1} dBFS, f0 {quality.FundamentalFrequencyHz:F1} Hz, peak {quality.PeakSample:F3}.");
				if (!quality.IsValid || quality.SignalLevelDbFs < -70.0 || Math.Abs(quality.FundamentalFrequencyHz - 1000.0) > 5.0 || quality.PeakSample >= 0.995)
					return $"{testCase.Name}: không nhận đúng tone 1 kHz ở Mic 2 (mức {quality.SignalLevelDbFs:F1} dBFS, f0 {quality.FundamentalFrequencyHz:F1} Hz). Kiểm tra pin/nguồn mic, kết nối Mic 2, mute và đường phát rồi thử lại.";
			}
			return null;
		}
		catch (Exception ex)
		{
			return "Không xác minh được tín hiệu các route EXCL: " + ex.Message;
		}
		finally
		{
			_testRunner.ReuseSuiteNoiseFloor = false;
			_testRunner.SuiteSharedNoiseAssessment = null;
			_testRunner.SuiteSharedNoiseFloorByFrequency = new Dictionary<double, double>();
			_testRunner.SuiteSharedNoiseFloorDbFs = null;
			_audioEngine.Stop();
			_audioEngine.PlaybackChannel = previousPlaybackChannel;
			_audioEngine.RecordingChannel = previousRecordingChannel;
			_audioEngine.PlaybackSampleRate = previousSampleRate;
		}
	}

	private bool ConfirmAutoTestReconnect(AutoTestCaseItem test, string reason)
	{
		_audioEngine.StopAllAudio();
		// Release BEFORE the operator resets USB. Reopening a stale session after
		// replug must never resurrect the previous stream or analysis buffers.
		_audioEngine.CaptureSession.Reset();
		test.Status = "RETRY";
		test.StatusBrush = Brushes.Gold;
		test.FreqStatus = "TÍN HIỆU CHƯA ỔN ĐỊNH — CHỜ ĐO LẠI";
		test.FreqBrush = Brushes.Gold;
		test.ThdStatus = "CHƯA KẾT LUẬN";
		test.ThdBrush = Brushes.Gold;
		LblVerdict.Text = "TÍN HIỆU CHƯA ỔN ĐỊNH — LẦN 1/2";
		LblVerdict.Foreground = Brushes.Gold;
		BorderVerdict.BorderBrush = Brushes.Gold;
		bool retry = ModernMessageBox.ShowRetryCancel(Window.GetWindow(this),
			$"[{test.Id}] Tín hiệu chưa ổn định — lượt đầu chưa đạt, chưa kết luận FAIL.\n\n"
			+ "Rút dây USB FastTrack Pro rồi cắm lại, kiểm tra dây tín hiệu. Khi đã kết nối xong, nhấn nút bên dưới để đo lần 2.\n"
			+ "Chỉ báo FAIL khi cả hai lượt đều không đạt.\n\n" + reason,
			"Tín hiệu chưa ổn định — cần đo lại", out bool cancelled, "Đã cắm lại — Đo lần 2");
		if (!retry || cancelled || _discardCurrentMeasurementRequested || !_isExecutingAutoSuite)
		{
			CancelAndDiscardCurrentMeasurement();
			LblVerdict.Text = "ĐÃ DỪNG — CHƯA KẾT LUẬN";
			return false;
		}
		_testRunner.SuiteSharedNoiseAssessment = null;
		_testRunner.SuiteSharedNoiseFloorDbFs = null;
		_testRunner.SuiteSharedNoiseFloorByFrequency = new Dictionary<double, double>();
		_autoTestMicFloorReferenceDbFs = null;
		_autoTestMicContinuityFailureStatus = "";
		AutoDetectDevices();
		return true;
	}

	private int CountSavedAutoTestPasses(AutoTestResumeSession session)
	{
		if (session.Cases.Length != _autoTestCases.Count || !Directory.Exists(session.Folder)) return 0;
		int count = 0;
		for (int i = 0; i < session.Cases.Length; i++)
		{
			if (!ReferenceEquals(session.Cases[i], _autoTestCases[i]) || session.Cases[i].Status != "PASS") break;
			string graphPath = System.IO.Path.Combine(session.Folder, "FRA-" + BuildAutoTestId(i) + ".png");
			if (!_autoTestGraphPaths.Contains(graphPath, StringComparer.OrdinalIgnoreCase) || !File.Exists(graphPath)) break;
			count++;
		}
		return count;
	}

	private bool PreservePassedAutoTestsForResume()
	{
		AutoTestResumeSession? session = _autoTestResumeSession;
		if (session == null || !string.Equals(session.Folder, _autoTestSessionFolder, StringComparison.OrdinalIgnoreCase)) return false;
		int passedCount = CountSavedAutoTestPasses(session);
		if (!CanResumeAutoTestSession(session.Model)) return false;
		_autoTestGraphPaths.Clear();
		for (int i = 0; i < passedCount; i++)
			_autoTestGraphPaths.Add(System.IO.Path.Combine(session.Folder, "FRA-" + BuildAutoTestId(i) + ".png"));
		AppendLog("Auto Test", $"Giữ {passedCount} bài PASS của {session.Serial}; lần đo lại bắt đầu từ {BuildAutoTestId(passedCount)}.");
		return true;
	}

	private int GetAutoTestResumeIndex(string model)
	{
		AutoTestResumeSession? session = _autoTestResumeSession;
		if (session == null || !CanResumeAutoTestSession(model))
			return 0;
		int count = CountSavedAutoTestPasses(session);
		return count < _autoTestCases.Count ? count : 0;
	}

	private async void BtnStartAutoTest_Click(object sender, RoutedEventArgs e)
	{
        if (await TryRetryQaForCurrentSelectionAsync()) return;
		if (IsTestingBusy) return;
		_allowInlineEffectImpulse = true;
		_autoTestInterrupted = false;
		CloseRoutingScope();
		if (C360ModelDefinition.Matches(_activeInOutConfig)) { await RunC360ModelAutoTestAsync(); return; }
		using IDisposable? workflow = TryBeginRoutingWorkflow();
		if (workflow == null) return;
		BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
		BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
		LblVerdict.Text = "ĐANG ĐO...";
		LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
		if (_activeInOutConfig == null || _autoTestCases.Count == 0)
		{
			string reason = _activeInOutConfig == null
				? "Chưa có cấu hình In/Out cho model hiện tại."
				: "Danh sách bài đo Auto Test đang trống.";
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), reason + Environment.NewLine + "Vui lòng chọn lại Model hoặc kiểm tra file cấu hình trước khi bắt đầu đo tự động.", "Chưa có cấu hình Auto Test", ModernMessageBox.MessageBoxType.Warning);
			AppendLog("Auto Test", "Chặn bắt đầu đo: " + reason);
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
			LblVerdict.Text = "CHƯA CÓ CẤU HÌNH";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			return;
		}
		if ((Application.Current.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Bộ đo âm học nâng cao đang thực hiện phép đo. Vui lòng chờ hoàn tất trước khi bắt đầu Auto Test.", "Thiết bị đang bận", ModernMessageBox.MessageBoxType.Warning);
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
			LblVerdict.Text = "ĐANG CHỜ...";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
			return;
		}
		_leaveAudioReleasedAfterAutoTest = true;
		_autoTestMicFloorReferenceDbFs = null;
		_autoTestMicContinuityFailureStatus = "";
		StopRoutingHeadroomMonitor();
		_audioEngine.StopContinuousPlayback();
		_audioEngine.StopContinuousCapture();
		_audioEngine.Stop();
		bool wasExclusiveBeforeAutoTest = _audioEngine.UseExclusivePlayback;
		string playbackDeviceIdBeforeAutoTest = SelectedPlaybackDevice?.ID ?? "";
		string recordingDeviceIdBeforeAutoTest = SelectedRecordingDevice?.ID ?? "";
		int? playbackChannelBeforeAutoTest = _audioEngine.PlaybackChannel;
		int? recordingChannelBeforeAutoTest = _audioEngine.RecordingChannel;
		bool completedSuite = false;
		bool removeBluetooth = _activeInOutConfig.BluetoothAutoRemove;
		ulong? measuredBluetoothAddress = SoncaBleManager.Instance.ConnectedBluetoothAddress;
		string? bluetoothCleanupFolder = null;
		try
		{
		SelectPlaybackMode("Shared");
		_audioEngine.UseExclusivePlayback = false;
		_testRunner.ReuseSuiteNoiseFloor = false;
		_testRunner.SuiteSharedNoiseAssessment = null;
		_testRunner.SuiteSharedNoiseFloorByFrequency = new Dictionary<double, double>();
		_testRunner.SuiteSharedNoiseFloorDbFs = null;
		if (!AreAllAutoTestDevicesConnected(out string missingDevices))
		{
			bool cancelPressed;
			ModernMessageBox.ShowRetryCancelWithAutoPoll(
				message: "Cần kết nối đủ thiết bị của toàn bộ Auto Test trước khi đo:" + Environment.NewLine + missingDevices,
				owner: Window.GetWindow((DependencyObject)(object)this),
				title: "Chờ đủ thiết bị Auto Test",
				pollFunc: () => AreAllAutoTestDevicesConnected(out _),
				cancelPressed: out cancelPressed);
			if (cancelPressed || !AreAllAutoTestDevicesConnected(out _))
			{
				BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
				BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
				LblVerdict.Text = "ĐANG CHỜ...";
				LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(161, 161, 170));
				SelectPlaybackMode("Shared");
		_audioEngine.UseExclusivePlayback = false;
				return;
			}
		}
		if (!_audioEngine.UseExclusivePlayback && AudioEngine.IsWindowsMonoAudioEnabled())
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Windows đang bật Mono audio nên shared mode sẽ trộn Left và Right. Hãy tắt Settings > Accessibility > Audio > Mono audio trước khi chạy Auto Test.", "Không thể đo riêng Left / Right", ModernMessageBox.MessageBoxType.Error);
			AppendLog("Auto Test", "Đã chặn phép đo vì Windows Accessibility Mono audio đang bật; kết quả Left/Right sẽ bị trộn.");
			return;
		}
		// Keep the selected rate and length for acquisition and reference identity.
		ConfigureSelectedSweep();
		SyncPlaybackVolumeFromWindows();
		AppendLog("Auto Test", $"Dùng cùng sweep {Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate):F0} mẫu với Line chuẩn đang chọn.");
		List<string> missingStandards = GetMissingStandardReferenceTests();
		if (missingStandards.Count > 0)
		{
			string missingList = string.Join(Environment.NewLine, missingStandards.Select(s => $"• {s}"));
			ModernMessageBox.Show(
				Window.GetWindow((DependencyObject)(object)this),
				$"Chưa nạp được Line chuẩn phù hợp cho các bài đo sau:{Environment.NewLine}{Environment.NewLine}{missingList}{Environment.NewLine}{Environment.NewLine}Nếu đã đo line trước đó, hãy kiểm tra đúng model, route, mức phát và file chuẩn. Chỉ cần bấm \"TÌM LINE CHUẨN\" khi chưa có file phù hợp.",
				"Chưa đủ cấu hình Line chuẩn",
				ModernMessageBox.MessageBoxType.Warning);
			AppendLog("Auto Test", "Chặn Auto Test vì chưa có cấu hình line chuẩn: " + string.Join(", ", missingStandards));
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
			LblVerdict.Text = "CHƯA ĐỦ LINE CHUẨN";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			return;
		}
		SaveConfig();
		_discardCurrentMeasurementRequested = false;
		MainWindow mainWindow = Application.Current.MainWindow as MainWindow;
		string currentModel = mainWindow?.ComboModels?.SelectedItem?.ToString()?.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(currentModel))
		{
			currentModel = "MISAM";
		}
		int resumeIndex = GetAutoTestResumeIndex(currentModel);
		bool resumingAutoTest = CanResumeAutoTestSession(currentModel);
		if (!resumingAutoTest) _reviewedAutoTestCase = null;
		string autoGeneratedId = resumingAutoTest ? _autoTestResumeSession!.Serial : GenerateAutoTestId(currentModel);
		// Every automatic run receives a new product identity. Clear any product
		// left by a previous QR/manual session before resolving this run on upload.
		SetCurrentProduct(null);
		if (mainWindow?.TxtSerialNumber != null)
		{
			mainWindow.TxtSerialNumber.Text = autoGeneratedId;
		}
		if (TxtProductSerial != null)
		{
			TxtProductSerial.Text = autoGeneratedId;
			TxtProductSerial.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153));
		}
		string text = autoGeneratedId;
		string path = DateTime.Now.ToString("yyyyMMdd");
		string text2 = DateTime.Now.ToString("yyyyMMdd_HHmmss");
		string path2 = text + "_" + text2;
		string path3 = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail data");
		_autoTestSessionFolder = resumingAutoTest ? _autoTestResumeSession!.Folder : System.IO.Path.Combine(path3, path, path2);
		if (!resumingAutoTest)
		{
			_autoTestGraphPaths.Clear();
			_autoTestResumeSession = new AutoTestResumeSession(currentModel, autoGeneratedId, _autoTestSessionFolder,
				_autoTestCases.ToArray(), ParseDoubleSafe(TxtFreqTolerance.Text, 3.0));
		}
		else
			AppendLog("Auto Test", $"Tiếp tục phiên {autoGeneratedId} từ {BuildAutoTestId(resumeIndex)}; giữ {resumeIndex} bài PASS và ảnh đã lưu.");
		try
		{
			if (!Directory.Exists(_autoTestSessionFolder))
			{
				Directory.CreateDirectory(_autoTestSessionFolder);
			}
		}
		catch (Exception ex)
		{
			AppendLog("Error", "Could not create auto test session folder: " + ex.Message);
			_autoTestSessionFolder = null;
		}
		BtnStart.IsEnabled = false;
		BtnStartAutoTest.IsEnabled = false;
		BtnSaveStandardReference.IsEnabled = false;
		BtnNoiseTest.IsEnabled = false;
		BtnSaveStandard.IsEnabled = false;
		_isExecutingAutoSuite = true;
		bool suitePassed = true;
		bool devicesReadyForServerUpload = true;
		bool invalidQaAcquisition = false;
		for (int i = 0; i < _autoTestCases.Count; i++)
		{
			AutoTestCaseItem autoTestCase = _autoTestCases[i];
			if (i < resumeIndex)
				continue;
			autoTestCase.ResponseFrequencies = Array.Empty<double>();
			autoTestCase.ResponseLevels = Array.Empty<double>();
			autoTestCase.DistortionMeasurements = new Dictionary<double, ToneQualityMetrics>();
			autoTestCase.AdditionalResults = Array.Empty<ConfiguredTestResult>();
			if (i == resumeIndex)
			{
				autoTestCase.Status = "RUNNING";
				autoTestCase.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				autoTestCase.FreqStatus = "ĐANG ĐO";
				autoTestCase.FreqBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				autoTestCase.ThdStatus = "ĐANG ĐO";
				autoTestCase.ThdBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			}
			else
			{
				autoTestCase.Status = "WAITING";
				autoTestCase.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
				autoTestCase.FreqStatus = "ĐANG CHỜ";
				autoTestCase.FreqBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
				autoTestCase.ThdStatus = "ĐANG CHỜ";
				autoTestCase.ThdBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
			}
		}
		var attemptCounts = new Dictionary<int, int>();
        var attemptVotes = new Dictionary<int,List<string>>();
		for (int testIndex = resumeIndex; testIndex < _autoTestCases.Count; testIndex++)
		{
			AutoTestCaseItem test2 = _autoTestCases[testIndex];
			if (!_isExecutingAutoSuite)
			{
				break;
			}
			string autoTestId = BuildAutoTestId(testIndex);
			_autoTestAttempt = attemptCounts.GetValueOrDefault(testIndex) + 1;
			attemptCounts[testIndex] = _autoTestAttempt;
			_currentRunningTestCase = test2;
			test2.AdditionalResults = Array.Empty<ConfiguredTestResult>();
			if (!ValidateAutoTestCaseConfiguration(test2))
			{
				suitePassed = false; invalidQaAcquisition = true;
				AppendLog("Auto Test", test2.AdditionalStatus);
				break;
			}
			_currentTestSuccess = null;
			test2.Status = "RUNNING";
			test2.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			test2.FreqStatus = "ĐANG ĐO";
			test2.FreqBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			test2.ThdStatus = "ĐANG ĐO";
			test2.ThdBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
			if (_reviewedAutoTestCase == null)
            { LblVerdict.Text = "ĐANG ĐO " + autoTestId + "..."; LblVerdict.Foreground = Brushes.Gold; }
			Stopwatch routeTransition = Stopwatch.StartNew();
			_audioEngine.StopAllAudio();
			ApplyTestCaseConfigCore(test2.Config, selectDevices: false);
			bool flag = false;
			while (!flag)
			{
				if (!_isExecutingAutoSuite)
				{
					break;
				}
				string playbackConfigKey = test2.Config.PlaybackOut ?? "";
				string playbackDeviceName = "";
				_activeInOutConfig.Devices.Input?.TryGetValue(playbackConfigKey, out playbackDeviceName);
				string recordingConfigKey = test2.Config.RecordingIn ?? "";
				string recordingDeviceName = "";
				_activeInOutConfig.Devices.Output?.TryGetValue(recordingConfigKey, out recordingDeviceName);
				using var playbackLease = new DeviceEnumerationLease(_audioEngine.GetPlaybackDevices());
				List<MMDevice> playbackDevices = playbackLease.Devices;
				using var recordingLease = new DeviceEnumerationLease(_audioEngine.GetRecordingDevices());
				List<MMDevice> recordingDevices = recordingLease.Devices;
				MMDevice matchedPlayback = ResolvePlaybackDevice(playbackConfigKey, playbackDeviceName, playbackDevices, requireConfiguredMatch: true);
				MMDevice matchedRecording = ResolveRecordingDevice(recordingConfigKey, recordingDeviceName, recordingDevices, requireConfiguredMatch: true);
				if (matchedPlayback != null && matchedRecording != null)
				{
					flag = true;
					// Refresh UI endpoint wrappers only if the current inventory is missing this route.
					if (!ComboPlayback.Items.Cast<DeviceItem>().Any(i => i.Device.ID == matchedPlayback.ID)
						|| !ComboRecording.Items.Cast<DeviceItem>().Any(i => i.Device.ID == matchedRecording.ID))
						AutoDetectDevices();
					_isAutoSelectingDevices = true;
					try
					{
					DeviceItem deviceItem = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault((DeviceItem i) => i.Device.ID == matchedPlayback.ID);
					if (deviceItem != null)
					{
						ComboPlayback.SelectedItem = deviceItem;
					}
					DeviceItem deviceItem2 = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault((DeviceItem i) => i.Device.ID == matchedRecording.ID);
					if (deviceItem2 != null)
					{
						ComboRecording.SelectedItem = deviceItem2;
					}
					}
					finally { _isAutoSelectingDevices = false; }
					UpdateSelectedDeviceFormats();
					_audioEngine.PlaybackSampleRate = 44100;
					_audioEngine.PlaybackChannel = ResolvePlaybackChannel(test2.Config);
					_audioEngine.RecordingChannel = ResolveRecordingChannel(test2.Config);
					_testRunner.PlaybackLevelDbFs = PlaybackLevelDbfs;
					SyncChannelSelectionsFromEngine();
					string playbackSide = ((_audioEngine.PlaybackChannel == 0) ? "Left" : ((_audioEngine.PlaybackChannel == 1) ? "Right" : "Stereo"));
					string recordingSide = ((_audioEngine.RecordingChannel == 0) ? "Mic 1 / Left" : ((_audioEngine.RecordingChannel == 1) ? "Mic 2 / Right" : "Stereo mix"));
					AppendLog("Auto Test", $"[{test2.Id}] Route: {matchedPlayback.FriendlyName} ({playbackSide}) -> {matchedRecording.FriendlyName} ({recordingSide}), {(_audioEngine.UseExclusivePlayback ? "EXCL" : "Shared")}. Đợi thiết bị ổn định...");
					int settleMs = AutoTestRouteSettleMilliseconds;
					int remainingBetweenMeasurementsMs = Math.Max(0, settleMs - (int)routeTransition.ElapsedMilliseconds);
					AppendLog("Auto Test", $"[{test2.Id}] Chuyển route và chờ {settleMs} ms (còn {remainingBetweenMeasurementsMs} ms).");
					if (remainingBetweenMeasurementsMs > 0)
						await Task.Delay(remainingBetweenMeasurementsMs);
				}
				else
				{
					bool cancelPressed = false;
					ModernMessageBox.ShowRetryCancelWithAutoPoll(message: $"Chưa phát hiện ngõ in [{playbackConfigKey}] - out [{recordingConfigKey}]\n\nVui lòng kiểm tra lại thiết bị kết nối.", owner: Window.GetWindow((DependencyObject)(object)this), title: "Chưa tìm thấy ngõ kết nối", pollFunc: delegate
					{
						using var pollPlaybackLease = new DeviceEnumerationLease(_audioEngine.GetPlaybackDevices());
						List<MMDevice> playbackDevices2 = pollPlaybackLease.Devices;
						using var pollRecordingLease = new DeviceEnumerationLease(_audioEngine.GetRecordingDevices());
						List<MMDevice> recordingDevices2 = pollRecordingLease.Devices;
						MMDevice mMDevice = ResolvePlaybackDevice(playbackConfigKey, playbackDeviceName, playbackDevices2, requireConfiguredMatch: true);
						MMDevice mMDevice2 = ResolveRecordingDevice(recordingConfigKey, recordingDeviceName, recordingDevices2, requireConfiguredMatch: true);
						return mMDevice != null && mMDevice2 != null;
					}, cancelPressed: out cancelPressed);
					if (cancelPressed)
					{
						devicesReadyForServerUpload = false;
						CancelAndDiscardCurrentMeasurement();
						break;
					}
				}
			}
			if (!_isExecutingAutoSuite)
			{
				suitePassed = false;
				test2.Status = "INVALID";
				test2.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
				break;
			}
			lock (_freqs)
			{
				_freqs.Clear();
				_dbValues.Clear();
			}
			if (_reviewedAutoTestCase == null)
            {
                PlotFreqResponse.Plot.Clear(); ClearThdPlots(); InitCharts();
			TxtFreqStatus.Text = "";
			TxtThdStatus.Text = "";
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
			LblVerdict.Text = "ĐANG ĐO " + autoTestId + "...";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
            }
			_testRunner.FreqResponseToleranceDb = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
			_testRunner.ThdLimitPercent = ParseDoubleSafe(TxtThdLimit.Text, 0.5);
			double? rubBuzzTestFreq = test2.Config.RubBuzzTestFreq;
			_testRunner.IsRubBuzzTest = rubBuzzTestFreq.HasValue;
			if (rubBuzzTestFreq.HasValue)
			{
				_testRunner.RubBuzzTestFreq = rubBuzzTestFreq.Value;
				_testRunner.RubBuzzLimit = test2.Config.RubBuzzLimit ?? 1.5;
			}
			CheckAndLoadStandardDevice();
			_testRunner.StandardCurve = _standardCurve;
			ConfigureProductionMeasurement(test2.Config);
			_testRunner.EnableNoiseDiagnostics = true;
			MMDevice playbackDevice = SelectedPlaybackDevice;
			MMDevice recordingDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;
			if (playbackDevice == null || recordingDevice == null)
			{
				devicesReadyForServerUpload = false;
				suitePassed = false;
				invalidQaAcquisition = true;
				test2.Status = "INVALID";
				test2.FreqStatus = "THIẾU THIẾT BỊ — CHƯA ĐO";
				test2.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
				break;
			}
			if (!rubBuzzTestFreq.HasValue)
			{
				test2.FreqStatus = "ĐANG ỔN ĐỊNH ROUTE";
				AppendLog("Auto Test", $"[{test2.Id}] Route đã ổn định; TestRunner sẽ thu nền Mic 2 một lần trước khi phát và dừng nếu nền vượt ngưỡng.");
				lock (_freqs)
				{
					_freqs.Clear();
					_dbValues.Clear();
				}
				if (_reviewedAutoTestCase == null)
                { PlotFreqResponse.Plot.Clear(); ClearThdPlots(); InitCharts(); }
				_currentTestSuccess = null;
				test2.Status = "RUNNING";
				test2.StatusBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				AppendLog("Auto Test", "[" + test2.Id + "] Bắt đầu một lượt đo chính, không chạy sweep làm nóng.");
			}
			test2.Acquisition = new AudioQaAcquisitionSnapshot(PlaybackLevelDbfs, _audioEngine.PlaybackSampleRate, 0,
				_audioEngine.UseExclusivePlayback, _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel,
				playbackDevice.ID, recordingDevice.ID, _testRunner.ThdLimitPercent, DateTimeOffset.UtcNow)
                { PlaybackDeviceName = playbackDevice.FriendlyName, RecordingDeviceName = recordingDevice.FriendlyName };
			await RunTrackedTestAsync(playbackDevice, recordingDevice);
			test2.Acquisition = (test2.Acquisition with { RecordingSampleRate = _audioEngine.RecordingSampleRate,
				PlaybackLevelDbfs = rubBuzzTestFreq.HasValue && double.IsFinite(_testRunner.LastRubBuzzPlaybackLevelDbFs)
					? _testRunner.LastRubBuzzPlaybackLevelDbFs : test2.Acquisition.PlaybackLevelDbfs })
                .WithSampleRates(_audioEngine.LastMeasurementSampleRates, _audioEngine.MeasurementSampleRateGuard != null);
			bool cancelled = _discardCurrentMeasurementRequested || !_isExecutingAutoSuite;
			bool acquisitionInvalid = AutoTestAcquisitionInvalid;
			string acquisitionReason = _lastTrackedTestError;
			if (!cancelled && !acquisitionInvalid)
			{
				try
				{
					if (!await CheckAutoTestMicContinuityAsync(recordingDevice, test2.Id))
					{
						acquisitionInvalid = true;
						acquisitionReason = _autoTestMicContinuityFailureStatus;
					}
				}
				catch (Exception ex)
				{
					acquisitionInvalid = true;
					acquisitionReason = GetMeasurementErrorMessage(ex);
				}
			}
			if (string.IsNullOrWhiteSpace(acquisitionReason) && acquisitionInvalid)
				acquisitionReason = _testRunner.Steps.FirstOrDefault(step => step.Status is "Fail" or "Invalid")?.Details ?? "Bản thu chưa hợp lệ.";
			bool frequencyPassed = _testRunner.Steps.ElementAtOrDefault(1)?.Status == "Pass";
			bool distortionPassed = AutoTestDistortionPassed;
			if (test2.Config.HasAdditionalTests && !cancelled)
			{
				test2.Status = "RUNNING"; test2.StatusBrush = Brushes.Gold;
				if (_reviewedAutoTestCase == null) { LblVerdict.Text = test2.Id + " — ĐANG KIỂM TRA BỔ SUNG"; LblVerdict.Foreground = Brushes.Gold; }
			}
			await RunTrackedConfiguredTestsAsync(test2, playbackDevice, recordingDevice, !acquisitionInvalid && !cancelled);
			cancelled |= _discardCurrentMeasurementRequested || !_isExecutingAutoSuite;
			bool additionalInvalid = test2.AdditionalResults.Any(r => r.Invalid);
			bool passed = frequencyPassed && distortionPassed && !acquisitionInvalid && test2.AdditionalResults.All(r => r.Passed);
			if (cancelled || _discardCurrentMeasurementRequested || !_isExecutingAutoSuite)
			{
				suitePassed = false;
				break;
			}
			if (_testRunner.MissingFrequencyLimits)
			{
				test2.Status = "INVALID";
				test2.FreqStatus = "CHƯA ĐỦ LINE CHUẨN";
				test2.StatusBrush = Brushes.Gold;
				suitePassed = false;
				invalidQaAcquisition = true;
				devicesReadyForServerUpload = false;
				break;
			}
			if (_testRunner.LastResponseLimitAssessment?.InvalidKind == ResponseLimitInvalidKind.NothingToEvaluate)
			{
				test2.Status = "INVALID";
				test2.FreqStatus = "CẤU HÌNH HÕM LOẠI HẾT ĐIỂM XÉT";
				test2.StatusBrush = Brushes.Gold;
				suitePassed = false;
				invalidQaAcquisition = true;
				devicesReadyForServerUpload = false;
				break;
			}
            if (!attemptVotes.TryGetValue(testIndex,out var votes)) attemptVotes[testIndex]=votes=new();
            votes.Add(passed?"PASS":acquisitionInvalid || additionalInvalid?"INVALID":"FAIL");
            var vote=TestRunVote.Count(votes,test2.Config.MaximumRuns,test2.Config.RequiredPasses);
            if (SingleRunMarginPolicy.CanAccept(test2.Config.IsSingleRunMarginEnabled, test2.Config.EffectiveSingleRunMarginDb,
                votes.Count, passed && !additionalInvalid ? "PASS" : "FAIL", false, _testRunner.LastResponseLimitAssessment, out double margin))
            {
                vote = new TestRunVote("PASS", 1, 1, false);
                test2.SingleRunEarlyPassSummary = SingleRunMarginPolicy.Describe(margin, test2.Config.EffectiveSingleRunMarginDb);
                AppendLog("Auto Test", test2.SingleRunEarlyPassSummary);
            }
            else test2.SingleRunEarlyPassSummary = "";
            AppendLog("Auto Test",$"[{test2.Id}] {vote.Passes}/{vote.Attempts} lượt đạt; cần {test2.Config.RequiredPasses}/{test2.Config.MaximumRuns}.");
			if (vote.NeedsAnotherRun)
			{
				if (acquisitionInvalid && _autoTestAttempt == 1 && !ConfirmAutoTestReconnect(test2, acquisitionReason))
				{
					suitePassed = false;
					devicesReadyForServerUpload = false;
					break;
				}
				// Resolve endpoints again; only the completed vote reaches final QA.
				testIndex--;
				continue;
			}
			if (vote.Status == "INVALID")
			{
				test2.Status = "INVALID";
				test2.FreqStatus = $"{vote.Passes}/{vote.Attempts} LƯỢT ĐẠT — CHƯA ĐỦ PHÉP ĐO HỢP LỆ";
				test2.ThdStatus = additionalInvalid ? test2.AdditionalStatus : acquisitionReason;
				test2.StatusBrush = Brushes.Gold;
				suitePassed = false;
				invalidQaAcquisition = true;
				break;
			}
            passed=vote.Status=="PASS";
			if (_testRunner.IsRubBuzzTest || double.IsFinite(_testRunner.LastMeasuredThdPercent))
			{
				test2.ThdStatus = (_testRunner.IsRubBuzzTest ? _testRunner.Steps[1].Details : _testRunner.Steps[2].Details) + $" · {vote.Passes}/{vote.Attempts} lượt đạt";
				test2.ThdBrush = (_testRunner.IsRubBuzzTest ? frequencyPassed : distortionPassed) ? Brushes.MediumSpringGreen : Brushes.Salmon;
			}
			if (!passed) suitePassed = false;
			_reviewedAutoTestCase = null;
			CaptureAutoTestResultForReview(test2);
			test2.Status = passed ? "PASS" : "FAIL";
			test2.StatusBrush = passed ? Brushes.MediumSpringGreen : Brushes.Salmon;
            DisplayAutoTestResult(test2);
            SaveFailureScreenshots(autoTestId, forceAlways: true);
			if (!passed)
			{
				AppendLog("Auto Test", $"[{test2.Id}] Bản thu hợp lệ nhưng vượt giới hạn FEQ/THD hoặc phép kiểm tra bổ sung. Dừng tại bài này; bấm Auto Test để đo lại từ đây.");
				break;
			}
		}

		string completedAutoTestSessionFolder = _autoTestSessionFolder;
		bluetoothCleanupFolder = completedAutoTestSessionFolder;
		completedSuite = _autoTestCases.All(test => test.Status is "PASS" or "FAIL" or "INVALID");
		suitePassed &= !_discardCurrentMeasurementRequested && _autoTestCases.All(test => test.Status == "PASS");
        if (!_discardCurrentMeasurementRequested && ChkSendToServer.IsChecked == true
            && _autoTestCases.All(t => t.Status is "PASS" or "FAIL"))
            RememberCompletedMeasurement(text, currentModel, completedAutoTestSessionFolder, suitePassed, devicesReadyForServerUpload);
		if (suitePassed)
			_autoTestResumeSession = null;
		else if (!PreservePassedAutoTestsForResume())
			_autoTestResumeSession = null;
		_isExecutingAutoSuite = false;
        if (_reviewedAutoTestCase != null) DisplayAutoTestResult(_reviewedAutoTestCase);
		_autoTestSessionFolder = null;
		_currentRunningTestCase = null;
		BtnStart.IsEnabled = true;
		BtnStartAutoTest.IsEnabled = !suitePassed;
		BtnSaveStandardReference.IsEnabled = true;
		BtnNoiseTest.IsEnabled = true;
		await RestoreUserRoutingAfterAutoTestAsync(playbackDeviceIdBeforeAutoTest, recordingDeviceIdBeforeAutoTest, playbackChannelBeforeAutoTest, recordingChannelBeforeAutoTest);
		SelectPlaybackMode("Shared");
		_audioEngine.UseExclusivePlayback = false;
		if (_discardCurrentMeasurementRequested)
		{
			BtnSaveStandard.IsEnabled = false;
			BtnStartAutoTest.IsEnabled = true;
			return;
		}
		SetFinalVerdict(suitePassed);
		if (invalidQaAcquisition && _autoTestCases.Any(test => test.Status == "FAIL"))
		{
			LblVerdict.Text = "FAIL — 2 LẦN ĐO CHƯA HỢP LỆ";
			LblVerdict.Foreground = Brushes.Salmon;
		}
		else if (invalidQaAcquisition && !_testRunner.MissingFrequencyLimits)
		{
			LblVerdict.Text = "CHƯA ĐO HỢP LỆ — CHƯA KẾT LUẬN";
			LblVerdict.Foreground = Brushes.Gold;
		}
		if (suitePassed)
		{
			BtnStartAutoTest.IsEnabled = true;
		}
		if (_autoTestCases.Any(test => test.Status is "WAITING" or "RUNNING" or "RETRY" or "INVALID"))
		{
			AppendLog("Server", "Chưa upload phiên Auto Test bị ngắt; cần đo tiếp các bài còn lại trước khi kết luận tổng.");
			return;
		}
		if (invalidQaAcquisition || ChkSendToServer.IsChecked != true)
		{
			return;
		}
		await UploadCompletedAutoTestAsync(text, currentModel, completedAutoTestSessionFolder, suitePassed, devicesReadyForServerUpload);
		}
		catch (Exception ex)
		{
			AppendLog("Auto Test", "Dừng do lỗi: " + GetMeasurementErrorMessage(ex));
			if (!PreservePassedAutoTestsForResume())
				_autoTestResumeSession = null;
			_isExecutingAutoSuite = false;
			_currentRunningTestCase = null;
			_autoTestSessionFolder = null;
			BtnStart.IsEnabled = true;
			BtnStartAutoTest.IsEnabled = true;
			BtnSaveStandardReference.IsEnabled = true;
			BtnNoiseTest.IsEnabled = true;
			await RestoreUserRoutingAfterAutoTestAsync(playbackDeviceIdBeforeAutoTest, recordingDeviceIdBeforeAutoTest, playbackChannelBeforeAutoTest, recordingChannelBeforeAutoTest);
			SelectPlaybackMode("Shared");
		_audioEngine.UseExclusivePlayback = false;
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), GetMeasurementErrorMessage(ex), "Auto Test chưa hoàn tất", ModernMessageBox.MessageBoxType.Error);
		}
		finally
		{
			_audioEngine.Stop();
			StopRoutingHeadroomMonitor();
			_audioEngine.StopContinuousPlayback();
			_audioEngine.StopContinuousCapture();
			await CompleteAutoTestBluetoothAsync(completedSuite, removeBluetooth, measuredBluetoothAddress, bluetoothCleanupFolder);
			foreach (var row in _autoTestCases.Where(r => r.Status is "RUNNING" or "RETRY"))
			{ row.Status = "WAITING"; row.StatusBrush = Brushes.Gray; }
			_autoTestAttempt = 0;
			_leaveAudioReleasedAfterAutoTest = false;
			AppendLog("Auto Test", "Đã giải phóng phát/thu sau Auto Test; khôi phục Headroom khi đủ ngõ phát và ngõ thu.");
			if (base.IsLoaded)
				Dispatcher.BeginInvoke((Action)MaintainRoutingHeadroomMonitor, DispatcherPriority.Background);
		}
	}

	private static bool ProductMatchesUploadIdentity(ProductInfo? product, string identity)
	{
		if (product == null || string.IsNullOrWhiteSpace(identity)) return false;
		string expected = identity.Trim();
		return string.Equals(product.Id?.Trim(), expected, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(product.ProductCode?.Trim(), expected, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(product.SerialNumber?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
	}

	private bool _legacyStandardsImported;

	private string GetStandardsDirectory()
	{
		string directory = System.IO.Path.Combine(GetUserDataDirectory(), "save standards");
		Directory.CreateDirectory(directory);
		if (_legacyStandardsImported) return directory;
		_legacyStandardsImported = true;
		string legacyRoot = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "save standards");
		try
		{
			if (Directory.Exists(legacyRoot))
			{
				foreach (string source in Directory.EnumerateFiles(legacyRoot, "standard_*.csv", SearchOption.AllDirectories))
				{
					string target = System.IO.Path.Combine(directory, System.IO.Path.GetRelativePath(legacyRoot, source));
					Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
					if (!File.Exists(target)) File.Copy(source, target);
				}
			}
			foreach (string source in Directory.EnumerateFiles(AppDomain.CurrentDomain.BaseDirectory, "standard_*.csv"))
			{
				string target = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(source));
				if (!File.Exists(target)) File.Copy(source, target);
			}
		}
		catch (Exception ex)
		{
			AppendLog("Standard", "Không nhập được line chuẩn từ thư mục bản cũ: " + ex.Message);
		}
		return directory;
	}

	private bool HasDistinctDisplayIdFallback =>
		_appliedTestConfig != null
		&& !string.IsNullOrWhiteSpace(_appliedTestConfig.ReferenceTestId)
		&& !string.Equals(_appliedTestConfig.ReferenceTestId.Trim(), _appliedTestConfig.id?.Trim(), StringComparison.OrdinalIgnoreCase);

	private string GetStandardDeviceKey(bool useCurrentDisplayId = false) => GetStandardDeviceKeyForBasis(_testRunner.CurrentFrequencyResponseLevelBasis, useCurrentDisplayId: useCurrentDisplayId);
	private string AppliedModelTestFingerprint(bool useCurrentDisplayId = false)
    {
        if(_appliedTestConfig==null)return "";
        var acquisition=JsonSerializer.SerializeToNode(_appliedTestConfig)!.AsObject();
        acquisition.Remove(nameof(TestConfig.MaximumRuns));acquisition.Remove(nameof(TestConfig.RequiredPasses));
        if (!useCurrentDisplayId && !string.IsNullOrWhiteSpace(_appliedTestConfig.ReferenceTestId))
        {
            acquisition["id"] = _appliedTestConfig.ReferenceTestId.Trim();
        }
        acquisition.Remove(nameof(TestConfig.ReferenceTestId));
        C360Reference.RemoveJudgementFields(acquisition, _appliedTestConfig, _audioEngine?.PlaybackSampleRate ?? 44100);
        string json=_activeInOutConfig?.TestVolume is {IsEmpty:false} volume
            ? JsonSerializer.Serialize(new{test=acquisition,TestVolume=volume}) : acquisition.ToJsonString();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    }

	private string GetStandardDeviceKeyForBasis(string levelBasis, bool useLegacyPlaybackName = false, double? toleranceOverride = null, bool useCurrentDisplayId = false)
	{
		if (ComboPlayback == null || ComboRecording == null || TxtFreqTolerance == null || TxtThdLimit == null)
		{
			return "";
		}
		string value = _appliedTestConfig != null && !useLegacyPlaybackName
			? "MODEL_" + MakeSafeFileToken((Application.Current?.MainWindow as MainWindow)?.ComboModels?.SelectedItem?.ToString() ?? "UNKNOWN")
				+ "_OUT_" + MakeSafeFileToken(_appliedTestConfig.PlaybackOut ?? "UNKNOWN")
			: ((ComboPlayback.SelectedItem is DeviceItem deviceItem) ? deviceItem.DisplayName : "UnknownOut");
		string value2 = ((ComboRecording.SelectedItem is DeviceItem deviceItem2) ? deviceItem2.DisplayName : "UnknownIn");
		double value3 = PlaybackLevelDbfs;
		double value4 = toleranceOverride ?? ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
		object obj;
		if (!AudioEngine.flagGenerateSeperateSine)
		{
			TestRunner testRunner = _testRunner;
			if (testRunner == null || !testRunner.UseLogSweepFrequencyResponse)
			{
				TestRunner testRunner2 = _testRunner;
				obj = ((testRunner2 != null && testRunner2.UseCombinedMultitoneFrequencyResponse) ? $"MULTITONE_ALL_P1100_D{_testRunner.MultitoneDurationSeconds:0.##}" : $"MULTITONE_P1100_D{_testRunner.MultitoneDurationSeconds:0.##}");
			}
			else
			{
			obj = $"LOGSWEEP_{ResponseSmoothing.AnalysisId}_N{Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate):F0}";
			}
		}
		else
		{
			obj = "SINE";
		}
		string value5 = (string)obj;
		MicrophoneCalibration microphoneCalibration = ResolveFeqMicrophoneCalibration();
		string value6 = ((microphoneCalibration == null) ? "NO_CAL" : ("CAL_" + MakeSafeFileToken(microphoneCalibration.Name))) + (_appliedTestConfig?.MicrophoneCalibration is { } attachment ? "_" + attachment.Sha256 : "");
		int? recordingChannel = _audioEngine.RecordingChannel;
		object obj2;
		if (recordingChannel.HasValue)
		{
			int valueOrDefault = recordingChannel.GetValueOrDefault();
			obj2 = $"CH{valueOrDefault + 1}";
		}
		else
		{
			obj2 = "CHMIX";
		}
		string value7 = (string)obj2;
		string playbackChannelKey = _audioEngine.PlaybackChannel == 0 ? "OUT_L" : _audioEngine.PlaybackChannel == 1 ? "OUT_R" : "OUT_LR";
		string value8 = $"FS{Math.Round((_testRunner?.PlaybackFrequencyScale ?? 1.0) * 1000000.0):F0}";
		string playbackMode = (_audioEngine.UseExclusivePlayback ? "EXCL" : "SHARED");
		string text = $"{levelBasis}_{_referenceRouteKey}_{value5}_{playbackChannelKey}_{value7}_{value8}_{value6}_{value}_IN_{value2}_WV_{value3:F0}_SR{_audioEngine.PlaybackSampleRate}_PM{playbackMode}_T_{value4:F1}";
		if (_appliedTestConfig != null)
		{
			text += "_CFG" + AppliedModelTestFingerprint(useCurrentDisplayId)[..16];
		}
		char[] invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
		char[] array = invalidFileNameChars;
		char[] array2 = array;
		char[] array3 = array2;
		foreach (char oldChar in array3)
		{
			text = text.Replace(oldChar, '_');
		}
		return text.Replace(' ', '_');
	}

	private async Task<bool> UploadCompletedAutoTestCoreAsync(CompletedMeasurementPendingQaSession saved)
	{
        string fallbackSerial = saved.Serial, modelForSession = saved.Model;
        string completedAutoTestSessionFolder = saved.Folder;
        bool suitePassed = saved.Passed, devicesReadyForServerUpload = saved.DevicesReadyForUpload;
        string serialNumberForUpload = saved.Serial;
		string modelForUpload = modelForSession;
		if (string.IsNullOrWhiteSpace(modelForUpload))
		{
			modelForUpload = "MISAM";
		}
		if (string.IsNullOrWhiteSpace(serialNumberForUpload) || string.Equals(serialNumberForUpload, "CHƯA CÓ SERIAL", StringComparison.OrdinalIgnoreCase) || string.Equals(serialNumberForUpload, "CHUA CO SERIAL", StringComparison.OrdinalIgnoreCase))
		{
			serialNumberForUpload = fallbackSerial;
		}
		if (!ProductMatchesUploadIdentity(ServerEngine.CurrentProduct, serialNumberForUpload))
		{
			ServerEngine.CurrentProduct = null;
		}
		if (ServerEngine.CurrentProduct == null)
		{
			try
			{
				ProductResolveResult resolveResult = await ServerEngine.ResolveProductAsync(serialNumberForUpload, serialNumberForUpload, modelForUpload);
				if (resolveResult?.Product != null)
				{
					ServerEngine.CurrentProduct = resolveResult.Product;
				}
				else
				{
					ProductInfo productInfo = await ServerEngine.CheckProductStatusAsync(serialNumberForUpload, modelForUpload);
					if (productInfo != null)
					{
						ServerEngine.CurrentProduct = productInfo;
					}
				}
			}
			catch (Exception ex2)
			{
				Exception ex3 = ex2;
				Exception exResolve = ex3;
				AppendLog("Server", "Không thể resolve sản phẩm tự động trên Server: " + exResolve.Message);
			}
			if (ServerEngine.CurrentProduct == null)
			{
				ServerEngine.CurrentProduct = new ProductInfo
				{
					Id = serialNumberForUpload,
					ProductCode = serialNumberForUpload,
					SerialNumber = serialNumberForUpload,
					Model = modelForUpload
				};
			}
		}
		if (!ProductMatchesUploadIdentity(ServerEngine.CurrentProduct, serialNumberForUpload))
		{
			AppendLog("Server", $"Không upload QA: sản phẩm server không khớp ID phiên đo {serialNumberForUpload}.");
            throw new IOException($"Sản phẩm server không khớp ID phiên đo {serialNumberForUpload}.");
		}
		if (!suitePassed && !devicesReadyForServerUpload)
		{
			AppendLog("Server", "Skipped FAIL upload because not all configured audio devices were connected.");
            throw new IOException("Chưa xác nhận đủ thiết bị audio theo cấu hình.");
		}
		List<string> list2 = saved.GraphPaths.Where(File.Exists).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		string text4 = completedAutoTestSessionFolder ?? AppDomain.CurrentDomain.BaseDirectory;
		if (list2.Count != saved.Cases.Length)
		{
			AppendLog("Server", $"Không upload QA: chỉ lưu được {list2.Count}/{saved.Cases.Length} ảnh đáp tuyến.");
            throw new IOException($"Chỉ lưu được {list2.Count}/{saved.Cases.Length} ảnh QA.");
		}
		string uploadSessionId = System.IO.Path.GetFileName(completedAutoTestSessionFolder ?? text4);
		AppendLog("Server", $"Upload một log QA cho sản phẩm {serialNumberForUpload}, phiên {uploadSessionId}, gồm {saved.Cases.Length} bài đo.");
        if (!ReferenceEquals(saved, _pendingQaCompletionSession))
            throw new OperationCanceledException("Đã bỏ phiên QA; không gửi kết quả sau khi đổi model hoặc xóa kết quả.");
		if (!(await ServerEngine.UploadAudioQaResultAsync(steps: saved.Cases.Select(delegate(AutoTestCaseItem autoTestCaseItem, int index)
		{
			string text7 = BuildAutoTestId(index);
			string text8 = ((!string.IsNullOrEmpty(autoTestCaseItem.Config?.PlaybackOut)) ? autoTestCaseItem.Config.PlaybackOut : "Line In 3.5mm");
			string text9 = ((!string.IsNullOrEmpty(autoTestCaseItem.Config?.RecordingIn)) ? autoTestCaseItem.Config.RecordingIn : "Measurement Mic");
			string value4 = null;
			string value5 = null;
			_activeInOutConfig?.Devices?.Input?.TryGetValue(text8, out value4);
			_activeInOutConfig?.Devices?.Output?.TryGetValue(text9, out value5);
				var acquisition = autoTestCaseItem.Acquisition;
                if (!string.IsNullOrWhiteSpace(acquisition?.PlaybackDeviceName)) value4 = acquisition.PlaybackDeviceName;
                if (!string.IsNullOrWhiteSpace(acquisition?.RecordingDeviceName)) value5 = acquisition.RecordingDeviceName;
				string value6 = ((string.IsNullOrWhiteSpace(value4) || text8.Contains(value4, StringComparison.OrdinalIgnoreCase)) ? text8 : (text8 + " (" + value4 + ")"));
			string value7 = ((string.IsNullOrWhiteSpace(value5) || text9.Contains(value5, StringComparison.OrdinalIgnoreCase)) ? text9 : (text9 + " (" + value5 + ")"));
			double value8 = acquisition?.PlaybackLevelDbfs ?? autoTestCaseItem.Config?.PlaybackLevelDbfs ?? double.NaN;
			double value9 = acquisition?.ThdLimitPercent ?? autoTestCaseItem.Config?.Thd1000Hz ?? 0.5;
			string details = $"Out: {value6}{Environment.NewLine}In: {value7}{Environment.NewLine}FRA: {autoTestCaseItem.FreqStatus}{Environment.NewLine}THD&Detail: {autoTestCaseItem.ThdStatus}{Environment.NewLine}Noise: {autoTestCaseItem.NoiseStatus}{Environment.NewLine}Playback Out: {text8}{Environment.NewLine}Recording In: {text9}{Environment.NewLine}Signal Level: {value8:F1} dBFS{Environment.NewLine}App Sample Rate: {acquisition?.PlaybackSampleRate} Hz{Environment.NewLine}Capture Sample Rate: {acquisition?.RecordingSampleRate} Hz{Environment.NewLine}Playback Mode: {(acquisition?.ExclusivePlayback == true ? "EXCL" : "Shared")}{Environment.NewLine}THD Limit: {value9:F1}{Environment.NewLine}Acquisition: {JsonSerializer.Serialize(acquisition)}";
			details += Environment.NewLine + $"freq_range: [{MeasurementCriteria.Range(autoTestCaseItem.Config).MinimumHz:0.##}, {MeasurementCriteria.Range(autoTestCaseItem.Config).MaximumHz:0.##}] Hz; THD_1000Hz: {value9:0.###}%";
            details += Environment.NewLine + $"THD_80: {autoTestCaseItem.Config?.EvaluateThd80 == true}; THD_4000: {autoTestCaseItem.Config?.EvaluateThd4000 == true}; TestVolume: {JsonSerializer.Serialize(_activeInOutConfig?.TestVolume)}";
            details += Environment.NewLine + $"AllowLineOutliers: {autoTestCaseItem.Config?.AllowLineOutliers == true}; AllowedLineOutlierPercent: {autoTestCaseItem.Config?.EffectiveLineOutlierPercent ?? 0:0.##}%; SweepLengthSamples: {autoTestCaseItem.Config?.SweepLengthSamples?.ToString() ?? "legacy duration"}";
            details += Environment.NewLine + autoTestCaseItem.AdditionalStatus + Environment.NewLine + string.Join(Environment.NewLine, autoTestCaseItem.MeasurementValues.Select(v => v.ThongSo + ": " + v.GiaTri));
			details = AppendResponseLimitQaDetails(details, autoTestCaseItem);
			details = AudioQaRouteDetails.Header(autoTestCaseItem, _activeInOutConfig) + Environment.NewLine + details;
				return new ServerEngine.AudioQaStepResult(text7 + " - " + autoTestCaseItem.Name, autoTestCaseItem.Status, details);
		}), product: ServerEngine.CurrentProduct, passed: suitePassed, graphImagePaths: list2, deviceReady: devicesReadyForServerUpload, uploadSessionId: uploadSessionId)))
		{
            if (ServerEngine.LastAudioQaPendingPath is string pending && File.Exists(pending))
            { AppendLog("QA", "Đã lưu phiếu QA chờ gửi trên đĩa; gửi lại qua hàng đợi QA."); return true; }
            throw new IOException(ServerEngine.LastError ?? "Lỗi upload kết quả lên server.");
		}
		else
		{
			AppendLog("Server", $"Upload thành công đúng một log QA cho {serialNumberForUpload}, phiên {uploadSessionId}.");
            return true;
		}
	}

	public static string FormatFrequencyHoverLimitLine(ResponseLimitPoint limit, double measuredDb, string levelUnit) =>
		!limit.HasReference
			? "\nChưa có line chuẩn/giới hạn tại điểm này"
			: $"\nChuẩn: {limit.TargetDb:+0.00;-0.00;0.00} {levelUnit} | Δ {measuredDb - limit.TargetDb:+0.00;-0.00;0.00} dB | [{limit.LowerDb?.ToString("F2") ?? "không xét"}, {limit.UpperDb?.ToString("F2") ?? "không xét"}]\n{limit.Reason}";

	public static string AppendResponseLimitQaDetails(string details, AutoTestCaseItem autoTestCaseItem)
	{
		if (autoTestCaseItem.ResponseAssessment is { } responseAssessment)
			details += Environment.NewLine + responseAssessment.Describe();
		if (autoTestCaseItem.ResponseLimits is { DipWindows.Count: > 0 } responseSettings)
			details += Environment.NewLine + responseSettings.DescribeWindows();
        if (!string.IsNullOrEmpty(autoTestCaseItem.SingleRunEarlyPassSummary))
            details += Environment.NewLine + autoTestCaseItem.SingleRunEarlyPassSummary;
		return details;
	}

	public static bool ValidateAutoTestCaseConfiguration(AutoTestCaseItem test2)
	{
		try
		{
			test2.Config.ValidateAdditionalTests();
			test2.Config.ValidateMeasurementCriteria();
			test2.Config.ValidateDipWindows();
			test2.Config.ValidateRunPolicy();
			return true;
		}
		catch (Exception ex)
		{
			test2.AdditionalResults = new[] { new ConfiguredTestResult("Cấu hình bổ sung", "INVALID", ex.Message) };
			test2.Status = "INVALID";
			test2.StatusBrush = Brushes.Gold;
			return false;
		}
	}


	private string GetStandardDeviceFileName(bool useCurrentDisplayId = false)
	{
		string standardDeviceKey = GetStandardDeviceKey(useCurrentDisplayId);
		return GetStandardDeviceFileNameForKey(standardDeviceKey);
	}

	private string GetStandardDeviceFileNameForKey(string standardDeviceKey)
	{
		if (string.IsNullOrEmpty(standardDeviceKey))
		{
			return "";
		}
		string rootStandardsDir = GetStandardsDirectory();
		string mainPath = BuildLengthSafeStandardFilePath(rootStandardsDir, standardDeviceKey);
		if (File.Exists(mainPath))
		{
			return mainPath;
		}
		string migratedHashPath = BuildHashedStandardFilePath(rootStandardsDir, standardDeviceKey);
		if (File.Exists(migratedHashPath)) return migratedHashPath;
		string migratedLongPath = System.IO.Path.Combine(rootStandardsDir, "standard_" + standardDeviceKey + ".csv");
		if (File.Exists(migratedLongPath)) return migratedLongPath;
		string modelName = (Application.Current.MainWindow as MainWindow)?.ComboModels?.SelectedItem?.ToString()?.Trim() ?? "";
		if (!string.IsNullOrEmpty(modelName))
		{
			char[] invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
			char[] array = invalidFileNameChars;
			foreach (char oldChar in array)
			{
				modelName = modelName.Replace(oldChar, '_');
			}
			modelName = modelName.Replace(' ', '_');
			string modelDir = System.IO.Path.Combine(rootStandardsDir, modelName);
			if (Directory.Exists(modelDir))
			{
				string modelPath = BuildLengthSafeStandardFilePath(modelDir, standardDeviceKey);
				if (File.Exists(modelPath))
				{
					return modelPath;
				}
				string modelHashPath = BuildHashedStandardFilePath(modelDir, standardDeviceKey);
				if (File.Exists(modelHashPath)) return modelHashPath;
				string modelLongPath = System.IO.Path.Combine(modelDir, "standard_" + standardDeviceKey + ".csv");
				if (File.Exists(modelLongPath)) return modelLongPath;
			}
		}
		return mainPath;
	}

	private string? FindReferenceWithLegacyTolerance(string levelBasis)
	{
		double tolerance = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
		if (tolerance <= 0.0 || tolerance > 12.0) return null;
		foreach (double multiplier in new[] { 10.0, 100.0, 1000.0 })
		{
			string key = GetStandardDeviceKeyForBasis(levelBasis, toleranceOverride: tolerance * multiplier);
			string path = GetStandardDeviceFileNameForKey(key);
			if (File.Exists(path)) return path;
		}
		return null;
	}

	private static string BuildLengthSafeStandardFilePath(string directory, string standardDeviceKey)
	{
		string text = "standard_" + standardDeviceKey + ".csv";
		string text2 = System.IO.Path.Combine(directory, text);
		if (text2.Length <= 210 && text.Length <= 220)
		{
			return text2;
		}
		return BuildHashedStandardFilePath(directory, standardDeviceKey);
	}

	private static string BuildHashedStandardFilePath(string directory, string standardDeviceKey)
	{
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(standardDeviceKey))).Substring(0, 20);
		string prefix = standardDeviceKey.Length <= 72 ? standardDeviceKey : standardDeviceKey[..72].TrimEnd('_', '.', ' ');
		return System.IO.Path.Combine(directory, $"standard_{prefix}_{hash}.csv");
	}

	private static string MakeSafeFileToken(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "UNKNOWN";
		}
		string text = value.Trim();
		char[] invalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
		char[] array = invalidFileNameChars;
		char[] array2 = array;
		char[] array3 = array2;
		foreach (char oldChar in array3)
		{
			text = text.Replace(oldChar, '_');
		}
		return text.Replace(' ', '_');
	}

	public static string GenerateAutoTestId(string? model)
	{
		string modelKey = BomCsvParser.NormalizeModelKey(model ?? "");
		if (string.IsNullOrWhiteSpace(modelKey))
		{
			modelKey = "MISAM";
		}
		string datePart = DateTime.Now.ToString("ddMMyy");
		string prefix = modelKey + "_" + datePart + "_";
		int maxSeq = 0;
		try
		{
			string failDataDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail data");
			if (Directory.Exists(failDataDir))
			{
				foreach (string dir in Directory.EnumerateDirectories(failDataDir, prefix + "*", SearchOption.AllDirectories))
				{
					string dirName = System.IO.Path.GetFileName(dir);
					if (dirName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						string remainder = dirName.Substring(prefix.Length);
						string seqStr = remainder.Split('_')[0];
						if (int.TryParse(seqStr, out var parsedSeq) && parsedSeq > maxSeq)
						{
							maxSeq = parsedSeq;
						}
					}
				}
			}
		}
		catch
		{
		}
		string seqFilePath = System.IO.Path.Combine(GetUserDataDirectory(), "autotest_sequence.json");
		try
		{
			if (File.Exists(seqFilePath))
			{
				string json = File.ReadAllText(seqFilePath);
				Dictionary<string, int> dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
				if (dict != null && dict.TryGetValue(prefix, out var storedSeq) && storedSeq > maxSeq)
				{
					maxSeq = storedSeq;
				}
			}
		}
		catch
		{
		}
		int nextSeq = maxSeq + 1;
		try
		{
			Dictionary<string, int> dict2 = new Dictionary<string, int>();
			if (File.Exists(seqFilePath))
			{
				string json2 = File.ReadAllText(seqFilePath);
				dict2 = JsonSerializer.Deserialize<Dictionary<string, int>>(json2) ?? new Dictionary<string, int>();
			}
			dict2[prefix] = nextSeq;
			string dir2 = System.IO.Path.GetDirectoryName(seqFilePath);
			if (!string.IsNullOrEmpty(dir2) && !Directory.Exists(dir2))
			{
				Directory.CreateDirectory(dir2);
			}
			File.WriteAllText(seqFilePath, JsonSerializer.Serialize(dict2));
		}
		catch
		{
		}
		return $"{prefix}{nextSeq:D4}";
	}

	private string BuildAutoTestId(int zeroBasedIndex)
	{
		return _autoTestCases.ElementAtOrDefault(zeroBasedIndex)?.Id ?? $"IO{zeroBasedIndex + 1:000}";
	}

	private static void SaveServerGraphPng(Plot plot, string destinationPath)
	{
		string text = System.IO.Path.GetDirectoryName(destinationPath) ?? AppDomain.CurrentDomain.BaseDirectory;
		Directory.CreateDirectory(text);
		{
			using var rendered = plot.GetImage(1920, 1080);
			BitmapFrame bitmapFrame;
			using (var bitmapStream = new MemoryStream(rendered.GetImageBytes()))
			{
				bitmapFrame = new PngBitmapDecoder(bitmapStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
			}
			double scaleX = 800.0 / (double)bitmapFrame.PixelWidth;
			double scaleY = 450.0 / (double)bitmapFrame.PixelHeight;
			TransformedBitmap source = new TransformedBitmap(bitmapFrame, new ScaleTransform(scaleX, scaleY));
			PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
			pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
			using FileStream stream = File.Create(destinationPath);
			pngBitmapEncoder.Save(stream);
		}
	}

	private void SaveFullResponseGraphPng(string destinationPath)
	{
		AxisLimits previousLimits = PlotFreqResponse.Plot.Axes.GetLimits();
		try
		{
			ApplyFrequencyChartView(fitVerticalData: true);
			SaveServerGraphPng(PlotFreqResponse.Plot, destinationPath);
		}
		finally
		{
			PlotFreqResponse.Plot.Axes.SetLimits(previousLimits.Left, previousLimits.Right, previousLimits.Bottom, previousLimits.Top);
			PlotFreqResponse.Refresh();
		}
	}

	private void CaptureAutoTestResultForReview(AutoTestCaseItem test)
	{
		lock (_freqs)
		{
			int count = _testRunner.IsRubBuzzTest ? 0 : Math.Min(_freqs.Count, _dbValues.Count);
			test.ResponseFrequencies = _freqs.Take(count).ToArray();
			test.ResponseLevels = _dbValues.Take(count).ToArray();
		}
		test.ResponseLevelUnit = _testRunner.NormalizeFrequencyResponseToOneKilohertz ? "dBr" : "dBFS";
		test.UnsmoothedResponse = new Dictionary<double, double>(_rawResponse);
		test.UnsmoothedStandardCurve = new Dictionary<double, double>(_rawStandardCurve);
		test.ImpulseResult = _testRunner.LastSweepResult;
		test.RubBuzzMetrics = _testRunner.IsRubBuzzTest ? _testRunner.LastRubBuzzMetrics : null;
		test.RubBuzzSpectrum = _testRunner.IsRubBuzzTest && _testRunner.LastRubBuzzSpectrum is { } spectrum
			? new FrequencySpectrum(spectrum.FrequenciesHz.ToArray(), spectrum.Magnitudes.ToArray()) : null;
		test.MeasurementValues = BuildMeasurementValues(_testRunner.LastSweepResult, false).ToArray();
		test.StandardCurve = _standardCurve == null
			? new Dictionary<double, double>() : new Dictionary<double, double>(_standardCurve);
		test.FrequencyLimits = _frequencyLimits == null
			? new Dictionary<double, FrequencyLimitPoint>()
			: new Dictionary<double, FrequencyLimitPoint>(_frequencyLimits);
		test.CriticalZones = _testRunner.CriticalZones?.ToArray() ?? Array.Empty<CriticalFrequencyZone>();
		test.ResponseLimits = _testRunner.ResponseLimits == null ? ResponseLimitSettings.Capture(test.Config, _testRunner.FreqResponseToleranceDb)
			: _testRunner.ResponseLimits with { Range = new(_testRunner.EvaluationMinFrequencyHz, _testRunner.EvaluationMaxFrequencyHz), ToleranceDb = _testRunner.FreqResponseToleranceDb,
				CriticalZones = test.CriticalZones.ToArray(), DipWindows = _testRunner.ResponseLimits.DipWindows.Select(w => w with { }).ToArray() };
		test.ResponseAssessment = _testRunner.LastResponseLimitAssessment;
		test.BassPassed = _testRunner.BassPassed;
		test.MidPassed = _testRunner.MidPassed;
		test.TreblePassed = _testRunner.TreblePassed;
		test.SilentInputDetected = _testRunner.SilentInputDetected;
		test.FeqRepeatabilityInvalid = _testRunner.FeqRepeatabilityInvalid;
		test.DistortionMeasurements = _testRunner.LastToneQualities == null
			? new Dictionary<double, ToneQualityMetrics>()
			: new Dictionary<double, ToneQualityMetrics>(_testRunner.LastToneQualities);
	}

	private void SaveFailureScreenshots(string stepName, bool forceAlways = false)
	{
		if (_testRunner.IsRubBuzzTest && (AutoTestAcquisitionInvalid || _testRunner.LastRubBuzzMetrics?.IsValid != true)) return;
		string.IsNullOrEmpty((Application.Current.MainWindow as MainWindow)?.TxtSerialNumber?.Text?.Trim() ?? "UNKNOWN_SERIAL");
		string.IsNullOrEmpty((Application.Current.MainWindow as MainWindow)?.ComboModels?.SelectedItem?.ToString()?.Trim() ?? "UNKNOWN_MODEL");
		stepName = MakeSafeFileToken(stepName);
		string text = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail data");
		if (_isExecutingAutoSuite && !string.IsNullOrEmpty(_autoTestSessionFolder))
		{
			text = _autoTestSessionFolder;
		}
		if (!Directory.Exists(text))
		{
			try
			{
				Directory.CreateDirectory(text);
			}
			catch (Exception ex)
			{
				AppendLog("Error", "Could not create folder: " + ex.Message);
			}
		}
		DateTime.Now.ToString("yyyyMMdd_HHmmss");
		if (forceAlways || (_testRunner.IsRubBuzzTest ? !_testRunner.ThdPassed : !_testRunner.BassPassed || !_testRunner.MidPassed || !_testRunner.TreblePassed))
		{
			string text2 = "FRA-" + stepName + ".png";
			string text3 = System.IO.Path.Combine(text, text2);
			try
			{
				if (_testRunner.IsRubBuzzTest)
				{
					WpfPlot plot = _testRunner.RubBuzzTestFreq < 500 ? PlotThdFft : _testRunner.RubBuzzTestFreq < 2500 ? PlotThdFft1k : PlotThdFft4k;
					SaveServerGraphPng(plot.Plot, text3);
				}
				else if (ImpulseEffectHost.Content is ImpulseAnalysisView impulse && ImpulseEffectHost.Visibility == Visibility.Visible) impulse.SaveGraph(text3);
                else SaveFullResponseGraphPng(text3);
				if (_isExecutingAutoSuite)
				{
					_autoTestGraphPaths.Add(text3);
				}
				AppendLog("Export", (_testRunner.IsRubBuzzTest ? "Đã lưu ảnh phổ rè/rung: " : "Saved FEQ screenshot: ") + text2);
			}
			catch (Exception ex2)
			{
				AppendLog("Error", "Failed to save FEQ screenshot: " + ex2.Message);
			}
		}
	}

	private List<string> GetMissingStandardReferenceTests()
	{
		var missing = new List<string>();
		if (_autoTestCases == null || _autoTestCases.Count == 0) return missing;

		_suppressChartUpdates = true;
		var savedTestConfig = _appliedTestConfig;
		var savedRouteKey = _referenceRouteKey;
		var savedPlaybackChannel = _audioEngine.PlaybackChannel;
		var savedRecordingChannel = _audioEngine.RecordingChannel;
		var savedScale = _testRunner.PlaybackFrequencyScale;
		var savedLogSweep = _testRunner.UseLogSweepFrequencyResponse;
		var savedMultitone = _testRunner.UseCombinedMultitoneFrequencyResponse;
		var savedPlaybackLevel = PlaybackLevelDbfs;
		var savedTolerance = TxtFreqTolerance?.Text;
		var savedThdLimit = TxtThdLimit?.Text;
		var savedPlaybackItem = ComboPlayback?.SelectedItem;
		var savedRecordingItem = ComboRecording?.SelectedItem;
		var savedStandardCurve = _standardCurve;
		var savedRawStandardCurve = _rawStandardCurve;
		var savedStandardEffects = _standardImpulseEffects;
		var savedOneKilohertzLevel = _standardOneKilohertzLevelDbFs;
		var savedFrequencyLimits = _frequencyLimits;
		var savedStandardKey = _loadedStandardDeviceKey;

		try
		{
			foreach (var testCase in _autoTestCases)
			{
				if (testCase.Config == null)
				{
					missing.Add($"{testCase.Id} ({testCase.Name}): Cấu hình bài đo bị trống");
					continue;
				}
				// Rub & Buzz test doesn't require standard frequency curve
				if (testCase.Config.RubBuzzTestFreq.HasValue)
				{
					continue;
				}

				ApplyTestCaseConfig(testCase.Config);
				if (!HasComparableStandardCurve() || _standardCurve?.Count < 2 || _frequencyLimits?.Count < 2)
				{
					missing.Add($"{testCase.Id} ({testCase.Name}) — không có line cho route, mức phát và chế độ đo hiện tại");
				}
			}
		}
		finally
		{
			_suppressChartUpdates = false;
			if (savedTestConfig != null)
			{
				ApplyTestCaseConfig(savedTestConfig);
			}
			else
			{
				_referenceRouteKey = savedRouteKey;
				_audioEngine.PlaybackChannel = savedPlaybackChannel;
				_audioEngine.RecordingChannel = savedRecordingChannel;
				SyncChannelSelectionsFromEngine();
				_testRunner.PlaybackFrequencyScale = savedScale;
				_testRunner.UseLogSweepFrequencyResponse = savedLogSweep;
				_testRunner.UseCombinedMultitoneFrequencyResponse = savedMultitone;
				PlaybackLevelDbfs = savedPlaybackLevel;
				if (TxtFreqTolerance != null) TxtFreqTolerance.Text = savedTolerance;
				if (TxtThdLimit != null) TxtThdLimit.Text = savedThdLimit;
				_isAutoSelectingDevices = true;
				try
				{
					if (ComboPlayback != null) ComboPlayback.SelectedItem = savedPlaybackItem;
					if (ComboRecording != null) ComboRecording.SelectedItem = savedRecordingItem;
				}
				finally { _isAutoSelectingDevices = false; }
				_standardCurve = savedStandardCurve;
				_rawStandardCurve = savedRawStandardCurve;
				_standardImpulseEffects = savedStandardEffects;
				_standardOneKilohertzLevelDbFs = savedOneKilohertzLevel;
				_frequencyLimits = savedFrequencyLimits;
				_loadedStandardDeviceKey = savedStandardKey;
				if (PlotFreqResponse != null)
				{
					UpdateFreqResponseChart();
				}
			}
		}

		return missing;
	}

	private void CheckAndLoadStandardDevice()
	{
		_standardPolarityReference = null;
		_rawStandardCurve = new Dictionary<double, double>();
		_standardImpulseEffects = null;
		_standardCurve = null;
		_frequencyLimits = null;
		_standardOneKilohertzLevelDbFs = null;
		_loadedStandardDeviceKey = "";
		try
		{
			string standardDeviceKey = GetStandardDeviceKey();
			if (!string.IsNullOrEmpty(standardDeviceKey))
			{
				string standardDeviceFileName = GetStandardDeviceFileName();
				bool useRawReferenceInNormalizedMode = false;
				bool legacyTolerance = false;
				if (!File.Exists(standardDeviceFileName) && HasDistinctDisplayIdFallback)
				{
					string fallbackKey = GetStandardDeviceKey(useCurrentDisplayId: true);
					string fallbackFile = GetStandardDeviceFileNameForKey(fallbackKey);
					if (File.Exists(fallbackFile))
					{
						standardDeviceFileName = fallbackFile;
						standardDeviceKey = fallbackKey;
					}
				}
				if (!File.Exists(standardDeviceFileName) && _appliedTestConfig != null)
				{
					string legacyKey = GetStandardDeviceKeyForBasis(_testRunner.CurrentFrequencyResponseLevelBasis, useLegacyPlaybackName: true);
					string legacyFile = GetStandardDeviceFileNameForKey(legacyKey);
					if (File.Exists(legacyFile)) standardDeviceFileName = legacyFile;
				}
				if (!File.Exists(standardDeviceFileName))
				{
					string? legacyToleranceFile = FindReferenceWithLegacyTolerance(_testRunner.CurrentFrequencyResponseLevelBasis);
					if (legacyToleranceFile != null)
					{
						standardDeviceFileName = legacyToleranceFile;
						legacyTolerance = true;
					}
				}
				if (!File.Exists(standardDeviceFileName) && _testRunner.NormalizeFrequencyResponseToOneKilohertz)
				{
					string rawKey = GetStandardDeviceKeyForBasis(TestRunner.FrequencyResponseLevelBasis);
					string rawFile = GetStandardDeviceFileNameForKey(rawKey);
					if (!File.Exists(rawFile) && HasDistinctDisplayIdFallback)
					{
						string fallbackRawKey = GetStandardDeviceKeyForBasis(TestRunner.FrequencyResponseLevelBasis, useCurrentDisplayId: true);
						string fallbackRawFile = GetStandardDeviceFileNameForKey(fallbackRawKey);
						if (File.Exists(fallbackRawFile))
						{
							rawFile = fallbackRawFile;
							rawKey = fallbackRawKey;
						}
					}
					if (!File.Exists(rawFile) && _appliedTestConfig != null)
					{
						string legacyRawKey = GetStandardDeviceKeyForBasis(TestRunner.FrequencyResponseLevelBasis, useLegacyPlaybackName: true);
						rawFile = GetStandardDeviceFileNameForKey(legacyRawKey);
						if (!File.Exists(rawFile)) rawFile = FindUniqueLegacyReferenceFile(legacyRawKey, TestRunner.FrequencyResponseLevelBasis) ?? rawFile;
					}
					if (File.Exists(rawFile))
					{
						standardDeviceFileName = rawFile;
						useRawReferenceInNormalizedMode = true;
					}
				}
				if (File.Exists(standardDeviceFileName))
				{
					Dictionary<double, double> dictionary = new Dictionary<double, double>();
					Dictionary<double, FrequencyLimitPoint> dictionary2 = new Dictionary<double, FrequencyLimitPoint>();
					double num = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
					string[] array = File.ReadAllLines(standardDeviceFileName);
					string primaryFingerprint = AppliedModelTestFingerprint(useCurrentDisplayId: false);
					string fallbackFingerprint = HasDistinctDisplayIdFallback ? AppliedModelTestFingerprint(useCurrentDisplayId: true) : "";
					bool matchesFingerprint = array.Contains("# ModelTestConfig=" + primaryFingerprint)
						|| (!string.IsNullOrEmpty(fallbackFingerprint) && array.Contains("# ModelTestConfig=" + fallbackFingerprint));
					if (_appliedTestConfig != null && !matchesFingerprint)
						throw new InvalidDataException("Cấu hình bài đo đã đổi hoặc line cũ chưa có thông tin cấu hình; cần tìm lại line chuẩn.");
					string expectedBasis = useRawReferenceInNormalizedMode ? TestRunner.FrequencyResponseLevelBasis : _testRunner.CurrentFrequencyResponseLevelBasis;
					if (!array.Contains("# LevelBasis=" + expectedBasis))
						throw new InvalidDataException("Line chuẩn không khớp chế độ đo hiện tại; cần tìm lại line chuẩn.");
					if (_testRunner.UseLogSweepFrequencyResponse)
					{
						if (!array.Contains("# ResponseAnalysis=" + ResponseSmoothing.AnalysisId))
							throw new InvalidDataException("Line chuẩn khác smoothing/băng so sánh 1/24 toàn dải 20 Hz–20 kHz; cần tìm lại line chuẩn.");
						long expectedSweepSamples = (long)Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate);
						string? sweepLine = array.FirstOrDefault(line => line.StartsWith("# SweepSamples=", StringComparison.Ordinal));
						if (sweepLine != null && (!long.TryParse(sweepLine["# SweepSamples=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long savedSweepSamples)
							|| savedSweepSamples != expectedSweepSamples))
							throw new InvalidDataException($"Line chuẩn không khớp length: file {savedSweepSamples} mẫu, phép đo {expectedSweepSamples} mẫu.");
					}
					string? levelLine = array.FirstOrDefault(line => line.StartsWith("# Reference1kHzDbFS=", StringComparison.Ordinal));
					if (levelLine != null && TryParseCsvDouble(levelLine["# Reference1kHzDbFS=".Length..], out double referenceLevel))
						_standardOneKilohertzLevelDbFs = referenceLevel;
					string[] array2 = array;
					string[] array3 = array2;
					string[] array4 = array3;
					foreach (string text in array4)
					{
						if (string.IsNullOrWhiteSpace(text) || text.StartsWith("Frequency"))
						{
							continue;
						}
						string[] array5 = text.Split(',');
						if (array5.Length < 2 || !TryParseCsvDouble(array5[0], out var parsed) || !TryParseCsvDouble(array5[1], out var parsed2))
						{
							continue;
						}
						dictionary[parsed] = parsed2;
						double lowerDb = parsed2 - num;
						double upperDb = parsed2 + num;
						bool result = false;
						if (array5.Length >= 4 && !legacyTolerance)
						{
							if (TryParseCsvDouble(array5[2], out var parsed3))
							{
								lowerDb = parsed3;
							}
							if (TryParseCsvDouble(array5[3], out var parsed4))
							{
								upperDb = parsed4;
							}
						}
						if (array5.Length >= 5)
						{
							bool.TryParse(array5[4].Trim(), out result);
						}
						dictionary2[parsed] = new FrequencyLimitPoint(parsed, parsed2, lowerDb, upperDb, result);
					}
					if (useRawReferenceInNormalizedMode)
					{
						if (dictionary.Count < 2 || !TryInterpolateCurve(dictionary, 1000.0, out double rawOneKilohertzDbFs))
							throw new InvalidDataException("Line dBFS cũ không có mức 1 kHz hợp lệ để chuẩn hóa.");
						_standardOneKilohertzLevelDbFs = rawOneKilohertzDbFs;
						dictionary = dictionary.ToDictionary(point => point.Key, point => point.Value - rawOneKilohertzDbFs);
						dictionary2 = dictionary2.ToDictionary(point => point.Key, point => new FrequencyLimitPoint(
							point.Value.FrequencyHz, point.Value.TargetDb - rawOneKilohertzDbFs,
							point.Value.LowerDb - rawOneKilohertzDbFs, point.Value.UpperDb - rawOneKilohertzDbFs,
							point.Value.Critical));
					}
					if (legacyTolerance && dictionary.Count >= 2 && !useRawReferenceInNormalizedMode)
					{
						string repairedPath = GetStandardDeviceFileNameForKey(standardDeviceKey);
						SaveReferenceCurve(repairedPath, dictionary.Keys.ToArray(), dictionary.Values.ToArray(), num, _standardOneKilohertzLevelDbFs);
						AppendLog("Standard", "Đã sửa giới hạn dB bị phóng đại và chuyển line cũ sang khóa cấu hình đúng.");
						standardDeviceFileName = repairedPath;
					}
					if (dictionary.Count > 0)
					{
						LoadRawStandardCurve(standardDeviceFileName, expectedBasis, useRawReferenceInNormalizedMode);
						_standardCurve = dictionary;
						_frequencyLimits = dictionary2;
						_loadedStandardDeviceKey = standardDeviceKey;
						AppendLog("Standard", $"Đã nạp đúng file chuẩn [{System.IO.Path.GetFileName(standardDeviceFileName)}], {dictionary.Count} điểm.");
						if (useRawReferenceInNormalizedMode)
							AppendLog("Standard", $"Dùng line dBFS đã lưu và quy đổi theo mốc 1 kHz {_standardOneKilohertzLevelDbFs:F1} dBFS; giữ nguyên file chuẩn gốc.");
						if (double.IsFinite(_testRunner.LastOneKilohertzLevelDbFs))
							UpdateOneKilohertzLevel(_testRunner.LastOneKilohertzLevelDbFs);
					}
				}
				else
				{
						AppendLog("Standard", "Chưa có line chuẩn cho cấu hình này; cần tìm lại line chuẩn. File: " + System.IO.Path.GetFileName(standardDeviceFileName));
				}
			}
		}
		catch (Exception ex)
		{
			AppendLog("Standard", "Không nạp được line chuẩn: " + ex.Message);
		}
		if (PlotFreqResponse != null)
		{
			UpdateFreqResponseChart();
		}
	}

	private void UpdateSweepLengthLabels()
	{
		if (ComboSweepLength == null) return;
		int sampleRate = GetSelectedPlaybackSampleRate();
		foreach (ComboBoxItem item in ComboSweepLength.Items.OfType<ComboBoxItem>())
		{
			if (!int.TryParse(item.Tag?.ToString(), out int length)) continue;
			string size = length == 1048576 ? "1M" : $"{length / 1024}K";
			item.Content = $"{size} · {length / (double)sampleRate:0.00} s";
		}
	}

	private string? FindUniqueLegacyReferenceFile(string legacyKey, string levelBasis)
	{
		// Older file names were hashed from the Windows endpoint name. The route and
		// sweep method remain in the readable prefix even when that name changes.
		if (_appliedTestConfig == null || string.IsNullOrWhiteSpace(_referenceRouteKey)
			|| _audioEngine.PlaybackSampleRate != 44100) return null;
		string keyPrefix = legacyKey.Length > 72 ? legacyKey[..72].TrimEnd('_', '.', ' ') : legacyKey;
		string filePrefix = "standard_" + keyPrefix;
		string root = GetStandardsDirectory();
		if (!Directory.Exists(root)) return null;
		long expectedSweepSamples = (long)Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate);
		var candidates = Directory.EnumerateFiles(root, "standard_*.csv", SearchOption.AllDirectories)
			.Where(path => System.IO.Path.GetFileName(path).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
			.Where(path =>
			{
				string[] metadata = File.ReadLines(path).TakeWhile(line => !line.StartsWith("Frequency", StringComparison.Ordinal)).ToArray();
				return metadata.Contains("# LevelBasis=" + levelBasis)
					&& metadata.Contains(FormattableString.Invariant($"# SweepSamples={expectedSweepSamples}"));
			})
			.ToList();
		var distinct = candidates.GroupBy(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
			.Select(group => group.First()).ToList();
		if (distinct.Count == 1) return distinct[0];
		if (distinct.Count > 1) AppendLog("Standard", $"Có {distinct.Count} line chuẩn cũ khác nhau cho {_referenceRouteKey}; cần chọn đúng file hiệu chuẩn.");
		return null;
	}

	private static bool TryParseCsvDouble(string value, out double parsed)
	{
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
		{
			return double.IsFinite(parsed);
		}
		return false;
	}

	private void ConfigureProductionMeasurement(TestConfig? config)
	{
		_testRunner.ImpulseEffectsSettings = config?.ImpulseEffects ?? new ImpulseEffectSettings();
		if (config?.TestEffect == true)
		{
			_testRunner.UseLogSweepFrequencyResponse = true;
			_isApplyingMeasurementMethod = true;
			ChkUseLogSweep.IsChecked = true;
			_isApplyingMeasurementMethod = false;
		}
		_testRunner.ApplyDistortionCalibration = ChkDistortionCalibration.IsChecked == true;
		_testRunner.UseHarmonicFrequencyAsDistortionReference = ChkDistortionHarmonicReference.IsChecked == true;
		if (_isExecutingAutoSuite)
		{
			_audioEngine.RecordingChannel = ResolveRecordingChannel(config);
			_audioEngine.PlaybackChannel = ResolvePlaybackChannel(config);
			SyncChannelSelectionsFromEngine();
		}
		_testRunner.PlaybackFrequencyScale = ResolvePlaybackFrequencyScale(config);
		_testRunner.PlaybackLevelDbFs = PlaybackLevelDbfs;
		_testRunner.RubBuzzPlaybackLevelDbFs = config?.PlaybackLevelDbfs;
		config?.ValidateDipWindows();
		_testRunner.ResponseLimits = ResponseLimitSettings.Capture(config, _testRunner.FreqResponseToleranceDb);
        _testRunner.SingleRunPassWhenMarginEnabled = config?.IsSingleRunMarginEnabled == true;
        _testRunner.SingleRunPassMarginDb = config?.EffectiveSingleRunMarginDb ?? 1.0;
		_testRunner.UseCombinedMultitoneFrequencyResponse = IsCombinedMultitoneMethod(config?.FrequencyResponseMethod);
		if (config?.TestEffect == true) _testRunner.UseCombinedMultitoneFrequencyResponse = false;
		_testRunner.FeqMicrophoneCalibration = ResolveFeqMicrophoneCalibration();
		int? recordingChannel = _audioEngine.RecordingChannel;
		object obj;
		if (recordingChannel.HasValue)
		{
			int valueOrDefault = recordingChannel.GetValueOrDefault();
			obj = $"kênh {valueOrDefault + 1}";
		}
		else
		{
			obj = "cả hai kênh";
		}
		string value = (string)obj;
		AppendLog("FEQ", (_testRunner.FeqMicrophoneCalibration == null) ? $"Thu {value}; không áp dụng calibration đáp tuyến mic cho route này; hệ số phát {_testRunner.PlaybackFrequencyScale:F5}." : $"Thu {value}; áp dụng calibration [{_testRunner.FeqMicrophoneCalibration.Name}] cho FEQ; hệ số phát {_testRunner.PlaybackFrequencyScale:F5}.");
		_testRunner.FrequencyLimits = _frequencyLimits;
		_testRunner.CriticalZones = config?.CriticalZones?.Where((CriticalZoneConfig zone) => zone.MinHz > 0.0 && zone.MaxHz > 0.0).Select((CriticalZoneConfig zone) => new CriticalFrequencyZone(zone.MinHz, zone.MaxHz)).ToList() ?? new List<CriticalFrequencyZone>();
		_testRunner.AmbientRecordingDevice = null;
		if (config != null && !string.IsNullOrWhiteSpace(config.AmbientRecordingIn))
		{
			AppendLog("Noise", "Bỏ qua Ambient Recording In trong cấu hình; dùng Scope trên ngõ thu DUT.");
		}
		bool flag = config == null || config.RecordingIn.IndexOf("mic", StringComparison.OrdinalIgnoreCase) >= 0;
		_testRunner.EnableNoiseDiagnostics = config?.NoiseDiagnostics ?? flag;
		_testRunner.DutMicrophoneCalibrationOffsetDb = config?.DutMicCalibrationOffsetDb;
		_testRunner.AmbientMicrophoneCalibrationOffsetDb = config?.AmbientMicCalibrationOffsetDb;
		_testRunner.AmbientNoiseLimitDbSpl = config?.AmbientNoiseLimitDbSpl ?? 70.0;
		_testRunner.AmbientNoiseLimitDbFs = config?.AmbientNoiseLimitDbFs ?? (-45.0);
		_testRunner.AmbientNoiseMaxRetries = Math.Clamp(config?.AmbientNoiseRetries ?? 1, 0, 3);
		_testRunner.ThdNLimitPercent = PositiveOrNull(config?.ThdNLimit);
        config?.ValidateMeasurementCriteria();
        var configuredRange = MeasurementCriteria.Range(config);
        _testRunner.EvaluationMinFrequencyHz = configuredRange.MinimumHz;
        _testRunner.EvaluationMaxFrequencyHz = configuredRange.MaximumHz;
        _testRunner.ThdLimitPercent = config?.Thd1000Hz ?? ParseDoubleSafe(TxtThdLimit.Text, .5);
		_testRunner.BassThdLimitPercent = PositiveOrNull(config?.BassThdLimit);
		_testRunner.MidThdLimitPercent = config?.Thd1000Hz ?? _testRunner.ThdLimitPercent;
		_testRunner.TrebleThdLimitPercent = PositiveOrNull(config?.TrebleThdLimit);
		_testRunner.EvaluateThd80 = config?.EvaluateThd80 == true;
		_testRunner.EvaluateThd4000 = config?.EvaluateThd4000 == true;
		_testRunner.BassDistortionSampleScale = ResolveBassThdSampleScale(config);
		_testRunner.BassThdFrequencyHz = config?.EvaluateThd80 == true ? 80 : ResolveThdFrequency(config?.BassThdFrequency, 80.0, 20.0, 249.0);
		_testRunner.MidThdFrequencyHz = 1000.0;
		_testRunner.TrebleThdFrequencyHz = config?.EvaluateThd4000 == true ? 4000 : ResolveThdFrequency(config?.TrebleThdFrequency, 4000.0, 4000.0, 10000.0);
		_testRunner.LogSweepVerificationRuns = Math.Clamp(config?.LogSweepRuns ?? 1, 1, 3);
		_testRunner.AllowedFrequencyOutlierPercent = config?.EffectiveLineOutlierPercent ?? 0;
		_testRunner.LogSweepDurationSeconds = config?.ResolveSweepSeconds(_audioEngine.PlaybackSampleRate) ?? GetSelectedLogSweepDuration();
		_testRunner.MultitoneDurationSeconds = config?.MultitoneDurationSeconds ?? GetSelectedMultitoneDuration();
		_testRunner.RequireLogSweepPhaseAlignment = config?.RequireLogSweepPhaseAlignment ?? true;
		_testRunner.MinimumSinadDb = PositiveOrNull(config?.MinimumSinadDb);
		_testRunner.MinimumSnrDb = PositiveOrNull(config?.MinimumSnrDb);
		_testRunner.MaximumDcOffset = PositiveOrNull(config?.MaximumDcOffset);
		_testRunner.MaximumToneFrequencyErrorHz = PositiveOrNull(config?.MaximumToneFrequencyErrorHz) ?? 5.0;
		_testRunner.MaximumClippedSamplePercent = PositiveOrNull(config?.MaximumClippedSamplesPercent) ?? 0.01;
		_testRunner.MinimumInputSignalDbFs = ResolveMinimumInputSignalDbFs(config);
		InitializeConfiguredThdPlots();
	}

	private static int? ResolveRecordingChannel(TestConfig? config)
	{
		int? num = config?.RecordingChannel;
		if (num.HasValue)
		{
			int valueOrDefault = num.GetValueOrDefault();
			if (valueOrDefault < 1)
			{
				throw new InvalidOperationException("Recording Channel phải bắt đầu từ 1.");
			}
			return valueOrDefault - 1;
		}
		return null;
	}

	private static int? ResolvePlaybackChannel(TestConfig? config)
	{
		int? num = config?.PlaybackChannel;
		if (num.HasValue)
		{
			int valueOrDefault = num.GetValueOrDefault();
			if (valueOrDefault < 1)
			{
				throw new InvalidOperationException("Playback Channel phải bắt đầu từ 1.");
			}
			return valueOrDefault - 1;
		}
		return null;
	}

	private static double ResolvePlaybackFrequencyScale(TestConfig? config)
	{
		double num = config?.PlaybackFrequencyScale ?? 1.0;
		if (!double.IsFinite(num) || num < 0.8 || num > 1.2)
		{
			throw new InvalidOperationException("Playback Frequency Scale phải nằm trong khoảng 0.8 đến 1.2.");
		}
		return num;
	}

	private static double ResolveMinimumInputSignalDbFs(TestConfig? config)
	{
		double num = config?.MinimumInputSignalDbFs ?? (-70.0);
		if (!double.IsFinite(num) || num > -70.0)
		{
			num = -70.0;
		}
		if (num < -90.0)
		{
			num = -90.0;
		}
		return num;
	}

	private static bool IsCombinedMultitoneMethod(string? method)
	{
		if (!string.Equals(method, "LogSweep", StringComparison.OrdinalIgnoreCase) && !string.Equals(method, "REW", StringComparison.OrdinalIgnoreCase))
		{
			return !string.Equals(method, "MultitoneSegmented", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static double ResolveBassThdSampleScale(TestConfig? config)
	{
		double num = config?.BassThdSampleScale ?? 0.35;
		if (!double.IsFinite(num) || num < 0.05 || num > 1.0)
		{
			throw new InvalidOperationException("Bass THD Sample Scale phải nằm trong khoảng 0.05 đến 1.0.");
		}
		return num;
	}

	private static double ResolveThdFrequency(double? configuredFrequency, double fallback, double minimum, double maximum)
	{
		double num = configuredFrequency ?? fallback;
		if (!double.IsFinite(num) || !(num >= minimum) || !(num <= maximum))
		{
			return fallback;
		}
		return num;
	}

	private static double? PositiveOrNull(double? value)
	{
		if (!value.HasValue || !double.IsFinite(value.Value) || !(value.Value > 0.0))
		{
			return null;
		}
		return value;
	}

	private async void BtnSaveStandard_Click(object sender, RoutedEventArgs e)
	{
		if (C360ModelDefinition.Matches(_activeInOutConfig)) { await RunC360ModelAutoTestAsync(findReference: true, singleTest: _appliedTestConfig ?? _autoTestCases.FirstOrDefault()?.Config); return; }
		if (_activeMeasurementTask != null || _freqs.Count < 2 || _freqs.Count != _dbValues.Count
			|| _freqs.Any(f => !double.IsFinite(f) || f <= 0.0) || _dbValues.Any(v => !double.IsFinite(v))
			|| _testRunner.FeqRepeatabilityInvalid || _testRunner.SilentInputDetected || _testRunner.InputClippingDetected)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Line đo chưa hoàn tất hoặc tín hiệu chưa hợp lệ; không lưu chuẩn.", "Chưa thể lưu line chuẩn", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		try
		{
			string path = GetStandardDeviceFileName();
			SaveReferenceCurve(path, _freqs, _dbValues, ParseDoubleSafe(TxtFreqTolerance.Text, 3.0));
			CheckAndLoadStandardDevice();
			if (!HasComparableStandardCurve() || _standardCurve?.Count != _freqs.Count)
				throw new InvalidDataException("File vừa lưu không nạp lại được đủ điểm line chuẩn.");
			AppendLog("Line", $"Đã lưu line chuẩn {_freqs.Count} điểm: {path}");
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Đã lưu line chuẩn và nạp lại từ file:\n" + path, "Lưu line chuẩn", ModernMessageBox.MessageBoxType.Info);
		}
		catch (Exception ex)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Không lưu được line chuẩn: " + ex.Message, "Lỗi lưu line", ModernMessageBox.MessageBoxType.Error);
		}
	}

	private MicrophoneCalibration? ResolveFeqMicrophoneCalibration()
	{
		if (_appliedTestConfig?.MicrophoneCalibration is { } attachment) return attachment.Resolve();
		return MicCalibrations.Resolve(SelectedRecordingDevice?.ID ?? "", SelectedRecordingDevice?.FriendlyName ?? "");
	}

	private void BtnExportStandard_Click(object sender, RoutedEventArgs e) => BtnSaveStandard_Click(sender, e);

	private void SaveReferenceCurve(string filePath, IReadOnlyList<double> frequencies, IReadOnlyList<double> values, double tolerance, double? referenceOneKilohertzDbFs = null)
	{
		bool sameCapture = frequencies.SequenceEqual(_freqs) && values.SequenceEqual(_dbValues);
		if (frequencies.Count == 0 || frequencies.Count != values.Count
			|| frequencies.Any(f => !double.IsFinite(f) || f <= 0.0)
			|| values.Any((double v) => !double.IsFinite(v)))
		{
			throw new InvalidOperationException("Line chuẩn không đủ dữ liệu hợp lệ.");
		}
		string directoryName = System.IO.Path.GetDirectoryName(filePath);
		if (string.IsNullOrWhiteSpace(directoryName))
		{
			throw new InvalidOperationException("Thư mục lưu line chuẩn không hợp lệ.");
		}
		Directory.CreateDirectory(directoryName);
		string text = System.IO.Path.Combine(directoryName, ".standard-" + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			using (StreamWriter streamWriter = new StreamWriter(text))
			{
				streamWriter.WriteLine("# LevelBasis=" + _testRunner.CurrentFrequencyResponseLevelBasis);
                TestRunner.WriteBandAverageCsvHeader(streamWriter, _testRunner.LastResponseLimitAssessment);
				if (_appliedTestConfig != null) streamWriter.WriteLine("# ModelTestConfig=" + AppliedModelTestFingerprint());
				if (_testRunner.UseLogSweepFrequencyResponse)
					streamWriter.WriteLine("# ResponseAnalysis=" + ResponseSmoothing.AnalysisId);
				streamWriter.WriteLine(FormattableString.Invariant($"# SweepSamples={Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate):F0}"));
				streamWriter.WriteLine(FormattableString.Invariant($"# SampleRate={_audioEngine.PlaybackSampleRate}"));
				double referenceLevel = referenceOneKilohertzDbFs ?? _testRunner.LastOneKilohertzLevelDbFs;
				if (double.IsFinite(referenceLevel))
					streamWriter.WriteLine(FormattableString.Invariant($"# Reference1kHzDbFS={referenceLevel:F4}"));
				string unit = _testRunner.NormalizeFrequencyResponseToOneKilohertz ? "dBr" : "dBFS";
				streamWriter.WriteLine($"Frequency (Hz),Target ({unit}),Lower Limit ({unit}),Upper Limit ({unit}),Critical");
				for (int num = 0; num < frequencies.Count; num++)
				{
					streamWriter.WriteLine(FormattableString.Invariant($"{frequencies[num]:0.####},{values[num]:F4},{values[num] - tolerance:F4},{values[num] + tolerance:F4},False"));
				}
			}
			string jsonPath = System.IO.Path.ChangeExtension(filePath, ".json");
			string jsonTemporaryPath = jsonPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				double jsonReferenceLevel = referenceOneKilohertzDbFs ?? _testRunner.LastOneKilohertzLevelDbFs;
				var portableLine = new
				{
					format = "SoncaAudioInspector.ReferenceCurve.V1",
					analysisId = ResponseSmoothing.AnalysisId,
					csvSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(text))),
					rawPoints = (sameCapture ? _rawResponse : new Dictionary<double, double>()).OrderBy(p => p.Key).Select(p => new { frequencyHz = p.Key, levelDb = p.Value }).ToArray(),
					impulseEffectsReference = !sameCapture ? null : _savingCompositeReference ? _pendingEffectsReference
						: ImpulseEffectReference.From(_testRunner.LastSweepResult?.ImpulseEffects, _testRunner.ImpulseEffectsSettings),
					polarityReference = !sameCapture ? null : _savingCompositeReference ? _pendingPolarityReference : CurrentPolarityReference(),
					levelBasis = _testRunner.CurrentFrequencyResponseLevelBasis,
					sweepSamples = (int)Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate),
					sampleRate = _audioEngine.PlaybackSampleRate,
					reference1kHzDbFS = double.IsFinite(jsonReferenceLevel) ? jsonReferenceLevel : (double?)null,
					points = frequencies.Select((frequency, index) => new
					{
						frequencyHz = frequency,
						targetDb = values[index],
						lowerDb = values[index] - tolerance,
						upperDb = values[index] + tolerance
					}).ToArray()
				};
				File.WriteAllText(jsonTemporaryPath, JsonSerializer.Serialize(portableLine,
					new JsonSerializerOptions { WriteIndented = true }));
				File.Move(jsonTemporaryPath, jsonPath, overwrite: true);
			}
			finally
			{
				if (File.Exists(jsonTemporaryPath)) File.Delete(jsonTemporaryPath);
			}
			File.Move(text, filePath, overwrite: true);
		}
		finally
		{
			if (File.Exists(text))
			{
				File.Delete(text);
			}
		}
	}

	private bool CanReuseCalibrationReference(bool retryPreviousFailure)
	{
		return !retryPreviousFailure
			&& HasComparableStandardCurve()
			&& _standardCurve is { Count: >= 2 }
			&& _frequencyLimits is { Count: >= 2 };
	}

	private async void BtnSaveStandardReference_Click(object sender, RoutedEventArgs e)
	{
		if (C360ModelDefinition.Matches(_activeInOutConfig)) { await RunC360ModelAutoTestAsync(findReference: true); return; }
		CloseRoutingScope();
		using IDisposable? workflow = TryBeginRoutingWorkflow();
		if (workflow == null) return;
		if (_autoTestCases.Count == 0)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Chưa có cấu hình bài test nào được nạp!", "Thông báo", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		List<AutoTestCaseItem> calibrationTests = _autoTestCases
			.Where(test => test.Config != null && !test.Config.RubBuzzTestFreq.HasValue).ToList();
		if (calibrationTests.Count == 0)
		{
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Không có bài đo đáp tuyến để tạo line chuẩn.", "Tìm line chuẩn", ModernMessageBox.MessageBoxType.Warning);
			return;
		}
		ConfigureSelectedSweep();
		SaveConfig();
		SelectPlaybackMode("Shared");
		_audioEngine.UseExclusivePlayback = false;
		_discardCurrentMeasurementRequested = false;
		_isFindingStandardReference = true;
		bool isCancelled = false;
		List<string> savedFiles = new List<string>();
		List<string> reusedFiles = new List<string>();
		List<string> failedReferenceTests = new List<string>();
		// An unsuccessful attempt must be retried even when an older reference
		// happens to exist for the same route. Completed steps can be reused.
		HashSet<AutoTestCaseItem> retryFailedCases = calibrationTests
			.Where(test => test.Status is "CHUẨN CHƯA ỔN ĐỊNH" or "LỖI LƯU CHUẨN" or "CHƯA CÓ CHUẨN MỚI")
			.ToHashSet();
		try
		{
			if (_isFreqExpanded || _isThdExpanded)
			{
				RestoreChartsLayout();
			}
			PanelAutoTestList.Visibility = Visibility.Visible;
			ColAutoTestList.Width = new GridLength(1.0, GridUnitType.Star);
			foreach (AutoTestCaseItem item3 in calibrationTests)
			{
				item3.Status = "WAITING";
				item3.FreqStatus = "ĐANG CHỜ TÌM LINE";
				item3.ThdStatus = "KHÔNG ÁP DỤNG — TÌM LINE";
			}
			BtnStart.IsEnabled = false;
			BtnStartAutoTest.IsEnabled = false;
			BtnSaveStandardReference.IsEnabled = false;
			BtnNoiseTest.IsEnabled = false;
			BtnSaveStandard.IsEnabled = false;
			const int maxRuns = 3;
			for (int testIdx = 0; testIdx < calibrationTests.Count; testIdx++)
			{
				AutoTestCaseItem testItem = calibrationTests[testIdx];
				if (isCancelled || _discardCurrentMeasurementRequested)
				{
					isCancelled = true;
					break;
				}
				_audioEngine.StopAllAudio();
				ApplyTestCaseConfig(testItem.Config);
				try
				{
					ConfigureProductionMeasurement(testItem.Config);
				}
				catch (Exception configEx)
				{
					testItem.Status = "INVALID";
					testItem.FreqStatus = "LỖI CẤU HÌNH: " + configEx.Message;
					testItem.StatusBrush = Brushes.Gold;
					TxtFreqStatus.Text = configEx.Message;
					BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(66, 52, 8));
					BorderVerdict.BorderBrush = Brushes.Gold;
					LblVerdict.Text = "LỖI CẤU HÌNH — CHƯA ĐO";
					LblVerdict.Foreground = Brushes.Gold;
					AppendLog("Lỗi cấu hình", $"[{testItem.Id}] {configEx.Message}");
					if (IsLoaded)
						ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), $"Cấu hình bài {testItem.Id} không hợp lệ:\n{configEx.Message}", "Lỗi cấu hình", ModernMessageBox.MessageBoxType.Error);
					return;
				}
				if (CanReuseCalibrationReference(retryFailedCases.Contains(testItem)))
				{
					testItem.Status = "ĐÃ CÓ LINE";
					testItem.FreqStatus = "ĐÃ NẠP LINE CHUẨN HỢP LỆ";
					testItem.ThdStatus = "KHÔNG ÁP DỤNG — TÌM LINE";
					testItem.StatusBrush = Brushes.MediumSpringGreen;
					testItem.FreqBrush = Brushes.MediumSpringGreen;
					reusedFiles.Add($"{testItem.Id} ({testItem.Name}): {_standardCurve.Count} điểm hợp lệ");
					LblVerdict.Text = testItem.Id + " — ĐÃ CÓ LINE, BỎ QUA ĐO LẠI";
					AppendLog("Calibration", $"[{testItem.Id}] Line chuẩn khớp cấu hình hiện tại và đã nạp đủ điểm; bỏ qua phép đo.");
					continue;
				}
				_testRunner.LogSweepVerificationRuns = 1;
				_testRunner.ReuseSuiteNoiseFloor = false;
				testItem.Status = "RUNNING";
				testItem.ThdStatus = "KHÔNG ÁP DỤNG — TÌM LINE";
				testItem.StatusBrush = Brushes.Gold;
				await Task.Delay(1000);
				if (_discardCurrentMeasurementRequested)
				{
					isCancelled = true;
					break;
				}
				MMDevice playbackDevice = SelectedPlaybackDevice;
				MMDevice recordingDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;
				if (playbackDevice == null || recordingDevice == null)
				{
					ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), $"Chưa chọn đủ thiết bị phát và thu cho bài test {testItem.Id} ({testItem.Name})!", "Lỗi thiết bị", ModernMessageBox.MessageBoxType.Error);
					isCancelled = true;
					break;
				}
				_testRunner.PlaybackLevelDbFs = PlaybackLevelDbfs;
				_testRunner.FreqResponseToleranceDb = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
				_testRunner.ThdLimitPercent = testItem.Config.Thd1000Hz;
				_testRunner.StandardCurve = null;
				_testRunner.FrequencyLimits = null;
				_testRunner.CriticalZones = new List<CriticalFrequencyZone>();
				_testRunner.EnableNoiseDiagnostics = true;
				_testRunner.AmbientRecordingDevice = null;
				_standardCurve = null;
				_frequencyLimits = null;
				_testRunner.IsRubBuzzTest = false;
				testItem.FreqStatus = "ĐANG ỔN ĐỊNH ROUTE";
				LblVerdict.Text = "[" + testItem.Id + "] ĐANG ỔN ĐỊNH ROUTE...";
				AppendLog("Calibration", $"[{testItem.Id}] Đã tắt phát bài trước; đo tối đa 3 lượt. Nếu 2 lượt đầu đạt độ lặp lại thì dùng ngay trung bình cặp đó; nếu chưa đạt mới đo lượt 3 để chọn cặp tối ưu.");
				var validReferenceRuns = new List<Dictionary<double, double>>();
				var validRawReferenceRuns = new List<IReadOnlyDictionary<double, double>>();
				var validEffectReferences = new List<ImpulseEffectReference?>();
				var validPolarityReferences = new List<SweepPolarityReference?>();
				var validReferenceLevels = new List<double>();
				int totalRuns = maxRuns;
				int run = 1;
				int noiseRetriesForCurrentRun = 0;
				ReferenceCurvePair? earlyPair = null;
				while (run <= totalRuns)
				{
					if (_discardCurrentMeasurementRequested)
					{
						isCancelled = true;
						break;
					}
					TxtFreqStatus.Text = "";
					TxtThdStatus.Text = "KHÔNG ÁP DỤNG — TÌM LINE";
					BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
					BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
					LblVerdict.Text = $"[{testItem.Id}] RUN {run}/{totalRuns}...";
					LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
					AppendLog("Calibration", $"[{testItem.Id} - {testItem.Name}] Starting calibration run {run}/{totalRuns}");
					testItem.FreqStatus = $"ĐANG LẤY LINE — {run}/{totalRuns}";
					lock (_freqs)
					{
						_freqs.Clear();
						_dbValues.Clear();
					}
					await RunTrackedTestAsync(playbackDevice, recordingDevice);
					if (_discardCurrentMeasurementRequested)
					{
						isCancelled = true;
						break;
					}
					if (_freqs.Count > 0 && _freqs.Count == _dbValues.Count)
					{
						UpdateFreqResponseChart();
					}
					if (!_testRunner.ReferenceAcquisitionValid || _freqs.Count == 0 || _dbValues.Count == 0)
					{
						string value = ((!string.IsNullOrWhiteSpace(_testRunner.LastFeqInvalidReason)) ? _testRunner.LastFeqInvalidReason : (_testRunner.SilentInputDetected ? "Không nhận được tín hiệu âm thanh (micro im lặng)." : ((_freqs.Count == 0) ? "Không có dữ liệu tần số." : "Dữ liệu đáp tuyến không đầy đủ hoặc không hữu hạn.")));
						if (_testRunner.LastFeqInvalidIsRetryableNoise && noiseRetriesForCurrentRun < 1)
						{
							noiseRetriesForCurrentRun++;
							testItem.FreqStatus = $"NHIỄU — ĐO BÙ LƯỢT {run} ({noiseRetriesForCurrentRun}/{1})";
							AppendLog("Calibration", $"[{testItem.Id}] Lượt hợp lệ {run}/{totalRuns} bị nhiễu: {value} Tự động cộng 1 lượt đo bù; lượt nhiễu không được đưa vào line chuẩn.");
							await Task.Delay(700);
							if (_discardCurrentMeasurementRequested)
							{
								isCancelled = true;
								break;
							}
							continue;
						}
						ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), $"Lỗi xảy ra hoặc phép đo bị hủy ở bài test {testItem.Id}, lần chạy thứ {run}.\nChi tiết: {value}", "Lỗi", ModernMessageBox.MessageBoxType.Error);
						isCancelled = true;
						break;
					}
					validReferenceRuns.Add(_freqs.Select((frequency, index) => (frequency, db: _dbValues[index]))
						.ToDictionary(point => point.frequency, point => point.db));
					validReferenceLevels.Add(_testRunner.LastOneKilohertzLevelDbFs);
					validRawReferenceRuns.Add(new Dictionary<double, double>(_rawResponse));
					validEffectReferences.Add(ImpulseEffectReference.From(_testRunner.LastSweepResult?.ImpulseEffects, _testRunner.ImpulseEffectsSettings));
					validPolarityReferences.Add(CurrentPolarityReference());
					noiseRetriesForCurrentRun = 0;

					// Nếu 2 lượt đầu đã đạt chuẩn độ lặp lại thì không cần đo lượt thứ 3, lấy trung bình theo cặp 1-2
					if (validReferenceRuns.Count == 2)
					{
						try
						{
							double repeatTol = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
							var testPair = ReferenceCurveSelector.SelectClosestPair(validReferenceRuns, pair =>
								Math.Abs(validReferenceLevels[validReferenceRuns.IndexOf(pair[0])] - validReferenceLevels[validReferenceRuns.IndexOf(pair[1])]) <= 3.0
								&& !TestRunner.AssessFeqRepeatabilityInRange(pair, _testRunner.UseLogSweepFrequencyResponse,
									repeatTol, _testRunner.CriticalZones, _testRunner.EvaluationMaxFrequencyHz, _testRunner.EvaluationMinFrequencyHz, new ResponseLimitPolicy(_testRunner.ResponseLimits!, null, null).SkipRepeatabilityAt).IsInvalid,
								evaluationMinHz: _testRunner.EvaluationMinFrequencyHz, evaluationMaxHz: _testRunner.EvaluationMaxFrequencyHz,
								evaluateFrequency: f => !new ResponseLimitPolicy(_testRunner.ResponseLimits!, null, null).SkipRepeatabilityAt(f));
							if (testPair.MaximumDifferenceDb <= 3.0)
							{
								earlyPair = testPair;
								AppendLog("Độ lặp lại", $"[{testItem.Id}] 2 lượt đầu đã đạt chuẩn ổn định (lệch tối đa {testPair.MaximumDifferenceDb:F2} dB <= 3.00 dB tại {testPair.MaximumDifferenceFrequencyHz:0.#} Hz). Không cần đo lượt 3, tính trung bình theo cặp 1-2.");
								break;
							}
							else
							{
								AppendLog("Độ lặp lại", $"[{testItem.Id}] 2 lượt đầu lệch {testPair.MaximumDifferenceDb:F2} dB (> 3.00 dB). Tiến hành đo lượt 3 để chọn cặp tối ưu.");
							}
						}
						catch (Exception ex)
						{
							AppendLog("Độ lặp lại", $"[{testItem.Id}] 2 lượt đầu chưa đạt điều kiện ổn định ({ex.Message}). Tiến hành đo lượt 3 để chọn cặp tối ưu.");
						}
					}

					run++;
					await Task.Delay(400);
				}
				if (isCancelled || validReferenceRuns.Count < 2)
				{
					isCancelled = true;
					break;
				}
				ReferenceCurvePair selectedPair;
				try
				{
					if (earlyPair != null)
					{
						selectedPair = earlyPair;
					}
					else
					{
						double repeatTolerance = ParseDoubleSafe(TxtFreqTolerance.Text, 3.0);
						selectedPair = ReferenceCurveSelector.SelectClosestPair(validReferenceRuns, pair =>
							Math.Abs(validReferenceLevels[validReferenceRuns.IndexOf(pair[0])] - validReferenceLevels[validReferenceRuns.IndexOf(pair[1])]) <= 3.0
							&& !TestRunner.AssessFeqRepeatabilityInRange(pair, _testRunner.UseLogSweepFrequencyResponse,
									repeatTolerance, _testRunner.CriticalZones, _testRunner.EvaluationMaxFrequencyHz, _testRunner.EvaluationMinFrequencyHz, new ResponseLimitPolicy(_testRunner.ResponseLimits!, null, null).SkipRepeatabilityAt).IsInvalid,
								evaluationMinHz: _testRunner.EvaluationMinFrequencyHz, evaluationMaxHz: _testRunner.EvaluationMaxFrequencyHz,
								evaluateFrequency: f => !new ResponseLimitPolicy(_testRunner.ResponseLimits!, null, null).SkipRepeatabilityAt(f));
						if (selectedPair.MaximumDifferenceDb > 3.0)
							throw new InvalidOperationException($"Cặp gần nhất {selectedPair.FirstRun}-{selectedPair.SecondRun} vẫn lệch {selectedPair.MaximumDifferenceDb:F2} dB tại {selectedPair.MaximumDifferenceFrequencyHz:0.#} Hz (> 3.00 dB).");
					}
				}
				catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
				{
					testItem.Status = "CHUẨN CHƯA ỔN ĐỊNH";
					testItem.FreqStatus = ex.Message;
					testItem.StatusBrush = Brushes.Salmon;
					testItem.FreqBrush = Brushes.Salmon;
					LblVerdict.Text = testItem.Id + " — CHƯA CÓ CẶP ĐẠT";
					AppendLog("Không lưu chuẩn", $"[{testItem.Id}] {ex.Message} Giữ nguyên file chuẩn cũ.");
					failedReferenceTests.Add(testItem.Id + ": " + ex.Message);
					AppendLog("Calibration", $"Dừng tại {testItem.Id}; không chuyển sang bài line chuẩn kế tiếp. Hãy đo lại từ đầu.");
					break;
				}
				List<double> list = selectedPair.AverageCurve.Keys.OrderBy(f => f).ToList();
				List<double> list2 = list.Select(f => selectedPair.AverageCurve[f]).ToList();
				var rawFirst = validRawReferenceRuns[selectedPair.FirstRun - 1];
				var rawSecond = validRawReferenceRuns[selectedPair.SecondRun - 1];
				_rawResponse = rawFirst.Keys.Where(rawSecond.ContainsKey).ToDictionary(f => f, f => (rawFirst[f] + rawSecond[f]) / 2);
				lock (_freqs) { _freqs.Clear(); _freqs.AddRange(list); _dbValues.Clear(); _dbValues.AddRange(list2); }
				string selectedRuns = $"{selectedPair.FirstRun} và {selectedPair.SecondRun}";
				AppendLog("Độ lặp lại", $"[{testItem.Id}] Chọn lượt {selectedRuns}; lệch lớn nhất {selectedPair.MaximumDifferenceDb:F2} dB tại {selectedPair.MaximumDifferenceFrequencyHz:0.#} Hz, RMS {selectedPair.RmsDifferenceDb:F2} dB. Line chuẩn là trung bình dB của hai lượt; lượt còn lại không được dùng.");
				try
				{
					string standardDeviceFileName = GetStandardDeviceFileName();
					double selectedOneKilohertzLevel = (validReferenceLevels[selectedPair.FirstRun - 1] + validReferenceLevels[selectedPair.SecondRun - 1]) / 2.0;
					_savingCompositeReference = true;
					_pendingEffectsReference = ImpulseEffectReference.Average(validEffectReferences[selectedPair.FirstRun - 1],
						validEffectReferences[selectedPair.SecondRun - 1], _testRunner.ImpulseEffectsSettings.EchoDelayToleranceMs);
					_pendingPolarityReference = SweepPolarityReference.Combine(validPolarityReferences[selectedPair.FirstRun - 1], validPolarityReferences[selectedPair.SecondRun - 1]);
					try { SaveReferenceCurve(standardDeviceFileName, list, list2, ParseDoubleSafe(TxtFreqTolerance.Text, 3.0), selectedOneKilohertzLevel); }
					finally { _savingCompositeReference = false; _pendingEffectsReference = null; _pendingPolarityReference = null; }
					CheckAndLoadStandardDevice();
					if (!HasComparableStandardCurve() || _standardCurve?.Count != list.Count)
						throw new InvalidDataException("File line chuẩn vừa lưu không nạp lại được đủ điểm.");
					savedFiles.Add($"{testItem.Id} ({testItem.Name}): {System.IO.Path.GetFileName(standardDeviceFileName)}");
					testItem.Status = "DA LUU";
					testItem.FreqStatus = $"TB lượt {selectedRuns} — Lệch {selectedPair.MaximumDifferenceDb:F2} dB";
					testItem.StatusBrush = Brushes.MediumSpringGreen;
					testItem.FreqBrush = Brushes.MediumSpringGreen;
					LblVerdict.Text = testItem.Id + " — ĐÃ LƯU TRUNG BÌNH CẶP ĐẠT";
					AppendLog("Calibration", $"[{testItem.Id}] Đã lưu trung bình lượt {selectedRuns}, sai lệch tối đa {selectedPair.MaximumDifferenceDb:F2} dB (<= 3.00 dB).");
				}
				catch (Exception ex)
				{
					testItem.Status = "LỖI LƯU CHUẨN";
					AppendLog("Calibration", ex.Message);
					failedReferenceTests.Add(testItem.Id + ": " + ex.Message);
					AppendLog("Calibration", $"Dừng tại {testItem.Id}; không chuyển sang bài line chuẩn kế tiếp. Hãy đo lại từ đầu.");
					break;
				}
				_freqs = list;
				_dbValues = list2;
				UpdateFreqResponseChart();
				await Task.Delay(500);
			}
			if (isCancelled)
			{
				foreach (AutoTestCaseItem item7 in calibrationTests.Where((AutoTestCaseItem t) => t.Status == "RUNNING"))
				{
					item7.Status = "CHƯA CÓ CHUẨN MỚI";
					item7.FreqStatus = "Lượt đo bị dừng hoặc không hợp lệ";
					item7.StatusBrush = Brushes.Salmon;
				}
			}
			if (!isCancelled && failedReferenceTests.Count == 0 && savedFiles.Count + reusedFiles.Count > 0)
			{
				BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(6, 95, 70));
				BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
				LblVerdict.Text = $"LINE CHUẨN ĐỦ — MỚI {savedFiles.Count}, ĐÃ CÓ {reusedFiles.Count}";
				LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153));
				ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this),
					$"Line chuẩn đã đủ cho {savedFiles.Count + reusedFiles.Count} bài test. Đã lưu mới {savedFiles.Count}, bỏ qua {reusedFiles.Count} bài đã có line hợp lệ."
					+ (savedFiles.Count > 0 ? "\n\nLưu mới:\n" + string.Join("\n", savedFiles) : "")
					+ (reusedFiles.Count > 0 ? "\n\nĐã có:\n" + string.Join("\n", reusedFiles) : ""),
					"Tìm Line Chuẩn Thành Công", ModernMessageBox.MessageBoxType.Info);
			}
			else if (!isCancelled && savedFiles.Count + reusedFiles.Count > 0)
			{
				BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 63, 18));
				BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21));
				LblVerdict.Text = $"DỪNG TẠI LINE LỖI — MỚI {savedFiles.Count}, ĐÃ CÓ {reusedFiles.Count}";
				LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 224, 71));
					ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Đã dừng tại line không đạt. Bấm lại để đo bài lỗi; line hợp lệ đã lưu sẽ được bỏ qua.\n\nĐã có:\n" + string.Join("\n", reusedFiles) + "\n\nLưu mới:\n" + string.Join("\n", savedFiles) + "\n\nLỗi:\n" + string.Join("\n", failedReferenceTests), "Tìm Line Chuẩn Đã Dừng", ModernMessageBox.MessageBoxType.Warning);
			}
			else if (!isCancelled && failedReferenceTests.Count > 0)
			{
				BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 10, 10));
				BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
					LblVerdict.Text = "DỪNG TẠI LINE LỖI — CẦN ĐO LẠI";
				LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
					ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Đã dừng tại line không đạt; các bài sau chưa được đo. Đo lại từ đầu:\n\n" + string.Join("\n", failedReferenceTests), "Line Chuẩn Chưa Ổn Định", ModernMessageBox.MessageBoxType.Warning);
			}
			else if (!_discardCurrentMeasurementRequested)
			{
				BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
				BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
				LblVerdict.Text = $"DỪNG — ĐÃ LƯU {savedFiles.Count} LINE CHUẨN";
				LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			}
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			Exception ex4 = ex3;
			Exception ex5 = ex4;
			AppendLog("Lỗi tìm line chuẩn", ex5.Message);
			BorderVerdict.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 10, 10));
			BorderVerdict.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
			LblVerdict.Text = "LỖI TÌM LINE — CÓ THỂ ĐO LẠI";
			LblVerdict.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
			ModernMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "Quy trình tìm line chuẩn đã dừng.\nChi tiết: " + ex5.Message, "Lỗi tìm line chuẩn", ModernMessageBox.MessageBoxType.Error);
		}
		finally
		{
			_isFindingStandardReference = false;
			BtnStart.IsEnabled = true;
			BtnStartAutoTest.IsEnabled = true;
			BtnSaveStandardReference.IsEnabled = true;
			BtnNoiseTest.IsEnabled = true;
			BtnSaveStandard.IsEnabled = !_discardCurrentMeasurementRequested && _freqs.Count > 0;
			try
			{
				CheckAndLoadStandardDevice();
			}
			catch (Exception ex6)
			{
				AppendLog("Cảnh báo", "Không thể nạp lại line chuẩn sau khi kết thúc: " + ex6.Message);
			}
		}
	}

	private void MaintainRoutingHeadroomMonitor()
	{
		if (_audioEngine == null || !base.IsLoaded || IsTestingBusy || _leaveAudioReleasedAfterAutoTest)
		{
			if (_isRoutingHeadroomRunning)
			{
				StopRoutingHeadroomMonitor();
			}
			return;
		}
		MMDevice selectedPlaybackDevice = SelectedPlaybackDevice;
		MMDevice selectedRecordingDevice = SelectedRecordingDevice;
		bool flag = selectedPlaybackDevice != null && selectedRecordingDevice != null;
		if (BorderRoutingHeadroom != null)
		{
			BorderRoutingHeadroom.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (BorderHeadroomPrompt != null)
		{
			BorderHeadroomPrompt.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		}
		BtnToggleHeadroom.Content = _routingHeadroomEnabled ? "TẠM DỪNG" : "TIẾP TỤC";
		if (!_routingHeadroomEnabled)
		{
			StopRoutingHeadroomMonitor();
			RewMeterRouting.ShowUnavailable("ĐÃ TẠM DỪNG HEADROOM");
			return;
		}
		if (!flag || selectedRecordingDevice == null)
		{
			if (_isRoutingHeadroomRunning)
			{
				StopRoutingHeadroomMonitor();
			}
		}
		else
		{
			if (_isRoutingHeadroomRunning && _audioEngine.IsContinuousCaptureActive
				&& string.Equals(_routingHeadroomRecordingId, selectedRecordingDevice.ID, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			if (DateTime.UtcNow < _headroomRetryAtUtc) return;
			StopRoutingHeadroomMonitor();
			try
			{
				_routingHeadroomRecordingId = selectedRecordingDevice.ID;
				RewMeterRouting?.PrepareForLiveCapture();
				long session = _routingMeterBuffer.Start();
				System.Threading.Interlocked.Exchange(ref _routingMeterSession, session);
				_audioEngine.StartContinuousCapture(selectedRecordingDevice, delegate(float[] samples, int _)
				{
					_routingMeterBuffer.Push(samples, session);
				}, error =>
				{
					_ = Dispatcher.InvokeAsync(() =>
					{
						if (!_routingMeterBuffer.IsCurrent(session)) return;
						StopRoutingHeadroomMonitor();
						ScheduleHeadroomRecovery(error);
					});
				});
				_isRoutingHeadroomRunning = true;
				_headroomRetryAtUtc = DateTime.MinValue;
			}
			catch (Exception ex)
			{
				_isRoutingHeadroomRunning = false;
				_routingHeadroomRecordingId = "";
				_routingMeterBuffer.Stop();
				ScheduleHeadroomRecovery(ex);
			}
		}
	}

	private void StopRoutingHeadroomMonitor()
	{
		if (_isRoutingHeadroomRunning)
		{
			_isRoutingHeadroomRunning = false;
			_routingMeterBuffer.Stop();
			_routingHeadroomRecordingId = "";
			try
			{
				_audioEngine?.StopContinuousCapture();
			}
			catch
			{
			}
			RewMeterRouting?.Reset();
		}
	}

	private void ScheduleHeadroomRecovery(Exception error)
	{
		_headroomFailureCount = Math.Min(5, _headroomFailureCount + 1);
		_headroomRetryAtUtc = DateTime.UtcNow.AddSeconds(Math.Min(30, 2 << _headroomFailureCount));
		RewMeterRouting.ShowUnavailable("MẤT TÍN HIỆU THU — ĐANG CHỜ");
		AppendLog("Headroom", error.Message);
	}

	private void BtnToggleHeadroom_Click(object sender, RoutedEventArgs e)
	{
		if (IsTestingBusy) return;
		_routingHeadroomEnabled = !_routingHeadroomEnabled;
		_headroomRetryAtUtc = DateTime.MinValue;
		_headroomFailureCount = 0;
		SaveConfig();
		MaintainRoutingHeadroomMonitor();
	}

	private async Task RefreshPendingQaAsync()
	{
		try
		{
			var pending = await Task.Run(ServerEngine.GetPendingAudioQaUploads);
			BtnRetryQaUpload.Visibility = pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
			BtnRetryQaUpload.Content = $"GỬI LẠI KẾT QUẢ CHỜ ({pending.Count})";
		}
		catch (Exception ex) { AppendLog("Kết quả chờ gửi", ex.Message); }
	}

	private async void BtnRetryQaUpload_Click(object sender, RoutedEventArgs e)
	{
		using var workflow = TryBeginRoutingWorkflow();
		if (workflow == null) return;
		StopRoutingHeadroomMonitor();
		BtnRetryQaUpload.IsEnabled = false;
		try
		{
			var pending = await Task.Run(ServerEngine.GetPendingAudioQaUploads);
			foreach (var item in pending)
			{
				bool allowUncertain = false;
				if (!item.Result.SafeToRetry)
				{
					allowUncertain = ModernMessageBox.Show(Application.Current.MainWindow,
						$"Chưa xác định server đã nhận kết quả của sản phẩm {item.Result.ProductId} hay chưa. " +
						"Hãy kiểm tra lịch sử QA trên server trước; gửi lại có thể tạo bản ghi trùng.\n\n" +
						"Bạn đã kiểm tra và muốn gửi lại kết quả này?", "Kiểm tra trước khi gửi lại", ModernMessageBox.MessageBoxType.Confirmation);
					if (!allowUncertain) break;
				}
				if (!await ServerEngine.RetryPendingAudioQaAsync(item.Path, allowUncertain))
				{
					string error = ServerEngine.LastError ?? "Chưa gửi được kết quả; dữ liệu vẫn được giữ.";
					AppendLog("Gửi lại QA", error);
					ModernMessageBox.Show(Window.GetWindow(this), error, "Chưa gửi được kết quả QA", ModernMessageBox.MessageBoxType.Error);
					break;
				}
				AppendLog("Gửi lại QA", $"Đã gửi kết quả đã lưu của sản phẩm {item.Result.ProductId}.");
			}
		}
		catch (Exception ex) { AppendLog("Gửi lại QA", ex.Message); }
		finally { BtnRetryQaUpload.IsEnabled = true; }
	}

	private void SetRoutingControlsLocked(bool locked)
	{
		if (locked)
		{
			_lockedRoutingControls = new Dictionary<UIElement, bool>();
			foreach (string name in new[] { "ComboPlayback", "ComboRecording", "ComboRoutingPlaybackChannel",
				"ComboRoutingRecordingChannel", "ComboPlaybackSampleRate", "ComboPlaybackMode", "ComboSweepLength",
				"TxtFreqTolerance", "TxtThdLimit", "TxtLogSweepDuration", "TxtMultitoneDuration", "ChkUseLogSweep",
				"SliderPlaybackLevelDbfs", "TxtPlaybackLevelDbfs", "BtnDecreaseLevelDbfs",
				"BtnIncreaseLevelDbfs", "BtnToggleHeadroom", "BtnImportMicrophoneCalibration", "ChkDistortionCalibration" })
				if (FindName(name) is UIElement control)
				{
					_lockedRoutingControls[control] = control.IsEnabled;
					control.IsEnabled = false;
				}
		}
		else if (_lockedRoutingControls != null)
		{
			foreach (var item in _lockedRoutingControls) item.Key.IsEnabled = item.Value;
			_lockedRoutingControls = null;
		}
	}

	public void ReleaseDeviceItems()
	{
		_isAutoSelectingDevices = true;
		try { DisposeRoutingDeviceItems(); }
		finally { _isAutoSelectingDevices = false; }
	}

	private void DisposeRoutingDeviceItems(IEnumerable<MMDevice>? extraDevices = null)
	{
		var devices = ComboPlayback.Items.Cast<DeviceItem>().Concat(ComboRecording.Items.Cast<DeviceItem>())
			.Select(item => item.Device).Concat(extraDevices ?? Array.Empty<MMDevice>()).Distinct().ToArray();
		ComboPlayback.Items.Clear();
		ComboRecording.Items.Clear();
		foreach (MMDevice device in devices) try { device.Dispose(); } catch { }
	}

	private async void ComboRoutingPlaybackChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (ComboRoutingPlaybackChannel == null || _audioEngine == null || _isUpdatingChannelSelectors)
			return;
		if (_activeMeasurementTask != null || _isExecutingAutoSuite || _isFindingStandardReference)
		{
			SyncChannelSelectionsFromEngine();
			return;
		}
		int? playbackChannel = GetDeviceChannelCount(SelectedPlaybackDevice) < 2 ? null : ComboRoutingPlaybackChannel.SelectedIndex switch
		{
			1 => 0,
			2 => 1,
			_ => null,
		};
		if (_audioEngine.PlaybackChannel == playbackChannel)
			return;
		int settleVersion = ++_playbackRouteSettleVersion;
		_audioEngine.Stop();
		_audioEngine.StopContinuousPlayback();
		StopRoutingHeadroomMonitor();
		_audioEngine.PlaybackChannel = playbackChannel;
		BtnStart.IsEnabled = false;
		BtnStartAutoTest.IsEnabled = false;
		BtnSaveStandardReference.IsEnabled = false;
		BtnNoiseTest.IsEnabled = false;
		BtnSaveStandard.IsEnabled = false;
		SaveConfig();
		AppendLog("Audio Routing", $"Đã chọn {(playbackChannel == 0 ? "Left" : playbackChannel == 1 ? "Right" : "L+R")}; kênh phát không chọn nhận mẫu 0. Đợi 1 giây để ổn định route.");
		await Task.Delay(1000);
		if (settleVersion != _playbackRouteSettleVersion || _isExecutingAutoSuite || _isFindingStandardReference || _activeMeasurementTask != null)
			return;
		MaintainRoutingHeadroomMonitor();
		CheckAndLoadStandardDevice();
		BtnStart.IsEnabled = true;
		BtnStartAutoTest.IsEnabled = true;
		BtnSaveStandardReference.IsEnabled = true;
		BtnNoiseTest.IsEnabled = true;
	}

	private void ComboRoutingRecordingChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (ComboRoutingRecordingChannel == null || _audioEngine == null || _isUpdatingChannelSelectors)
		{
			return;
		}
		if (IsModelTransitionBusy)
		{
			SyncChannelSelectionsFromEngine();
			return;
		}
		if (GetDeviceChannelCount(SelectedRecordingDevice) < 2)
		{
			_audioEngine.RecordingChannel = null;
			return;
		}
		int selectedIndex = ComboRoutingRecordingChannel.SelectedIndex;
		bool flag = false;
		bool flag2 = false;
		if (1 == 0)
		{
		}
		int? num = selectedIndex switch
		{
			1 => 0, 
			2 => 1, 
			_ => null, 
		};
		if (1 == 0)
		{
		}
		int? num2 = num;
		bool flag3 = false;
		int? num3 = num2;
		bool flag4 = false;
		int? recordingChannel = num3;
		if (_audioEngine != null)
		{
			_audioEngine.RecordingChannel = recordingChannel;
			SaveConfig();
		}
		if (_isRoutingHeadroomRunning)
		{
			StopRoutingHeadroomMonitor();
			MaintainRoutingHeadroomMonitor();
		}
	}

	public void SyncChannelSelectionsFromEngine()
	{
		if (_audioEngine == null)
		{
			return;
		}
		UpdateChannelSelectorAvailability();
		_isUpdatingChannelSelectors = true;
		try
		{
			if (ComboRoutingPlaybackChannel != null)
			{
				int num = ((_audioEngine.PlaybackChannel == 0) ? 1 : ((_audioEngine.PlaybackChannel == 1) ? 2 : 0));
				if (ComboRoutingPlaybackChannel.SelectedIndex != num)
				{
					ComboRoutingPlaybackChannel.SelectedIndex = num;
				}
			}
			if (ComboRoutingRecordingChannel != null)
			{
				int num2 = ((_audioEngine.RecordingChannel == 0) ? 1 : ((_audioEngine.RecordingChannel == 1) ? 2 : 0));
				if (ComboRoutingRecordingChannel.SelectedIndex != num2)
				{
					ComboRoutingRecordingChannel.SelectedIndex = num2;
				}
			}
		}
		finally
		{
			_isUpdatingChannelSelectors = false;
		}
	}

}
