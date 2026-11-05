using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoncaAudioInspector.Update;

namespace SoncaAudioInspector;

public sealed class VersionInfoView : UserControl
{
    public const string HistoryUrl = "https://inventory.acnos.store/update";
    private readonly TextBlock _features = Text("", "VersionFeatures");
    private readonly TextBlock _feed = Text("", "VersionFeed");
    private readonly TextBlock _status = Text("", "VersionUpdateStatus");
    private readonly Button _check = Button("Kiểm tra cập nhật", "CheckVersionUpdate");
    private UpdateCheckState _state = new();
    public VersionInfoView()
    {
        var stack = new StackPanel { Margin = new Thickness(24), MaxWidth = 1050, HorizontalAlignment = HorizontalAlignment.Left };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        string assembly = typeof(AppVersion).Assembly.Location;
        var title = Text($"{AppVersion.Display} ({AppVersion.Current.ToString(3)})", "RunningVersion");
        title.FontSize = 25; title.Foreground = Brushes.MediumSpringGreen; stack.Children.Add(title);
        stack.Children.Add(Text("Ngày build (giờ máy): " + File.GetLastWriteTime(assembly).ToString("dd/MM/yyyy HH:mm:ss"), "VersionBuildDate"));
        stack.Children.Add(Text("TÍNH NĂNG CỦA BẢN ĐANG CHẠY", "VersionFeaturesHeading")); stack.Children.Add(_features);
        stack.Children.Add(Text("KÊNH CẬP NHẬT", "VersionFeedHeading")); stack.Children.Add(_feed); stack.Children.Add(_status);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; stack.Children.Add(actions);
        actions.Children.Add(_check);
        var history = Button("Lịch sử phiên bản", "VersionHistory"); actions.Children.Add(history);
        _check.Click += async (_, _) =>
        {
            if (_state.CheckRequested == null || Window.GetWindow(this) is not { } owner) return;
            _check.IsEnabled = false;
            try { await _state.CheckRequested(owner); }
            catch (Exception ex) { _state.SetStatus("Lỗi: " + ShortError(ex)); }
            finally { _check.IsEnabled = _state.CheckRequested != null; }
        };
        history.Click += (_, _) =>
        {
            try { Process.Start(HistoryStartInfo()); }
            catch (Exception ex) { _state.SetStatus("Lỗi: " + ShortError(ex)); }
        };
        Loaded += (_, _) => { _state.PropertyChanged -= StateChanged; _state.PropertyChanged += StateChanged; RefreshState(); };
        Unloaded += (_, _) => _state.PropertyChanged -= StateChanged;
        LoadFromDirectory(AppContext.BaseDirectory); RefreshState();
    }
    public static ProcessStartInfo HistoryStartInfo() => new(HistoryUrl) { UseShellExecute = true };
    public void Bind(UpdateCheckState state)
    {
        _state.PropertyChanged -= StateChanged; _state = state;
        if (IsLoaded) _state.PropertyChanged += StateChanged;
        RefreshState();
    }
    public void LoadFromDirectory(string directory)
    {
        string path = Path.Combine(directory, "version-info.json");
        try
        {
            if (!File.Exists(path)) { _features.Text = "Bản phát triển — chưa có ghi chú phát hành"; return; }
            var info = JsonSerializer.Deserialize<InstalledVersionInfo>(File.ReadAllBytes(path), UpdateManifest.JsonOptions)
                ?? throw new InvalidDataException("Nội dung rỗng.");
            if (UpdateManifest.ParseVersion(info.Version) != AppVersion.Current || info.ReleasedUtc == default
                || info.Features == null || info.Features.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Version hoặc danh sách tính năng không hợp lệ.");
            _features.Text = info.Features.Length == 0 ? "Chưa có ghi chú tính năng cho bản này." : string.Join("\n", info.Features.Select(f => "• " + f));
        }
        catch (Exception ex) { _features.Text = "Không đọc được version-info.json: " + ShortError(ex); }
    }
    private static string ShortError(Exception ex)
    {
        string message = ex is JsonException ? "JSON không hợp lệ." : ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length > 180 ? message[..180] + "…" : message;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) RefreshState(); else _ = Dispatcher.BeginInvoke(RefreshState);
    }
    private void RefreshState() { _feed.Text = _state.Feed; _status.Text = _state.Status; _check.IsEnabled = _state.CheckRequested != null; }
    private static TextBlock Text(string text, string name) => new() { Text = text, Name = name, TextWrapping = TextWrapping.Wrap,
        FontSize = 15, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 6, 0, 8) };
    private static Button Button(string text, string name) => new() { Content = text, Name = name, Padding = new Thickness(16, 9, 16, 9),
        Margin = new Thickness(0, 0, 12, 0), Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)), Foreground = Brushes.WhiteSmoke };
}
