using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScottPlot.WPF;
using PlotColor = ScottPlot.Color;

namespace SoncaAudioInspector;

internal static class DiagnosticViewStyle
{
    public static TextBlock Text(string value, double size = 13, string color = "#D4D4D8") => new()
    { Text = value, FontSize = size, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    public static void Plot(WpfPlot plot, string yLabel)
    {
        plot.Plot.FigureBackground.Color = PlotColor.FromHex("#121214");
        plot.Plot.DataBackground.Color = PlotColor.FromHex("#101011");
        plot.Plot.Axes.Color(PlotColor.FromHex("#D4D4D8"));
        plot.Plot.Grid.MajorLineColor = PlotColor.FromHex("#34343B");
        plot.Plot.Axes.Left.Label.Text = yLabel;
    }
    public static DataGrid Table()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false,
            CanUserDeleteRows = false, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)), Foreground = Brushes.WhiteSmoke,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.AutoGeneratingColumn += (_, e) => e.Column.Header = e.PropertyName switch
        {
            "H" => "Bậc", "Output_Hz" => "Tần số hài (Hz)", "Level_dBc" => "Mức (dBc)",
            "Nen_uoc_tinh_dBc" => "Nền ước tính (dBc)", "Window_ms" => "Cửa sổ (ms)",
            "Cach_hai_ms" => "Khoảng cách hài (ms)", "Trang_thai" => "Trạng thái",
            "Time_ms" => "Thời gian (ms)", "Peak_dBFS" => "Đỉnh (dBFS)", "Pha_deg" => "Pha tương đối (°)", _ => e.PropertyName
        };
        return grid;
    }
    public static string Number(double value, string format = "0.0") => double.IsFinite(value) ? value.ToString(format) : "—";
}

public sealed class HighOrderHarmonicView : UserControl
{
    private readonly TextBlock _status = DiagnosticViewStyle.Text("Chưa có H10–H35. Đo log-sweep mới để tự phân tích cùng lượt thu.");
    private readonly ComboBox _frequency = new() { MinWidth = 130, Margin = new Thickness(8) };
    private readonly DataGrid _table = DiagnosticViewStyle.Table();
    private readonly WpfPlot _plot = new() { MinHeight = 150 };
    private readonly TextBlock _summary = DiagnosticViewStyle.Text("", 13, "#F59E0B");
    private HighOrderHarmonicResult? _result;
    private double[] _frequencies = Array.Empty<double>();

    public HighOrderHarmonicView()
    {
        var layout = new Grid { Margin = new Thickness(8) }; Content = layout;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(2, GridUnitType.Star), new GridLength(3, GridUnitType.Star), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        layout.Children.Add(_status);
        var toolbar = new WrapPanel(); toolbar.Children.Add(DiagnosticViewStyle.Text("F0 (Hz)")); toolbar.Children.Add(_frequency);
        toolbar.Children.Add(_summary);
        Grid.SetRow(toolbar, 1); layout.Children.Add(toolbar);
        Grid.SetRow(_plot, 2); layout.Children.Add(_plot); Grid.SetRow(_table, 3); layout.Children.Add(_table);
        var note = DiagnosticViewStyle.Text("H10–H35 / Rub & Buzz · phân tích tự động từ cùng bản thu, không phát thêm tín hiệu.", 12, "#A1A1AA");
        Grid.SetRow(note, 4); layout.Children.Add(note);
        DiagnosticViewStyle.Plot(_plot, "Mức hài (dBc)"); _plot.Plot.Axes.Bottom.Label.Text = "Bậc hài";
        _table.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyName is "Level_dBc" or "Nen_uoc_tinh_dBc" && _result?.ReferenceAtHarmonicFrequency == true)
                e.Column.Header = e.PropertyName == "Level_dBc" ? "Mức / H1 tại n×F0 (dB)" : "Nền / H1 tại n×F0 (dB)";
        };
        _plot.Plot.Axes.SetLimits(9, 36, -140, 0);
        _frequency.SelectionChanged += (_, _) => Draw();
    }

    public void Update(HighOrderHarmonicResult? result)
    {
        if (ReferenceEquals(_result, result)) return;
        _result = result;
        _frequencies = result?.Points.Select(p => p.FundamentalHz).Distinct().Order().ToArray() ?? Array.Empty<double>();
        _frequency.ItemsSource = _frequencies.Select(f => f.ToString("0.##", CultureInfo.InvariantCulture)).ToArray();
        _frequency.SelectedIndex = _frequencies.Length == 0 ? -1 : Array.FindIndex(_frequencies, f => f >= 100);
        _status.Text = result == null ? "Chưa có H10–H35. Đo log-sweep mới để tự phân tích cùng lượt thu."
            : result.Points.Count == 0 ? result.Note
            : $"ESS {result.SweepSeconds:0.000} s · băng thông hài {result.OutputBandwidthHz:0} Hz · đủ dải H10–H35 đến fundamental {result.FullOrderMaximumFundamentalHz:0.#} Hz. " + result.Note;
        Draw();
    }

    private void Draw()
    {
        _plot.Plot.Clear(); _table.ItemsSource = null; _summary.Text = "";
        if (_result != null && _frequency.SelectedIndex >= 0)
        {
            var points = _result.Points.Where(p => p.FundamentalHz == _frequencies[_frequency.SelectedIndex]).OrderBy(p => p.Order).ToArray();
            var measured = points.Where(p => p.IsUsable).ToArray();
            double sum = Math.Sqrt(measured.Sum(p => Math.Pow(10, p.LevelDbc / 10))) * 100;
            _summary.Text = measured.Length == 0 ? "Chưa có hài đủ độ nổi để tính tổng; xem nền và trạng thái từng bậc."
                : _result.ReferenceAtHarmonicFrequency ? $"{measured.Length}/26 hài nổi trên nền · tham chiếu H1 tại n×F0"
                : $"{measured.Length}/26 hài nổi trên nền · tổng phần đo được {sum:0.0000}% (không gồm hài dưới nền/ngoài băng)";
            _plot.Plot.Axes.Left.Label.Text = _result.ReferenceAtHarmonicFrequency ? "Mức / H1 tại n×F0 (dB)" : "Mức hài (dBc theo F0)";
            _table.ItemsSource = points.Select(p => new { H = "H" + p.Order, Output_Hz = DiagnosticViewStyle.Number(p.OutputHz),
                Level_dBc = DiagnosticViewStyle.Number(p.LevelDbc, "0.00"), Nen_uoc_tinh_dBc = DiagnosticViewStyle.Number(p.NoiseLimitDbc),
                Window_ms = DiagnosticViewStyle.Number(p.WindowMs, "0.000"), Cach_hai_ms = DiagnosticViewStyle.Number(p.SeparationMs, "0.000"), Trang_thai = p.Status }).ToArray();
            var trace = _plot.Plot.Add.Scatter(points.Select(p => (double)p.Order).ToArray(), points.Select(p => p.LevelDbc).ToArray());
            trace.Color = PlotColor.FromHex("#F59E0B"); trace.MarkerSize = 5; trace.LineWidth = 1.5f;
        }
        _plot.Plot.Axes.SetLimits(9, 36, -140, 0); _plot.Refresh();
    }
}

public sealed class MechanicalDiagnosticView : UserControl
{
    private readonly MechanicalTestKind _kind;
    private readonly Func<MechanicalTestRequest, Task<MechanicalTestResult>> _run;
    private readonly Action _cancel;
    private readonly TextBox _frequency = new() { Text = "40", Width = 75, Margin = new Thickness(6), Padding = new Thickness(7, 4, 7, 4),
        MinHeight = 29, VerticalContentAlignment = VerticalAlignment.Center, Foreground = Brushes.WhiteSmoke,
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 31)), BorderBrush = new SolidColorBrush(Color.FromRgb(69, 69, 77)) };
    private readonly ComboBox _level = new() { Width = 105, Margin = new Thickness(6) };
    private readonly ComboBox _duration = new() { Width = 85, Margin = new Thickness(6) };
    private readonly ComboBox _margin = new() { Width = 80, Margin = new Thickness(6) };
    private readonly Button _play = new() { Content = "▶ PHÁT / ĐO", Margin = new Thickness(12, 4, 4, 4), Background = new SolidColorBrush(Color.FromRgb(6, 95, 70)) };
    private readonly Button _stop = new() { Content = "■ DỪNG", Margin = new Thickness(4), IsEnabled = false, Foreground = Brushes.OrangeRed };
    private readonly TextBlock _status = DiagnosticViewStyle.Text("Chưa đo", 18, "#A1A1AA");
    private readonly TextBlock _message = DiagnosticViewStyle.Text("Chọn thông số và bấm Phát / Đo. Chuyển tab không tự phát âm thanh.");
    private readonly TextBlock _metrics = DiagnosticViewStyle.Text("", 13, "#60A5FA");
    private readonly TextBlock _acquisition = DiagnosticViewStyle.Text("Dùng ngõ phát/thu và kênh đang chọn trong Audio Routing.", 12, "#A1A1AA");
    private readonly WpfPlot _plot = new() { MinHeight = 170 };
    private readonly DataGrid _events = DiagnosticViewStyle.Table();
    private readonly string _instructions;

    public MechanicalDiagnosticView(MechanicalTestKind kind, Func<MechanicalTestRequest, Task<MechanicalTestResult>> run, Action cancel)
    {
        _kind = kind; _run = run; _cancel = cancel;
        foreach (double level in new[] { -36.0, -30, -24, -18, -12, -9, -6, -3 }) _level.Items.Add(level);
        _level.SelectedItem = -18.0;
        foreach (double duration in new[] { 1.0, 1.5, 2, 3, 5 }) _duration.Items.Add(duration);
        _duration.SelectedItem = kind == MechanicalTestKind.LooseParticle ? 1.5 : 2.0;
        foreach (double margin in new[] { 6.0, 9, 12, 18, 24, 30 }) _margin.Items.Add(margin);
        _margin.SelectedItem = 12.0;
        var layout = new Grid { Margin = new Thickness(8) }; Content = layout;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto,
            new GridLength(3, GridUnitType.Star), new GridLength(kind == MechanicalTestKind.LooseParticle ? 170 : 0), GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var toolbar = new WrapPanel();
        void Control(string name, UIElement control) { toolbar.Children.Add(DiagnosticViewStyle.Text(name)); toolbar.Children.Add(control); }
        Control("Hz", _frequency); Control("dBFS peak", _level); Control("Giây", _duration);
        if (kind == MechanicalTestKind.LooseParticle) Control("Ngưỡng +dB", _margin);
        toolbar.Children.Add(_play); toolbar.Children.Add(_stop); layout.Children.Add(toolbar);
        Grid.SetRow(_status, 1); layout.Children.Add(_status); Grid.SetRow(_message, 2); layout.Children.Add(_message);
        Grid.SetRow(_metrics, 3); layout.Children.Add(_metrics); Grid.SetRow(_plot, 4); layout.Children.Add(_plot);
        Grid.SetRow(_events, 5); layout.Children.Add(_events); Grid.SetRow(_acquisition, 6); layout.Children.Add(_acquisition);
        DiagnosticViewStyle.Plot(_plot, "Đường bao cao tần (dBFS)"); _plot.Plot.Axes.Bottom.Label.Text = "Thời gian từ đầu đoạn ổn định (s)";
        _plot.Plot.Axes.SetLimits(0, 1.5, -140, 0);
        _play.Click += async (_, _) => await RunAsync(); _stop.Click += (_, _) => _cancel();
        _play.ToolTip = "Thu nền, phát tone có fade, phân tích bản thu mới. Mức dBFS không phải Xmax của màng loa.";
        _acquisition.Text += kind == MechanicalTestKind.LooseParticle
            ? " HPF >2 kHz và kiểm tra pha xung theo tone. Giữ yên để tránh nhầm va chạm ngoài loa."
            : " Hilbert 3–8 kHz sau loại hài đồng bộ. Đặt mic gần vị trí nghi rò; tiếng cổng hơi cũng có thể gây điều biên.";
        _acquisition.Text += " dBFS không xác định Xmax; bắt đầu thấp, tăng theo giới hạn loa/fixture.";
        _instructions = _acquisition.Text;
    }

    public void SetBusy(bool busy)
    {
        _play.IsEnabled = !busy; _stop.IsEnabled = busy;
        _frequency.IsEnabled = _level.IsEnabled = _duration.IsEnabled = _margin.IsEnabled = !busy;
    }

    private async Task RunAsync()
    {
        try
        {
            if (!double.TryParse(_frequency.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                throw new ArgumentException("Nhập tần số 20–200 Hz.");
            var request = new MechanicalTestRequest(_kind, f, (double)_level.SelectedItem, (double)_duration.SelectedItem, (double)_margin.SelectedItem);
            request.Validate();
            _status.Text = "ĐANG ĐO…"; _status.Foreground = Brushes.DeepSkyBlue; _message.Text = "Thu nền → phát/thu tone → phân tích. Có thể bấm Dừng bất kỳ lúc nào.";
            _metrics.Text = ""; _events.ItemsSource = null; _plot.Plot.Clear(); _plot.Refresh();
            Show(await _run(request));
        }
        catch (OperationCanceledException) { _status.Text = "ĐÃ DỪNG"; _message.Text = "Lượt thu bị hủy; không dùng dữ liệu đang đo."; }
        catch (Exception ex) { _status.Text = "CHƯA ĐO ĐƯỢC"; _message.Text = ex.Message; }
    }

    public void Show(MechanicalTestResult result)
    {
        _status.Text = result.Status; _status.Foreground = result.IsValid ? Brushes.Orange : Brushes.OrangeRed;
        _message.Text = result.Message;
        if (!string.IsNullOrEmpty(result.Acquisition)) _acquisition.Text = $"{result.CapturedAt:HH:mm:ss} · {result.Acquisition}\n" + _instructions;
        if (!result.IsValid)
        {
            _metrics.Text = "Chưa có số đo hợp lệ."; _events.ItemsSource = null;
            _plot.Plot.Clear(); _plot.Refresh(); return;
        }
        _metrics.Text = $"f0 {DiagnosticViewStyle.Number(result.FundamentalHz, "0.000")} Hz · mức {DiagnosticViewStyle.Number(result.SignalDbFs)} dBFS · nền {DiagnosticViewStyle.Number(result.NoiseDbFs)} dBFS · cao tần {DiagnosticViewStyle.Number(result.HighBandDbFs)} dBFS"
            + (result.IsValid ? $" · phân tích {result.AnalysisSeconds:0.00} s" : "")
            + (_kind == MechanicalTestKind.LooseParticle ? $" · {result.Events.Count} xung · ngưỡng {DiagnosticViewStyle.Number(result.ThresholdDbFs)} dBFS"
                : $" · điều biên {DiagnosticViewStyle.Number(result.ModulationPercent)}% · độ nổi {DiagnosticViewStyle.Number(result.ModulationProminenceDb)} dB");
        _events.ItemsSource = result.Events.Select(e => new { Time_ms = DiagnosticViewStyle.Number(e.TimeMs, "0.00"), Peak_dBFS = DiagnosticViewStyle.Number(e.PeakDbFs), Pha_deg = DiagnosticViewStyle.Number(e.PhaseDegrees) }).ToArray();
        _plot.Plot.Clear();
        if (result.Envelope.Count > 0)
        {
            var trace = _plot.Plot.Add.Scatter(result.Envelope.Select(p => p.TimeSeconds).ToArray(), result.Envelope.Select(p => p.Value).ToArray());
            trace.Color = PlotColor.FromHex(_kind == MechanicalTestKind.LooseParticle ? "#F59E0B" : "#22D3EE"); trace.MarkerSize = 0;
            if (_kind == MechanicalTestKind.LooseParticle && double.IsFinite(result.ThresholdDbFs))
            { var threshold = _plot.Plot.Add.HorizontalLine(result.ThresholdDbFs); threshold.Color = PlotColor.FromHex("#EF4444"); threshold.Text = "Ngưỡng xung"; }
            _plot.Plot.Axes.SetLimits(0, result.Envelope[^1].TimeSeconds, -140, Math.Min(0, result.Envelope.Max(p => p.Value) + 10));
        }
        _plot.Refresh();
    }
}
