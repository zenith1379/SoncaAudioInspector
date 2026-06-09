using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using NAudio.CoreAudioApi;
using ScottPlot;

using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SoncaAudioInspector
{
    public class AppConfig
    {
        public double PlaybackVolume { get; set; } = 80;
        public double RecordingGain { get; set; } = 100;
        public double FreqTolerance { get; set; } = 3.0;
        public double ThdLimit { get; set; } = 0.5;
        public bool UseUsbPlayback { get; set; } = true;
    }

    public class DeviceItem
    {
        public MMDevice Device { get; set; }
        public string DisplayName { get; set; }

        public DeviceItem(MMDevice device, string displayName)
        {
            Device = device;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }

    public partial class MainWindow : Window
    {
        private AudioEngine _audioEngine;
        private TestRunner _testRunner;

        private List<double> _freqs = new List<double>();
        private List<double> _dbValues = new List<double>();

        public MainWindow()
        {
            InitializeComponent();
            
            _audioEngine = new AudioEngine();
            _testRunner = new TestRunner(_audioEngine);

            // Register TestRunner events
            _testRunner.OnStepsChanged += Steps => Dispatcher.Invoke(() => ListSteps.ItemsSource = Steps.ToList());
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

            _testRunner.OnTestCompleted += Success => Dispatcher.Invoke(() => SetFinalVerdict(Success));

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
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
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
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
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
            double[] frequencies = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
            string[] labels = { "20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "20k" };

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

                if (dbFS.Max() > -50.0)
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
            BtnDetect.IsEnabled = true;
            BtnNoiseTest.IsEnabled = true;

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
            BtnDetect.IsEnabled = false;
            BtnNoiseTest.IsEnabled = false;

            // Update limits in runner
            if (double.TryParse(TxtFreqTolerance.Text, out double fTol))
                _testRunner.FreqResponseToleranceDb = fTol;
            if (double.TryParse(TxtThdLimit.Text, out double thdLim))
                _testRunner.ThdLimitPercent = thdLim;

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

        private void BtnDetect_Click(object sender, RoutedEventArgs e)
        {
            AutoDetectDevices();
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
            BtnDetect.IsEnabled = false;
            BtnNoiseTest.IsEnabled = false;

            var usbDevice = (ComboPlayback.SelectedItem as DeviceItem)?.Device;
            var btDevice = (ComboBluetooth.SelectedItem as DeviceItem)?.Device;
            var playbackDevice = RadioUsbPlayback.IsChecked == true ? usbDevice : btDevice;
            var recordingDevice = (ComboRecording.SelectedItem as DeviceItem)?.Device;

            await _testRunner.RunNoiseTestAsync(playbackDevice, recordingDevice);

            BtnStart.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            BtnDetect.IsEnabled = true;
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
        }

        private bool _isFullscreen = false;
        private WindowStyle _previousWindowStyle;
        private WindowState _previousWindowState;
        private ResizeMode _previousResizeMode;

        private void BtnToggleFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFullscreen)
            {
                _previousWindowStyle = this.WindowStyle;
                _previousWindowState = this.WindowState;
                _previousResizeMode = this.ResizeMode;

                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.WindowState = WindowState.Maximized;
                _isFullscreen = true;
            }
            else
            {
                this.WindowStyle = _previousWindowStyle;
                this.ResizeMode = _previousResizeMode;
                this.WindowState = _previousWindowState;
                _isFullscreen = false;
            }
        }

        private void BtnCloseApp_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            AutoDetectDevices();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            var result = MessageBox.Show(
                "Do you want to exit Sonca Audio Inspector?", 
                "Confirm Exit", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
            }
            else
            {
                base.OnClosing(e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _audioEngine?.Dispose();
            base.OnClosed(e);
        }
    }
}