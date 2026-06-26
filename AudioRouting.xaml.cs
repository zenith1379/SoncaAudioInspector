using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using ScottPlot;

using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SoncaAudioInspector
{
    public partial class AudioRouting : UserControl
    {
        private AudioEngine _audioEngine;
        private TestRunner _testRunner;

        private List<double> _freqs = new List<double>();
        private List<double> _dbValues = new List<double>();

        private InOutConfig _activeInOutConfig;
        private List<AutoTestCaseItem> _autoTestCases = new List<AutoTestCaseItem>();
        private bool _isExecutingAutoSuite = false;
        private AutoTestCaseItem _currentRunningTestCase = null;
        private bool? _currentTestSuccess = null;
        private Dictionary<double, double> _standardCurve = null;

        public AudioRouting()
        {
            InitializeComponent();
        }

        public void InitializeRouting(AudioEngine audioEngine, TestRunner testRunner)
        {
            _audioEngine = audioEngine;
            _testRunner = testRunner;

            // Register TestRunner events
            _testRunner.OnStepsChanged += Steps => Dispatcher.Invoke(() => 
            {
                ListSteps.ItemsSource = Steps.ToList();
                SidebarScrollViewer.ScrollToEnd();

                var freqStepGlobal = Steps.ElementAtOrDefault(1);
                if (freqStepGlobal != null && (freqStepGlobal.Status == "Pass" || freqStepGlobal.Status == "Fail"))
                {
                    UpdateFreqResponseChart();
                }

                if (_isExecutingAutoSuite && _currentRunningTestCase != null)
                {
                    var freqStep = Steps.ElementAtOrDefault(1);
                    if (freqStep != null)
                    {
                        if (freqStep.Status == "Waiting")
                        {
                            _currentRunningTestCase.FreqStatus = "WAITING";
                            _currentRunningTestCase.FreqBrush = new WpfSolidColorBrush(WpfColor.FromRgb(113, 113, 122));
                        }
                        else if (freqStep.Status == "Running")
                        {
                            _currentRunningTestCase.FreqStatus = "RUNNING";
                            _currentRunningTestCase.FreqBrush = new WpfSolidColorBrush(WpfColor.FromRgb(250, 204, 21)); // Yellow
                        }
                        else if (freqStep.Status == "Pass" || freqStep.Status == "Fail")
                        {
                            string details = freqStep.Details;
                            if (details.Contains("Max Deviation:"))
                            {
                                int idx = details.IndexOf("(Limit:");
                                if (idx > 0) details = details.Substring(0, idx).Trim();
                            }
                            _currentRunningTestCase.FreqStatus = $"{freqStep.Status.ToUpper()} ({details})";
                            _currentRunningTestCase.FreqBrush = freqStep.Status == "Pass" ? 
                                new WpfSolidColorBrush(WpfColor.FromRgb(52, 211, 153)) : 
                                new WpfSolidColorBrush(WpfColor.FromRgb(248, 113, 113));
                        }
                    }

                    var thdStep = Steps.ElementAtOrDefault(2);
                    if (thdStep != null)
                    {
                        if (thdStep.Status == "Waiting")
                        {
                            _currentRunningTestCase.ThdStatus = "WAITING";
                            _currentRunningTestCase.ThdBrush = new WpfSolidColorBrush(WpfColor.FromRgb(113, 113, 122));
                        }
                        else if (thdStep.Status == "Running")
                        {
                            _currentRunningTestCase.ThdStatus = "RUNNING";
                            _currentRunningTestCase.ThdBrush = new WpfSolidColorBrush(WpfColor.FromRgb(250, 204, 21)); // Yellow
                        }
                        else if (thdStep.Status == "Pass" || thdStep.Status == "Fail")
                        {
                            string details = thdStep.Details;
                            if (details.Contains("THD:"))
                            {
                                int idx = details.IndexOf("(Limit:");
                                if (idx > 0) details = details.Substring(0, idx).Trim();
                            }
                            _currentRunningTestCase.ThdStatus = $"{thdStep.Status.ToUpper()} ({details})";
                            _currentRunningTestCase.ThdBrush = thdStep.Status == "Pass" ? 
                                new WpfSolidColorBrush(WpfColor.FromRgb(52, 211, 153)) : 
                                new WpfSolidColorBrush(WpfColor.FromRgb(248, 113, 113));
                        }
                    }
                }
            });
            
            _testRunner.OnLogMessage += (source, msg) => Dispatcher.Invoke(() => AppendLog(source, msg));
            
            _testRunner.OnFrequencyResponsePoint += (freq, db) => Dispatcher.Invoke(() =>
            {
                _freqs.Add(freq);
                _dbValues.Add(db);
                UpdateFreqResponseChart();
            });

            _testRunner.OnThdSpectrumReady += (frequencies, magnitudes, thdPercent) => Dispatcher.Invoke(() =>
            {
                UpdateThdFftChart(frequencies, magnitudes, thdPercent);
            });

            _testRunner.OnTestCompleted += Success => Dispatcher.Invoke(() => 
            {
                if (_isExecutingAutoSuite && _currentRunningTestCase != null)
                {
                    _currentTestSuccess = Success;
                }
                else
                {
                    SetFinalVerdict(Success);
                }
            });

            _testRunner.OnTestSubstatusChanged += (type, details) => Dispatcher.Invoke(() =>
            {
                if (type == "Freq")
                {
                    TxtFreqStatus.Text = details;
                    if (!string.IsNullOrEmpty(details) && details != "Finished")
                    {
                        BorderFreqChart.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(16, 185, 129)); // Active green
                        BorderThdChart.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42)); // Dimmed
                    }
                    else
                    {
                        BorderFreqChart.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42)); // Default
                    }
                }
                else if (type == "THD")
                {
                    TxtThdStatus.Text = details;
                    if (!string.IsNullOrEmpty(details) && details != "Finished")
                    {
                        BorderThdChart.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(59, 130, 246)); // Active blue
                        BorderFreqChart.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42)); // Dimmed
                    }
                    else
                    {
                        BorderThdChart.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42)); // Default
                    }
                }
            });

            // Load saved settings if any
            LoadConfig();

            // Load and detect audio devices
            AutoDetectDevices();
            
            // Pre-initialize charts with dark styling
            InitCharts();

            TxtFreqTolerance.TextChanged += (s, e) => CheckAndLoadStandardDevice();
            TxtThdLimit.TextChanged += (s, e) => CheckAndLoadStandardDevice();
            CheckAndLoadStandardDevice();
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "routing_value.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        SliderPlaybackVolume.Value = config.PlaybackVolume;
                        SliderRecordingGain.Value = config.RecordingGain;
                        TxtFreqTolerance.Text = config.FreqTolerance.ToString("F1");
                        TxtThdLimit.Text = config.ThdLimit.ToString("F2");
                        RadioUsbPlayback.IsChecked = config.UseUsbPlayback;
                        RadioBtPlayback.IsChecked = !config.UseUsbPlayback;

                        // Update initial engine values
                        _audioEngine.PlaybackVolume = config.PlaybackVolume / 100.0;
                        _audioEngine.RecordingGain = config.RecordingGain / 100.0;
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var config = new AppConfig
                {
                    PlaybackVolume = SliderPlaybackVolume.Value,
                    RecordingGain = SliderRecordingGain.Value,
                    FreqTolerance = double.TryParse(TxtFreqTolerance.Text, out double ft) ? ft : 3.0,
                    ThdLimit = double.TryParse(TxtThdLimit.Text, out double tl) ? tl : 0.5,
                    UseUsbPlayback = RadioUsbPlayback.IsChecked == true
                };
                string json = JsonSerializer.Serialize(config);
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "routing_value.json");
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private void AutoDetectDevices()
        {
            try
            {
                // Clear combo boxes
                ComboPlayback.Items.Clear();
                ComboBluetooth.Items.Clear();
                ComboRecording.Items.Clear();

                var playbackDevs = _audioEngine.GetPlaybackDevices();
                var recordingDevs = _audioEngine.GetRecordingDevices();

                AppendLog("System", $"Found {playbackDevs.Count} playback and {recordingDevs.Count} recording devices.");

                foreach (var d in playbackDevs) 
                {
                    bool isBluetooth = d.FriendlyName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("Stereo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0;

                    string displayName = isBluetooth ? $"[BT] {d.FriendlyName}" : $"[USB/Wired] {d.FriendlyName}";

                    if (!isBluetooth)
                    {
                        ComboPlayback.Items.Add(new DeviceItem(d, displayName));
                    }
                    else
                    {
                        ComboBluetooth.Items.Add(new DeviceItem(d, displayName));
                    }
                    AppendLog("Device", $"Playback Out Found: {d.FriendlyName} {(isBluetooth ? "[Bluetooth]" : "[Wired/USB]")}");
                }
                foreach (var d in recordingDevs) 
                {
                    bool isBluetooth = d.FriendlyName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("Stereo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       d.FriendlyName.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0;

                    string displayName = isBluetooth ? $"[BT] {d.FriendlyName}" : $"[USB/Wired] {d.FriendlyName}";

                    ComboRecording.Items.Add(new DeviceItem(d, displayName));
                    AppendLog("Device", $"Recording In Found: {d.FriendlyName}");
                }

                // Auto select target devices from the same list instances
                var autoPlayback = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault(item => 
                    item.Device.FriendlyName.IndexOf("MI_LCD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Device.FriendlyName.IndexOf("MI LCD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Device.FriendlyName.IndexOf("MI_SAM", StringComparison.OrdinalIgnoreCase) >= 0);

                var autoBluetooth = ComboBluetooth.Items.Cast<DeviceItem>().FirstOrDefault(item => 
                    item.Device.FriendlyName.IndexOf("MI_LCD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Device.FriendlyName.IndexOf("MI LCD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Device.FriendlyName.IndexOf("MI_SAM", StringComparison.OrdinalIgnoreCase) >= 0);

                var autoRecording = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault(item => 
                    item.Device.FriendlyName.IndexOf("SONCA", StringComparison.OrdinalIgnoreCase) >= 0);

                if (autoPlayback != null) 
                {
                    ComboPlayback.SelectedItem = autoPlayback;
                    AppendLog("AutoSelect", $"Matched Output: {autoPlayback.Device.FriendlyName}");
                }
                else if (ComboPlayback.Items.Count > 0) 
                {
                    ComboPlayback.SelectedIndex = 0;
                    AppendLog("AutoSelect", $"Fallback Output (No MI_LCD/MI_SAM found): {((DeviceItem)ComboPlayback.SelectedItem).Device.FriendlyName}");
                }

                if (autoBluetooth != null) 
                {
                    ComboBluetooth.SelectedItem = autoBluetooth;
                    AppendLog("AutoSelect", $"Matched Bluetooth: {autoBluetooth.Device.FriendlyName}");
                }
                else if (ComboBluetooth.Items.Count > 0) 
                {
                    ComboBluetooth.SelectedIndex = 0;
                }

                if (autoRecording != null) 
                {
                    ComboRecording.SelectedItem = autoRecording;
                    AppendLog("AutoSelect", $"Matched Input: {autoRecording.Device.FriendlyName}");
                }
                else if (ComboRecording.Items.Count > 0) 
                {
                    ComboRecording.SelectedIndex = 0;
                    AppendLog("AutoSelect", $"Fallback Input (No SONCA found): {((DeviceItem)ComboRecording.SelectedItem).Device.FriendlyName}");
                }

                AppendLog("System", "Device discovery finished.");
            }
            catch (Exception ex)
            {
                AppendLog("Error", $"Failed to list audio devices: {ex.Message}");
            }
        }

        public bool ApplyModelDevices(InOutConfig inOutConfig, out string missingMessage)
        {
            _activeInOutConfig = inOutConfig;
            var inputs = inOutConfig?.Devices?.Input ?? new Dictionary<string, string>();
            var outputs = inOutConfig?.Devices?.Output ?? new Dictionary<string, string>();

            var playbackDevs = _audioEngine.GetPlaybackDevices();
            var recordingDevs = _audioEngine.GetRecordingDevices();

            List<string> missing = new List<string>();

            // Check config inputs against playback devices
            Dictionary<string, MMDevice> matchedPlaybacks = new Dictionary<string, MMDevice>();
            foreach (var kvp in inputs)
            {
                string key = kvp.Key;
                string targetName = kvp.Value;
                var match = playbackDevs.FirstOrDefault(d => d.FriendlyName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null)
                {
                    missing.Add($"- Ngõ vào thiết bị (Playback Out): {key} (yêu cầu chứa \"{targetName}\")");
                }
                else
                {
                    matchedPlaybacks[key] = match;
                }
            }

            // Check config outputs against recording devices
            Dictionary<string, MMDevice> matchedRecordings = new Dictionary<string, MMDevice>();
            foreach (var kvp in outputs)
            {
                string key = kvp.Key;
                string targetName = kvp.Value;
                var match = recordingDevs.FirstOrDefault(d => d.FriendlyName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match == null)
                {
                    missing.Add($"- Ngõ ra thiết bị (Recording In): {key} (yêu cầu chứa \"{targetName}\")");
                }
                else
                {
                    matchedRecordings[key] = match;
                }
            }

            if (missing.Count > 0)
            {
                missingMessage = string.Join("\n", missing);
                PanelAutoTestList.Visibility = Visibility.Collapsed;
                return false;
            }

            missingMessage = null;

            // Load matched recording into ComboRecording
            if (matchedRecordings.Count > 0)
            {
                var firstRecording = matchedRecordings.Values.First();
                var item = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device.ID == firstRecording.ID);
                if (item != null)
                {
                    ComboRecording.SelectedItem = item;
                    AppendLog("ModelSelect", $"Auto-selected Recording In: {item.DisplayName}");
                }
            }

            // Load matched playbacks into ComboPlayback and ComboBluetooth
            foreach (var kvp in matchedPlaybacks)
            {
                var dev = kvp.Value;
                string key = kvp.Key;
                
                bool isBluetooth = key.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   dev.FriendlyName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   dev.FriendlyName.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   dev.FriendlyName.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   dev.FriendlyName.IndexOf("Stereo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   dev.FriendlyName.IndexOf("BTH", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isBluetooth)
                {
                    var item = ComboBluetooth.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device.ID == dev.ID);
                    if (item != null)
                    {
                        ComboBluetooth.SelectedItem = item;
                        AppendLog("ModelSelect", $"Auto-selected Bluetooth Out: {item.DisplayName}");
                    }
                }
                else
                {
                    var item = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device.ID == dev.ID);
                    if (item != null)
                    {
                        ComboPlayback.SelectedItem = item;
                        AppendLog("ModelSelect", $"Auto-selected Playback Out: {item.DisplayName}");
                    }
                }
            }

            // Build test cases list
            _autoTestCases.Clear();
            if (inOutConfig?.Tests != null)
            {
                foreach (var tc in inOutConfig.Tests)
                {
                    _autoTestCases.Add(new AutoTestCaseItem
                    {
                        Id = tc.id,
                        Name = tc.name,
                        Status = "WAITING",
                        StatusBrush = new WpfSolidColorBrush(WpfColor.FromRgb(113, 113, 122)),
                        Config = tc
                    });
                }
            }
            ListAutoTestCases.ItemsSource = null;
            ListAutoTestCases.ItemsSource = _autoTestCases;

            PanelAutoTestList.Visibility = Visibility.Visible;

            return true;
        }

        private void InitCharts()
        {
            // Dark styling helper for ScottPlot 5
            ApplyDarkThemeToPlot(PlotFreqResponse.Plot);
            ApplyDarkThemeToPlot(PlotThdFft.Plot);

            // Configure Freq Response Axes
            PlotFreqResponse.Plot.Title("Normalized Frequency Response");
            PlotFreqResponse.Plot.Axes.Left.Label.Text = "Amplitude (dBr)";
            PlotFreqResponse.Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";

            // Draw limits
            double tolerance = double.Parse(TxtFreqTolerance.Text);
            
            var line1 = PlotFreqResponse.Plot.Add.HorizontalLine(tolerance);
            line1.Color = ScottPlot.Colors.Red;
            line1.LineStyle.Width = 1.5f;
            line1.LineStyle.Pattern = LinePattern.Dashed;

            var line2 = PlotFreqResponse.Plot.Add.HorizontalLine(-tolerance);
            line2.Color = ScottPlot.Colors.Red;
            line2.LineStyle.Width = 1.5f;
            line2.LineStyle.Pattern = LinePattern.Dashed;

            // Set Log-Scale Ticks manually for the X-axis
            ConfigureLogarithmicXAxis(PlotFreqResponse.Plot);

            PlotFreqResponse.Plot.Axes.SetLimits(1.3, 4.3, -12, 12); // log10(20) to log10(20000)
            PlotFreqResponse.Refresh();

            // Configure THD Axes
            PlotThdFft.Plot.Title("1 kHz Sine FFT Spectrum");
            PlotThdFft.Plot.Axes.Left.Label.Text = "Magnitude (dBFS)";
            PlotThdFft.Plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
            PlotThdFft.Plot.Axes.SetLimits(0, 10000, -90, 0); // 0 to 10kHz Linear
            PlotThdFft.Refresh();
        }

        private void ConfigureLogarithmicXAxis(Plot plot)
        {
            var ticks = new List<Tick>();
            double[] frequencies = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 20000 };
            string[] labels = { "20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "15k", "20k" };

            for (int i = 0; i < frequencies.Length; i++)
            {
                ticks.Add(new Tick(Math.Log10(frequencies[i]), labels[i]));
            }

            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.ToArray());
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
            PlotFreqResponse.Plot.Clear();

            // Re-apply limits
            double tolerance = double.Parse(TxtFreqTolerance.Text);
            
            var line1 = PlotFreqResponse.Plot.Add.HorizontalLine(tolerance);
            line1.Color = ScottPlot.Colors.Red;
            line1.LineStyle.Width = 1.5f;
            line1.LineStyle.Pattern = LinePattern.Dashed;

            var line2 = PlotFreqResponse.Plot.Add.HorizontalLine(-tolerance);
            line2.Color = ScottPlot.Colors.Red;
            line2.LineStyle.Width = 1.5f;
            line2.LineStyle.Pattern = LinePattern.Dashed;

            // 1. Draw Bass band (20Hz - 250Hz)
            var spanBass = PlotFreqResponse.Plot.Add.VerticalSpan(Math.Log10(20), Math.Log10(250));
            spanBass.LineStyle.Width = 0;
            if (_testRunner != null && !_testRunner.BassPassed)
            {
                spanBass.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.12f); // Red tint for fail
            }
            else
            {
                spanBass.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(0.08f); // Dark tint for normal
            }

            // 2. Draw Mid band (250Hz - 4kHz)
            var spanMid = PlotFreqResponse.Plot.Add.VerticalSpan(Math.Log10(250), Math.Log10(4000));
            spanMid.LineStyle.Width = 0;
            if (_testRunner != null && !_testRunner.MidPassed)
            {
                spanMid.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.12f); // Red tint for fail
            }
            else
            {
                spanMid.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(0.04f); // Slightly lighter tint
            }

            // 3. Draw Treble band (4kHz - 20kHz)
            var spanTreble = PlotFreqResponse.Plot.Add.VerticalSpan(Math.Log10(4000), Math.Log10(20000));
            spanTreble.LineStyle.Width = 0;
            if (_testRunner != null && !_testRunner.TreblePassed)
            {
                spanTreble.FillStyle.Color = ScottPlot.Color.FromHex("#EF4444").WithAlpha(0.12f); // Red tint for fail
            }
            else
            {
                spanTreble.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(0.08f); // Dark tint for normal
            }

            // Draw vertical dashed lines at boundaries
            var lineBassMid = PlotFreqResponse.Plot.Add.VerticalLine(Math.Log10(250));
            lineBassMid.Color = ScottPlot.Color.FromHex("#3F3F46");
            lineBassMid.LineStyle.Width = 1f;
            lineBassMid.LineStyle.Pattern = LinePattern.Dashed;

            var lineMidTreble = PlotFreqResponse.Plot.Add.VerticalLine(Math.Log10(4000));
            lineMidTreble.Color = ScottPlot.Color.FromHex("#3F3F46");
            lineMidTreble.LineStyle.Width = 1f;
            lineMidTreble.LineStyle.Pattern = LinePattern.Dashed;

            // Draw text labels for bands at the top
            double xBassText = (Math.Log10(20) + Math.Log10(250)) / 2.0;
            double xMidText = (Math.Log10(250) + Math.Log10(4000)) / 2.0;
            double xTrebleText = (Math.Log10(4000) + Math.Log10(20000)) / 2.0;

            var txtBass = PlotFreqResponse.Plot.Add.Text(_testRunner != null && !_testRunner.BassPassed ? "BASS (FAIL)" : "BASS", xBassText, 11);
            txtBass.LabelFontColor = _testRunner != null && !_testRunner.BassPassed ? ScottPlot.Colors.Red : ScottPlot.Color.FromHex("#A1A1AA");
            txtBass.LabelFontSize = 10;
            txtBass.LabelBold = true;
            txtBass.LabelAlignment = Alignment.UpperCenter;

            var txtMid = PlotFreqResponse.Plot.Add.Text(_testRunner != null && !_testRunner.MidPassed ? "MID (FAIL)" : "MIDDLE", xMidText, 11);
            txtMid.LabelFontColor = _testRunner != null && !_testRunner.MidPassed ? ScottPlot.Colors.Red : ScottPlot.Color.FromHex("#A1A1AA");
            txtMid.LabelFontSize = 10;
            txtMid.LabelBold = true;
            txtMid.LabelAlignment = Alignment.UpperCenter;

            var txtTreble = PlotFreqResponse.Plot.Add.Text(_testRunner != null && !_testRunner.TreblePassed ? "TREBLE (FAIL)" : "TREBLE", xTrebleText, 11);
            txtTreble.LabelFontColor = _testRunner != null && !_testRunner.TreblePassed ? ScottPlot.Colors.Red : ScottPlot.Color.FromHex("#A1A1AA");
            txtTreble.LabelFontSize = 10;
            txtTreble.LabelBold = true;
            txtTreble.LabelAlignment = Alignment.UpperCenter;

            // Plot standard/reference curve if loaded
            if (_standardCurve != null && _standardCurve.Count > 0)
            {
                double[] xStdLog = _standardCurve.Keys.Select(f => Math.Log10(f)).ToArray();
                double[] yStdDb = _standardCurve.Values.ToArray();

                var spStd = PlotFreqResponse.Plot.Add.Scatter(xStdLog, yStdDb);
                spStd.LineWidth = 2f;
                spStd.Color = ScottPlot.Color.FromHex("#EAB308"); // Yellow/Gold color
                spStd.MarkerSize = 5f;
            }

            bool isSilent = _dbValues.Count > 0 && _dbValues.All(v => v < -30);

            if (_freqs.Count > 0)
            {
                double[] xLog = _freqs.Select(f => Math.Log10(f)).ToArray();
                double[] yDb = _dbValues.ToArray();

                var sp = PlotFreqResponse.Plot.Add.Scatter(xLog, yDb);
                sp.LineWidth = 3f;
                sp.Color = ScottPlot.Color.FromHex("#10B981"); // neon green
                sp.MarkerSize = 6f;
            }

            if (isSilent)
            {
                var txt = PlotFreqResponse.Plot.Add.Text("NO SIGNAL DETECTED", 2.8, 0);
                txt.LabelFontColor = ScottPlot.Colors.Red;
                txt.LabelFontSize = 20;
                txt.LabelBold = true;
                txt.LabelAlignment = Alignment.MiddleCenter;
            }

            if (_testRunner != null && _testRunner.HasComparedToStandard)
            {
                var txtDev = PlotFreqResponse.Plot.Add.Text($"Lệch chuẩn: {_testRunner.LastAvgDevPercent:F1}%", 1.4, 8.5);
                txtDev.LabelFontColor = ScottPlot.Color.FromHex("#EAB308"); // Gold/Yellow
                txtDev.LabelFontSize = 13;
                txtDev.LabelBold = true;
                txtDev.LabelAlignment = Alignment.UpperLeft;
            }

            PlotFreqResponse.Plot.Legend.IsVisible = false;
            ConfigureLogarithmicXAxis(PlotFreqResponse.Plot);
            PlotFreqResponse.Plot.Axes.SetLimits(1.3, 4.3, -12, 12);
            PlotFreqResponse.Refresh();
        }

        private void UpdateThdFftChart(double[] frequencies, double[] magnitudes, double thdPercent)
        {
            PlotThdFft.Plot.Clear();

            bool isSilent = true;

            if (frequencies != null && frequencies.Length > 0)
            {
                // Convert magnitudes to dBFS (max is 0dBFS)
                double[] dbFS = magnitudes.Select(m => Math.Max(-100, 20 * Math.Log10(m + 1e-9))).ToArray();

                if (dbFS.Max() > -70.0)
                {
                    isSilent = false;
                }

                var sp = PlotThdFft.Plot.Add.Scatter(frequencies, dbFS);
                sp.LineWidth = 1.5f;
                sp.Color = ScottPlot.Color.FromHex("#3B82F6"); // neon blue
                sp.MarkerSize = 0f;
            }

            // Annotation for THD text (only show if it is not 0.0, which means it's a THD test, not a noise floor test)
            if (thdPercent > 0.0)
            {
                if (isSilent)
                {
                    var txt = PlotThdFft.Plot.Add.Text("NO SIGNAL DETECTED", 5000, -45);
                    txt.LabelFontColor = ScottPlot.Colors.Red;
                    txt.LabelFontSize = 20;
                    txt.LabelBold = true;
                    txt.LabelAlignment = Alignment.MiddleCenter;
                }
                else
                {
                    var text = PlotThdFft.Plot.Add.Text($"THD: {thdPercent:F3}%", 5000, -15);
                    text.LabelFontColor = ScottPlot.Color.FromHex("#F4F4F5");
                    text.LabelFontSize = 14;
                    text.LabelBold = true;
                }
            }

            PlotThdFft.Plot.Axes.SetLimits(0, 10000, -90, 0);
            PlotThdFft.Refresh();
        }

        private void SetFinalVerdict(bool success)
        {
            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            BtnNoiseTest.IsEnabled = true;
            BtnSaveStandard.IsEnabled = _freqs.Count > 0;

            if (success)
            {
                BorderVerdict.Background = new WpfSolidColorBrush(WpfColor.FromRgb(6, 95, 70)); // Deep green
                BorderVerdict.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(16, 185, 129)); // Neon green
                LblVerdict.Text = "PASS";
                LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(52, 211, 153));
            }
            else
            {
                BorderVerdict.Background = new WpfSolidColorBrush(WpfColor.FromRgb(153, 27, 27)); // Deep red
                BorderVerdict.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(239, 68, 68)); // Neon red
                LblVerdict.Text = "FAIL";
                LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(248, 113, 113));
            }
        }

        private void AppendLog(string source, string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            System.Diagnostics.Debug.WriteLine($"[{time}] [{source}] {message}");
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            // Save settings for next time
            SaveConfig();

            // Reset UI
            _freqs.Clear();
            _dbValues.Clear();
            PlotFreqResponse.Plot.Clear();
            PlotThdFft.Plot.Clear();
            InitCharts();

            TxtFreqStatus.Text = "";
            TxtThdStatus.Text = "";
            BorderVerdict.Background = new WpfSolidColorBrush(WpfColor.FromRgb(24, 24, 27));
            BorderVerdict.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42));
            LblVerdict.Text = "TESTING...";
            LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(250, 204, 21)); // Yellow

            BtnStart.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            BtnNoiseTest.IsEnabled = false;
            BtnSaveStandard.IsEnabled = false;

            // Update limits in runner
            if (double.TryParse(TxtFreqTolerance.Text, out double fTol))
                _testRunner.FreqResponseToleranceDb = fTol;
            if (double.TryParse(TxtThdLimit.Text, out double thdLim))
                _testRunner.ThdLimitPercent = thdLim;

            CheckAndLoadStandardDevice();
            _testRunner.StandardCurve = _standardCurve;

            var usbDevice = (ComboPlayback.SelectedItem as DeviceItem)?.Device;
            var btDevice = (ComboBluetooth.SelectedItem as DeviceItem)?.Device;
            var playbackDevice = RadioUsbPlayback.IsChecked == true ? usbDevice : btDevice;
            var recordingDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;

            await _testRunner.RunTestAsync(playbackDevice, recordingDevice);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _testRunner.Cancel();
            AppendLog("System", "Test cancelled by user.");
            SetFinalVerdict(false);
            LblVerdict.Text = "CANCELLED";
            LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(248, 113, 113));
        }

        private async void BtnNoiseTest_Click(object sender, RoutedEventArgs e)
        {
            PlotThdFft.Plot.Clear();
            PlotThdFft.Refresh();
            
            BorderVerdict.Background = new WpfSolidColorBrush(WpfColor.FromRgb(24, 24, 27));
            BorderVerdict.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42));
            LblVerdict.Text = "ANALYZING NOISE...";
            LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(250, 204, 21)); // Yellow

            BtnStart.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            BtnNoiseTest.IsEnabled = false;

            var usbDevice = (ComboPlayback.SelectedItem as DeviceItem)?.Device;
            var btDevice = (ComboBluetooth.SelectedItem as DeviceItem)?.Device;
            var playbackDevice = RadioUsbPlayback.IsChecked == true ? usbDevice : btDevice;
            var recordingDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;

            await _testRunner.RunNoiseTestAsync(playbackDevice, recordingDevice);

            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            BtnNoiseTest.IsEnabled = true;
            LblVerdict.Text = "NOISE DONE";
            LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(52, 211, 153)); // Green
        }

        private void SliderPlaybackVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioEngine != null)
            {
                _audioEngine.PlaybackVolume = e.NewValue / 100.0;
            }
            if (LblPlaybackVolume != null)
            {
                LblPlaybackVolume.Text = $"{(int)e.NewValue}%";
            }
            CheckAndLoadStandardDevice();
        }

        private void SliderRecordingGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioEngine != null)
            {
                _audioEngine.RecordingGain = e.NewValue / 100.0;
            }
            if (LblRecordingGain != null)
            {
                LblRecordingGain.Text = $"{(int)e.NewValue}%";
            }
            CheckAndLoadStandardDevice();
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            AutoDetectDevices();
        }

        private void ComboDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtFreqTolerance == null || TxtThdLimit == null) return;

            var usbItem = ComboPlayback.SelectedItem as DeviceItem;
            var btItem = ComboBluetooth.SelectedItem as DeviceItem;

            string usbName = usbItem?.Device?.FriendlyName ?? usbItem?.DisplayName ?? "";
            string btName = btItem?.Device?.FriendlyName ?? btItem?.DisplayName ?? "";

            bool hasMic = usbName.IndexOf("MIC 1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          usbName.IndexOf("MIC 2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          btName.IndexOf("MIC 1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          btName.IndexOf("MIC 2", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasMic)
            {
                TxtFreqTolerance.Text = "4.5";
                TxtThdLimit.Text = "0.8";
            }
            else
            {
                TxtFreqTolerance.Text = "3.0";
                TxtThdLimit.Text = "0.5";
            }

            CheckAndLoadStandardDevice();
        }

        private async void BtnStartAutoTest_Click(object sender, RoutedEventArgs e)
        {
            if (_activeInOutConfig == null || _autoTestCases.Count == 0) return;

            BtnStart.IsEnabled = false;
            BtnStartAutoTest.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            BtnNoiseTest.IsEnabled = false;
            BtnSaveStandard.IsEnabled = false;

            _isExecutingAutoSuite = true;
            bool suitePassed = true;

            foreach (var test in _autoTestCases)
            {
                test.Status = "WAITING";
                test.StatusBrush = new WpfSolidColorBrush(WpfColor.FromRgb(113, 113, 122));
                test.FreqStatus = "WAITING";
                test.FreqBrush = new WpfSolidColorBrush(WpfColor.FromRgb(113, 113, 122));
                test.ThdStatus = "WAITING";
                test.ThdBrush = new WpfSolidColorBrush(WpfColor.FromRgb(113, 113, 122));
            }

            foreach (var test in _autoTestCases)
            {
                if (!_isExecutingAutoSuite) break;

                _currentRunningTestCase = test;
                _currentTestSuccess = null;

                test.Status = "RUNNING";
                test.StatusBrush = new WpfSolidColorBrush(WpfColor.FromRgb(250, 204, 21)); // Yellow

                // 1. Map and Select target Playback Out device
                string playbackConfigKey = test.Config.PlaybackOut; // e.g. "SoundCard"
                string playbackDeviceName = "";
                _activeInOutConfig.Devices.Input?.TryGetValue(playbackConfigKey, out playbackDeviceName);

                if (!string.IsNullOrEmpty(playbackDeviceName))
                {
                    // Find in ComboPlayback or ComboBluetooth
                    var usbItem = ComboPlayback.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device.FriendlyName.IndexOf(playbackDeviceName, StringComparison.OrdinalIgnoreCase) >= 0);
                    var btItem = ComboBluetooth.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device.FriendlyName.IndexOf(playbackDeviceName, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (usbItem != null)
                    {
                        ComboPlayback.SelectedItem = usbItem;
                        RadioUsbPlayback.IsChecked = true;
                        RadioBtPlayback.IsChecked = false;
                    }
                    else if (btItem != null)
                    {
                        ComboBluetooth.SelectedItem = btItem;
                        RadioUsbPlayback.IsChecked = false;
                        RadioBtPlayback.IsChecked = true;
                    }
                }

                // 2. Map and Select target Recording In device
                string recordingConfigKey = test.Config.RecordingIn; // e.g. "6.5 Jack"
                string recordingDeviceName = "";
                _activeInOutConfig.Devices.Output?.TryGetValue(recordingConfigKey, out recordingDeviceName);

                if (!string.IsNullOrEmpty(recordingDeviceName))
                {
                    var recItem = ComboRecording.Items.Cast<DeviceItem>().FirstOrDefault(i => i.Device.FriendlyName.IndexOf(recordingDeviceName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (recItem != null)
                    {
                        ComboRecording.SelectedItem = recItem;
                    }
                }

                // Reset UI & charts for this individual run
                _freqs.Clear();
                _dbValues.Clear();
                PlotFreqResponse.Plot.Clear();
                PlotThdFft.Plot.Clear();
                InitCharts();

                TxtFreqStatus.Text = "";
                TxtThdStatus.Text = "";
                BorderVerdict.Background = new WpfSolidColorBrush(WpfColor.FromRgb(24, 24, 27));
                BorderVerdict.BorderBrush = new WpfSolidColorBrush(WpfColor.FromRgb(39, 39, 42));
                LblVerdict.Text = $"TESTING {test.Id}...";
                LblVerdict.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(250, 204, 21));

                // Update limits in runner
                if (double.TryParse(TxtFreqTolerance.Text, out double fTol))
                    _testRunner.FreqResponseToleranceDb = fTol;
                if (double.TryParse(TxtThdLimit.Text, out double thdLim))
                    _testRunner.ThdLimitPercent = thdLim;

                CheckAndLoadStandardDevice();
                _testRunner.StandardCurve = _standardCurve;

                var usbDev = (ComboPlayback.SelectedItem as DeviceItem)?.Device;
                var btDev = (ComboBluetooth.SelectedItem as DeviceItem)?.Device;
                var playbackDevice = RadioUsbPlayback.IsChecked == true ? usbDev : btDev;
                var recordingDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;

                await _testRunner.RunTestAsync(playbackDevice, recordingDevice);

                // Wait for test to finish and set status
                bool testPassed = _currentTestSuccess == true;
                if (!testPassed)
                {
                    suitePassed = false;
                }

                test.Status = testPassed ? "PASS" : "FAIL";
                test.StatusBrush = testPassed ? new WpfSolidColorBrush(WpfColor.FromRgb(52, 211, 153)) : new WpfSolidColorBrush(WpfColor.FromRgb(248, 113, 113));
            }

            _isExecutingAutoSuite = false;
            _currentRunningTestCase = null;

            BtnStart.IsEnabled = true;
            BtnStartAutoTest.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            BtnNoiseTest.IsEnabled = true;

            SetFinalVerdict(suitePassed);
        }

        private string GetStandardDeviceFileName()
        {
            if (ComboPlayback == null || RadioBtPlayback == null || ComboBluetooth == null || 
                ComboRecording == null || SliderPlaybackVolume == null || SliderRecordingGain == null || 
                TxtFreqTolerance == null || TxtThdLimit == null)
            {
                return "";
            }

            string outDevice = ComboPlayback.SelectedItem is DeviceItem pDevice ? pDevice.DisplayName : "UnknownOut";
            if (RadioBtPlayback.IsChecked == true)
            {
                outDevice = ComboBluetooth.SelectedItem is DeviceItem btDevice ? btDevice.DisplayName : "UnknownOut";
            }
            string inDevice = ComboRecording.SelectedItem is DeviceItem rDevice ? rDevice.DisplayName : "UnknownIn";
            double playVol = SliderPlaybackVolume.Value;
            double recGain = SliderRecordingGain.Value;
            double tol = 0;
            double.TryParse(TxtFreqTolerance.Text, out tol);
            double thdLim = 0;
            double.TryParse(TxtThdLimit.Text, out thdLim);

            string key = $"{outDevice}_IN_{inDevice}_V_{playVol:F0}_G_{recGain:F0}_T_{tol:F1}_THD_{thdLim:F2}";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                key = key.Replace(c, '_');
            }
            key = key.Replace(' ', '_');
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"standard_{key}.csv");
        }

        private void CheckAndLoadStandardDevice()
        {
            _standardCurve = null;
            try
            {
                string filePath = GetStandardDeviceFileName();
                if (string.IsNullOrEmpty(filePath)) return;

                if (System.IO.File.Exists(filePath))
                {
                    var curve = new Dictionary<double, double>();
                    var lines = System.IO.File.ReadAllLines(filePath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Frequency")) continue;
                        var parts = line.Split(',');
                        if (parts.Length >= 2 && double.TryParse(parts[0], out double freq) && double.TryParse(parts[1], out double db))
                        {
                            curve[freq] = db;
                        }
                    }
                    if (curve.Count > 0)
                    {
                        _standardCurve = curve;
                    }
                }
            }
            catch { }

            if (PlotFreqResponse != null)
            {
                UpdateFreqResponseChart();
            }
        }

        private void BtnSaveStandard_Click(object sender, RoutedEventArgs e)
        {
            if (_freqs.Count == 0 || _dbValues.Count == 0 || _freqs.Count != _dbValues.Count)
            {
                MessageBox.Show("Vui lòng chạy đo FEQ trước khi lưu thiết bị chuẩn!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string filePath = GetStandardDeviceFileName();
                using (var writer = new System.IO.StreamWriter(filePath))
                {
                    writer.WriteLine("Frequency (Hz),Normalized Level (dBr)");
                    for (int i = 0; i < _freqs.Count; i++)
                    {
                        writer.WriteLine($"{_freqs[i]},{_dbValues[i]:F4}");
                    }
                }
                MessageBox.Show($"Đã lưu thông số thiết bị chuẩn thành công vào file:\n{System.IO.Path.GetFileName(filePath)}", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                CheckAndLoadStandardDevice();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lưu thiết bị chuẩn: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public class AutoTestCaseItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _status = "WAITING";
        private System.Windows.Media.Brush _statusBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));

        private string _freqStatus = "WAITING";
        private System.Windows.Media.Brush _freqBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));

        private string _thdStatus = "WAITING";
        private System.Windows.Media.Brush _thdBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public TestConfig Config { get; set; }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }

        public System.Windows.Media.Brush StatusBrush
        {
            get => _statusBrush;
            set
            {
                _statusBrush = value;
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        public string FreqStatus
        {
            get => _freqStatus;
            set
            {
                _freqStatus = value;
                OnPropertyChanged(nameof(FreqStatus));
            }
        }

        public System.Windows.Media.Brush FreqBrush
        {
            get => _freqBrush;
            set
            {
                _freqBrush = value;
                OnPropertyChanged(nameof(FreqBrush));
            }
        }

        public string ThdStatus
        {
            get => _thdStatus;
            set
            {
                _thdStatus = value;
                OnPropertyChanged(nameof(ThdStatus));
            }
        }

        public System.Windows.Media.Brush ThdBrush
        {
            get => _thdBrush;
            set
            {
                _thdBrush = value;
                OnPropertyChanged(nameof(ThdBrush));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}
