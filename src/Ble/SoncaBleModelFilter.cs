using System.Globalization;

namespace SoncaAudioInspector;

public static class SoncaBleModelFilter
{
    public static bool Matches(SoncaBleDeviceIdentity identity, string query)
    {
        if (identity.ModelId is not ushort id || identity.ModelNames.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(query)) return true;
        return query.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(part => MatchesPart(part, id, identity.ModelNames));
    }

    private static bool MatchesPart(string part, ushort id, IReadOnlyList<string> names)
    {
        string code = part.StartsWith("ID", StringComparison.OrdinalIgnoreCase) ? part[2..].TrimStart(' ', ':', '=') : part;
        bool hexadecimal = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (ushort.TryParse(hexadecimal ? code[2..] : code, hexadecimal ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out ushort number)) return id == number;
        string key = Normalize(part);
        return key.Length > 0 && names.Any(name => Normalize(name).Contains(key, StringComparison.Ordinal));
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
