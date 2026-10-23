using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

public sealed record ImpedanceCapture(string Context, ImpedanceRatio[] Ratios);
public sealed record ImpedanceSaved(string Context, ImpedanceSettings Settings, ImpedanceResult Result);
public sealed class ImpedanceWindow : Window
{
    private readonly Func<string, string, bool, ImpedanceSettings, string?, CancellationToken, Task<ImpedanceCapture>> _capture;
    private readonly Func<string, string, bool, ImpedanceSettings, string> _context;
    private readonly ComboBox _output = new() { MinWidth = 300 }, _input = new() { MinWidth = 300 };
    private readonly CheckBox _ready = new() { Content = "Đã tách nhánh/củ treble khỏi ampli; nguồn headphone → Rsense → tải, hai ngõ LINE IN độc lập, không phantom.", Foreground = Brushes.Gold },
        _swap = new() { Content = "Đảo kênh thu 1/2", Foreground = Brushes.WhiteSmoke };
    private readonly TextBox _sense = Box("100"), _known = Box("10"), _start = Box("2000"), _end = Box("18000"), _level = Box("-42"), _seconds = Box("3");
    private readonly TextBlock _status = DiagnosticViewStyle.Text("CHƯA ĐO · THỦ CÔNG", 20), _message = DiagnosticViewStyle.Text("Cần chuẩn hở → ngắn → điện trở chuẩn trước khi đo tải. Chưa có phần cứng thì chưa phát tín hiệu."),
        _calStatus = DiagnosticViewStyle.Text("Chưa có calibration."), _comparison = DiagnosticViewStyle.Text("Để tìm phần thuần trở nối tiếp, nạp mẫu củ treble chuẩn cùng fixture để đối chiếu.");
    private readonly WpfPlot _plot = new() { MinHeight = 250 };
    private readonly WpfPlot _phase = new() { MinHeight = 250 };
    private readonly List<Button> _actions = new();
    private readonly string _data;
    private CancellationTokenSource? _cancellation;
    private Task? _pending;
    private ImpedanceCalibration? _calibration;
    private ImpedanceSaved? _last;
    private bool _allowClose;
    public bool IsBusy => _pending is { IsCompleted: false };
    public void Cancel() => _cancellation?.Cancel();
    public async Task StopForShutdownAsync()
    { Cancel(); if (_pending != null) await _pending; _allowClose = true; Close(); }
    private static TextBox Box(string text) => new() { Text = text, Width = 75, Margin = new Thickness(5), Padding = new Thickness(5), Foreground = Brushes.WhiteSmoke, Background = new SolidColorBrush(Color.FromRgb(28, 28, 31)) };
    public ImpedanceWindow(Func<string, string, bool, ImpedanceSettings, string?, CancellationToken, Task<ImpedanceCapture>> capture,
        Func<string, string, bool, ImpedanceSettings, string> context, string data)
    {
        _capture = capture; _context = context; _data = Path.Combine(data, "impedance");
        Title = "SONCA · Impedance — đo trở kháng thủ công"; Width = 1200; Height = 840; MinWidth = 900; MinHeight = 650;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)); Foreground = Brushes.WhiteSmoke;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SoncaAudioInspector;component/ImpulseTheme.xaml") });
        var panel = new DockPanel { Margin = new Thickness(15) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(DiagnosticViewStyle.Text("IMPEDANCE · |Z| / R / X / PHA", 20));
        top.Children.Add(DiagnosticViewStyle.Text("Headphone L ──● CH1 ── Rsense không cảm ──● CH2 ── TẢI ── GND chung", 15, "#60A5FA"));
        top.Children.Add(DiagnosticViewStyle.Text("Đo điện trực tiếp, không qua DSP/LINE OUT C360. Dùng nguồn headphone mức nhỏ; không nối ngõ công suất ampli vào soundcard. Mặc định treble 2–18 kHz, −42 dBFS peak; giới hạn phát −30 dBFS.", 13, "#A1A1AA"));
        var routes = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) }; top.Children.Add(routes);
        routes.Children.Add(DiagnosticViewStyle.Text("Phát ")); routes.Children.Add(_output); routes.Children.Add(DiagnosticViewStyle.Text("Thu stereo ")); routes.Children.Add(_input); routes.Children.Add(_swap);
        using (var devices = new MMDeviceEnumerator())
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (device)
            (device.DataFlow == DataFlow.Render ? _output : _input).Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
        _output.SelectedIndex = _input.SelectedIndex = -1;
        top.Children.Add(_ready);
        var fields = new WrapPanel { Margin = new Thickness(0, 10, 0, 5) }; top.Children.Add(fields);
        foreach (var field in new[] { ("Rsense Ω", _sense), ("R chuẩn Ω", _known), ("Từ Hz", _start), ("Đến Hz", _end), ("dBFS peak", _level), ("Giây", _seconds) })
        { fields.Children.Add(DiagnosticViewStyle.Text(field.Item1)); fields.Children.Add(field.Item2); }
        var buttons = new WrapPanel(); top.Children.Add(buttons);
        void Button(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4), Foreground = Brushes.WhiteSmoke, Background = new SolidColorBrush(Color.FromRgb(35, 45, 50)) };
            button.Click += (_, _) => action(); buttons.Children.Add(button); _actions.Add(button);
        }
        foreach (string stage in new[] { "HỞ", "NGẮN", "R CHUẨN", "PHÁT / ĐO TẢI" }) Button(stage, () => { if (!IsBusy) _pending = RunAsync(stage); });
        Button("DỪNG", Cancel); Button("Lưu CSV", Export); Button("Lưu mẫu củ chuẩn", SaveBaseline); Button("So với mẫu củ chuẩn", Compare);
        top.Children.Add(_calStatus); top.Children.Add(_status); top.Children.Add(_message); top.Children.Add(_comparison);
        DiagnosticViewStyle.Plot(_plot, "|Z| / R / X (Ω)"); _plot.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
        DiagnosticViewStyle.Plot(_phase, "Pha (độ)"); _phase.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
        foreach (var graph in new[] { _plot, _phase })
        { graph.Plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#18181B"); graph.Plot.Legend.FontColor = ScottPlot.Color.FromHex("#E4E4E7"); graph.Plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#52525B"); }
        var plots = new TabControl(); plots.Items.Add(new TabItem { Header = "Trở kháng Ω", Content = _plot });
        plots.Items.Add(new TabItem { Header = "Pha °", Content = _phase }); panel.Children.Add(plots);
        Closing += async (_, e) => { if (_allowClose || !IsBusy) return; e.Cancel = true; Cancel(); if (_pending != null) await _pending; _allowClose = true; Close(); };
    }
    private ImpedanceSettings Settings()
    {
        double Number(TextBox box) => double.Parse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        var settings = new ImpedanceSettings(Number(_sense), Number(_known), Number(_start), Number(_end), Number(_level), Number(_seconds)); settings.Validate(); return settings;
    }
    private async Task RunAsync(string stage)
    {
        try
        {
            if (_ready.IsChecked != true || _output.SelectedItem is not ComboBoxItem output || _input.SelectedItem is not ComboBoxItem input)
                throw new InvalidOperationException("Chọn ngõ phát/thu và xác nhận đúng fixture trước khi phát. Cần hai ngõ thu LINE IN độc lập và Rsense; USB-C recording C360 chưa đủ.");
            var settings = Settings();
            string currentContext = _context((string)output.Tag, (string)input.Tag, _swap.IsChecked == true, settings);
            string calibrationPath = Path.Combine(_data, "calibration-" + currentContext + ".json");
            if (_calibration?.Context != currentContext) _calibration = File.Exists(calibrationPath)
                ? JsonSerializer.Deserialize<ImpedanceCalibration>(File.ReadAllText(calibrationPath)) : null;
            _calStatus.Text = _calibration == null ? "Chưa có calibration cùng cấu hình." : $"Calibration: Hở {_calibration.Open.Length} · Ngắn {_calibration.Short.Length} · R chuẩn {_calibration.Known.Length} điểm.";
            if (stage == "NGẮN" && _calibration?.Open.Length is not > 0 || stage == "R CHUẨN" && (_calibration?.Open.Length is not > 0 || _calibration.Short.Length == 0))
                throw new InvalidOperationException("Thực hiện đúng thứ tự chuẩn HỞ → NGẮN → R CHUẨN cùng fixture.");
            if (stage != "PHÁT / ĐO TẢI" && MessageBox.Show(this, stage == "HỞ" ? "Để hở hai đầu dây phía tải. Bắt đầu chuẩn hở?"
                : stage == "NGẮN" ? "Chập hai đầu dây phía tải, vẫn giữ Rsense nối tiếp. Bắt đầu chuẩn ngắn?"
                : $"Nối điện trở chuẩn không cảm {settings.KnownOhms:0.###} Ω vào vị trí tải. Bắt đầu?", "Calibration Impedance", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            _cancellation = new(); foreach (var action in _actions) if ((string)action.Content != "DỪNG") action.IsEnabled = false;
            foreach (Control control in new Control[] { _output, _input, _swap, _ready, _sense, _known, _start, _end, _level, _seconds }) control.IsEnabled = false;
            _status.Text = "ĐANG THU · " + stage;
            _last = null;
            _comparison.Text = "";
            _plot.Plot.Clear(); _phase.Plot.Clear(); _plot.Refresh(); _phase.Refresh();
            // A load measurement may only emit after calibration has been checked against the current context.
            var capture = await _capture((string)output.Tag, (string)input.Tag, _swap.IsChecked == true, settings,
                stage == "PHÁT / ĐO TẢI" ? _calibration?.Context : null,
                stage == "PHÁT / ĐO TẢI" ? PrepareLoadToken(settings) : _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested();
            if (stage == "HỞ" && capture.Ratios.Any(p => p.LoadSnrDb < 30 || p.Ratio.Magnitude < .79 || p.Ratio.Magnitude > 1.26))
                throw new InvalidOperationException("Chuẩn hở: hai kênh lệch quá 2 dB hoặc tín hiệu thấp; kiểm tra dây/gain.");
            if (stage == "NGẮN" && capture.Ratios.Any(p => p.Ratio.Magnitude > .15))
                throw new InvalidOperationException("Chuẩn ngắn chưa đúng: kênh tải chưa gần 0. Kiểm tra chập tải và hai kênh độc lập.");
            string path = Path.Combine(_data, "calibration-" + capture.Context + ".json");
            if (_calibration?.Context != capture.Context)
            {
                _calibration = File.Exists(path) ? JsonSerializer.Deserialize<ImpedanceCalibration>(File.ReadAllText(path)) : null;
                _calibration ??= new(capture.Context, settings, Array.Empty<ImpedanceRatio>(), Array.Empty<ImpedanceRatio>(), Array.Empty<ImpedanceRatio>(), DateTimeOffset.UtcNow);
            }
            if (stage == "HỞ") _calibration = _calibration with { Open = capture.Ratios, Short = Array.Empty<ImpedanceRatio>(), Known = Array.Empty<ImpedanceRatio>() };
            else if (stage == "NGẮN") _calibration = _calibration with { Short = capture.Ratios, Known = Array.Empty<ImpedanceRatio>() };
            else if (stage == "R CHUẨN") _calibration = _calibration with { Known = capture.Ratios };
            else
            {
                var result = ImpedanceMeasurement.Calculate(capture.Ratios, _calibration, capture.Context);
                _last = new(capture.Context, settings, result); Show(result); return;
            }
            if (_calibration.Open.Length > 0 && _calibration.Short.Length > 0 && _calibration.Known.Length > 0) ImpedanceMeasurement.ValidateCalibration(_calibration, capture.Context);
            Directory.CreateDirectory(_data); AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_calibration));
            _calStatus.Text = $"Calibration: Hở {_calibration.Open.Length} · Ngắn {_calibration.Short.Length} · R chuẩn {_calibration.Known.Length} điểm.";
            _status.Text = "ĐÃ THU CHUẨN " + stage; _message.Text = "Giữ nguyên ngõ, kênh, gain, Windows Levels và dây đo giữa các bước.";
        }
        catch (OperationCanceledException) { _status.Text = "ĐÃ DỪNG"; }
        catch (Exception ex) { _status.Text = "CHƯA ĐO HỢP LỆ"; _message.Text = ex.Message; }
        finally
        {
            _cancellation?.Dispose(); _cancellation = null;
            foreach (var action in _actions) action.IsEnabled = true;
            foreach (Control control in new Control[] { _output, _input, _swap, _ready, _sense, _known, _start, _end, _level, _seconds }) control.IsEnabled = true;
        }
    }
    private CancellationToken PrepareLoadToken(ImpedanceSettings settings)
    {
        if (_calibration == null || _calibration.Settings != settings) throw new InvalidOperationException("Cần hoàn tất chuẩn hở/ngắn/R chuẩn cùng cấu hình trước khi đo tải.");
        ImpedanceMeasurement.ValidateCalibration(_calibration, _calibration.Context); return _cancellation!.Token;
    }
    private void Show(ImpedanceResult result)
    {
        _status.Text = result.IsValid ? "ĐÃ ĐO TRỞ KHÁNG" : result.OpenOrHighImpedance ? "TẢI HỞ / TRỞ KHÁNG CAO" : "CHƯA ĐO HỢP LỆ"; _status.Foreground = result.IsValid ? Brushes.MediumSpringGreen : Brushes.Gold;
        _message.Text = result.Message; _plot.Plot.Clear(); _phase.Plot.Clear();
        var points = result.IsValid ? result.Points : result.DiagnosticPoints;
        if (points.Length > 0)
        {
            var f = points.Select(p => Math.Log10(p.FrequencyHz)).ToArray();
            var ticks = new[] { 500.0, 1000, 2000, 3000, 5000, 10000, 18000, 20000 }.Where(t => t >= points.Min(p => p.FrequencyHz) && t <= points.Max(p => p.FrequencyHz)).ToArray();
            foreach (var graph in new[] { _plot, _phase }) graph.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                ticks.Select(Math.Log10).ToArray(), ticks.Select(t => t >= 1000 ? $"{t/1000:0.#}k" : $"{t:0}").ToArray());
            var magnitude = _plot.Plot.Add.Scatter(f, points.Select(p => p.MagnitudeOhms).ToArray());
            magnitude.LegendText = result.IsValid ? "|Z| Ω" : "|Z| Ω · chẩn đoán tải hở/cao";
            if (!result.IsValid) magnitude.Color = ScottPlot.Colors.Magenta;
            if (result.IsValid)
            {
                _plot.Plot.Add.Scatter(f, points.Select(p => p.ResistanceOhms).ToArray()).LegendText = "R Ω";
                _plot.Plot.Add.Scatter(f, points.Select(p => p.ReactanceOhms).ToArray()).LegendText = "X Ω";
            }
            _plot.Plot.ShowLegend(); _plot.Plot.Axes.AutoScale();
            _phase.Plot.Add.Scatter(f, points.Select(p => p.PhaseDegrees).ToArray()); _phase.Plot.Axes.AutoScale();
        }
        else if (result.OpenOrHighImpedance) _message.Text += " Tỉ số trùng chuẩn hở: |Z| → ∞; không có giá trị hữu hạn để vẽ.";
        _plot.Refresh(); _phase.Refresh();
    }
    private void Export()
    {
        if (_last?.Result.IsValid != true) { _message.Text = "Chưa có bản đo hợp lệ để xuất."; return; }
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "impedance.csv" };
        if (dialog.ShowDialog(this) == true) File.WriteAllLines(dialog.FileName, new[] { "Hz,Ohms,Phase_deg,R_ohms,X_ohms" }.Concat(_last.Result.Points.Select(p => FormattableString.Invariant($"{p.FrequencyHz},{p.MagnitudeOhms},{p.PhaseDegrees},{p.ResistanceOhms},{p.ReactanceOhms}"))));
    }
    private void SaveBaseline()
    {
        if (_last?.Result.IsValid != true) { _message.Text = "Đo củ/nhánh treble chuẩn trước khi lưu mẫu."; return; }
        var dialog = new SaveFileDialog { Filter = "Mẫu impedance|*.json", FileName = "treble-reference.json" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_last));
    }
    private void Compare()
    {
        if (_last?.Result.IsValid != true) { _message.Text = "Cần bản đo hợp lệ để đối chiếu."; return; }
        var dialog = new OpenFileDialog { Filter = "Mẫu impedance|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var baseline = JsonSerializer.Deserialize<ImpedanceSaved>(File.ReadAllText(dialog.FileName));
            if (baseline?.Context != _last.Context || baseline.Result.IsValid != true) throw new InvalidOperationException("Mẫu khác ngõ/gain/calibration/dải tần; chưa so được điện trở nối tiếp.");
            _comparison.Text = ImpedanceMeasurement.CompareSeriesResistance(_last.Result.Points, baseline.Result.Points);
        }
        catch (Exception ex) { _comparison.Text = ex.Message; }
    }
}
