using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed partial class ModelConfigEditorWindow
{
    private ModelConfig _draft = new();
    private TabControl _tabs = null!;
    private ListBox _testList = null!;
    private StackPanel _fields = null!, _fieldTarget = null!;
    private readonly List<Action> _commits = new();
    private TestConfig? _editing;
    private bool _refreshing, _buildingForm, _updatingJson, _jsonPending;

    private TabControl CreateForm(ModelConfig initial)
    {
        _draft = JsonSerializer.Deserialize<ModelConfig>(JsonSerializer.Serialize(initial, JsonOptions))!;
        _tabs = new TabControl();
        var layout = new Grid { Margin = new Thickness(12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(235) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(0, 0, 15, 0) }; layout.Children.Add(sidebar);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(actions, Dock.Bottom); sidebar.Children.Add(actions);
        actions.Children.Add(ActionButton("+ Thêm bài", () => Guard(() =>
        {
            Flush(); int i = 1; while (_draft.testItems.InOut.Tests.Any(t => t.id == $"IO{i:000}")) i++;
            var devices = _draft.testItems.InOut.Devices;
            var test = new TestConfig { id = $"IO{i:000}", name = "Bài đo mới", PlaybackOut = devices.Input.Keys.FirstOrDefault() ?? "OUT1", RecordingIn = devices.Output.Keys.FirstOrDefault() ?? "MIC1", FrequencyResponseMethod = "LogSweep" };
            _draft.testItems.InOut.Tests.Add(test); RefreshTests(test); UpdateJsonProjection();
        })));
        actions.Children.Add(ActionButton("Xóa bài", () => Guard(() =>
        {
            if (_editing == null) return;
            _draft.testItems.InOut.Tests.Remove(_editing); _commits.Clear(); _editing = null; RefreshTests(); UpdateJsonProjection();
        })));
        _testList = new ListBox { Name = "ConfigTests", Background = Brushes.Black, Foreground = Brushes.White, DisplayMemberPath = "name" }; sidebar.Children.Add(_testList);
        _fields = new StackPanel { Margin = new Thickness(4, 0, 12, 10) };
        var scroll = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        _tabs.Items.Add(new TabItem { Header = "Bài đo & tiêu chí PASS/FAIL", Content = layout });
        _testList.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            try { CommitFields(); BuildTest(_testList.SelectedItem as TestConfig); UpdateJsonProjection(); }
            catch (Exception ex) { ShowEditorError(ex); _refreshing = true; _testList.SelectedItem = _editing; _refreshing = false; }
        };
        var json = new DockPanel { Margin = new Thickness(12) };
        var help = new StackPanel(); DockPanel.SetDock(help, Dock.Top); json.Children.Add(help);
        help.Children.Add(Note("JSON ánh xạ trực tiếp bài đo và tiêu chí, tự cập nhật khi nhập form. Nếu sửa JSON, bấm Áp dụng JSON vào bài đo. Có thể chuyển tab bất cứ lúc nào."));
        help.Children.Add(ActionButton("Áp dụng JSON vào bài đo", () => Guard(() => { ApplyJson(); _error.Text = "Đã nạp JSON vào bài đo. Bấm Lưu model hoặc Gửi lên server."; })));
        json.Children.Add(_json); _tabs.Items.Add(new TabItem { Header = "JSON nâng cao", Content = json });
        _json.TextChanged += (_, _) => { if (!_updatingJson && !_refreshing) _jsonPending = true; };
        _name.TextChanged += (_, _) => SynchronizeForm();
        // Navigation never validates or rebuilds controls. Invalid JSON cannot trap focus.
        RefreshForm(); return _tabs;
    }
    private ModelConfig ReadEditorModel()
    {
        if (_jsonPending) ApplyJson(); else Flush();
        return ParseModel(_name.Text, JsonSerializer.Serialize(_draft, JsonOptions));
    }
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) };
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 4, 7, 4), MinWidth = 80 }; button.Click += (_, _) => action(); return button;
    }
    private void ShowEditorError(Exception ex) { _error.Foreground = Brushes.Gold; _error.Text = ex.Message; }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { ShowEditorError(ex); } }
    private void RefreshForm()
    {
        string? selectedId = _editing?.id;
        _commits.Clear(); _editing = null;
        RefreshTests(_draft.testItems.InOut.Tests.FirstOrDefault(t => t.id == selectedId));
        UpdateJsonProjection();
    }
    private void RefreshTests(TestConfig? selected = null)
    {
        _refreshing = true; _testList.ItemsSource = null; _testList.ItemsSource = _draft.testItems.InOut.Tests;
        _testList.SelectedItem = selected ?? _draft.testItems.InOut.Tests.FirstOrDefault(); _refreshing = false;
        BuildTest(_testList.SelectedItem as TestConfig);
    }
    private void CommitFields() { foreach (var action in _commits) action(); }
    private void Flush()
    {
        CommitFields(); _draft.model = _name.Text.Trim();
        var config = _draft.testItems.InOut;
        foreach (string key in config.Devices.Input.Keys.Where(k => !config.Tests.Any(t => t.PlaybackOut == k)).ToArray()) config.Devices.Input.Remove(key);
        foreach (string key in config.Devices.Output.Keys.Where(k => !config.Tests.Any(t => t.RecordingIn == k)).ToArray()) config.Devices.Output.Remove(key);
    }
    private void ApplyJson()
    {
        var parsed = ParseModel(_name.Text, _json.Text);
        _draft = parsed; RefreshForm();
    }
    private void UpdateJsonProjection()
    {
        _updatingJson = true;
        try { _json.Text = JsonSerializer.Serialize(_draft, JsonOptions); _jsonPending = false; }
        finally { _updatingJson = false; }
    }
    private void SynchronizeForm()
    {
        if (_buildingForm || _refreshing || _updatingJson) return;
        try { Flush(); UpdateJsonProjection(); _error.Foreground = Brushes.LightGray; _error.Text = "JSON đã khớp với bài đo và tiêu chí. Lưu model để lưu máy này; Gửi lên server để chia sẻ cho máy khác."; }
        catch (Exception ex) { ShowEditorError(ex); }
    }
    private void Field(string label, string? value, Action<string> set, string? hint = null)
    {
        _fieldTarget.Children.Add(Label(label)); var input = new TextBox { Text = value ?? "", ToolTip = hint ?? label };
        _fieldTarget.Children.Add(input); _commits.Add(() => set(input.Text.Trim()));
        input.TextChanged += (_, _) => SynchronizeForm();
        if (hint != null) _fieldTarget.Children.Add(Note(hint));
    }
    private void Number(string label, double? value, Action<double?> set, double min, double max, bool required = false)
    {
        Field(label, value?.ToString("R", CultureInfo.InvariantCulture), text =>
        {
            if (text.Length == 0 && !required) { set(null); return; }
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < min || n > max)
                throw new InvalidDataException($"{label}: nhập số từ {min} đến {max}.");
            set(n);
        });
    }
    private void Check(string label, bool value, Action<bool> set, string hint)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 2), FontSize = 14, ToolTip = hint };
        check.Checked += (_, _) => { set(true); SynchronizeForm(); }; check.Unchecked += (_, _) => { set(false); SynchronizeForm(); }; _fieldTarget.Children.Add(check); _fieldTarget.Children.Add(Note(hint));
    }
    private void Choice(string label, string[] choices, string selected, Action<string> set)
    {
        _fieldTarget.Children.Add(Label(label));
        var values = choices.Contains(selected) || selected.Length == 0 ? choices : choices.Append(selected).ToArray();
        var combo = new ComboBox { ItemsSource = values, SelectedItem = selected, ToolTip = label };
        _fieldTarget.Children.Add(combo); _commits.Add(() => set(combo.SelectedItem?.ToString() ?? ""));
        combo.SelectionChanged += (_, _) => SynchronizeForm();
    }
    private void BuildTest(TestConfig? test)
    {
        _buildingForm = true;
        _editing = test; _commits.Clear(); _fields.Children.Clear(); _fieldTarget = _fields;
        if (test == null) { _fieldTarget.Children.Add(Note("Bấm + Thêm bài để tạo bài đo.")); _buildingForm = false; return; }
        var sections = new TabControl(); _fields.Children.Add(sections);
        void Section(string title)
        {
            _fieldTarget = new StackPanel { Margin = new Thickness(10) };
            sections.Items.Add(new TabItem { Header = title, Content = _fieldTarget });
        }
        Section("Đường tín hiệu");
        _fieldTarget.Children.Add(Label("Thông tin & đường tín hiệu"));
        Field("Mã bài đo (không trùng)", test.id, v => test.id = v);
        Field("Tên bài đo", test.name, v => test.name = v);
        Check("Chạy bài này trong Auto Test", test.IsEnabled, v => test.Enable = v, "Bỏ tích để tạm ngừng bài mà vẫn giữ thông số.");
        Choice("Chuỗi tự động", new[] { "Âm học thông thường", "Điện tử C360 (BLE)" }, C360ModelDefinition.Matches(_draft.testItems.InOut) ? "Điện tử C360 (BLE)" : "Âm học thông thường",
            v => _draft.testItems.InOut.AutomationProfile = v == "Điện tử C360 (BLE)" ? C360ModelDefinition.Profile : null);
        Choice("Ngõ loa điều khiển qua BLE", new[] { "Không dùng", "MIC IN · Echo / Reverb", "MIC IN · Di tần (FBX)", "LINE IN", "USB A", "Mic thực tế · LINE IN" },
            test.SpeakerStage switch { "MicEffects" => "MIC IN · Echo / Reverb", "MicFrequencyShift" => "MIC IN · Di tần (FBX)", "LineIn" => "LINE IN", "UsbA" => "USB A", "AcousticMic" => "Mic thực tế · LINE IN", _ => "Không dùng" },
            v => test.SpeakerStage = v switch { "MIC IN · Echo / Reverb" => "MicEffects", "MIC IN · Di tần (FBX)" => "MicFrequencyShift", "LINE IN" => "LineIn", "USB A" => "UsbA", "Mic thực tế · LINE IN" => "AcousticMic", _ => null });
        if (test.SpeakerStage == "AcousticMic")
        {
            Check("Đã đo chỉnh bằng mic thật — cho phép bật Auto Test", !test.AcousticSetupPending, v => test.AcousticSetupPending = !v,
                "Chưa xác nhận: lưu tạm cấu hình, chưa chạy tự động. Chọn đúng mic, calibration và mức phát/thu trước khi xác nhận.");
        }
        var devices = _draft.testItems.InOut.Devices;
        Field("Tên ngõ PHÁT → loa", test.PlaybackOut, v => test.PlaybackOut = v);
        Field("Tên thiết bị Windows (phát)", devices.Input.GetValueOrDefault(test.PlaybackOut), v => devices.Input[test.PlaybackOut] = v, "Nhập tên nhận diện loa/soundcard, ví dụ Analog Connector 1/2. Áp dụng cho các bài cùng tên ngõ phát.");
        Field("Tên ngõ THU ← micro", test.RecordingIn, v => test.RecordingIn = v);
        Field("Tên thiết bị Windows (thu)", devices.Output.GetValueOrDefault(test.RecordingIn), v => devices.Output[test.RecordingIn] = v, "Nhập tên nhận diện micro/soundcard, ví dụ FastTrack Pro. Kênh thu chọn ở dưới.");
        void Channel(string label, int? value, Action<int?> set) => Choice(label, new[] { "Cả hai / trộn", "1 — Left", "2 — Right" },
            value == null ? "Cả hai / trộn" : value == 1 ? "1 — Left" : value == 2 ? "2 — Right" : value.ToString()!,
            v => set(v == "Cả hai / trộn" ? null : int.Parse(v.Split(' ')[0], CultureInfo.InvariantCulture)));
        Channel("Kênh phát", test.PlaybackChannel, v => test.PlaybackChannel = v);
        Channel("Kênh thu", test.RecordingChannel, v => test.RecordingChannel = v);
        var calibrationLabel = Note(test.MicrophoneCalibration == null ? "Chưa đính calibration riêng cho bài đo." : $"Calibration: {test.MicrophoneCalibration.FileName} · SHA256 {test.MicrophoneCalibration.Sha256[..12]}");
        _fieldTarget.Children.Add(calibrationLabel);
        _fieldTarget.Children.Add(ActionButton("Chọn file calibration…", () => Guard(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Microphone calibration|*.txt;*.cal;*.csv|Tất cả|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            test.MicrophoneCalibration = TestMicrophoneCalibration.Import(dialog.FileName);
            calibrationLabel.Text = $"Calibration: {test.MicrophoneCalibration.FileName} · SHA256 {test.MicrophoneCalibration.Sha256[..12]}";
            UpdateJsonProjection();
        })));
        _fieldTarget.Children.Add(ActionButton("Bỏ calibration riêng", () => { test.MicrophoneCalibration = null; calibrationLabel.Text = "Chưa đính calibration riêng cho bài đo."; UpdateJsonProjection(); }));
        Choice("Phương pháp đáp tuyến", new[] { "LogSweep", "Multitone" }, test.FrequencyResponseMethod ?? "LogSweep", v => test.FrequencyResponseMethod = v);
        Number("Mức phát (dBFS peak, trống = mức hiện tại)", test.PlaybackLevelDbfs, v => test.PlaybackLevelDbfs = v, -60, 0);
        Number("Windows Levels ngõ phát (%, trống = giữ nguyên)", test.WindowsPlaybackPercent, v => test.WindowsPlaybackPercent = v, 1, 100);
        Number("Windows Levels ngõ thu (%, trống = giữ nguyên)", test.WindowsRecordingPercent, v => test.WindowsRecordingPercent = v, 1, 100);
        _fieldTarget.Children.Add(Note("Chỉ áp dụng ngõ phát/thu của bài đo, đọc lại và khôi phục sau đo hoặc hủy. Mức Windows khác mức phát dBFS và gain DSP của loa; không tự thay ngưỡng THD."));
        Number("Giới hạn THD (%)", test.ThdLimit, v => test.ThdLimit = v, .001, 100);
        Number("Mức tín hiệu thu tối thiểu (dBFS)", test.MinimumInputSignalDbFs, v => test.MinimumInputSignalDbFs = v, -120, 0);
        Section("Bật/tắt kiểm tra");
        _fieldTarget.Children.Add(Label("Phép kiểm tra bổ sung → tự thêm PASS / FAIL và nguyên nhân"));
        Check("TestEffect — kiểm tra echo / reverb", test.TestEffect, v => test.TestEffect = v, "Tự dùng log sweep. So line effect; chưa có line thì mặc định yêu cầu echo. Đặt kỳ vọng phía dưới.");
        Check("TestAirLeak — rò khí", test.TestAirLeak, v => test.TestAirLeak = v, "Tone bass riêng, tìm nhiễu cao tần điều biên. FAIL là dấu hiệu nghi rò khí, cần đối chiếu mẫu chuẩn.");
        Check("TestLooseParticle — hạt rác", test.TestLooseParticle, v => test.TestLooseParticle = v, "Tìm xung phi chu kỳ. Xung chưa đủ bằng chứng sẽ hiện INVALID, yêu cầu đo lại.");
        Check("TestSoundLevel — lệch mức âm so line chuẩn", test.TestSoundLevel, v => test.TestSoundLevel = v, "So mức thu tuyệt đối cùng route/gain. Thiếu line hoặc bản thu lỗi sẽ không PASS.");
        Number("Cho phép lệch mức âm ± (dB)", test.SoundLevelToleranceDb, v => test.SoundLevelToleranceDb = v!.Value, .01, 30, true);
        Section("Tone cơ khí");
        _fieldTarget.Children.Add(Label("Thông số tone cho rò khí / hạt rác"));
        Number("Tần số tone (Hz)", test.MechanicalFrequencyHz, v => test.MechanicalFrequencyHz = v!.Value, 20, 200, true);
        Number("Mức tone (dBFS peak)", test.MechanicalLevelDbFs, v => test.MechanicalLevelDbFs = v!.Value, -60, -3, true);
        Number("Thời lượng tone (giây)", test.MechanicalDurationSeconds, v => test.MechanicalDurationSeconds = v!.Value, 1, 5, true);
        Number("Ngưỡng xung trên nền (dB)", test.MechanicalPeakMarginDb, v => test.MechanicalPeakMarginDb = v!.Value, 6, 30, true);
        Section("Echo / Reverb");
        _fieldTarget.Children.Add(Label("Kỳ vọng effect"));
        var effect = test.ImpulseEffects ?? new ImpulseEffectSettings();
        static string Expect(bool? v) => v.HasValue ? v.Value ? "Phải có" : "Không được có" : "Theo line / mặc định";
        static bool? ParseExpect(string v) => v == "Phải có" ? true : v == "Không được có" ? false : null;
        var choices = new[] { "Theo line / mặc định", "Phải có", "Không được có" };
        Choice("Echo", choices, Expect(effect.ExpectEcho), v => effect = effect with { ExpectEcho = ParseExpect(v) });
        Choice("Reverb", choices, Expect(effect.ExpectReverb), v => effect = effect with { ExpectReverb = ParseExpect(v) });
        Number("Delay echo chuẩn (ms, trống = theo line)", effect.ExpectedEchoDelayMs, v => effect = effect with { ExpectedEchoDelayMs = v }, 10, 2000);
        Number("Cho phép lệch delay ± (ms)", effect.EchoDelayToleranceMs, v => effect = effect with { EchoDelayToleranceMs = v!.Value }, .1, 1000, true);
        Number("Thu thêm đuôi sau sweep (giây)", effect.CaptureTailSeconds, v => effect = effect with { CaptureTailSeconds = v!.Value }, 1, 10, true);
        _commits.Add(() => { if (test.TestEffect || test.ImpulseEffects != null) test.ImpulseEffects = effect; });
        Section("Di tần MIC / FBX");
        var shift = test.FrequencyShift ?? new FrequencyShiftSettings();
        _fieldTarget.Children.Add(Note("Bài MIC IN · Di tần phát riêng ba tone 700 / 1700 / 3700 Hz khi FBX tắt và bật; không suy ra gain chống hú dB. Ngõ thu giữ nguyên LINE OUT."));
        Number("Thời lượng multitone mỗi trạng thái FBX (giây)", shift.DurationSeconds, v => shift = shift with { DurationSeconds = v!.Value }, 2, 5, true);
        Number("Ngưỡng xác định di tần (Hz)", shift.DetectionThresholdHz, v => shift = shift with { DetectionThresholdHz = v!.Value }, .3, 15, true);
        Choice("Kỳ vọng di tần trong cả đường MIC", new[] { "Chỉ ghi nhận có / chưa thấy", "Phải có", "Không được có" },
            shift.ExpectedDetected == true ? "Phải có" : shift.ExpectedDetected == false ? "Không được có" : "Chỉ ghi nhận có / chưa thấy",
            v => shift = shift with { ExpectedDetected = v == "Phải có" ? true : v == "Không được có" ? false : null });
        _commits.Add(() => { if (test.SpeakerStage == "MicFrequencyShift" || test.FrequencyShift != null) test.FrequencyShift = shift; });
        Choice("Kỳ vọng thay đổi khi bật FBX", new[] { "Chỉ ghi nhận", "Phải thay đổi", "Không được thay đổi" },
            shift.ExpectedFbxChange == true ? "Phải thay đổi" : shift.ExpectedFbxChange == false ? "Không được thay đổi" : "Chỉ ghi nhận",
            v => shift = shift with { ExpectedFbxChange = v == "Phải thay đổi" ? true : v == "Không được thay đổi" ? false : null });
        _fieldTarget.Children.Add(Note("Các giới hạn nâng cao và trường riêng của model được giữ nguyên; JSON luôn ánh xạ dữ liệu form."));
        _buildingForm = false;
    }
}
