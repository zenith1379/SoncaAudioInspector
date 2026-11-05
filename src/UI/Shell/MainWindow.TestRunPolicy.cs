using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector;

public partial class MainWindow
{
    internal static void KeepNewerAudioConfiguration(CheckingConfig live,CheckingConfig persisted)
    {
        for(int i=0;i<live.models.Count;i++)
        {
            var memory=live.models[i];var disk=persisted.models.FirstOrDefault(m=>string.Equals(m.model,memory.model,StringComparison.OrdinalIgnoreCase));
            if(disk?.AudioConfigUpdatedUtc is {} saved && saved>(memory.AudioConfigUpdatedUtc ?? DateTimeOffset.MinValue))
            {memory.testItems.InOut=disk.testItems.InOut;memory.AudioConfigUpdatedUtc=saved;}
        }
    }
    public void EditSelectedTestConfiguration(string id) => OpenModelEditorForTest(false,id);
    public async Task UpdateTestRunPolicyAsync(string id,int maximum,int required)
    {
        if(_modelEditorOpen || _audioRoutingView?.IsModelTransitionBusy==true || IsStandardMeasurementBusy)
            throw new InvalidOperationException("Chờ đo hoặc đóng trình sửa model trước khi đổi cấu hình bài đo.");
        string name=ComboModels.SelectedItem?.ToString() ?? "";
        var original=_checkingConfig.models.SingleOrDefault(m=>m.model==name) ?? throw new InvalidOperationException("Chọn model trước khi sửa bài đo.");
        var candidate=JsonSerializer.Deserialize<ModelConfig>(JsonSerializer.Serialize(original))!;
        var test=candidate.testItems.InOut.Tests.Single(t=>t.id==id);
        test.MaximumRuns=maximum;test.RequiredPasses=required;test.ValidateRunPolicy();
        void Store(ModelConfig value)
        {
            int index=_checkingConfig.models.FindIndex(m=>m.model==name);
            var before=_checkingConfig.models[index];_checkingConfig.models[index]=value;
            if(!SaveCheckingConfig()){_checkingConfig.models[index]=before;throw new IOException("Không lưu được cấu hình model.");}
            SelectModel(name);
        }
        candidate.AudioConfigUpdatedUtc=DateTimeOffset.UtcNow;Store(candidate);
        try {Store(await ServerEngine.PublishAudioModelConfigAsync(candidate));}
        catch(Exception ex){throw new InvalidOperationException("Đã lưu tại máy; chưa đồng bộ server: "+ex.Message,ex);}
    }
}
