using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

public sealed partial class ModelConfigEditorWindow
{
    private Func<TestConfig, ResponseLimitPreviewData?>? _responsePreviewProvider;
    public Func<TestConfig, ResponseLimitPreviewData?>? ResponsePreviewProvider
    {
        get => _responsePreviewProvider;
        set { _responsePreviewProvider = value; RefreshResponseLimitPreview(); }
    }
    private WpfPlot? _responseLimitPreview;
    private TextBlock? _dipWarnings, _responsePreviewNote, _responseToleranceNote;
    private TestConfig? _responseLimitTest;
    private bool _committingDipRows, _dipRefreshQueued;

    private sealed class DipRow(DipWindowConfig window, int number, Action changed) : INotifyPropertyChanged
    {
        public DipWindowConfig Window { get; } = window;
        public int Number { get; } = number;
        public bool Enabled { get => Window.Enabled; set { Window.Enabled = value; changed(); } }
        private string _min = window.MinHz.ToString("R", CultureInfo.InvariantCulture), _max = window.MaxHz.ToString("R", CultureInfo.InvariantCulture);
        public string Min { get => _min; set { _min = value; changed(); } }
        public string Max { get => _max; set { _max = value; changed(); } }
        public string Mode { get => Window.Exclude ? "Loại khỏi xét" : "Bỏ giới hạn dưới"; set { Window.Mode = value == "Loại khỏi xét" ? "Exclude" : null; changed(); } }
        public string Note { get => Window.Note ?? ""; set { Window.Note = string.IsNullOrWhiteSpace(value) ? null : value; changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Commit()
        {
            if (!double.TryParse(Min.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double min))
            {
                var pair = ParseFrequencyRange(Min); min = pair[0]; _max = pair[1].ToString("R", CultureInfo.InvariantCulture);
                PropertyChanged?.Invoke(this, new(nameof(Max)));
            }
            if (!double.TryParse(Max.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double max))
                throw new ArgumentException($"Khoảng #{Number}: nhập tần số hợp lệ.");
            Window.MinHz = min; Window.MaxHz = max;
        }
    }

    private void QueueDipRefresh()
    {
        if (_buildingForm || _committingDipRows || _dipRefreshQueued) return;
        _dipRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() => { _dipRefreshQueued = false; SynchronizeForm(); }));
    }

    private void BuildResponseLimits(TestConfig test)
    {
        _responseLimitTest = test;
        BuildOptionalPolicy(test, "CheckBandAverage", "Kiểm tra độ lệch trung bình từng dải (Bass / Mid / Treble, không tính điểm hõm)",
            "TxtBandAverageTolerance", "Giới hạn lệch trung bình dải (±dB, mặc định 1,5 dB)",
            () => test.BandAverageEnabled, value => test.BandAverageEnabled = value,
            () => test.BandAverageToleranceDb, value => test.BandAverageToleranceDb = value, 1.5, test.ValidateDipWindows);
        BuildOptionalPolicy(test, "CheckSingleRunMargin", "1 lượt là đủ khi cách mọi biên ≥ M (dB)",
            "TxtSingleRunMarginDb", "Khoảng cách tới mọi biên M (dB, mặc định 1,0 dB)",
            () => test.SingleRunPassWhenMarginDbEnabled, value => test.SingleRunPassWhenMarginDbEnabled = value,
            () => test.SingleRunPassMarginDb, value => test.SingleRunPassMarginDb = value, 1.0, test.ValidateRunPolicy);
        var marginWarning = Note("Cảnh báo: dễ lọt lỗi chập chờn (rè, tiếp xúc lỏng).");
        marginWarning.Foreground = Brushes.Gold; _fieldTarget.Children.Add(marginWarning);

        _responseToleranceNote = Note(""); _fieldTarget.Children.Add(_responseToleranceNote);
        var enabled = new CheckBox { Name = "DipWindowsEnabled", Content = "Bật khoảng xét hõm cho bài này", IsChecked = test.DipWindowsEnabled == true,
            Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) };
        _fieldTarget.Children.Add(enabled);
        enabled.Checked += (_, _) => { test.DipWindowsEnabled = true; SynchronizeForm(); };
        enabled.Unchecked += (_, _) => { test.DipWindowsEnabled = false; SynchronizeForm(); };
        _fieldTarget.Children.Add(Note("Nhập nhanh 56–75 Hz hoặc [56, 75] vào ô Từ Hz. Cả hai chế độ đều loại điểm hõm khỏi X/N; vượt biên trên LowerOnly và CRITICAL vẫn FAIL riêng."));
        var grid = new DataGrid { Name = "DipWindowsGrid", AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            MinHeight = 85, MaxHeight = 240, Background = Brushes.Black, Foreground = Brushes.White,
            RowBackground = new SolidColorBrush(Color.FromRgb(24, 24, 27)), AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(32, 32, 37)), HeadersVisibility = DataGridHeadersVisibility.Column };
        Binding Bind(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(DipRow.Number)), IsReadOnly = true, Width = 30 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Bật", Binding = Bind(nameof(DipRow.Enabled)), Width = 40 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Từ Hz", Binding = Bind(nameof(DipRow.Min)), Width = 85 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Đến Hz", Binding = Bind(nameof(DipRow.Max)), Width = 85 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Chế độ", ItemsSource = new[] { "Bỏ giới hạn dưới", "Loại khỏi xét" }, SelectedItemBinding = Bind(nameof(DipRow.Mode)), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ghi chú", Binding = Bind(nameof(DipRow.Note)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var rows = new List<DipRow>();
        void Rebuild()
        {
            rows.Clear(); rows.AddRange((test.DipWindows ?? []).Select((w, i) => new DipRow(w, i + 1, QueueDipRefresh)));
            grid.ItemsSource = null; grid.ItemsSource = rows;
        }
        var remove = new FrameworkElementFactory(typeof(Button)); remove.SetValue(ContentControl.ContentProperty, "Xóa");
        remove.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => Guard(() =>
        {
            if (((FrameworkElement)sender).DataContext is DipRow row) test.DipWindows?.Remove(row.Window);
            Rebuild(); SynchronizeForm();
        })));
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Xóa", CellTemplate = new DataTemplate { VisualTree = remove }, Width = 55 });
        Rebuild(); _fieldTarget.Children.Add(grid);
        _commits.Add(() =>
        {
            _committingDipRows = true;
            try { grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); foreach (var row in rows) row.Commit(); test.ValidateDipWindows(); }
            finally { _committingDipRows = false; }
        });
        var add = ActionButton("+ Thêm khoảng", () => Guard(() =>
        {
            Flush(); test.DipWindows ??= [];
            if (test.DipWindows.Count >= 12) throw new ArgumentException("Tối đa 12 khoảng xét hõm.");
            double min = test.DipWindows.Count == 0 ? 56 : Math.Min(19900, test.DipWindows.Max(w => w.MaxHz) + 1);
            test.DipWindows.Add(new() { MinHz = min, MaxHz = Math.Min(20000, min * 75 / 56) });
            Rebuild(); SynchronizeForm();
        }));
        add.Name = "AddDipWindow"; _fieldTarget.Children.Add(add);
        _dipWarnings = Note(""); _dipWarnings.Name = "DipWarnings"; _dipWarnings.Foreground = Brushes.Gold; _fieldTarget.Children.Add(_dipWarnings);
        _responsePreviewNote = Note(""); _fieldTarget.Children.Add(_responsePreviewNote);
        _responseLimitPreview = new WpfPlot { Name = "ResponseLimitPreview", Height = 320, Margin = new Thickness(0, 6, 0, 0) };
        _fieldTarget.Children.Add(_responseLimitPreview);
        RefreshResponseLimitPreview();
    }

    private void BuildOptionalPolicy(TestConfig test, string checkName, string label, string inputName, string inputLabel,
        Func<bool?> getEnabled, Action<bool?> setEnabled, Func<double?> getValue, Action<double?> setValue, double defaultValue, Action validate)
    {
        var check = new CheckBox { Name = checkName, Content = label, IsChecked = getEnabled() == true,
            Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 4) };
        _fieldTarget.Children.Add(check); _fieldTarget.Children.Add(Note(inputLabel));
        var input = new TextBox { Name = inputName, Text = getValue()?.ToString("R", CultureInfo.InvariantCulture) ?? "",
            IsEnabled = getEnabled() == true, Margin = new Thickness(0, 2, 0, 4) };
        _fieldTarget.Children.Add(input);
        check.Checked += (_, _) =>
        {
            setEnabled(true); input.IsEnabled = true;
            if (getValue() == null) input.Text = defaultValue.ToString("0.0", CultureInfo.InvariantCulture);
            SynchronizeForm();
        };
        check.Unchecked += (_, _) => { setEnabled(false); input.IsEnabled = false; SynchronizeForm(); };
        input.TextChanged += (_, _) => SynchronizeForm();
        _commits.Add(() =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) setValue(null);
            else if (double.TryParse(input.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) setValue(value);
            else throw new ArgumentException($"{test.id}: {inputLabel}: nhập số hợp lệ.");
            validate();
        });
    }

    private void RefreshResponseLimitPreview()
    {
        if (_responseLimitPreview == null || _responseLimitTest == null) return;
        try
        {
            var test = _responseLimitTest;
            var data = _responsePreviewProvider?.Invoke(test);
            double tolerance = data?.ToleranceDb ?? 3;
            var settings = ResponseLimitSettings.Capture(test, tolerance);
            var reference = data?.StandardCurve;
            bool illustrative = reference == null || reference.Count == 0;
            reference = illustrative ? new Dictionary<double, double> { [20] = 0, [20000] = 0 } : reference;
            var limits = illustrative ? null : data?.FrequencyLimits;
            var plot = _responseLimitPreview.Plot; plot.Clear();
            plot.FigureBackground.Color = ScottPlot.Color.FromHex("#18181B"); plot.DataBackground.Color = ScottPlot.Color.FromHex("#101012");
            plot.Axes.Color(ScottPlot.Color.FromHex("#D1D5DB"));
            var policy = new ResponseLimitPolicy(settings, reference, limits);
            var grid = ResponseLimitPlot.Grid(settings, reference!.Keys.Concat(Enumerable.Range(0, 121).Select(i => 20 * Math.Pow(1000, i / 120d))));
            double labelY = reference!.Values.Where(double.IsFinite).DefaultIfEmpty(0).Max() + tolerance + 3;
            foreach (var (low, high, alpha, name) in new[] { (TestRunner.BassMin, TestRunner.BassMax, .08, "BASS"), (TestRunner.MidMin, TestRunner.MidMax, .04, "MID"), (TestRunner.TrebleMin, TestRunner.TrebleMax, .08, "TREBLE") })
            {
                var band = plot.Add.HorizontalSpan(Math.Log10(low), Math.Log10(high)); band.LineStyle.Width = 0;
                band.FillStyle.Color = ScottPlot.Color.FromHex("#1F2937").WithAlpha(alpha);
                var label = plot.Add.Text($"{name} ±{tolerance:0.0} dB", Math.Log10(Math.Sqrt(low * high)), labelY);
                label.LabelFontColor = ScottPlot.Color.FromHex("#9CA3AF"); label.LabelFontSize = 9;
            }
            var target = grid.Where(f => ResponseLimitPolicy.TryInterpolate(reference, f, out _)).ToArray();
            var line = plot.Add.Scatter(target.Select(Math.Log10).ToArray(), target.Select(f => { ResponseLimitPolicy.TryInterpolate(reference, f, out double value); return value; }).ToArray());
            line.Color = ScottPlot.Color.FromHex("#EAB308"); line.LineWidth = 2; line.MarkerSize = 0;
            if (data?.MeasuredCurve.Count > 0)
            {
                var measured = data.MeasuredCurve.OrderBy(p => p.Key).ToArray();
                var last = plot.Add.Scatter(measured.Select(p => Math.Log10(p.Key)).ToArray(), measured.Select(p => p.Value).ToArray());
                last.Color = ScottPlot.Color.FromHex("#10B981"); last.LineWidth = 1.5f; last.MarkerSize = 0;
            }
            ResponseLimitPlot.Draw(plot, policy, grid, drawCritical: true);
            foreach (double f in new[] { TestRunner.BassMax, TestRunner.MidMax }) { var separator = plot.Add.VerticalLine(Math.Log10(f)); separator.Color = ScottPlot.Color.FromHex("#4B5563"); }
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(new[] { 20d, 50, 100, 250, 1000, 4000, 10000, 20000 }.Select(f => new ScottPlot.Tick(Math.Log10(f), f >= 1000 ? $"{f / 1000:0.#}k" : f.ToString("0"))).ToArray());
            plot.Axes.Bottom.Label.Text = "Tần số (Hz, trục log)"; plot.Axes.Left.Label.Text = "Mức thu / biên (dB)";
            plot.Axes.AutoScale(); var axes = plot.Axes.GetLimits(); plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), Math.Min(axes.Bottom, -5), Math.Max(axes.Top, 5));
            _responseToleranceNote!.Text = $"Dung sai chung ±{tolerance:0.0} dB cho Bass/Mid/Treble — đổi tại Audio Routing → Freq Tolerance. Biên có sẵn trong CSV được giữ nguyên.";
            _responsePreviewNote!.Text = illustrative ? "Chưa có LINE CHUẨN trong phiên — minh họa quanh 0 dB" : data!.Source;
            _dipWarnings!.Text = test.DipWindowWarnings();
            _responseLimitPreview.Refresh();
        }
        catch (Exception ex) { ShowEditorError(ex); }
    }
}
