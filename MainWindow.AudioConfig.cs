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
}
