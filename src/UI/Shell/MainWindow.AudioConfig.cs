using System.Diagnostics;

namespace SoncaAudioInspector;

public partial class MainWindow
{
    private bool _modelEditorOpen;
    private async Task LoadSharedAudioConfigsAsync()
    {
        if (!ServerEngine.HasValidApiKey) return;
        try
        {
            var models = await ServerEngine.GetAudioModelConfigsAsync();
            if (_modelEditorOpen || _audioRoutingView.IsTestingBusy || _checkingConfig == null) return;
            bool changed = false;
            foreach (var incoming in models)
            {
                if (!IsSupportedModel(incoming.model)) continue;
                var existing = _checkingConfig.models.FirstOrDefault(m => string.Equals(m.model, incoming.model, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing.AudioConfigUpdatedUtc >= incoming.AudioConfigUpdatedUtc) continue;
                if (existing == null) _checkingConfig.models.Add(incoming);
                else
                {
                    // Sharing audio setup must preserve the machine's BOM/scan layout.
                    existing.testItems.InOut = incoming.testItems.InOut;
                    existing.AudioConfigUpdatedUtc = incoming.AudioConfigUpdatedUtc;
                }
                changed = true;
            }
            if (!changed) return;
            if (!SaveCheckingConfig()) throw new InvalidOperationException("Không lưu được cấu hình bài đo từ server.");
            string? selected = ComboModels.SelectedItem?.ToString();
            ComboModels.Items.Clear();
            foreach (var model in _checkingConfig.models) ComboModels.Items.Add(model.model);
            if (selected != null) SelectModel(selected);
        }
        catch (Exception ex) { Debug.WriteLine("Đồng bộ cấu hình bài đo: " + ex.Message); }
    }

    private readonly SemaphoreSlim _audioConfigPublishGate = new(1, 1);
    private ModelConfig? FindAudioConfig(string name) => _checkingConfig?.models.FirstOrDefault(m =>
        string.Equals(m.model.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    private bool IsNewestLocalAudioConfig(ModelConfig saved) =>
        FindAudioConfig(saved.model)?.AudioConfigUpdatedUtc == saved.AudioConfigUpdatedUtc;

    /// <summary>Publishes a locally saved model without blocking the operator. Publishes run one at a time and a
    /// save superseded by a newer local save is skipped. The verified readback only updates the timestamp,
    /// so the selected model and any running measurement are untouched.</summary>
    private async Task PublishAudioConfigInBackgroundAsync(ModelConfig saved)
    {
        string? error = null;
        await _audioConfigPublishGate.WaitAsync();
        try
        {
            if (!IsNewestLocalAudioConfig(saved)) return;
            var shared = await ServerEngine.PublishAudioModelConfigAsync(saved);
            if (IsNewestLocalAudioConfig(saved) && FindAudioConfig(saved.model) is { } current)
            {
                current.AudioConfigUpdatedUtc = shared.AudioConfigUpdatedUtc;
                if (!SaveCheckingConfig()) Debug.WriteLine("Đồng bộ server: chưa ghi được mốc thời gian server vào checking_config.json.");
            }
            Debug.WriteLine($"Đồng bộ server: đã gửi và đọc lại model {saved.model}.");
        }
        catch (Exception ex) { error = ex.Message; Debug.WriteLine($"Đồng bộ server model {saved.model}: {ex}"); }
        finally { _audioConfigPublishGate.Release(); }
        if (error == null) return;
        // Report after measurement/editing so the notice never interrupts a running test.
        while (_modelEditorOpen || _audioRoutingView?.IsTestingBusy == true || IsStandardMeasurementBusy) await Task.Delay(1000);
        if (!_isLoggingOut && IsVisible && IsNewestLocalAudioConfig(saved))
            ModernMessageBox.Show(this, $"Model {saved.model} đã lưu tại máy nhưng chưa đồng bộ server:\n{error}\n\nKhi có kết nối, mở Sửa model và bấm Lưu model để gửi lại.",
                "Chưa đồng bộ server", ModernMessageBox.MessageBoxType.Error);
    }
}
