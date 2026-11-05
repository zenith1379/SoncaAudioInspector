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
public sealed class ImpedanceView : UserControl
{
    private readonly Func<string, string, bool, ImpedanceSettings, string?, CancellationToken, Task<ImpedanceCapture>> _capture;
    private readonly Func<string, string, bool, ImpedanceSettings, string> _context;
    private readonly ComboBox _output = new() { MinWidth = 300 }, _input = new() { MinWidth = 300 };
    private readonly CheckBox _swap = new() { Content = "Đảo kênh thu 1/2", Foreground = Brushes.WhiteSmoke };
    private readonly TextBox _sense = Box("100"), _known = Box("10"), _start = Box("20"), _end = Box("20000"), _level = Box("-42"), _seconds = Box("10"), _inputOhms = Box("20000"), _leadOhms = Box("0");
    private readonly CheckBox _components = new() { Content = "Hiện R / X", Foreground = Brushes.WhiteSmoke };
    private readonly TextBlock _status = DiagnosticViewStyle.Text("CHƯA ĐO", 18), _message = DiagnosticViewStyle.Text("Đã đấu mạch Rsense + hai kênh thu: chọn ngõ → bấm ĐO. Chưa có calibration vẫn vẽ chẩn đoán; chuẩn hở để dùng kết quả ohm. Calibration tự lưu cho cùng cấu hình."),
        _calStatus = DiagnosticViewStyle.Text("Chưa có calibration."), _comparison = DiagnosticViewStyle.Text("Để tìm phần thuần trở nối tiếp, nạp mẫu củ treble chuẩn cùng fixture để đối chiếu.");
    private readonly WpfPlot _plot = new() { MinHeight = 250 };
    private readonly WpfPlot _phase = new() { MinHeight = 250 };
    private readonly List<Button> _actions = new();
    private readonly string _data;
    private CancellationTokenSource? _cancellation;
    private Task? _pending;
    private ImpedanceCalibration? _calibration;
    private ImpedanceSaved? _last;
    public bool IsBusy => _pending is { IsCompleted: false };
    public void Cancel() => _cancellation?.Cancel();
    public async Task StopForShutdownAsync()
    { Cancel(); if (_pending != null) await _pending;  }
    private static TextBox Box(string text) => new() { Text = text, Width = 75, Margin = new Thickness(5), Padding = new Thickness(5), Foreground = Brushes.WhiteSmoke, Background = new SolidColorBrush(Color.FromRgb(28, 28, 31)) };
    public ImpedanceView(Func<string, string, bool, ImpedanceSettings, string?, CancellationToken, Task<ImpedanceCapture>> capture,
        Func<string, string, bool, ImpedanceSettings, string> context, string data)
    {
        _capture = capture; _context = context; _data = Path.Combine(data, "impedance");
        MinWidth = 800; MinHeight = 550;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)); Foreground = Brushes.WhiteSmoke;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SoncaAudioInspector;component/src/Themes/ImpulseTheme.xaml") });
        var panel = new DockPanel { Margin = new Thickness(15) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(DiagnosticViewStyle.Text("IMPEDANCE · SWEEP", 20));
        top.Children.Add(DiagnosticViewStyle.Text("Headphone L ──● CH1 ── Rsense không cảm ──● CH2 ── TẢI ── GND chung", 15, "#60A5FA"));
        var routes = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) }; top.Children.Add(routes);
        routes.Children.Add(DiagnosticViewStyle.Text("Phát ")); routes.Children.Add(_output); routes.Children.Add(DiagnosticViewStyle.Text("Thu stereo ")); routes.Children.Add(_input); routes.Children.Add(_swap);
        using (var devices = new MMDeviceEnumerator())
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (device)
            (device.DataFlow == DataFlow.Render ? _output : _input).Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
        _output.SelectedIndex = _input.SelectedIndex = -1;
        var guide = new StackPanel();
        foreach (string line in new[] {
            "1. Cần Rsense không cảm (thường 100 Ω) và thu đồng thời hai điểm điện áp. Tên thiết bị hoặc định dạng stereo chưa chứng minh hai kênh điện độc lập.",
            "2. Nối dây: headphone L vào CH1 và đầu Rsense; đầu kia Rsense vào CH2 và cực + tải. Cực − tải và mass cả hai kênh nối GND chung. Tách loa khỏi ampli.",
            "3. Chọn Phát / Thu stereo, nhập Rsense; giữ nguyên gain, âm lượng và dây. Chỉ dùng headphone mức nhỏ, không nối ngõ công suất hoặc phantom vào soundcard.",
            "4. Bấm ĐO để xem đường ước tính trước hiệu chuẩn. Muốn dùng kết quả ohm: tháo tải, bấm Chuẩn hở, nối lại tải rồi ĐO. Nên dùng sweep 10 giây trở lên.",
            "5. Chuẩn ngắn/R chuẩn là tùy chọn nâng cao để bù dây và sai số linh kiện. R INPUT là trở ngõ vào soundcard, R dây dùng khi chưa chuẩn ngắn.",
            "6. Muốn xác định điện trở nối tiếp: lưu mẫu củ chuẩn, rồi so với cùng củ + mạch cần kiểm tra. Một đường Z đơn lẻ không đủ kết luận có điện trở bảo vệ." }) guide.Children.Add(DiagnosticViewStyle.Text(line, 13));
        top.Children.Add(new Expander { Foreground = Brushes.WhiteSmoke, Header = "Hướng dẫn nối dây và đo — mở trước lần đầu", Content = guide, IsExpanded = false, Margin = new Thickness(0, 6, 0, 6) });
        var fields = new WrapPanel { Margin = new Thickness(0, 10, 0, 5) }; top.Children.Add(fields);
        foreach (var field in new[] { ("Rsense Ω", _sense), ("Từ Hz", _start), ("Đến Hz", _end), ("dBFS peak", _level), ("Giây", _seconds) })
        { fields.Children.Add(DiagnosticViewStyle.Text(field.Item1)); fields.Children.Add(field.Item2); }
        var buttons = new WrapPanel(); top.Children.Add(buttons);
        void Button(Panel target, string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(4), Foreground = Brushes.WhiteSmoke, Background = new SolidColorBrush(Color.FromRgb(35, 45, 50)) };
            button.Click += (_, _) => action(); target.Children.Add(button); _actions.Add(button);
        }
        Button(buttons, "Chuẩn hở", () => { if (!IsBusy) _pending = RunAsync("HỞ"); });
        Button(buttons, "ĐO", () => { if (!IsBusy) _pending = RunAsync("PHÁT / ĐO TẢI"); });
        Button(buttons, "DỪNG", Cancel); Button(buttons, "Lưu CSV", Export); buttons.Children.Add(_components);
        _components.Checked += (_,_) => { if (_last != null) Show(_last.Result); }; _components.Unchecked += (_,_) => { if (_last != null) Show(_last.Result); };
        var advanced = new StackPanel(); var extra = new WrapPanel(); advanced.Children.Add(extra);
        foreach (var field in new[] { ("R INPUT Ω", _inputOhms), ("R dây Ω", _leadOhms), ("R chuẩn Ω", _known) })
        { extra.Children.Add(DiagnosticViewStyle.Text(field.Item1)); extra.Children.Add(field.Item2); }
        var calibrationButtons = new WrapPanel(); advanced.Children.Add(calibrationButtons);
        advanced.Children.Add(DiagnosticViewStyle.Text("Chuẩn ngắn: chập hai dây phía tải, giữ Rsense. R chuẩn: thay tải bằng điện trở không cảm có giá trị đã biết.",12));
        foreach (string stage in new[] { "NGẮN", "R CHUẨN" }) Button(calibrationButtons, "Chuẩn " + stage.ToLowerInvariant(), () => { if (!IsBusy) _pending = RunAsync(stage); });
        Button(calibrationButtons, "Lưu mẫu củ chuẩn", SaveBaseline); Button(calibrationButtons, "So với mẫu củ chuẩn", Compare);
        top.Children.Add(new Expander { Header = "Hiệu chuẩn nâng cao / so mẫu", Content = advanced, Foreground = Brushes.WhiteSmoke });
        advanced.Children.Add(_comparison);
        top.Children.Add(_calStatus); top.Children.Add(_status); top.Children.Add(_message);
        DiagnosticViewStyle.Plot(_plot, "|Z| / R / X (Ω)"); _plot.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
        DiagnosticViewStyle.Plot(_phase, "Pha (độ)"); _phase.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
        foreach (var graph in new[] { _plot, _phase })
        { graph.Plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#18181B"); graph.Plot.Legend.FontColor = ScottPlot.Color.FromHex("#E4E4E7"); graph.Plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#52525B"); }
        _plot.Plot.Axes.Right.IsVisible = true; _plot.Plot.Axes.Right.Label.Text = "Pha (°)"; panel.Children.Add(_plot);
    }
    private ImpedanceSettings Settings()
    {
        double Number(TextBox box) => double.Parse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        var settings = new ImpedanceSettings(Number(_sense), Number(_known), Number(_start), Number(_end), Number(_level), Number(_seconds), Number(_inputOhms), Number(_leadOhms)); settings.Validate(); return settings;
    }
    private async Task RunAsync(string stage)
    {
        try
        {
            if (_output.SelectedItem is not ComboBoxItem output || _input.SelectedItem is not ComboBoxItem input)
                throw new InvalidOperationException("Chọn ngõ phát và thu stereo. Mạch đo cần Rsense, CH1 trước Rsense và CH2 tại tải, tải tách khỏi ampli.");
            var settings = Settings();
            string currentContext = _context((string)output.Tag, (string)input.Tag, _swap.IsChecked == true, settings);
            string calibrationPath = Path.Combine(_data, "calibration-" + currentContext + ".json");
            if (_calibration?.Context != currentContext) _calibration = File.Exists(calibrationPath)
                ? JsonSerializer.Deserialize<ImpedanceCalibration>(File.ReadAllText(calibrationPath)) : null;
            _calStatus.Text = _calibration == null ? "Chưa có calibration cùng cấu hình · ĐO sẽ vẽ chẩn đoán chưa hiệu chuẩn." : $"Calibration: Hở {_calibration.Open.Length} · Ngắn {_calibration.Short.Length} · R chuẩn {_calibration.Known.Length} điểm.";
            if (stage == "NGẮN" && _calibration?.Open.Length is not > 0 || stage == "R CHUẨN" && (_calibration?.Open.Length is not > 0 || _calibration.Short.Length == 0))
                throw new InvalidOperationException("Thực hiện đúng thứ tự chuẩn HỞ → NGẮN → R CHUẨN cùng fixture.");
            _cancellation = new(); foreach (var action in _actions) if ((string)action.Content != "DỪNG") action.IsEnabled = false;
            foreach (Control control in new Control[] { _output, _input, _swap, _sense, _known, _start, _end, _level, _seconds, _inputOhms, _leadOhms }) control.IsEnabled = false;
            _status.Text = "ĐANG THU · " + stage;
            _last = null;
            _comparison.Text = "";
            _plot.Plot.Clear(); _phase.Plot.Clear(); _plot.Refresh(); _phase.Refresh();
            // Reuse real calibration only when it matches. A first measurement is diagnostic, never a qualified ohm result.
            if (stage == "PHÁT / ĐO TẢI" && _calibration != null)
            {
                if (_calibration.Settings != settings) throw new InvalidOperationException("Thông số đã đổi so với calibration; cần chuẩn lại.");
                ImpedanceMeasurement.ValidateCalibration(_calibration, currentContext);
            }
            var capture = await _capture((string)output.Tag, (string)input.Tag, _swap.IsChecked == true, settings,
                stage == "PHÁT / ĐO TẢI" ? _calibration?.Context : null,
                _cancellation.Token);
            _cancellation.Token.ThrowIfCancellationRequested();
            if (capture.Context != currentContext) throw new InvalidOperationException("Cấu hình thu/phát đã đổi trong lúc đo; không dùng bản thu.");
            if (stage == "PHÁT / ĐO TẢI")
            {
                var result = _calibration == null ? ImpedanceMeasurement.EstimateUncalibrated(capture.Ratios, settings)
                    : ImpedanceMeasurement.Calculate(capture.Ratios, _calibration, capture.Context);
                _last = new(capture.Context, settings, result); Show(result); return;
            }
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
            foreach (Control control in new Control[] { _output, _input, _swap, _sense, _known, _start, _end, _level, _seconds, _inputOhms, _leadOhms }) control.IsEnabled = true;
        }
    }
    private void Show(ImpedanceResult result)
    {
        _status.Text = result.IsValid ? "ĐÃ ĐO TRỞ KHÁNG" : result.Uncalibrated ? "CHƯA HIỆU CHUẨN · CHẨN ĐOÁN" : result.OpenOrHighImpedance ? "TẢI HỞ / TRỞ KHÁNG CAO" : "CHƯA ĐO HỢP LỆ"; _status.Foreground = result.IsValid ? Brushes.MediumSpringGreen : Brushes.Gold;
        _message.Text = result.Message; _plot.Plot.Clear(); _phase.Plot.Clear();
        var points = result.IsValid ? result.Points : result.DiagnosticPoints;
        if (points.Length > 0)
        {
            var f = points.Select(p => Math.Log10(p.FrequencyHz)).ToArray();
            var ticks = new[] { 20.0,50,100,200,500,1000,2000,5000,10000,20000 }.Where(t => t >= points.Min(p => p.FrequencyHz) && t <= points.Max(p => p.FrequencyHz)).ToArray();
            foreach (var graph in new[] { _plot, _phase }) graph.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                ticks.Select(Math.Log10).ToArray(), ticks.Select(t => t >= 1000 ? $"{t/1000:0.#}k" : $"{t:0}").ToArray());
            var magnitude = _plot.Plot.Add.Scatter(f, points.Select(p => p.MagnitudeOhms).ToArray());
            magnitude.LegendText = result.IsValid ? "|Z| Ω" : result.Uncalibrated ? "|Z| Ω · chưa hiệu chuẩn" : "|Z| Ω · chẩn đoán tải hở/cao";
            magnitude.Color = ScottPlot.Colors.Magenta; magnitude.MarkerSize = 0; magnitude.LineWidth = 2;
            if (result.IsValid && _components.IsChecked == true)
            {
                _plot.Plot.Add.Scatter(f, points.Select(p => p.ResistanceOhms).ToArray()).LegendText = "R Ω";
                _plot.Plot.Add.Scatter(f, points.Select(p => p.ReactanceOhms).ToArray()).LegendText = "X Ω";
            }
            var phase = _plot.Plot.Add.Scatter(f, points.Select(p=>p.PhaseDegrees).ToArray()); phase.Axes.YAxis = _plot.Plot.Axes.Right;
            phase.Color = ScottPlot.Colors.Gold; phase.MarkerSize = 0; phase.LegendText = "Pha °"; phase.LinePattern = ScottPlot.LinePattern.Dashed;
            _plot.Plot.ShowLegend(); _plot.Plot.Axes.AutoScale();
            _plot.Plot.Axes.SetLimitsY(-180, 180, _plot.Plot.Axes.Right);
            _phase.Plot.Add.Scatter(f, points.Select(p => p.PhaseDegrees).ToArray()); _phase.Plot.Axes.AutoScale();
        }
        else if (result.OpenOrHighImpedance && !result.Uncalibrated) _message.Text += " Tỉ số trùng chuẩn hở: |Z| → ∞; không có giá trị hữu hạn để vẽ.";
        _plot.Refresh(); _phase.Refresh();
    }
    private void Export()
    {
        if (_last?.Result.IsValid != true) { _message.Text = "Chưa có bản đo hợp lệ để xuất."; return; }
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "impedance.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllLines(dialog.FileName, new[] { "Hz,Ohms,Phase_deg,R_ohms,X_ohms" }.Concat(_last.Result.Points.Select(p => FormattableString.Invariant($"{p.FrequencyHz},{p.MagnitudeOhms},{p.PhaseDegrees},{p.ResistanceOhms},{p.ReactanceOhms}"))));
    }
    private void SaveBaseline()
    {
        if (_last?.Result.IsValid != true) { _message.Text = "Đo củ/nhánh treble chuẩn trước khi lưu mẫu."; return; }
        var dialog = new SaveFileDialog { Filter = "Mẫu impedance|*.json", FileName = "treble-reference.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_last));
    }
    private void Compare()
    {
        if (_last?.Result.IsValid != true) { _message.Text = "Cần bản đo hợp lệ để đối chiếu."; return; }
        var dialog = new OpenFileDialog { Filter = "Mẫu impedance|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var baseline = JsonSerializer.Deserialize<ImpedanceSaved>(File.ReadAllText(dialog.FileName));
            if (baseline?.Context != _last.Context || baseline.Result.IsValid != true) throw new InvalidOperationException("Mẫu khác ngõ/gain/calibration/dải tần; chưa so được điện trở nối tiếp.");
            _comparison.Text = ImpedanceMeasurement.CompareSeriesResistance(_last.Result.Points, baseline.Result.Points);
        }
        catch (Exception ex) { _comparison.Text = ex.Message; }
    }
}

// Compatibility host for standalone diagnostic tools; the app embeds ImpedanceView in Ctrl+Shift+I.
public sealed class ImpedanceWindow : Window
{
    public ImpedanceView View { get; }
    public bool IsBusy => View.IsBusy;
    public void Cancel() => View.Cancel();
    public async Task StopForShutdownAsync() { await View.StopForShutdownAsync(); Close(); }
    public ImpedanceWindow(Func<string, string, bool, ImpedanceSettings, string?, CancellationToken, Task<ImpedanceCapture>> capture,
        Func<string, string, bool, ImpedanceSettings, string> context, string data)
    {
        Title = "SONCA · Impedance"; Width = 1200; Height = 840;
        Content = View = new(capture, context, data);
        Closing += async (_, e) => { if (!View.IsBusy) return; e.Cancel = true; await View.StopForShutdownAsync(); Close(); };
    }
}
