using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private CancellationTokenSource? _c360ModelCancellation;
    private TaskCompletionSource? _c360ModelFinished;
    internal Func<IC360ElectronicBench>? C360BenchFactory = null;
    internal Func<IReadOnlyList<C360ModelEndpoint>>? C360EndpointProvider = null;
    internal Func<CancellationToken, Task<bool>>? EnsureC360Connection = null;

    public async Task StopC360ForShutdownAsync()
    {
        if (_impedanceWindow is { } impedance) await impedance.StopForShutdownAsync();
        var completed = _c360ModelFinished?.Task;
        _c360ModelCancellation?.Cancel();
        if (completed != null) await completed;
    }

    private C360ElectronicSetup C360MachineDefaults()
    {
        string path = Path.Combine(GetUserDataDirectory(), "c360-electronic.json");
        try { if (File.Exists(path) && JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(path)) is { } saved) return saved; }
        catch (Exception ex) when (ex is JsonException or IOException) { AppendLog("C360", ex.Message); }
        return new("unused", "", "unused", _audioEngine.PlaybackSampleRate is 44100 or 48000 ? _audioEngine.PlaybackSampleRate : 44100);
    }

    private C360ModelPlan BuildC360Plan(InOutConfig config)
    {
        if (C360EndpointProvider is { } provide) return C360ModelPlan.Create(config, provide(), C360MachineDefaults());
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (device)
            endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        return C360ModelPlan.Create(config, endpoints, C360MachineDefaults());
    }

    private bool CheckC360ModelDevices(InOutConfig config, out string missingMessage)
    {
        try { _ = BuildC360Plan(config); missingMessage = ""; return true; }
        catch (Exception ex) { missingMessage = ex.Message; return false; }
    }

    public async Task PrepareModelBleAsync(InOutConfig config)
    {
        if (!C360ModelDefinition.Matches(config)) return;
        try { if (await SoncaBleManager.Instance.EnsureConnectedAsync(C360MachineDefaults().BleControllerAddress)) RememberC360Controller(); }
        catch (Exception ex) { AppendLog("BLE", "Chuẩn bị kết nối: " + ex.Message); }
    }

    private void RememberC360Controller()
    {
        if (SoncaBleManager.Instance.ConnectedBluetoothAddress is not ulong address) return;
        var saved = C360MachineDefaults();
        if (saved.BleControllerAddress != address)
            AtomicFile.WriteAllText(Path.Combine(GetUserDataDirectory(), "c360-electronic.json"),
                JsonSerializer.Serialize(saved with { BleControllerAddress = address }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RunC360ModelAutoTestAsync(bool findReference = false, TestConfig? singleTest = null)
    {
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null || _activeInOutConfig == null) return;
        using var cancellation = new CancellationTokenSource();
        _c360ModelCancellation = cancellation;
        var finished = _c360ModelFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellation.Token;
        _discardCurrentMeasurementRequested = false; _isExecutingAutoSuite = true;
        (Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(IsSetupVisible);
        _leaveAudioReleasedAfterAutoTest = true; _reviewedAutoTestCase = null;
        StopRoutingHeadroomMonitor(); _audioEngine.StopAllAudio();
        string model = (Application.Current.MainWindow as MainWindow)?.ComboModels.SelectedItem?.ToString() ?? "C360";
        bool productSuite = !findReference && singleTest == null;
        string serial = productSuite ? GenerateAutoTestId(model) : (findReference ? "REFERENCE_" : "MANUAL_") + DateTime.Now.ToString("HHmmssfff");
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail data", DateTime.Now.ToString("yyyyMMdd"), serial + "_" + DateTime.Now.ToString("HHmmssfff"));
        _autoTestSessionFolder = folder; _autoTestGraphPaths.Clear();
        if (productSuite)
        {
            SetCurrentProduct(null);
            if (Application.Current.MainWindow is MainWindow main) main.TxtSerialNumber.Text = serial;
            TxtProductSerial.Text = serial;
        }
        var buttons = new[] { BtnStart, BtnStartAutoTest, BtnSaveStandardReference, BtnNoiseTest, BtnSaveStandard };
        var enabled = buttons.Select(b => b.IsEnabled).ToArray();
        foreach (var b in buttons) b.IsEnabled = false;
        var applied = new HashSet<C360ElectronicStage>();
        var timer = Stopwatch.StartNew();
        void Notice(string message, string title)
        {
            if (SignalWarningPresenter is { } present) present(message, title);
            else ModernMessageBox.Show(Window.GetWindow(this), message, title, ModernMessageBox.MessageBoxType.Warning);
        }
        try
        {
            var configuration = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(_activeInOutConfig))!;
            if (singleTest != null) foreach (var test in configuration.Tests) test.Enable = test.IsEnabled && test.id == singleTest.id;
            var plan = BuildC360Plan(configuration);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "model.json"), JsonSerializer.Serialize(plan.Configuration, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(folder, "acquisition.json"), JsonSerializer.Serialize(plan.Setup, new JsonSerializerOptions { WriteIndented = true }));
            ClearC360ModelResultsFor(plan.Stages);
            SetC360Verdict("ĐANG KẾT NỐI BLE…", Brushes.Gold);
            // Load the Fourier backend while Windows opens BLE, rather than on the first sweep.
            _ = Task.Run(() => SweepFourier.BackendName);
            bool connected = EnsureC360Connection != null ? await EnsureC360Connection(token)
                : await SoncaBleManager.Instance.EnsureConnectedAsync(plan.Setup.BleControllerAddress, token, userInitiated: true);
            token.ThrowIfCancellationRequested();
            if (!connected) throw new InvalidOperationException("Chưa kết nối được điều khiển BLE của loa. Mở Ctrl+Shift+H để kiểm tra đúng loa trước khi đo.");
            if (EnsureC360Connection == null) RememberC360Controller();
            double connectSeconds = timer.Elapsed.TotalSeconds;
            var bench = C360BenchFactory?.Invoke() ?? new C360AudioBench(_audioEngine, SoncaBleManager.Instance, folder);
            var baseline = await bench.ReadAsync(token);
            string mixerKey = string.Join(";", new ushort[] { 0x6102, 0x6103, 0x6400, 0x6402, 0x6403, 0x6467, 0x6254 }
                .Select(reg => $"{reg:X4}:{baseline.GetValueOrDefault(reg)}"));
            if (C360BenchFactory != null) mixerKey += ";CAPTURE_RATE:" + plan.Setup.SampleRate;
            else
            {
                using var devices = new MMDeviceEnumerator();
                using var capture = devices.GetDevice(plan.Setup.LineOutCaptureId);
                using var client = capture.AudioClient;
                mixerKey += ";CAPTURE_RATE:" + client.MixFormat.SampleRate;
            }
            var referenceBench = new C360ReferenceBench(bench, plan, model, GetStandardsDirectory(), mixerKey, findReference,
                ParseDoubleSafe(TxtFreqTolerance.Text, 3), C360BenchFactory != null ? null
                    : run => new C360AudioBench(_audioEngine, SoncaBleManager.Instance, Path.Combine(folder, "reference-run-" + run)));
            void Apply(C360ElectronicResult result)
            {
                if (!_isExecutingAutoSuite || token.IsCancellationRequested || _discardCurrentMeasurementRequested) return;
                if (result.Status != "RUNNING" && !applied.Add(result.Stage)) return;
                ApplyC360Progress(result, plan);
            }
            var progress = new Progress<C360ElectronicResult>(Apply);
            var results = await new C360ElectronicSuite().RunAsync(plan.Bind(referenceBench), plan.Setup, progress, token, plan.Stages);
            token.ThrowIfCancellationRequested();
            foreach (var result in results) Apply(result);
            bool valid = results.All(r => r.Status is "PASS" or "FAIL"), passed = results.All(r => r.Status == "PASS");
            File.WriteAllText(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(new { model, serial,
                seconds = timer.Elapsed.TotalSeconds, connectSeconds, results = results.Select(r => new { r.StageName, r.Status, r.Summary }) }, new JsonSerializerOptions { WriteIndented = true }));
            _isExecutingAutoSuite = false;
            SetC360Verdict(findReference ? valid && passed ? "LINE CHUẨN ĐỦ" : "LINE CHUẨN CHƯA ĐỦ — KIỂM TRA KẾT QUẢ"
                : valid ? passed ? "ĐẠT" : "KHÔNG ĐẠT" : "CHƯA ĐO ĐỦ — KIỂM TRA TÍN HIỆU", valid ? passed ? Brushes.MediumSpringGreen : Brushes.Salmon : Brushes.Gold);
            TxtMeasurementTiming.Text = $"Hoàn tất {timer.Elapsed.TotalSeconds:F1}s · chuẩn bị BLE {connectSeconds:F1}s";
            AppendLog("Auto Test", $"{model}: {timer.Elapsed.TotalSeconds:F1}s (chuẩn bị BLE {connectSeconds:F1}s); đã đọc lại và khôi phục trạng thái loa.");
            string warnings = string.Join("\n\n", results.Select(r => r.Status is "NO_SIGNAL" or "INVALID" ? r.StageName + ": " + r.Summary
                : r.Stage is C360ElectronicStage.LineIn or C360ElectronicStage.UsbA && MeasurementSignalStatus.LowSignalWarning(r.Measurement) is string warning
                    ? r.StageName + ": " + warning : null).Where(s => s != null));
            if (warnings.Length > 0) Notice(warnings, "Kiểm tra tín hiệu đo");
            token.ThrowIfCancellationRequested();
            if (!findReference && singleTest == null && valid && ChkSendToServer.IsChecked == true) await UploadCompletedAutoTestAsync(serial, model, folder, passed, true);
        }
        catch (OperationCanceledException)
        {
            SetC360Verdict("ĐÃ DỪNG", Brushes.Gray);
            AppendLog("Auto Test", "Đã dừng; chuỗi điều khiển đã hoàn tất khôi phục loa.");
        }
        catch (Exception ex)
        {
            SetC360Verdict("CHƯA HOÀN TẤT", Brushes.Gold); AppendLog("Auto Test", ex.Message);
            Notice(ex.Message, "Auto Test");
        }
        finally
        {
            _audioEngine.StopAllAudio(); _isExecutingAutoSuite = false; _currentRunningTestCase = null;
            if (_discardCurrentMeasurementRequested) DeleteDiscardedAutoTestArtifacts();
            _c360ModelCancellation = null; _autoTestSessionFolder = null;
            foreach (var row in _autoTestCases.Where(r => r.Status == "RUNNING"))
            { row.Status = "WAITING"; row.FreqStatus = "ĐÃ DỪNG"; row.ThdStatus = "CHƯA ĐO"; row.StatusBrush = Brushes.Gray; }
            for (int i = 0; i < buttons.Length; i++) buttons[i].IsEnabled = enabled[i];
            BtnStartAutoTest.IsEnabled = _activeInOutConfig != null;
            _c360ModelFinished = null; finished.TrySetResult();
            _ = Dispatcher.BeginInvoke(new Action(() => (Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(IsSetupVisible)));
        }
    }

    private void ClearC360ModelResults()
        => ClearC360ModelResultsFor(null);

    private void ClearC360ModelResultsFor(IReadOnlyList<C360ElectronicStage>? stages)
    {
        foreach (var row in _autoTestCases)
        {
            if (stages != null && !stages.Contains(C360ModelDefinition.Stage(row.Config))) continue;
            row.Status = "WAITING"; row.FreqStatus = row.ThdStatus = "ĐANG CHỜ";
            row.StatusBrush = row.FreqBrush = row.ThdBrush = Brushes.Gray; row.NoiseStatus = "CHƯA ĐO";
            row.DistortionText = "CHƯA CÓ DỮ LIỆU DISTORTION";
            row.ImpulseResult = null; row.Acquisition = null; row.AdditionalResults = Array.Empty<ConfiguredTestResult>();
            row.FrequencyShift = null;
            row.MeasurementValues = Array.Empty<MeasurementValue>(); row.ResponseFrequencies = row.ResponseLevels = Array.Empty<double>();
            row.UnsmoothedResponse = new Dictionary<double, double>(); row.StandardCurve = new Dictionary<double, double>();
            row.UnsmoothedStandardCurve = new Dictionary<double, double>(); row.DistortionMeasurements = new Dictionary<double, ToneQualityMetrics>();
        }
    }

    private void SetC360Verdict(string text, Brush color)
    {
        LblVerdict.Text = text; LblVerdict.Foreground = color;
        BorderVerdict.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); BorderVerdict.BorderBrush = color;
        TxtFailureDiagnosis.Visibility = Visibility.Collapsed;
    }

    private void ShowC360Review(AutoTestCaseItem row, double frequency)
    {
        TxtThdStatus.Text = "";
        if (row.Config.SpeakerStage != "MicEffects" && row.ImpulseResult is { } m)
        {
            double limit = Math.Abs(frequency - (row.Config.BassThdFrequency ?? 80)) < .01 ? row.Config.BassThdLimit ?? row.Config.ThdLimit ?? .5
                : Math.Abs(frequency - (row.Config.TrebleThdFrequency ?? 4000)) < .01 ? row.Config.TrebleThdLimit ?? row.Config.ThdLimit ?? .5
                : row.Config.MidThdLimit ?? row.Config.ThdLimit ?? .5;
            UpdateSweepDistortionMetricsWithLimit(_testRunner.BuildSweepDistortionSummary(m, frequency), limit, finalVerdict: true);
            TxtSweepDistortionMetrics.Text = $"{row.Id} — {row.Name}\n{row.ThdStatus}\n\n" + TxtSweepDistortionMetrics.Text;
            return;
        }
        TxtSweepDistortionMetrics.Text = $"{row.Id} — {row.Name}\n{row.DistortionText}";
        TxtThdVerdictBadge.Text = row.Config.SpeakerStage == "MicEffects" ? "ECHO / REVERB" : row.Config.SpeakerStage == "MicFrequencyShift" ? "DI TẦN MIC" : "CHƯA CÓ TÍN HIỆU";
        TxtThdVerdictBadge.Foreground = row.StatusBrush;
        BorderThdVerdictBadge.BorderBrush = row.StatusBrush;
    }

    private void ApplyC360Progress(C360ElectronicResult result, C360ModelPlan plan)
    {
        var config = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == result.Stage);
        var row = _autoTestCases.Single(t => t.Id == config.id);
        row.Config = config; row.Status = result.Status is "NO_SIGNAL" or "INVALID" ? "INVALID" : result.Status;
        row.StatusBrush = result.Status == "PASS" ? Brushes.MediumSpringGreen : result.Status == "FAIL" ? Brushes.Salmon : Brushes.Gold;
        if (result.Status == "RUNNING")
        {
            _currentRunningTestCase = row; row.FreqStatus = row.ThdStatus = "ĐANG ĐO";
            SetC360Verdict(row.Name + " — " + result.Summary, Brushes.Gold); return;
        }
        row.ImpulseResult = result.Measurement;
        row.StandardCurve = result.Reference?.Curve ?? new Dictionary<double, double>();
        row.UnsmoothedStandardCurve = new Dictionary<double, double>(row.StandardCurve);
        row.FrequencyShift = result.FrequencyShift;
        row.FreqStatus = result.Measurement == null || result.Status == "NO_SIGNAL" ? "CHƯA CÓ TÍN HIỆU" : "ĐÃ ĐO dBFS";
        if (result.Summary.StartsWith("ĐÃ CÓ LINE CHUẨN", StringComparison.Ordinal)) row.FreqStatus = "ĐÃ CÓ LINE CHUẨN";
        if (result.Summary.StartsWith("ĐÃ LƯU LINE CHUẨN", StringComparison.Ordinal)) row.FreqStatus = "ĐÃ LƯU LINE CHUẨN";
        row.FreqBrush = row.ThdBrush = row.StatusBrush;
        row.ThdStatus = result.Status == "NO_SIGNAL" ? "KHÔNG CÓ TÍN HIỆU THỬ" : result.Summary.Split('\n')[0];
        row.DistortionText = result.Summary;
        row.NoiseStatus = result.Measurement is { } measured ? $"SNR {measured.EstimatedSnrDb:F1} dB" : "CHƯA ĐO";
        if (result.Stage == C360ElectronicStage.MicEffects)
            row.AdditionalResults = new[] { new ConfiguredTestResult("Echo / Reverb", result.Status, result.Summary) };
        if (result.Stage == C360ElectronicStage.MicFrequencyShift)
        {
            row.FreqStatus = "KHÔNG ÁP DỤNG — MULTITONE";
            row.ThdStatus = result.FrequencyShift is { IsValid: true } measuredShift
                ? $"FBX bật {measuredShift.OnShiftHz:+0.00;-0.00;0.00} Hz · tắt {measuredShift.OffShiftHz:+0.00;-0.00;0.00} Hz · đổi {measuredShift.ShiftHz:+0.00;-0.00;0.00} Hz"
                : "CHƯA XÁC ĐỊNH — kiểm tra tone thử";
            var values = new List<MeasurementValue> { new("Bài đo", row.Name), new("Ngõ phát / thu", config.PlaybackOut + " → " + config.RecordingIn), new("Kết quả", result.Summary) };
            if (result.FrequencyShift is { } shift)
            {
                if (shift.IsValid)
                {
                    values.Add(new("Dịch tần FBX BẬT − TẮT", $"{shift.ShiftHz:+0.000;-0.000;0.000} ±{shift.UncertaintyHz:F2} Hz"));
                    values.Add(new("Di tần MIC khi FBX TẮT / BẬT", $"{shift.OffShiftHz:+0.000;-0.000;0.000} ±{shift.OffUncertaintyHz:F2} Hz / {shift.OnShiftHz:+0.000;-0.000;0.000} ±{shift.OnUncertaintyHz:F2} Hz"));
                    values.Add(new("Clock tương đối / sai số fit", $"{shift.RelativeClockPpm:F2} ppm / {shift.FitResidualHz:F3} Hz"));
                }
                foreach (var p in shift.Off.Concat(shift.On)) values.Add(new((shift.Off.Contains(p) ? "FBX tắt" : "FBX bật") + $" · {p.NominalHz:F0} Hz", $"{p.MeasuredHz:F3} Hz · ổn định {p.StabilityHz:F3} Hz · {p.LevelDbFs:F1} dBFS · nổi trên đỉnh nền cục bộ {p.NarrowBandSnrDb:F1} dB"));
                foreach (var p in shift.ToneLevelChanges)
                    values.Add(new($"Mức tone BẬT − TẮT · {p.FrequencyHz:0} Hz", $"{p.ChangeDb:+0.0;-0.0;0.0} dB ({p.OffDbFs:F1} → {p.OnDbFs:F1} dBFS)"));
                foreach (var a in shift.CaptureAttempts)
                    values.Add(new($"Thu FBX {(a.FbxEnabled ? "bật" : "tắt")} · lượt {a.Attempt}", a.Analysis.IsValid ? "Đủ chất lượng" : string.Join(" ", a.Analysis.Problems)));
                if (shift.IsValid && shift.Off.Count == 3 && shift.On.Count == 3)
                {
                    row.ResponseFrequencies = shift.Off.Select(p => p.NominalHz).ToArray();
                    row.ResponseLevels = shift.On.Select(p => p.MeasuredHz - p.NominalHz * (1 + shift.OnClockPpm / 1e6)).ToArray();
                    row.ResponseLevelUnit = "Hz";
                }
                var setup = plan.SetupFor(config);
                row.Acquisition = new(setup.LevelDbFs, setup.SampleRate, shift.SampleRate, false, setup.AnalogPlaybackChannel,
                    setup.CaptureChannel, setup.AnalogPlaybackId, setup.LineOutCaptureId, setup.ThdLimitPercent, DateTimeOffset.UtcNow);
            }
            row.MeasurementValues = values;
            DisplayAutoTestResult(row);
            if (row.ResponseFrequencies.Length > 0 && _autoTestSessionFolder != null)
            {
                string path = Path.Combine(_autoTestSessionFolder, row.Id + "-frequency-shift.png");
                SaveServerGraphPng(PlotFreqResponse.Plot, path); _autoTestGraphPaths.Add(path);
            }
        }
        if (result.Measurement is { } m)
        {
            var setup = plan.SetupFor(config);
            var response = m.FrequencyResponse;
            row.ResponseFrequencies = response.Select(p => p.FrequencyHz).ToArray();
            row.ResponseLevels = response.Select(p => p.LevelDb + m.ExcitationRmsDbFs).ToArray();
            row.ResponseLevelUnit = "dBFS";
            row.UnsmoothedResponse = row.ResponseFrequencies.Zip(row.ResponseLevels).ToDictionary(p => p.First, p => p.Second);
            row.Acquisition = new(setup.PlaybackLevel(result.Stage), setup.SampleRate, m.SampleRate, false,
                setup.AnalogPlaybackChannel, setup.CaptureChannel, setup.PlaybackId(result.Stage), setup.LineOutCaptureId, setup.ThdLimitPercent, DateTimeOffset.UtcNow);
            row.MeasurementValues = new MeasurementValue[] { new("Bài đo", row.Name), new("Ngõ phát", config.PlaybackOut), new("Ngõ thu", config.RecordingIn),
                new("Mức phát", $"{setup.PlaybackLevel(result.Stage):F1} dBFS peak"), new("Sample rate phát / thu", $"{setup.SampleRate} / {m.SampleRate} Hz"),
                new("RMS / noise floor", $"{m.SignalLevelDbFs:F2} / {m.NoiseFloorDbFs:F2} dBFS"), new("SNR", $"{m.EstimatedSnrDb:F2} dB"),
                new("Kết quả", result.Summary), new("Hợp lệ", m.Validity), new("Clipping", m.IsClipped ? "CÓ" : "Không") };
            DisplayAutoTestResult(row); UpdateSampleRateWarning(); SaveFailureScreenshots(row.Id, forceAlways: true);
        }
        AppendLog("Auto Test", row.Name + ": " + result.Summary);
    }
}
