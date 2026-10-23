using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoncaAudioInspector;

public static partial class ServerEngine
{
    private sealed class AudioConfigRow
    {
        public AudioConfigRow() { }
        public JsonElement Config { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
    private static ModelConfig ModelFromRow(AudioConfigRow row)
    {
        var model = JsonSerializer.Deserialize<ModelConfig>(row.Config.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException("Server trả cấu hình rỗng.");
        model = ModelConfigEditorWindow.ParseModel(model.model, JsonSerializer.Serialize(model, ModelConfigEditorWindow.JsonOptions));
        model.AudioConfigUpdatedUtc = row.UpdatedAt;
        return model;
    }
    public static async Task<IReadOnlyList<ModelConfig>> GetAudioModelConfigsAsync()
    {
        var data = await SendAuthorizedForDataAsync(HttpMethod.Get, "api/app/audio-config");
        var rows = JsonSerializer.Deserialize<List<AudioConfigRow>>(data.GetRawText(), JsonOptions) ?? new();
        return rows.Select(ModelFromRow).ToArray();
    }
    public static async Task<ModelConfig> PublishAudioModelConfigAsync(ModelConfig model)
    {
        var payload = JsonSerializer.SerializeToElement(model, ModelConfigEditorWindow.JsonOptions);
        var data = await SendAuthorizedForDataAsync(HttpMethod.Put, "api/app/audio-config", payload);
        var saved = JsonSerializer.Deserialize<AudioConfigRow>(data.GetRawText(), JsonOptions)
            ?? throw new InvalidOperationException("Server chưa xác nhận lưu cấu hình.");
        string endpoint = "api/app/audio-config?speakerModel=" + Uri.EscapeDataString(model.model);
        var readback = await SendAuthorizedForDataAsync(HttpMethod.Get, endpoint);
        var rows = JsonSerializer.Deserialize<List<AudioConfigRow>>(readback.GetRawText(), JsonOptions);
        var latest = rows?.SingleOrDefault();
        if (latest == null || !JsonNode.DeepEquals(JsonNode.Parse(payload.GetRawText()), JsonNode.Parse(latest.Config.GetRawText()))
            || !JsonNode.DeepEquals(JsonNode.Parse(payload.GetRawText()), JsonNode.Parse(saved.Config.GetRawText())))
            throw new InvalidOperationException("Cấu hình đọc lại chưa khớp dữ liệu đã gửi. Bản tại máy vẫn được giữ; thử tải/gửi lại.");
        return ModelFromRow(latest);
    }
}
