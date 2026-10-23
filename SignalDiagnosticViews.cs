using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScottPlot.WPF;
using PlotColor = ScottPlot.Color;

namespace SoncaAudioInspector;

public sealed class PolarityDiagnosticView : UserControl
{
    private readonly TextBlock _status = DiagnosticViewStyle.Text("CHƯA ĐO", 20);
    private readonly TextBlock _values = DiagnosticViewStyle.Text("", 16, "#60A5FA");
    private readonly TextBlock _message = DiagnosticViewStyle.Text("Đo log sweep để phân tích cực tính từ cùng IR.");
    public PolarityDiagnosticView()
    {
        var stack = new StackPanel { Margin = new Thickness(18) }; Content = stack;
        stack.Children.Add(DiagnosticViewStyle.Text("CỰC TÍNH · CÙNG LOG SWEEP", 18));
        stack.Children.Add(_status); stack.Children.Add(_values); stack.Children.Add(_message);
        stack.Children.Add(DiagnosticViewStyle.Text("Không phát thêm tín hiệu. So với Line chuẩn cùng ngõ/kênh/gain/calibration; phân tần có thể chủ động đảo cực theo thiết kế. Dấu IR đơn lẻ chưa xác định dây đấu sai.", 13, "#A1A1AA"));
    }
    public void Update(PolarityDiagnosticResult? result)
    {
        _status.Text = result?.Status ?? "CHƯA ĐO";
        _status.Foreground = result?.IsValid == true ? result.Measured == result.Reference ? Brushes.MediumSpringGreen : Brushes.OrangeRed : Brushes.Gold;
        _values.Text = result == null ? "" : $"IR lượt đo: {result.Measured} · Line chuẩn: {result.Reference}";
        _message.Text = result?.Message ?? "Đo log sweep để phân tích cực tính từ cùng IR.";
    }
}

public sealed class SignalDiagnosticView : UserControl
{
    private readonly SignalDiagnosticKind _kind;
    private readonly Func<SignalDiagnosticRequest, Task<SignalDiagnosticResult>> _run;
    private readonly Action _cancel;
    private readonly TextBox _frequency = Input("1000"), _low = Input("60"), _high = Input("7000"), _increase = Input("6"), _limit = Input("1");
    private readonly ComboBox _level = new() { Width = 90, Margin = new Thickness(5) };
    private readonly ComboBox _duration = new() { Width = 75, Margin = new Thickness(5) };
    private readonly Button _play = new() { Content = "▶ PHÁT / ĐO", Margin = new Thickness(12, 4, 4, 4), Background = new SolidColorBrush(Color.FromRgb(6, 95, 70)) };
    private readonly Button _stop = new() { Content = "■ DỪNG", Margin = new Thickness(4), IsEnabled = false, Foreground = Brushes.OrangeRed };
    private readonly TextBlock _status = DiagnosticViewStyle.Text("CHƯA ĐO", 20);
    private readonly TextBlock _value = DiagnosticViewStyle.Text("", 19, "#60A5FA");
    private readonly TextBlock _message = DiagnosticViewStyle.Text("Bấm Phát / Đo để thu mới. Chuyển tab không tự phát âm thanh.");
    private readonly TextBlock _details = DiagnosticViewStyle.Text("");
    private readonly TextBlock _acquisition = DiagnosticViewStyle.Text("Dùng ngõ/kênh phát và thu đang chọn trong cấu hình.", 12, "#A1A1AA");
    private readonly WpfPlot _plot = new() { MinHeight = 200 };
    private static TextBox Input(string value) => new() { Text = value, Width = 70, Margin = new Thickness(5), Padding = new Thickness(7, 4, 7, 4),
        Foreground = Brushes.WhiteSmoke, Background = new SolidColorBrush(Color.FromRgb(28, 28, 31)), BorderBrush = Brushes.DimGray };

    public SignalDiagnosticView(SignalDiagnosticKind kind, Func<SignalDiagnosticRequest, Task<SignalDiagnosticResult>> run, Action cancel)
    {
        _kind = kind; _run = run; _cancel = cancel;
        foreach (double level in new[] { -48.0, -42, -36, -30, -24, -18, -12, -9, -6, -3 }) _level.Items.Add(level);
        _level.SelectedItem = -24.0;
        foreach (double duration in new[] { 1.0, 1.5, 2, 3, 5 }) _duration.Items.Add(duration);
        _duration.SelectedItem = 2.0;
        var grid = new Grid { Margin = new Thickness(10) }; Content = grid;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto,
            GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new() { Height = height });
        var toolbar = new WrapPanel(); grid.Children.Add(toolbar);
        void Field(string label, UIElement control) { toolbar.Children.Add(DiagnosticViewStyle.Text(label)); toolbar.Children.Add(control); }
        if (kind == SignalDiagnosticKind.Imd) { Field("Bass Hz", _low); Field("Cao Hz", _high); }
        else { Field("Tone Hz", _frequency); Field("Tăng dB", _increase); }
        Field("dBFS peak", _level); Field("Giây / mức", _duration);
        Field(kind == SignalDiagnosticKind.Imd ? "Ngưỡng %" : "Ngưỡng dB", _limit);
        toolbar.Children.Add(_play); toolbar.Children.Add(_stop);
        void Row(UIElement element, int row) { Grid.SetRow(element, row); grid.Children.Add(element); }
        Row(_status, 1); Row(_value, 2); Row(_message, 3); Row(_details, 4); Row(_plot, 5); Row(_acquisition, 6);
        DiagnosticViewStyle.Plot(_plot, kind == SignalDiagnosticKind.Imd ? "Sideband / tone cao (dBc)" : "Mức cơ bản thu (dBFS)");
        _plot.Plot.Axes.Bottom.Label.Text = kind == SignalDiagnosticKind.Imd ? "Tần số (Hz)" : "Mức phát (dBFS peak)";
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { TargetTickCount = 8 };
        _plot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { TargetTickCount = 8 };
        _plot.Plot.Legend.BackgroundColor = PlotColor.FromHex("#18181B");
        _plot.Plot.Legend.FontColor = PlotColor.FromHex("#E4E4E7");
        _plot.Plot.Legend.OutlineColor = PlotColor.FromHex("#52525B");
        _play.Click += async (_, _) => await RunAsync(); _stop.Click += (_, _) => _cancel();
        _play.ToolTip = kind == SignalDiagnosticKind.Imd ? "Thu nền rồi phát đồng thời hai tone tỉ lệ biên độ 4:1. Không lấy IMD từ log sweep."
            : "Thu nền → đo mức thấp → đo mức cao. Giới hạn mức cao −3 dBFS peak; chọn mức phù hợp loa.";
        _acquisition.Text += kind == SignalDiagnosticKind.Imd ? " Phát riêng hai tone; tỉ số tại mic, không phải SPL."
            : " Hai lượt tone riêng; đo compression ngắn, chưa đánh giá nhiệt/SPL tối đa. Bắt đầu thấp theo giới hạn loa/fixture.";
    }

    public void SetBusy(bool busy)
    {
        _play.IsEnabled = !busy; _stop.IsEnabled = busy;
        foreach (Control control in new Control[] { _frequency, _low, _high, _increase, _limit, _level, _duration }) control.IsEnabled = !busy;
    }
    private static double Number(TextBox box) => double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value : throw new ArgumentException("Nhập tần số, bước tăng và ngưỡng bằng số hợp lệ.");
    private async Task RunAsync()
    {
        _plot.Plot.Clear(); _plot.Refresh(); _value.Text = ""; _details.Text = "";
        try
        {
            var request = new SignalDiagnosticRequest(_kind, Number(_frequency), Number(_low), Number(_high),
                (double)_level.SelectedItem, Number(_increase), (double)_duration.SelectedItem, Number(_limit));
            request.Validate();
            _status.Text = "ĐANG ĐO…"; _status.Foreground = Brushes.DeepSkyBlue;
            _message.Text = _kind == SignalDiagnosticKind.Imd ? "Thu nền → phát/thu hai tone → phân tích." : "Thu nền → phát/thu mức thấp → phát/thu mức cao → phân tích.";
            Show(await _run(request));
        }
        catch (OperationCanceledException) { _status.Text = "ĐÃ DỪNG"; _message.Text = "Lượt đo đã hủy; giữ nguyên sweep/line trước đó."; }
        catch (Exception ex) { _status.Text = "CHƯA ĐO ĐƯỢC"; _status.Foreground = Brushes.OrangeRed; _message.Text = ex.Message; }
    }
    public void Show(SignalDiagnosticResult result)
    {
        _status.Text = result.Status; _status.Foreground = result.IsValid ? Brushes.LightSkyBlue : Brushes.OrangeRed;
        _message.Text = result.Message; _details.Text = result.Details;
        _value.Text = result.IsValid ? $"{result.Value:0.000} {result.Unit}" : "Chưa có số đo hợp lệ.";
        if (result.Acquisition.Length > 0) _acquisition.Text = result.Acquisition;
        _plot.Plot.Clear();
        if (result.IsValid && result.Points.Count > 0)
        {
            var trace = _plot.Plot.Add.Scatter(result.Points.Select(p => p.X).ToArray(), result.Points.Select(p => p.Y).ToArray());
            trace.Color = PlotColor.FromHex("#60A5FA"); trace.MarkerSize = 6; trace.LineWidth = _kind == SignalDiagnosticKind.Imd ? 0 : 2;
            trace.LegendText = "Đã đo";
            if (_kind == SignalDiagnosticKind.Compression && result.Points.Count == 2)
            {
                var a = result.Points[0]; var b = result.Points[1];
                var ideal = _plot.Plot.Add.Scatter(new[] { a.X, b.X }, new[] { a.Y, a.Y + b.X - a.X });
                ideal.Color = PlotColor.FromHex("#A1A1AA"); ideal.LineWidth = 1; ideal.MarkerSize = 0;
                ideal.LegendText = "Tăng tuyến tính từ mức thấp";
            }
            _plot.Plot.Legend.IsVisible = _kind == SignalDiagnosticKind.Compression;
            _plot.Plot.Axes.AutoScale();
        }
        _plot.Refresh();
    }
}
