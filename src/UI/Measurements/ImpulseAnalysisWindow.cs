using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using PlotColor = ScottPlot.Color;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Runtime.CompilerServices;
using ScottPlot.DataSources;

namespace SoncaAudioInspector;

public sealed class ImpulseAnalysisWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    public ImpulseAnalysisWindow(StandardAcousticResult result)
    {
        Title = "Impulse / ETC / Schroeder — cùng lượt sweep";
        Width = 1400; Height = 850; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;
        Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
        Foreground = Brushes.WhiteSmoke;
        UseDarkTitleBar(this);
        Content = new ImpulseAnalysisView(result);
    }

    internal static void UseDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            int enabled = 1;
            var handle = new WindowInteropHelper(window).Handle;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) < 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        };
    }
}

public sealed class ImpulseAnalysisView : UserControl
{
    private static readonly ConditionalWeakTable<StandardAcousticResult, PreparedData> Prepared = new();
    private sealed class PreparedData
    {
        public readonly bool HasSamples;
        public readonly ImpulseTraceData Raw, Ir, Etc;
        public readonly Lazy<ImpulseTraceData> Windowed;
        public readonly (double Start, double End) InitialRange;
        public PreparedData(StandardAcousticResult result)
        {
            var data = result.ImpulsePlot;
            HasSamples = data?.Samples.Length > 0 && data.SampleRate > 0;
            static ImpulseTraceData Arrays(IReadOnlyList<TimeDomainPoint> points) =>
                new(points.Select(p => p.TimeSeconds).ToArray(), points.Select(p => p.Value).ToArray());
            if (HasSamples)
            {
                double[] times = new double[data!.Samples.Length];
                for (int i = 0; i < times.Length; i++) times[i] = (i - data.DirectIndex) / (double)data.SampleRate;
                Raw = new(times, data.Samples);
                Ir = ImpulsePlotAnalysis.PrepareLogMagnitude(data.Samples, data.SampleRate, data.DirectIndex);
                Etc = new(times, data.GetFullEnvelopeDb());
                double leftMs = Math.Min(Math.Max(0, -times[0]), Math.Max(0, result.ImpulseWindowLeftMs / 1000)) * 1000;
                double rightMs = Math.Min(Math.Max(.1, times[^1]),
                    Math.Max(1.0 / Math.Max(1, result.SampleRate), result.ImpulseWindowRightMs / 1000)) * 1000;
                Windowed = new(() => ImpulsePlotAnalysis.PrepareLogMagnitude(ImpulsePlotAnalysis.ApplyWindow(
                    data.Samples, data.SampleRate, data.DirectIndex, leftMs,
                    rightMs, result.ImpulseWindowKind), data.SampleRate, data.DirectIndex));
            }
            else
            {
                var points = data?.Impulse ?? result.ImpulseResponse;
                Raw = Arrays(points);
                Ir = Arrays(ImpulsePlotAnalysis.LogMagnitudeTrace(points, includeZeroCrossings: false));
                Etc = Arrays(data?.Envelope ?? result.EnergyTimeCurve);
                Windowed = new(() => new(Array.Empty<double>(), Array.Empty<double>()));
            }
            InitialRange = ImpulsePlotAnalysis.InitialTimeRange(Raw,
                Math.Max(0, result.ImpulseWindowLeftMs / 1000),
                Math.Max(.001, result.ImpulseWindowRightMs / 1000));
        }
    }
    private readonly PreparedData _prepared;
    private bool _windowedReady;
    private readonly StandardAcousticResult _result;
    public void SaveGraph(string path) => _plot.Plot.SavePng(path, 1600, 900);
    private readonly WpfPlot _plot = new(), _navigator = new();
    private readonly CheckBox _showIr = Toggle("Impulse", "#FF302B"), _showEtc = Toggle("Envelope (ETC)", "#EE00E9"),
        _showDecay = Toggle("Schroeder Integral", "#F4F4F5"), _showMetrics = Toggle("Thông số", "#D4D4D8"),
        _showRegression = Toggle("Hồi quy", "#FBBF24"), _showWindow = Toggle("Window", "#5798EF"),
        _showWindowed = Toggle("IR × Window", "#FFA07A");
    private readonly ComboBox _regressionChoice = new() { Width = 86, Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _readout = new() { Foreground = Brushes.LightSkyBlue, Margin = new Thickness(12, 5, 6, 5),
        Width = 350, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _metrics;
    private readonly SignalXY _ir, _etc, _windowed;
    private readonly Scatter _decay, _window;
    private readonly System.Windows.Shapes.Line _cursor = new() { Stroke = Brushes.LightSkyBlue, StrokeThickness = 1,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly VerticalLine _leftEdge, _rightEdge;
    private readonly HorizontalSpan _selection;
    private readonly Dictionary<string, Scatter> _regressions = new();
    private readonly double _start, _end;
    private readonly double _dataStart, _dataEnd;
    private readonly double _leftWindow, _rightWindow;
    private bool _draggingNavigator;
    private double _navLeft = double.NaN, _navRight = double.NaN;
    private long _lastCursorUpdate;

    public ImpulseAnalysisView(StandardAcousticResult result)
    {
        _result = result;
        Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ImpulseTheme.xaml", UriKind.Relative) });
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
        _prepared = Prepared.GetValue(result, static value => new PreparedData(value));
        bool hasSamples = _prepared.HasSamples;
        _dataStart = Math.Min(0, _prepared.Raw.Times.FirstOrDefault());
        _dataEnd = Math.Max(.1, _prepared.Raw.Times.LastOrDefault());
        (_start, _end) = _prepared.InitialRange;
        _leftWindow = Math.Min(-_dataStart, Math.Max(0, result.ImpulseWindowLeftMs / 1000));
        _rightWindow = Math.Min(_dataEnd, Math.Max(1.0 / Math.Max(1, result.SampleRate), result.ImpulseWindowRightMs / 1000));
        _showDecay.IsChecked = false; _showMetrics.IsChecked = false;
        _showRegression.IsChecked = false; _showWindowed.IsChecked = false;
        var layout = new Grid { Margin = new Thickness(6) }; Content = layout;
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(82), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var toolbar = new WrapPanel { Margin = new Thickness(4) };
        toolbar.Children.Add(new TextBlock { Text = "IMPULSE", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 5, 16, 5) });
        toolbar.Children.Add(_showMetrics); toolbar.Children.Add(_showRegression); toolbar.Children.Add(_regressionChoice);
        var reset = new Button { Content = "Vùng tín hiệu", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 4, 0),
            ToolTip = "Về vùng xung và cửa sổ đo. Toàn bộ IR cho phép xem cả nền và đuôi yếu ngoài vùng này." };
        reset.Click += (_, _) => ResetView(); toolbar.Children.Add(reset);
        var full = new Button { Content = "Toàn bộ IR", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 4, 0) };
        full.Click += (_, _) => ShowFullView(); toolbar.Children.Add(full);
        toolbar.Children.Add(new TextBlock { Text = "Cuộn để zoom · kéo để dịch · chọn vùng ở thanh dưới", Foreground = Brushes.Silver, Margin = new Thickness(10, 5, 0, 5) });
        layout.Children.Add(toolbar);
        var graph = new Grid(); Grid.SetRow(graph, 1); layout.Children.Add(graph); graph.Children.Add(_plot); graph.Children.Add(_cursor);
        ApplyPlotStyle(_plot); ApplyPlotStyle(_navigator);
        // Apply after every wheel zoom/pan as well as the initial reset. The
        // displayed dynamic range stops at the trace's -180 dBr numerical floor.
        _plot.Plot.Axes.Rules.Add(new ScottPlot.AxisRules.MaximumBoundary(
            _plot.Plot.Axes.Bottom, _plot.Plot.Axes.Left, new ScottPlot.AxisLimits(_dataStart, _dataEnd, -180, 20)));
        _ir = SignalTrace(_plot, _prepared.Ir, "#FF302B", "Impulse");
        _etc = SignalTrace(_plot, _prepared.Etc, "#EE00E9", "Envelope (ETC)");
        _ir.LineWidth = 2f; _etc.LineWidth = 1.6f;
        _decay = Trace(_plot, result.EnergyDecayCurve.Select(p => new TimeDomainPoint(p.TimeSeconds, p.LevelDb)).ToArray(),
            p => p.Value, "#F4F4F5", "Schroeder Integral");
        _decay.LineWidth = 2; _decay.IsVisible = false;
        _window = Trace(_plot, WindowPoints(), p => p.Value, "#5798EF", "Window"); _window.LineWidth = 2;
        // Compute this optional full-resolution gate only when requested.
        _windowed = SignalTrace(_plot, new(Array.Empty<double>(), Array.Empty<double>()), "#FFA07A", "IR × Window", () => _prepared.Windowed.Value);
        _windowed.LineWidth = 1.6f;
        _windowed.IsVisible = false;
        _showWindowed.IsEnabled = hasSamples;
        if (!hasSamples) _showWindowed.ToolTip = "Kết quả cũ chỉ lưu điểm rút gọn; đo lại để xem Windowed chính xác.";
        var origin = Marker(0, "Ref", "#5798EF");
        _leftEdge = Marker(-_leftWindow, "L", "#EF3340");
        _rightEdge = Marker(_rightWindow, "R", "#46BB43");
        _plot.Plot.Axes.Left.Label.Text = "dBr theo đỉnh IR";
        _plot.Plot.Axes.Bottom.Label.Text = "Thời gian từ direct (ms)";
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
            { TargetTickCount = 14, LabelFormatter = seconds => (seconds * 1000).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) };
        _plot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericFixedInterval(10);
        _plot.ToolTip = "IR/ETC theo đỉnh IR. Window dùng cùng hệ số với phép tính đáp tuyến; FDW nếu bật sẽ rút ngắn theo tần số. L/Ref/R không chỉnh bài đo.";
        _metrics = BuildMetrics(); _metrics.Visibility = Visibility.Collapsed; graph.Children.Add(_metrics);
        _plot.SizeChanged += (_, _) => { _cursor.Visibility = Visibility.Collapsed; PositionMetrics(); };
        Grid.SetRow(_navigator, 2); layout.Children.Add(_navigator);
        SignalTrace(_navigator, _prepared.Ir, "#FF302B", "").LineWidth = 1.3f;
        Trace(_navigator, WindowPoints(), p => p.Value, "#5798EF", "").LineWidth = 1.5f;
        _selection = _navigator.Plot.Add.HorizontalSpan(_start, _end);
        _selection.FillColor = PlotColor.FromHex("#60A5FA").WithAlpha(.12);
        _selection.LineColor = PlotColor.FromHex("#60A5FA"); _selection.LineWidth = 1;
        _navigator.Plot.Axes.SetLimits(_start, _end, -180, 20);
        _navigator.Plot.Axes.Left.IsVisible = false; _navigator.Plot.Axes.Bottom.IsVisible = false;
        _navigator.ToolTip = "Kéo tại đây để di chuyển vùng đang xem trên đồ thị chính.";
        var legend = new WrapPanel { Margin = new Thickness(55, 2, 0, 0) };
        legend.Children.Add(_showIr); legend.Children.Add(_showEtc); legend.Children.Add(_showWindow);
        legend.Children.Add(_showWindowed); legend.Children.Add(_showDecay); legend.Children.Add(_readout);
        Grid.SetRow(legend, 3); layout.Children.Add(legend);
        var note = new TextBlock { Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 4),
            Text = $"L −{_leftWindow * 1000:0.###} ms · Ref 0 ms · R {_rightWindow * 1000:0.###} ms · "
                + (result.ImpulseWindowKind == ImpulseWindowKind.Tukey25 ? "Tukey 0,25 · " : "Half-Hann (cũ) · ")
                + (result.FrequencyDependentWindowing ? $"FDW {result.FrequencyDependentWindowCycles:0.#} chu kỳ; Window hiển thị giới hạn IR tối đa. " : "FDW tắt · ")
                + (result.ImpulseEffects?.Describe() ?? result.Validity + " · " + result.Warning) };
        Grid.SetRow(note, 4); layout.Children.Add(note);
        BuildRegressions();
        _showIr.Click += (_, _) => { _ir.IsVisible = _showIr.IsChecked == true; _plot.Refresh(); };
        _showEtc.Click += (_, _) => { _etc.IsVisible = _showEtc.IsChecked == true; _plot.Refresh(); };
        _showDecay.Click += (_, _) => { _decay.IsVisible = _showDecay.IsChecked == true; _plot.Refresh(); };
        _showWindow.Click += (_, _) =>
        {
            _window.IsVisible = _leftEdge.IsVisible = _rightEdge.IsVisible = _showWindow.IsChecked == true;
            _plot.Refresh();
        };
        _showWindowed.Click += (_, _) =>
        {
            if (_showWindowed.IsChecked == true && !_windowedReady && hasSamples)
            {
                var trace = _prepared.Windowed.Value;
                _windowed.Data = new SignalXYSourceDoubleArray(trace.Times, trace.Values); _windowedReady = true;
            }
            _windowed.IsVisible = _showWindowed.IsChecked == true; _plot.Refresh();
        };
        _showMetrics.Click += (_, _) => { _metrics.Visibility = _showMetrics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; PositionMetrics(); };
        _showRegression.Click += (_, _) => UpdateRegression();
        _regressionChoice.SelectionChanged += (_, _) => UpdateRegression();
        _plot.Plot.RenderManager.AxisLimitsChanged += (_, _) => SyncNavigator();
        _plot.MouseMove += (_, e) => UpdateCursor(e.GetPosition(_plot));
        _plot.MouseLeave += (_, _) => _cursor.Visibility = Visibility.Collapsed;
        _navigator.PreviewMouseLeftButtonDown += (_, e) => { _draggingNavigator = true; _navigator.CaptureMouse(); MoveNavigator(e.GetPosition(_navigator)); e.Handled = true; };
        _navigator.PreviewMouseMove += (_, e) => { if (_draggingNavigator) { MoveNavigator(e.GetPosition(_navigator)); e.Handled = true; } };
        _navigator.PreviewMouseLeftButtonUp += (_, e) => { _draggingNavigator = false; _navigator.ReleaseMouseCapture(); e.Handled = true; };
        ResetView();
    }

    private static CheckBox Toggle(string label, string color) => new()
    { Content = label, IsChecked = true, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(8, 5, 8, 5) };

    private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-9, Math.Abs(value)));
    private static string Number(double value, string format = "0.000") => double.IsFinite(value) ? value.ToString(format) : "N/A";

    private IReadOnlyList<TimeDomainPoint> WindowPoints()
    {
        var times = new[] { _start, -_leftWindow, 0.0, _rightWindow, _end }
            .Concat(Enumerable.Range(0, 513).Select(i => -_leftWindow + _leftWindow * i / 512.0))
            .Concat(Enumerable.Range(0, 1025).Select(i => _rightWindow * i / 1024.0));
        return times.Distinct().OrderBy(t => t).Select(t => new TimeDomainPoint(t,
            Db(ImpulsePlotAnalysis.WindowWeight(t, _leftWindow, _rightWindow, _result.ImpulseWindowKind)))).ToArray();
    }

    private VerticalLine Marker(double time, string label, string color)
    {
        var line = _plot.Plot.Add.VerticalLine(time);
        line.Color = PlotColor.FromHex(color).WithAlpha(.35); line.LineWidth = 1;
        line.Text = label; line.LabelOppositeAxis = true; line.LabelFontSize = 12;
        line.ManualLabelAlignment = label == "L" ? ScottPlot.Alignment.LowerRight
            : label == "Ref" ? ScottPlot.Alignment.LowerLeft : ScottPlot.Alignment.LowerCenter;
        line.LabelStyle.OffsetX = label == "L" ? -5 : label == "Ref" ? 5 : 0;
        line.LabelStyle.BackgroundColor = PlotColor.FromHex(color);
        line.LabelStyle.Padding = 2;
        return line;
    }

    private static Scatter Trace(WpfPlot plot, IReadOnlyList<TimeDomainPoint> points, Func<TimeDomainPoint, double> value,
        string color, string name)
    {
        var trace = plot.Plot.Add.Scatter(points.Select(p => p.TimeSeconds).ToArray(), points.Select(p => Math.Max(-180, value(p))).ToArray());
        trace.Color = PlotColor.FromHex(color); trace.MarkerSize = 0; trace.LineWidth = 1; trace.LegendText = name;
        trace.FillY = false;
        return trace;
    }

    private static SignalXY SignalTrace(WpfPlot plot, ImpulseTraceData data, string color, string name,
        Func<ImpulseTraceData>? currentData = null)
    {
        // SignalXY retains min/max per screen column and all samples when zoomed
        // in. It avoids submitting hundreds of thousands of overlapping paths.
        var trace = new ImpulseSignalPlot(new SignalXYSourceDoubleArray(data.Times, data.Values), cacheGeometry: true);
        plot.Plot.PlottableList.Add(trace);
        trace.Color = PlotColor.FromHex(color); trace.MarkerSize = 0; trace.LineWidth = 1; trace.LegendText = name;
        plot.Plot.RenderManager.RenderStarting += (_, render) =>
        {
            if (!trace.IsVisible) return;
            double[] times = (currentData?.Invoke() ?? data).Times;
            int first = Array.BinarySearch(times, trace.Axes.XAxis.Min);
            int last = Array.BinarySearch(times, trace.Axes.XAxis.Max);
            if (first < 0) first = ~first;
            if (last < 0) last = ~last;
            // Antialiasing thousands of overlapping vertical strokes is costly
            // and adds no detail. Restore it automatically at sample-level zoom.
            trace.LineStyle.AntiAlias = last - first <= render.DataRect.Width * 2;
        };
        return trace;
    }

    private static void ApplyPlotStyle(WpfPlot plot)
    {
        plot.Plot.FigureBackground.Color = PlotColor.FromHex("#161618");
        plot.Plot.DataBackground.Color = PlotColor.FromHex("#101011");
        plot.Plot.Axes.Color(PlotColor.FromHex("#D4D4D8"));
        plot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 12;
        plot.Plot.Axes.Left.TickLabelStyle.FontSize = 12;
        plot.Plot.Grid.MajorLineColor = PlotColor.FromHex("#39393C");
        plot.Plot.Legend.IsVisible = false;
    }

    private Border BuildMetrics()
    {
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = "No Filter", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 5, 2, 8) });
        void Row(string name, string value, bool trusted = true)
        {
            var line = new Grid { Margin = new Thickness(5, 4, 5, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new TextBlock { Text = name, Foreground = Brushes.LightGray, FontSize = 12 });
            var text = new TextBlock { Text = value, Foreground = trusted ? Brushes.LightSkyBlue : Brushes.Orange, FontSize = 12, FontStyle = trusted ? FontStyles.Normal : FontStyles.Italic };
            Grid.SetColumn(text, 1); line.Children.Add(text); rows.Children.Add(line);
        }
        foreach (var estimate in _result.DecayEstimates)
        {
            double r = double.IsFinite(estimate.RSquared) ? -Math.Sqrt(Math.Clamp(estimate.RSquared, 0, 1)) : double.NaN;
            Row($"{estimate.Name} {estimate.StartDb:0}..{estimate.EndDb:0} dB", $"{Number(estimate.Rt60Seconds)} s; r {Number(r)}", estimate.IsValid && r <= -.98);
        }
        var t20 = _result.DecayEstimates.FirstOrDefault(p => p.Name == "T20");
        var t30 = _result.DecayEstimates.FirstOrDefault(p => p.Name == "T30");
        double curvature = t20?.Rt60Seconds > 0 && t30 != null ? 100 * (t30.Rt60Seconds / t20.Rt60Seconds - 1) : double.NaN;
        Row("Curvature", Number(curvature, "0.0") + " %", t20?.IsValid == true && t30?.IsValid == true);
        Row("Clarity C20", Number(_result.ImpulsePlot?.ClarityC20Db ?? double.NaN, "0.00") + " dB");
        Row("Clarity C50", Number(_result.ClarityC50Db, "0.00") + " dB");
        Row("Clarity C80", Number(_result.ClarityC80Db, "0.00") + " dB");
        Row("Definition D50", Number(_result.DefinitionD50Percent, "0.0") + " %");
        Row("Centre time TS", Number(_result.ImpulsePlot?.CentreTimeSeconds ?? double.NaN) + " s");
        return new Border { Child = rows, Background = new SolidColorBrush(Color.FromArgb(224, 30, 30, 34)), BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Width = 260, Margin = new Thickness(55, 35, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Màu cam: hồi quy chưa tin cậy/không đủ dữ liệu. RT60 chẩn đoán; không dùng làm kết luận mất effect." };
    }

    private void BuildRegressions()
    {
        foreach (var estimate in _result.DecayEstimates)
        {
            _regressionChoice.Items.Add(estimate.Name);
            var selected = _result.EnergyDecayCurve.Where(p => p.LevelDb <= estimate.StartDb && p.LevelDb >= estimate.EndDb).ToArray();
            if (selected.Length < 2 || !double.IsFinite(estimate.SlopeDbPerSecond)) continue;
            double intercept = selected.Average(p => p.LevelDb) - estimate.SlopeDbPerSecond * selected.Average(p => p.TimeSeconds);
            var fit = _plot.Plot.Add.Scatter(new[] { selected[0].TimeSeconds, selected[^1].TimeSeconds },
                new[] { intercept + estimate.SlopeDbPerSecond * selected[0].TimeSeconds, intercept + estimate.SlopeDbPerSecond * selected[^1].TimeSeconds });
            fit.Color = PlotColor.FromHex("#FBBF24"); fit.LineWidth = 2; fit.MarkerSize = 0; fit.IsVisible = false;
            _regressions[estimate.Name] = fit;
        }
        _regressionChoice.SelectedItem = _regressionChoice.Items.Contains("T20") ? "T20" : _regressionChoice.Items.Cast<object>().FirstOrDefault();
        UpdateRegression();
    }

    private void UpdateRegression()
    {
        foreach (var pair in _regressions) pair.Value.IsVisible = _showRegression.IsChecked == true && pair.Key == _regressionChoice.SelectedItem as string;
        _plot.Refresh();
    }

    private void ResetView()
    {
        _navigator.Plot.Axes.SetLimitsX(_start, _end);
        _plot.Plot.Axes.SetLimits(_start, _end, -180, 20); _plot.Refresh(); SyncNavigator();
    }

    private void ShowFullView()
    {
        _navigator.Plot.Axes.SetLimitsX(_dataStart, _dataEnd);
        _plot.Plot.Axes.SetLimits(_dataStart, _dataEnd, -180, 20); _plot.Refresh(); SyncNavigator();
    }

    private void SyncNavigator()
    {
        var limits = _plot.Plot.Axes.GetLimits();
        if (limits.Left == _navLeft && limits.Right == _navRight) return;
        _navLeft = limits.Left; _navRight = limits.Right;
        _selection.X1 = limits.Left; _selection.X2 = limits.Right; _navigator.Refresh();
        _cursor.Visibility = Visibility.Collapsed;
        PositionMetrics();
    }

    private void PositionMetrics()
    {
        if (_metrics.Visibility != Visibility.Visible) return;
        var limits = _plot.Plot.Axes.GetLimits();
        double width = limits.Right - limits.Left;
        if (width <= 0) return;
        double zeroPixel = 55 + Math.Max(0, _plot.ActualWidth - 70) * -limits.Left / width;
        bool fitsLeft = zeroPixel > 330;
        _metrics.HorizontalAlignment = fitsLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _metrics.Margin = fitsLeft ? new Thickness(55, 35, 0, 0) : new Thickness(0, 35, 20, 0);
    }

    private void MoveNavigator(Point position)
    {
        var limits = _plot.Plot.Axes.GetLimits();
        double navStart = _navigator.Plot.Axes.Bottom.Min, navEnd = _navigator.Plot.Axes.Bottom.Max;
        double width = Math.Min(navEnd - navStart, limits.Right - limits.Left);
        double center = _navigator.Plot.GetCoordinates((float)(position.X * _navigator.DisplayScale), (float)(position.Y * _navigator.DisplayScale)).X;
        double left = Math.Clamp(center - width / 2, navStart, navEnd - width);
        _plot.Plot.Axes.SetLimitsX(left, left + width); _plot.Refresh(); SyncNavigator();
    }

    private void UpdateCursor(Point position)
    {
        long now = Environment.TickCount64;
        if (now - _lastCursorUpdate < 16) return;
        _lastCursorUpdate = now;
        double time = _plot.Plot.GetCoordinates((float)(position.X * _plot.DisplayScale), (float)(position.Y * _plot.DisplayScale)).X;
        if (time < _dataStart || time > _dataEnd) return;
        static double Nearest(ImpulseTraceData data, double time)
        {
            if (data.Times.Length == 0) return double.NaN;
            int index = Array.BinarySearch(data.Times, time);
            if (index < 0) index = Math.Min(data.Times.Length - 1, ~index);
            if (index > 0 && Math.Abs(data.Times[index - 1] - time) < Math.Abs(data.Times[index] - time)) index--;
            return data.Values[index];
        }
        var rect = _plot.Plot.RenderManager.LastRender.DataRect;
        _cursor.X1 = _cursor.X2 = position.X;
        _cursor.Y1 = rect.Top / _plot.DisplayScale; _cursor.Y2 = rect.Bottom / _plot.DisplayScale;
        _cursor.Visibility = Visibility.Visible;
        _readout.Text = $"{time * 1000:0.00} ms · IR {Number(Db(Nearest(_prepared.Raw, time)), "0.0")} · ETC {Number(Nearest(_prepared.Etc, time), "0.0")} dBr";
    }
}
