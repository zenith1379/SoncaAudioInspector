using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed record MeasurementValue(string ThongSo, string GiaTri);

public sealed class MeasurementDetailsWindow : Window
{
    private readonly DataGrid _summary = Table();
    private readonly DataGrid _decays = Table();
    private readonly DataGrid _phase = Table();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly ContentControl _impulse = new() { Content = new TextBlock { Text = "Chưa có bản thu sweep. Đồ thị Impulse sẽ xuất hiện sau khi đo.", Margin = new Thickness(20), Foreground = Brushes.White } };
    private StandardAcousticResult? _result;
    private readonly HighOrderHarmonicView _highOrders = new();
    private readonly MechanicalDiagnosticView _looseParticle;
    private readonly MechanicalDiagnosticView _airLeak;
    private readonly PolarityDiagnosticView _polarity = new();
    private readonly SignalDiagnosticView _imd, _compression;
    private System.Threading.CancellationTokenSource? _diagnosticCancellation;
    public Func<MechanicalTestRequest, System.Threading.CancellationToken, Task<MechanicalTestResult>>? DiagnosticRunner { get; set; }
    public Func<SignalDiagnosticRequest, System.Threading.CancellationToken, Task<SignalDiagnosticResult>>? SignalDiagnosticRunner { get; set; }
    public bool IsDiagnosticRunning => _diagnosticCancellation != null;
    public bool IsMeasurementPending { get; set; }

    public MeasurementDetailsWindow()
    {
        Title = "Impulse / Thông số — Line / Line chuẩn"; Width = 1200; Height = 820; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;
        ImpulseAnalysisWindow.UseDarkTitleBar(this);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/ImpulseTheme.xaml", UriKind.Relative) });
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)); Foreground = Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(10) }; Content = layout;
        DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status);
        var tabs = new TabControl(); layout.Children.Add(tabs);
        _looseParticle = new(MechanicalTestKind.LooseParticle, RunDiagnosticAsync, CancelDiagnostic);
        _airLeak = new(MechanicalTestKind.AirLeak, RunDiagnosticAsync, CancelDiagnostic);
        _imd = new(SignalDiagnosticKind.Imd, RunSignalDiagnosticAsync, CancelDiagnostic);
        _compression = new(SignalDiagnosticKind.Compression, RunSignalDiagnosticAsync, CancelDiagnostic);
        tabs.Items.Add(new TabItem { Header = "Impulse / ETC / Schroeder", Content = _impulse });
        tabs.Items.Add(new TabItem { Header = "Tổng quan / Thu / Effect", Content = _summary });
        tabs.Items.Add(new TabItem { Header = "Cực tính", Content = _polarity });
        tabs.Items.Add(new TabItem { Header = "IMD · Hai tone", Content = _imd });
        tabs.Items.Add(new TabItem { Header = "Compression / Limiter", Content = _compression });
        tabs.Items.Add(new TabItem { Header = "H10–H35", Content = _highOrders });
        tabs.Items.Add(new TabItem { Header = "Hạt rác · LP", Content = _looseParticle });
        tabs.Items.Add(new TabItem { Header = "Rò khí · ALD", Content = _airLeak });
        tabs.Items.Add(new TabItem { Header = "EDT / T20 / T30", Content = _decays });
        tabs.Items.Add(new TabItem { Header = "Phase / Group delay", Content = _phase });
        Closed += (_, _) => CancelDiagnostic();
    }

    internal void CancelDiagnostic() => _diagnosticCancellation?.Cancel();

    private async Task<MechanicalTestResult> RunDiagnosticAsync(MechanicalTestRequest request)
        => await RunGuardedDiagnosticAsync(token => (DiagnosticRunner ?? throw new InvalidOperationException("Mở từ Audio Routing và chọn ngõ phát/thu."))(request, token));

    private async Task<SignalDiagnosticResult> RunSignalDiagnosticAsync(SignalDiagnosticRequest request)
        => await RunGuardedDiagnosticAsync(token => (SignalDiagnosticRunner ?? throw new InvalidOperationException("Mở từ Audio Routing và chọn ngõ phát/thu."))(request, token));

    private async Task<T> RunGuardedDiagnosticAsync<T>(Func<System.Threading.CancellationToken, Task<T>> run)
    {
        if (_diagnosticCancellation != null) throw new InvalidOperationException("Đang đo bài khác; bấm Dừng hoặc chờ hoàn tất.");
        using var cancellation = new System.Threading.CancellationTokenSource();
        _diagnosticCancellation = cancellation;
        SetDiagnosticsBusy(true);
        try { return await run(cancellation.Token); }
        finally
        {
            _diagnosticCancellation = null;
            SetDiagnosticsBusy(false);
        }
    }

    private void SetDiagnosticsBusy(bool busy)
    {
        _looseParticle.SetBusy(busy); _airLeak.SetBusy(busy); _imd.SetBusy(busy); _compression.SetBusy(busy);
    }

    public void UpdatePolarity(PolarityDiagnosticResult? result) => _polarity.Update(result);

    public void Update(string status, IReadOnlyList<MeasurementValue> summary, IReadOnlyDictionary<double, double> response, StandardAcousticResult? result)
    {
        _status.Text = status; _summary.ItemsSource = summary;
        if (!ReferenceEquals(_result, result) || result == null)
            _impulse.Content = result == null ? new TextBlock { Text = IsMeasurementPending ? "Đang thu / phân tích sweep…" : "Chưa có bản thu sweep.", Margin = new Thickness(20), Foreground = Brushes.White } : new ImpulseAnalysisView(result);
        _result = result;
        _polarity.Update(PolarityDiagnosticResult.Compare(result, "", null));
        _highOrders.Update(result?.HighOrderHarmonics);
        static string Value(double value, string format = "0.000") => double.IsFinite(value) ? value.ToString(format) : "Không khả dụng";
        _decays.ItemsSource = result?.DecayEstimates.Select(p => new { ThamSo = p.Name, T60_s = Value(p.Rt60Seconds), R2 = Value(p.RSquared),
            Tu_dB = p.StartDb, Den_dB = p.EndDb, HopLe = p.IsValid, TrangThai = p.Status }).ToArray();
        _phase.ItemsSource = result?.FrequencyResponse.Where(p => p.FrequencyHz >= 50 && p.FrequencyHz <= 18000)
            .Select(p => new { Hz = Value(p.FrequencyHz), Phase_deg = Value(p.PhaseDegrees), GroupDelay_ms = Value(p.GroupDelayMs),
                TransferGain_dB = Value(p.LevelDb) }).ToArray();
    }

    private static DataGrid Table()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)), Foreground = Brushes.WhiteSmoke,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        grid.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyName == "ThongSo") { e.Column.Header = "Thông số"; e.Column.Width = 270; }
            else if (e.PropertyName == "GiaTri") { e.Column.Header = "Giá trị"; e.Column.MinWidth = 450; }
            if (e.PropertyName is "ThongSo" or "GiaTri" && e.Column is DataGridTextColumn text)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
                text.ElementStyle = style;
                if (e.PropertyName == "GiaTri") e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
        };
        return grid;
    }
}
