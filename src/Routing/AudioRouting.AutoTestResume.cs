using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private sealed class C360AutoTestResumeSession(string model, string configuration, string serial, string folder, AutoTestCaseItem[] cases) : C360BleRestoreState
    {
        public string Model { get; } = model;
        public string Configuration { get; } = configuration;
        public string Serial { get; } = serial;
        public string Folder { get; } = folder;
        public AutoTestCaseItem[] Cases { get; } = cases;
        public string? MixerKey { get; set; }
        public int Continuations { get; set; }
        public Dictionary<C360ElectronicStage, C360ElectronicResult> Completed { get; } = new();
    }

    private C360AutoTestResumeSession? _c360AutoTestResumeSession;
    private bool _allowInlineEffectImpulse = true;
    private bool _autoTestInterrupted;

    public void InterruptMeasurementForEndpointLoss()
    {
        if (!_isExecutingAutoSuite) { CancelAndDiscardCurrentMeasurement(); return; }
        _autoTestInterrupted = true;
        _c360ModelCancellation?.Cancel();
        _testRunner?.Cancel();
        _audioEngine.StopAllAudio();
        // The running workflow still owns the endpoints until its finally/restore completes.
        if (_c360ModelCancellation == null) _isExecutingAutoSuite = false;
        AppendLog("Auto Test", "Thiết bị bị ngắt; giữ kết quả hoàn tất để tiếp tục sau khi kết nối lại.");
    }

    private string AutoTestConfigurationKey() => JsonSerializer.Serialize(_activeInOutConfig)
        + "|" + ParseDoubleSafe(TxtFreqTolerance.Text, 3).ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private C360AutoTestResumeSession? GetC360AutoTestResumeSession(string model)
    {
        var saved = _c360AutoTestResumeSession;
        return saved != null && saved.Model == model && saved.Configuration == AutoTestConfigurationKey()
            && Directory.Exists(saved.Folder) && saved.Cases.SequenceEqual(_autoTestCases)
            ? saved : null;
    }

    private void UpdateAutoTestStartButton()
    {
        if (BtnStartAutoTest == null) return;
        bool resume = _c360AutoTestResumeSession is { } saved && GetC360AutoTestResumeSession(saved.Model) != null
            || _autoTestResumeSession is { } generic && CanResumeAutoTestSession(generic.Model);
        bool pendingQa = _pendingQaCompletionSession != null;
        BtnStartAutoTest.Content = pendingQa ? "GỬI LẠI QA — ĐO XONG, QA CHƯA XONG" : resume ? "TIẾP TỤC ĐO TỰ ĐỘNG" : "BẮT ĐẦU ĐO TỰ ĐỘNG";
        BtnStartAutoTest.Background = pendingQa || resume ? Brushes.DarkOrange : new SolidColorBrush(Color.FromRgb(59, 130, 246));
        BtnStartAutoTest.ToolTip = pendingQa ? "Hoàn tất ảnh và gửi lại QA từ dữ liệu đã đo, không phát lại âm thanh." : resume
            ? "Tiếp tục bài chưa hoàn tất, giữ kết quả đã đo. Muốn đo lại từ đầu, bấm Dừng / Xóa kết quả ở trên."
            : "Bắt đầu phiên đo tự động mới.";
    }

    private bool CanResumeAutoTestSession(string model)
    {
        var saved = _autoTestResumeSession;
        return saved != null && saved.Model == model && Directory.Exists(saved.Folder)
            && saved.Cases.SequenceEqual(_autoTestCases)
            && Math.Abs(saved.FrequencyToleranceDb - ParseDoubleSafe(TxtFreqTolerance.Text, 3)) <= .001
            && saved.Cases.Any(t => t.Status != "PASS");
    }
}
