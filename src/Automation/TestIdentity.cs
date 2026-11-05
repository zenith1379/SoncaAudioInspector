using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public partial class TestConfig
{
    // Preserve the acquisition identity when only a displayed IO code changes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferenceTestId { get; set; }

    [JsonIgnore]
    public string DisplaySequenceLabel => $"{id} · {name}";
}
