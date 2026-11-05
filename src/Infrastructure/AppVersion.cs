namespace SoncaAudioInspector;

public static class AppVersion
{
    public static Version Current { get; } = Normalize(typeof(AppVersion).Assembly.GetName().Version!);
    public static string Display => Format(Current);
    public static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    public static string Format(Version value)
    {
        var v = Normalize(value);
        return "v" + (v.Revision > 0 ? v.ToString(4) : v.Build > 0 ? v.ToString(3) : v.ToString(2));
    }
}
