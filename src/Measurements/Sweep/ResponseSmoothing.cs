namespace SoncaAudioInspector;

/// <summary>Power averaging over log-frequency; leaves the source samples intact.</summary>
public static class ResponseSmoothing
{
    public const int OctaveFraction = 24;
    public const int SourcePointsPerOctave = 192;
    public const string AnalysisId = "TK25_L125_R500_NOFDW_RAW192_S24_20-20000_V4";

    public static Dictionary<double, double> Octave24(IReadOnlyDictionary<double, double> source, IEnumerable<double> centers)
    {
        var points = source.Where(p => p.Key > 0 && double.IsFinite(p.Key) && double.IsFinite(p.Value)).OrderBy(p => p.Key).ToArray();
        if (points.Length < 2) return new();
        double[] x = points.Select(p => Math.Log(p.Key)).ToArray();
        double[] power = points.Select(p => Math.Pow(10, p.Value / 10)).ToArray();
        double halfWidth = Math.Log(2) / (2 * OctaveFraction);
        double At(double position)
        {
            int index = Array.BinarySearch(x, position);
            if (index >= 0) return power[index];
            index = ~index;
            if (index == 0) return power[0];
            if (index == x.Length) return power[^1];
            double fraction = (position - x[index - 1]) / (x[index] - x[index - 1]);
            return power[index - 1] + fraction * (power[index] - power[index - 1]);
        }
        var result = new Dictionary<double, double>();
        foreach (double frequency in centers.Order())
        {
            double middle = Math.Log(frequency);
            if (middle < x[0] || middle > x[^1]) continue;
            double left = Math.Max(x[0], middle - halfWidth), right = Math.Min(x[^1], middle + halfWidth);
            double area = 0, previousX = left, previousPower = At(left);
            int first = Array.BinarySearch(x, left); first = first < 0 ? ~first : first + 1;
            for (int i = first; i < x.Length && x[i] < right; i++)
            {
                area += (x[i] - previousX) * (previousPower + power[i]) * .5;
                previousX = x[i]; previousPower = power[i];
            }
            area += (right - previousX) * (previousPower + At(right)) * .5;
            result[frequency] = 10 * Math.Log10(Math.Max(1e-30, area / Math.Max(1e-30, right - left)));
        }
        return result;
    }
}
