using ScottPlot;
using ScottPlot.TickGenerators;

namespace SoncaAudioInspector;

/// <summary>Retain every 1 dB grid tick, but label only those with room for readable text.</summary>
public sealed class ResponseDbTickGenerator : ITickGenerator
{
    private readonly NumericFixedInterval _grid = new(1);
    public double Interval => _grid.Interval;
    public double LabelInterval { get; private set; } = 1;
    public Tick[] Ticks { get; private set; } = Array.Empty<Tick>();
    public int MaxTickCount { get => _grid.MaxTickCount; set => _grid.MaxTickCount = value; }

    public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, Paint paint, LabelStyle labelStyle)
    {
        _grid.Regenerate(range, edge, size, paint, labelStyle);
        double span = range.Max - range.Min;
        double labelHeight = labelStyle.Measure("-100", paint).Height;
        double gap = Math.Max(16, labelHeight + 5);
        double minimumStep = span * gap / Math.Max(gap, size.Length);
        LabelInterval = ReadableInterval(minimumStep);
        Ticks = _grid.Ticks.Select(tick => new Tick(tick.Position,
            Math.Abs(tick.Position / LabelInterval - Math.Round(tick.Position / LabelInterval)) < 1e-7 ? tick.Label : "",
            tick.IsMajor)).ToArray();
    }

    private static double ReadableInterval(double required)
    {
        if (!double.IsFinite(required) || required <= 1) return 1;
        double scale = Math.Pow(10, Math.Floor(Math.Log10(required)));
        foreach (double step in new[] { 1d, 2d, 5d, 10d })
            if (step * scale >= required) return step * scale;
        return 10 * scale;
    }
}
