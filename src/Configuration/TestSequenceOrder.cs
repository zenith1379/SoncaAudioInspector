using System.Text.RegularExpressions;

namespace SoncaAudioInspector;

public static class TestSequenceOrder
{
    private static readonly Regex PrefixRegex = new(@"^([A-Za-z_-]+)(\d+)$", RegexOptions.Compiled);

    public static string EffectiveIdentity(TestConfig test)
    {
        if (test == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(test.ReferenceTestId))
            return test.ReferenceTestId.Trim();
        return test.id?.Trim() ?? string.Empty;
    }

    public static string DetectPrefix(IList<TestConfig> tests)
    {
        if (tests != null)
        {
            foreach (var test in tests)
            {
                if (string.IsNullOrWhiteSpace(test?.id)) continue;
                var match = PrefixRegex.Match(test.id.Trim());
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }
        return "IO";
    }

    public static void RenumberByPosition(IList<TestConfig> tests)
    {
        if (tests == null || tests.Count == 0) return;
        string prefix = DetectPrefix(tests);
        for (int i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            if (test == null) continue;
            string newId = $"{prefix}{i + 1:D3}";
            if (string.IsNullOrWhiteSpace(test.ReferenceTestId) &&
                !string.Equals(test.id?.Trim(), newId, StringComparison.OrdinalIgnoreCase))
            {
                test.ReferenceTestId = test.id?.Trim();
            }
            test.id = newId;
        }
    }

    public static void EnsureUniqueIdentityForAddedTest(IList<TestConfig> tests, TestConfig addedTest)
    {
        if (addedTest == null) return;
        var existingEffective = (tests ?? Array.Empty<TestConfig>())
            .Where(t => !ReferenceEquals(t, addedTest))
            .Select(EffectiveIdentity)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string current = EffectiveIdentity(addedTest);
        if (string.IsNullOrEmpty(current)) return;

        if (existingEffective.Contains(current))
        {
            int index = 2;
            string candidate;
            do
            {
                candidate = $"{current}-N{index}";
                index++;
            } while (existingEffective.Contains(candidate));
            addedTest.ReferenceTestId = candidate;
        }
    }

    public static bool MoveTest(IList<TestConfig> tests, int fromIndex, int toIndex)
    {
        if (tests == null) return false;
        if (fromIndex < 0 || fromIndex >= tests.Count) return false;
        if (toIndex < 0 || toIndex >= tests.Count) return false;
        if (fromIndex == toIndex) return false;

        var item = tests[fromIndex];
        tests.RemoveAt(fromIndex);
        tests.Insert(toIndex, item);
        RenumberByPosition(tests);
        return true;
    }
}
