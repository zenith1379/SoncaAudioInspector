using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private StandardAcousticResult? _inlineImpulseResult;
    private bool ShowInlineImpulse(AutoTestCaseItem? row)
    {
        if (ImpulseEffectHost == null) return false;
        bool show = _allowInlineEffectImpulse && row?.Config.SpeakerStage == "MicEffects" && row.ImpulseResult != null;
        ImpulseEffectHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlotFreqResponse.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            HideFrequencyHover();
            if (!ReferenceEquals(_inlineImpulseResult, row!.ImpulseResult))
            { ImpulseEffectHost.Content = new ImpulseAnalysisView(row.ImpulseResult!); _inlineImpulseResult = row.ImpulseResult; }
            TxtFrequencyChartHeading.Text = row!.Id + " · IMPULSE / ETC / SCHROEDER";
        }
        return show;
    }
    private MeasurementDetailsWindow? _measurementDetailsWindow;
    private IReadOnlyList<MeasurementValue>? _lineAcquisitionValues;
    private Window? _measurementShortcutHost;

    private void HandleMeasurementShortcut(object sender, KeyEventArgs e)
    {
        if (TryHandleMeasurementShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, () => ShowMeasurementDetails())) e.Handled = true;
    }

    private static bool TryHandleMeasurementShortcut(Key key, ModifierKeys modifiers, bool repeat, Action show)
    {
        if (key != Key.I || modifiers != (ModifierKeys.Control | ModifierKeys.Shift) || repeat) return false;
        show();
        return true;
    }

    private void ShowMeasurementDetails(bool pending = false)
    {
        if (_measurementDetailsWindow == null)
        {
            _measurementDetailsWindow = new MeasurementDetailsWindow { Owner = Window.GetWindow(this), DiagnosticRunner = RunMechanicalDiagnosticAsync,
                SignalDiagnosticRunner = RunSignalDiagnosticAsync, ImpedanceFactory = () => new ImpedanceView(RunImpedanceCaptureAsync, ImpedanceContext, GetUserDataDirectory()) };
            _measurementDetailsWindow.Closed += (_, _) => _measurementDetailsWindow = null;
            _measurementDetailsWindow.Show();
        }
        _measurementDetailsWindow.Activate();
        UpdateMeasurementDetails(pending);
    }

    private async Task<MechanicalTestResult> RunMechanicalDiagnosticAsync(MechanicalTestRequest request, System.Threading.CancellationToken cancellationToken)
    {
        request.Validate();
        CloseRoutingScope();
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null) throw new InvalidOperationException("Thiết bị đang đo Line/Auto Test hoặc quy trình khác. Chờ hoàn tất trước khi phát tone.");
        StopRoutingHeadroomMonitor();
        var playback = SelectedPlaybackDevice ?? throw new InvalidOperationException("Chưa chọn ngõ phát.");
        var recording = SelectedRecordingDevice ?? throw new InvalidOperationException("Chưa chọn ngõ thu.");
        return await CaptureMechanicalDiagnosticAsync(request, playback, recording, cancellationToken);
    }

    // Caller owns the routing workflow: manual diagnostic or the whole Auto Test suite.
    private async Task<MechanicalTestResult> CaptureMechanicalDiagnosticAsync(MechanicalTestRequest request,
        NAudio.CoreAudioApi.MMDevice playback, NAudio.CoreAudioApi.MMDevice recording, System.Threading.CancellationToken cancellationToken)
    {
        var data = await CaptureMechanicalSamplesAsync(request, playback, recording, cancellationToken);
        if (data.PrebuiltUnavailable != null) return data.PrebuiltUnavailable;
        var result = await Task.Run(() => MechanicalDiagnostics.Analyze(data.Captured!, data.Background!, data.SampleRate, request), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Acquisition = data.Route };
    }

    private async Task<MechanicalCaptureData> CaptureMechanicalSamplesAsync(MechanicalTestRequest request,
        NAudio.CoreAudioApi.MMDevice playback, NAudio.CoreAudioApi.MMDevice recording, System.Threading.CancellationToken cancellationToken)
    {
        request.Validate();
        if (!_audioEngine.UseExclusivePlayback && AudioEngine.IsWindowsMonoAudioEnabled())
            throw new InvalidOperationException("Tắt Windows Mono audio trước khi đo để giữ đúng kênh phát/thu.");
        string route = $"Out: {playback.FriendlyName} / {(_audioEngine.PlaybackChannel is int outChannel ? (outChannel + 1).ToString() : "L+R")} · In: {recording.FriendlyName} / {(_audioEngine.RecordingChannel is int inChannel ? (inChannel + 1).ToString() : "Trộn")}";
        try
        {
            var background = await _audioEngine.CaptureSilenceAsync(recording, null, .5, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            double noiseRms = Math.Sqrt(background.DutSamples.Sum(v => (double)v * v) / Math.Max(1, background.DutSamples.Length));
            if (background.DutSamples.Length < background.DutSampleRate / 4 || !double.IsFinite(noiseRms)
                || 20 * Math.Log10(Math.Max(1e-12, noiseRms)) > -55 || background.DutSamples.Any(v => Math.Abs(v) >= .05))
                return new(null, background.DutSamples, background.DutSampleRate, route, MechanicalTestResult.Unavailable(request.Kind, "NỀN CHƯA ĐẠT", "Nền cao, thiếu dữ liệu hoặc có xung ngoại lai; chưa phát tone. Chờ yên rồi đo lại.") with { Acquisition = route });
            float[] stimulus = MechanicalDiagnostics.GenerateTone(request, _audioEngine.PlaybackSampleRate);
            float[] captured = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, request.FrequencyHz,
                request.DurationSeconds, cancellationToken: cancellationToken, recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: stimulus);
            int rate = _audioEngine.RecordingSampleRate;
            cancellationToken.ThrowIfCancellationRequested();
            if (rate != background.DutSampleRate) throw new InvalidOperationException("Định dạng ngõ thu đã đổi giữa lượt nền/tone; đo lại.");
            return new(captured, background.DutSamples, rate, $"{route} · phát {_audioEngine.PlaybackSampleRate} Hz / thu {rate} Hz · {request.FrequencyHz:0.##} Hz / {request.LevelDbFs:0} dBFS peak / {request.DurationSeconds:0.0} s");
        }
        finally { _audioEngine.StopAllAudio(); }
    }

    private void UpdateMeasurementDetails(bool pending = false)
    {
        bool discarded = _discardCurrentMeasurementRequested && !pending;
        if (_isExecutingAutoSuite && _reviewedAutoTestCase != null && !discarded) pending = false;
        if (!pending && !discarded && _reviewedAutoTestCase is { Config.SpeakerStage: "MicFrequencyShift" } frequencyShift)
        {
            TxtMeasurementSummary.Text = frequencyShift.ThdStatus;
            if (_measurementDetailsWindow != null)
            {
                _measurementDetailsWindow.IsMeasurementPending = false;
                _measurementDetailsWindow.Update("MIC IN · Di tần (FBX) · " + frequencyShift.StatusDisplay,
                    frequencyShift.MeasurementValues, new Dictionary<double,double>(), null);
            }
            return;
        }
        var sweep = pending || discarded ? null : _reviewedAutoTestCase != null ? _reviewedAutoTestCase.ImpulseResult : _testRunner.LastSweepResult;
        if (TxtMeasurementSummary != null)
            TxtMeasurementSummary.Text = discarded ? "Đã hủy / đã xóa kết quả." : pending ? "Đang đo…" : sweep == null ? "Chưa có sweep"
                : $"RMS {sweep.SignalLevelDbFs:0.0} dBFS · SNR {sweep.EstimatedSnrDb:0.0} dB · delay {sweep.DirectArrivalMs:0.00} ms · "
                    + (sweep.ImpulseEffects is { IsValid: true } e ? e.Describe(_reviewedAutoTestCase?.Config.ImpulseEffects ?? _testRunner.ImpulseEffectsSettings) : "effect chưa đủ dữ liệu");
        if (_measurementDetailsWindow == null) return;
        var measured = pending || discarded ? new Dictionary<double, double>() : _reviewedAutoTestCase != null
            ? _reviewedAutoTestCase.ResponseFrequencies.Zip(_reviewedAutoTestCase.ResponseLevels).ToDictionary(p => p.First, p => p.Second)
            : _freqs.Zip(_dbValues).ToDictionary(p => p.First, p => p.Second);
        var rows = discarded ? Array.Empty<MeasurementValue>() : !pending && _reviewedAutoTestCase != null
            ? _reviewedAutoTestCase.MeasurementValues : BuildMeasurementValues(sweep, pending);
        string status = discarded ? "Đã hủy / đã xóa kết quả — lượt này không được sử dụng."
            : pending ? "Đang thu/phân tích — thông số chưa đo sẽ hiện Chưa đo."
            : $"{(_isFindingStandardReference ? "Line chuẩn" : "Line")} · {_referenceRouteKey} · {sweep?.Validity ?? "chưa có sweep"} · đáp tuyến 1/24 octave, 20 Hz–20 kHz";
        _measurementDetailsWindow.IsMeasurementPending = pending && !discarded;
        _measurementDetailsWindow.Update(status, rows, measured, sweep);
        _measurementDetailsWindow.UpdatePolarity(sweep != null && _polaritySnapshots.TryGetValue(sweep, out var polarity) ? polarity.Result : PolarityDiagnosticResult.Compare(sweep, "", null));
    }

    private IReadOnlyList<MeasurementValue> BuildAcquisitionValues()
    {
        var rows = new List<MeasurementValue>();
        void Add(string name, string value) => rows.Add(new(name, value));
        Add("Route / bài đo", _referenceRouteKey);
        Add("Ngõ phát", SelectedPlaybackDevice?.FriendlyName ?? "Chưa chọn");
        Add("Ngõ thu", (ComboRecording.SelectedItem as DeviceItem)?.Device.FriendlyName ?? "Chưa chọn");
        Add("Định dạng phát (thiết bị / Shared)", DescribeDeviceFormat(SelectedPlaybackDevice, playback: true));
        Add("Định dạng thu (thiết bị / Shared)", DescribeDeviceFormat(SelectedRecordingDevice, playback: false));
        Add("Kênh phát / thu", $"{(_audioEngine.PlaybackChannel.HasValue ? (_audioEngine.PlaybackChannel.Value + 1).ToString() : "Cả hai")} / {(_audioEngine.RecordingChannel.HasValue ? (_audioEngine.RecordingChannel.Value + 1).ToString() : "Cả hai")}");
        Add("Sample rate phát", $"{_audioEngine.PlaybackSampleRate} Hz");
        Add("Chuẩn sample rate đối chiếu", "44100 Hz cho các thiết bị");
        string rateWarning = BuildSampleRateWarning(_audioEngine.PlaybackSampleRate,
            ReadEndpointSampleRate(SelectedPlaybackDevice), ReadEndpointSampleRate(SelectedRecordingDevice), null);
        if (rateWarning.Length > 0) Add("Cảnh báo sample rate trước đo", rateWarning);
        Add("Length / thời gian sweep", $"{Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate):0} mẫu / {_testRunner.LogSweepDurationSeconds:0.000000} s");
        Add("Thời gian thu đuôi yêu cầu", $"{_testRunner.ImpulseEffectsSettings.CaptureTailSeconds:0.000} s");
        Add("Mức phát / Recording Gain số", $"{PlaybackLevelDbfs:0.00} dBFS peak / {_audioEngine.RecordingGain:0.00}x");
        Add("Chế độ / đơn vị / so sánh", $"{(_audioEngine.UseExclusivePlayback ? "Exclusive" : "WASAPI Shared")} / {(_testRunner.NormalizeFrequencyResponseToOneKilohertz ? "dBr chuẩn hóa 1k" : "dBFS không chuẩn hóa")} / 1/24, 20–20000 Hz");
        string distortionCalibration = !_testRunner.ApplyDistortionCalibration ? "Không áp dụng"
            : _testRunner.FeqMicrophoneCalibration is { } mic ? "Áp dụng " + mic.Name : "Đã bật nhưng không có file mic cho ngõ thu — không bù";
        Add("Calibration đáp tuyến / Distortion", $"{_testRunner.FeqMicrophoneCalibration?.Name ?? "Không"} / {distortionCalibration}");
        Add("Tham chiếu THD", _testRunner.UseHarmonicFrequencyAsDistortionReference ? "Tại tần số hài" : "Tại fundamental");
        Add("Âm lượng chung model", _activeInOutConfig?.TestVolume is {} volume ? JsonSerializer.Serialize(volume) : "Theo từng bài đo");
        Add("Windows phát / thu thực tế", $"{SelectedPlaybackDevice?.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0.##}% / {SelectedRecordingDevice?.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0.##}%");
        return rows;
    }

    private IReadOnlyList<MeasurementValue> BuildMeasurementValues(StandardAcousticResult? sweep, bool pending)
    {
        if (!pending && _reviewedAutoTestCase?.MeasurementValues is { Count: > 0 } saved) return saved;
        var rows = new List<MeasurementValue>(_lineAcquisitionValues ?? BuildAcquisitionValues());
        void Add(string name, string value) => rows.Add(new(name, value));
        if (!pending && !string.IsNullOrEmpty(_reviewedAutoTestCase?.SingleRunEarlyPassSummary ?? _testRunner.SingleRunEarlyPassSummary))
            Add("Chính sách 1 lượt", _reviewedAutoTestCase?.SingleRunEarlyPassSummary ?? _testRunner.SingleRunEarlyPassSummary);
        if (!pending && _testRunner.LastResponseLimitAssessment is { } responseAssessment)
            Add("Điểm LINE ngoài biên chuẩn", responseAssessment.Describe());
        if (DisplayedResponseLimitSettings() is { } responseSettings && responseSettings.DipWindows.Count > 0)
            Add("Khoảng xét hõm", responseSettings.DescribeWindows());
        if (!pending && _currentRunningTestCase != null)
            foreach (var result in _currentRunningTestCase.AdditionalResults) Add(result.Name + " — " + result.Status, result.Reason);
        static string N(double? value, string unit = "", string format = "0.000") => value is double v && double.IsFinite(v) ? v.ToString(format) + unit : "Chưa đo / không khả dụng";
        Add("Sample rate thu thực", sweep != null ? sweep.SampleRate + " Hz" : "Chưa mở thu");
        if (!pending && _testRunner.LastPreflightWarning.Length > 0) Add("Cảnh báo trước đo", _testRunner.LastPreflightWarning);
        if (sweep != null && sweep.SampleRate != StandardSampleRate)
            Add("Cảnh báo sample rate bản thu", $"Lượt thu được phân tích ở {sweep.SampleRate} Hz, khác chuẩn đối chiếu 44100 Hz. Default Format của thiết bị và định dạng xử lý Shared có thể khác nhau.");
        Add("RMS / Noise floor", N(sweep?.SignalLevelDbFs, " dBFS") + " / " + N(sweep?.NoiseFloorDbFs, " dBFS"));
        Add("RMS tín hiệu sweep phát", N(sweep?.ExcitationRmsDbFs, " dBFS"));
        Add("SNR", N(sweep?.EstimatedSnrDb, " dB"));
        Add("Peak thu / peak trước gain", N(sweep?.PeakSample) + " / " + N(sweep?.EstimatedPreGainPeak));
        Add("Clipping", sweep == null ? "Chưa đo" : sweep.IsClipped ? "CÓ — không dùng kết quả" : "Không");
        double? oneKhz = pending ? null : _testRunner.LastOneKilohertzLevelDbFs;
        Add("Mức 1 kHz / chuẩn / độ lệch", N(oneKhz, " dBFS") + " / " + N(_standardOneKilohertzLevelDbFs, " dBFS") + " / " + N(oneKhz - _standardOneKilohertzLevelDbFs, " dB"));
        Add("Max lệch đáp tuyến", N(pending ? null : _testRunner.LastFrequencyMaxDeviationDb, " dB"));
        Add("Direct arrival / cực tính", N(sweep?.DirectArrivalMs, " ms") + " / " + (sweep?.Polarity ?? "Chưa đo"));
        Add("Direct sample / hợp lệ", N(sweep?.DirectArrivalSample, " mẫu", "0") + " / " + (sweep?.HasPlausibleDirectArrival.ToString() ?? "Chưa đo"));
        Add("Tương quan sweep / độ nổi IR", N(sweep?.SweepCorrelation) + " / " + N(sweep?.ImpulsePeakToBackgroundDb, " dB"));
        Add("Clock / sai số fit / đã bù", N(sweep?.EstimatedClockDriftPpm, " ppm") + " / " + N(sweep?.ClockFitRmsSamples, " mẫu") + " / " + (sweep?.ClockDriftCorrectionApplied.ToString() ?? "Chưa đo"));
        Add("IR trái / phải / FDW", N(sweep?.ImpulseWindowLeftMs, " ms") + " / " + N(sweep?.ImpulseWindowRightMs, " ms") + " / "
            + (sweep == null ? "Chưa đo" : sweep.FrequencyDependentWindowing ? N(sweep.FrequencyDependentWindowCycles, " chu kỳ") : "Tắt")
            + (sweep == null ? "" : " · " + sweep.ImpulseWindowKind));
        Add("Tần số thấp nhất tin cậy theo IR", N(sweep?.GatedMinimumFrequencyHz, " Hz"));
        Add("C50 / C80 / D50", N(sweep?.ClarityC50Db, " dB") + " / " + N(sweep?.ClarityC80Db, " dB") + " / " + N(sweep?.DefinitionD50Percent, "%"));
        Add("C20 / Centre time TS", N(sweep?.ImpulsePlot?.ClarityC20Db, " dB") + " / " + N(sweep?.ImpulsePlot?.CentreTimeSeconds, " s"));
        foreach (var decay in sweep?.DecayEstimates ?? Array.Empty<DecayEstimate>()) Add(decay.Name, N(decay.Rt60Seconds, " s") + $" · R² {decay.RSquared:0.000} · {decay.Status}");
        foreach (var band in sweep?.OctaveBandRt60 ?? Array.Empty<BandRt60Result>()) Add($"Decay band {band.FrequencyHz:0} Hz", $"EDT {N(band.EdtSeconds, " s")} / T20 {N(band.T20Seconds, " s")} / T30 {N(band.T30Seconds, " s")} · hợp lệ {band.IsValid}");
        if (sweep?.ImpulseEffects is { } effects)
        {
            var effectSettings = _reviewedAutoTestCase?.Config.ImpulseEffects ?? _testRunner.ImpulseEffectsSettings;
            Add("Echo/reverb trong bản thu", effects.Describe(effectSettings));
            if (effects.ReverbVerification is { } verification)
            {
                Add("Đối chứng reverb khô/ướt", verification.Summary);
                foreach (var band in verification.Bands) Add($"Đuôi đối chứng {band.LowHz:0}–{band.HighHz:0} Hz",
                    $"{band.DurationMs:0} ms · nổi nền {band.TailSnrDb:0.0} dB · chênh khô/ướt {band.WetDryDifferenceDb:0.0} dB · R² {band.RSquared:0.000}");
            }
            Add("Dải phân tích reverb", effects.ReverbAnalysisBands);
            Add("Đuôi liên tục / độ nổi trên nền / độ phủ vùng đuôi", N(effects.DiffuseTailDurationMs, " ms") + " / "
                + N(effects.DiffuseTailSnrDb, " dB") + " / " + effects.ActiveTailOccupancy.ToString("P0"));
            Add("Năng lượng đuôi ngoài echo / độ phủ toàn cửa sổ", N(effects.DiffuseTailEnergyDb, " dBr") + " / " + effects.TailOccupancy.ToString("P0"));
            Add("IR noise / đuôi thu thực / bị cắt", N(effects.ImpulseNoiseDb, " dBr") + " / " + N(effects.CapturedTailSeconds, " s") + " / " + effects.TailTruncated);
            foreach (var tap in effects.EchoTaps) Add("Echo tap", N(tap.DelayMs, " ms") + " / " + N(tap.LevelDb, " dBr theo direct"));
            if (_standardImpulseEffects != null && _reviewedAutoTestCase?.Config.SpeakerStage == null)
                foreach (string note in _standardImpulseEffects.Compare(effects, effectSettings)) Add("So effect với line chuẩn", note);
        }
        foreach (var point in pending ? new Dictionary<double, ToneQualityMetrics>() : _testRunner.LastToneQualities)
            Add($"Distortion {point.Key:0} Hz", $"f0 {N(point.Value.FundamentalFrequencyHz, " Hz")} / THD {N(point.Value.ThdPercent, "%")} / THD+N {N(point.Value.ThdNPercent, "%")} / SINAD {N(point.Value.SinadDb, " dB")} / SNR {N(point.Value.SnrDb, " dB")} / DC {N(point.Value.DcOffset)} · hợp lệ {point.Value.IsValid}");
        Add("THD+N / SINAD / IMD từ log-sweep", "Không suy ra từ sweep; chỉ hiển thị khi có phép đo tương ứng.");
        if (sweep?.RewDistortion is { } stats)
            Add("THD sweep trung bình / cực đại", N(stats.AverageThdPercent, "%") + " / " + N(stats.MaxThdPercent, "%") + " tại " + N(stats.MaxThdFrequencyHz, " Hz") + " · hài trội " + stats.DominantHarmonic);
        if (sweep?.HighOrderHarmonics is { } highOrders)
            Add("H10–H35 / Rub & Buzz", highOrders.Points.Count == 0 ? highOrders.Note
                : $"ESS {highOrders.SweepSeconds:0.000} s · đủ băng thông H10–H35 đến f0 {highOrders.FullOrderMaximumFundamentalHz:0.#} Hz. Xem tab H10–H35; không tự kết luận lỗi loa.");
        if (!string.IsNullOrWhiteSpace(sweep?.Warning)) Add("Cảnh báo bản thu", sweep.Warning);
        foreach (var diagnostic in sweep?.Diagnostics ?? Array.Empty<MeasurementDiagnostic>()) Add(diagnostic.Severity + " · " + diagnostic.Code, diagnostic.Message + " " + diagnostic.Action);
        return rows;
    }

    private IReadOnlyDictionary<double, double>? GetDisplayedStandardCurve()
    {
        if (!_showUnsmoothedResponse) return _reviewedAutoTestCase?.StandardCurve ?? _standardCurve;
        var raw = _reviewedAutoTestCase?.UnsmoothedStandardCurve ?? _rawStandardCurve;
        return raw.Count > 0 ? raw : null;
    }

    private void SetupResponseContextMenu()
    {
        var menu = new ContextMenu();
        var smoothed = new MenuItem { Header = "1/24 octave — so sánh 20 Hz–20 kHz", IsCheckable = true, IsChecked = true };
        var raw = new MenuItem { Header = "No smoothing — dữ liệu trước làm mượt", IsCheckable = true };
        smoothed.Click += (_, _) => SetResponseSmoothing(false);
        raw.Click += (_, _) => SetResponseSmoothing(true);
        menu.Items.Add(smoothed); menu.Items.Add(raw);
        menu.Opened += (_, _) =>
        {
            smoothed.IsChecked = !_showUnsmoothedResponse; raw.IsChecked = _showUnsmoothedResponse;
            raw.IsEnabled = (_reviewedAutoTestCase?.UnsmoothedResponse ?? _rawResponse).Count > 0
                || (_reviewedAutoTestCase?.UnsmoothedStandardCurve ?? _rawStandardCurve).Count > 0;
        };
        PlotFreqResponse.ContextMenu = menu;
        PlotFreqResponse.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        PlotFreqResponse.PreviewMouseRightButtonUp += (_, e) =>
        {
            menu.PlacementTarget = PlotFreqResponse; menu.IsOpen = true; e.Handled = true;
        };
    }

    private void SetResponseSmoothing(bool unsmoothed)
    {
        _showUnsmoothedResponse = unsmoothed;
        UpdateFreqResponseChart();
    }

    private void ShowImpulseAnalysis()
    {
        var result = _reviewedAutoTestCase != null ? _reviewedAutoTestCase.ImpulseResult : _testRunner.LastSweepResult;
        if (result == null) return;
        new ImpulseAnalysisWindow(result) { Owner = Window.GetWindow(this) }.Show();
    }

    private void LoadRawStandardCurve(string csvPath, string basis, bool normalize)
    {
        _rawStandardCurve = new Dictionary<double, double>();
        _standardImpulseEffects = null;
        _standardPolarityReference = null;
        string path = Path.ChangeExtension(csvPath, ".json");
        if (!File.Exists(path)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (!root.TryGetProperty("analysisId", out var analysis) || analysis.GetString() != ResponseSmoothing.AnalysisId
                || root.GetProperty("levelBasis").GetString() != basis
                || root.GetProperty("csvSha256").GetString() != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csvPath)))
                || !root.TryGetProperty("rawPoints", out var points)) return;
            var curve = new Dictionary<double, double>();
            foreach (var point in points.EnumerateArray())
            {
                double f = point.GetProperty("frequencyHz").GetDouble(), value = point.GetProperty("levelDb").GetDouble();
                if (f > 0 && double.IsFinite(f) && double.IsFinite(value)) curve[f] = value;
            }
            if (normalize && _standardOneKilohertzLevelDbFs is double reference)
                curve = curve.ToDictionary(p => p.Key, p => p.Value - reference);
            _rawStandardCurve = curve;
            if (root.TryGetProperty("impulseEffectsReference", out var effects) && effects.ValueKind == JsonValueKind.Object)
                _standardImpulseEffects = effects.Deserialize<ImpulseEffectReference>();
            if (root.TryGetProperty("polarityReference", out var polarity) && polarity.ValueKind == JsonValueKind.Object)
                _standardPolarityReference = polarity.Deserialize<SweepPolarityReference>();
        }
        catch (Exception ex) { AppendLog("Line gốc", "Không đọc được dữ liệu trước làm mượt: " + ex.Message); }
    }
}
