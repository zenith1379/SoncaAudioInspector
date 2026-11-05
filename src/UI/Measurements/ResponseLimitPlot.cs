using ScottPlot;

namespace SoncaAudioInspector;

public static class ResponseLimitPlot
{
    public static double[] Grid(ResponseLimitSettings settings, IEnumerable<double> source) => source
        .Concat(settings.DipWindows.SelectMany(w => new[] { w.MinHz * (1 - 1e-8), w.MinHz, Math.Sqrt(w.MinHz * w.MaxHz), w.MaxHz, w.MaxHz * (1 + 1e-8) }))
        .Concat(new[] { settings.Range.MinimumHz, settings.Range.MaximumHz }).Where(f => double.IsFinite(f) && f >= 20 && f <= 20000).Distinct().Order().ToArray();

    public static void Draw(Plot plot, ResponseLimitPolicy policy, IEnumerable<double> frequencies, bool drawCritical = false)
    {
        var settings = policy.Settings;
        var grid = Grid(settings, frequencies);
        var points = grid.Select(f => policy.Evaluate(f, 0)).ToArray();
        double top = points.Where(p => p.UpperDb.HasValue).Select(p => p.UpperDb!.Value).DefaultIfEmpty(3).Max() + 1;
        for (int i = 0; i < settings.DipWindows.Count; i++)
        {
            var window = settings.DipWindows[i];
            bool enabled = settings.DipWindowsEnabled && window.Enabled;
            var span = plot.Add.HorizontalSpan(Math.Log10(window.MinHz), Math.Log10(window.MaxHz));
            span.FillStyle.Color = Color.FromHex("#60A5FA").WithAlpha(enabled ? .10 : 0);
            span.LineStyle.Width = enabled ? 0 : 1;
            span.LineStyle.Color = Color.FromHex("#60A5FA").WithAlpha(.35);
            span.LineStyle.Pattern = LinePattern.Dashed;
            var label = plot.Add.Text((i + 1).ToString(), Math.Log10(Math.Sqrt(window.MinHz * window.MaxHz)), top);
            label.LabelFontColor = Color.FromHex("#93C5FD"); label.LabelFontSize = 11;
        }
        void Shade(double minimum, double maximum)
        {
            if (minimum >= maximum) return;
            var span = plot.Add.HorizontalSpan(Math.Log10(minimum), Math.Log10(maximum));
            span.LineStyle.Width = 0; span.FillStyle.Color = Color.FromHex("#000000").WithAlpha(.30);
            if (Math.Log10(maximum / minimum) < .12) return;
            var label = plot.Add.Text("không xét", Math.Log10(Math.Sqrt(minimum * maximum)), top);
            label.LabelFontColor = Color.FromHex("#9CA3AF"); label.LabelFontSize = 9;
        }
        Shade(20, settings.Range.MinimumHz); Shade(settings.Range.MaximumHz, 20000);
        if (drawCritical)
            foreach (var zone in settings.CriticalZones)
            {
                var span = plot.Add.HorizontalSpan(Math.Log10(Math.Max(20, Math.Min(zone.MinHz, zone.MaxHz))), Math.Log10(Math.Min(20000, Math.Max(zone.MinHz, zone.MaxHz))));
                span.LineStyle.Width = 0; span.FillStyle.Color = Color.FromHex("#F59E0B").WithAlpha(.09);
            }
        void Limits(Func<ResponseLimitPoint, double?> select)
        {
            var xs = new List<double>(); var ys = new List<double>();
            void Flush()
            {
                if (xs.Count > 0)
                {
                    var line = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
                    line.Color = Color.FromHex("#F97316"); line.LineWidth = 1.5f; line.LinePattern = LinePattern.Dashed; line.MarkerSize = 0;
                }
                xs.Clear(); ys.Clear();
            }
            foreach (var point in points)
            {
                double? value = select(point);
                if (!point.IsValid || !point.Evaluated || !value.HasValue) { Flush(); continue; }
                xs.Add(Math.Log10(point.FrequencyHz)); ys.Add(value.Value);
            }
            Flush();
        }
        Limits(p => p.LowerDb); Limits(p => p.UpperDb);
    }
}
