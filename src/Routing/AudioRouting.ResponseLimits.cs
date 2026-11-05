using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private ResponseLimitSettings DisplayedResponseLimitSettings() => _reviewedAutoTestCase?.ResponseLimits
        ?? ResponseLimitSettings.Capture(_appliedTestConfig, ParseDoubleSafe(TxtFreqTolerance?.Text ?? "", 3));

    private ResponseLimitPolicy DisplayedResponseLimitPolicy() => new(DisplayedResponseLimitSettings(),
        _reviewedAutoTestCase?.StandardCurve ?? _standardCurve,
        _reviewedAutoTestCase?.FrequencyLimits ?? _frequencyLimits);

    // Read captured dictionaries only. The editor must never select devices or reload a reference.
    internal ResponseLimitPreviewData GetResponseLimitPreview(TestConfig candidate)
    {
        double tolerance = ParseDoubleSafe(TxtFreqTolerance?.Text ?? "", 3);
        static bool Matches(TestConfig first, TestConfig second)
        {
            if (!string.Equals(first.ReferenceTestId ?? first.id, second.ReferenceTestId ?? second.id, StringComparison.OrdinalIgnoreCase)) return false;
            JsonObject Key(TestConfig config)
            {
                var key = JsonSerializer.SerializeToNode(config)!.AsObject();
                key.Remove(nameof(TestConfig.MaximumRuns)); key.Remove(nameof(TestConfig.RequiredPasses));
                C360Reference.RemoveJudgementFields(key, config, 44100);
                return key;
            }
            return JsonNode.DeepEquals(Key(first), Key(second));
        }
        var matches = _autoTestCases.Where(row => Matches(candidate, row.Config)).ToArray();
        if (matches.Length == 1 && matches[0].StandardCurve.Count > 0)
        {
            var saved = matches[0];
            var measured = saved.ResponseFrequencies.Zip(saved.ResponseLevels).GroupBy(p => p.First).ToDictionary(g => g.Key, g => g.Last().Second);
            return new(new Dictionary<double, double>(saved.StandardCurve), measured,
                new Dictionary<double, FrequencyLimitPoint>(saved.FrequencyLimits), tolerance, "LINE CHUẨN và lần đo đã chụp của " + saved.DisplayName);
        }
        if (_appliedTestConfig != null && Matches(candidate, _appliedTestConfig) && _standardCurve?.Count > 0 && !string.IsNullOrEmpty(_loadedStandardDeviceKey))
            return new(new Dictionary<double, double>(_standardCurve), _freqs.Zip(_dbValues).GroupBy(p => p.First).ToDictionary(g => g.Key, g => g.Last().Second),
                _frequencyLimits == null ? new Dictionary<double, FrequencyLimitPoint>() : new(_frequencyLimits), tolerance, "LINE CHUẨN đang nạp của bài này; dữ liệu đã có trong phiên.");
        return new(new Dictionary<double, double>(), new Dictionary<double, double>(), new Dictionary<double, FrequencyLimitPoint>(), tolerance, "Chưa có dữ liệu cùng cấu hình trong phiên.");
    }
}
