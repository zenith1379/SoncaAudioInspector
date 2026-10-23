using ScottPlot;
using ScottPlot.Plottables;
using SkiaSharp;

namespace SoncaAudioInspector;

/// <summary>Dense IR columns have exact visible extrema, not a fill to a fixed baseline.</summary>
internal sealed class ImpulseSignalPlot(ISignalXYSource source, bool cacheGeometry) : SignalXY(source)
{
    public ImpulseSignalPlot(ISignalXYSource source) : this(source, false) { }
    private ISignalXYSource? _cachedSource;
    private object? _cacheKey;
    private SKPoint[]? _cachedLines;

    public override void Render(RenderPack render)
    {
        if (!IsVisible || Data.Count == 0) return;
        if (LineStyle.AntiAlias)
        {
            // At sample-level zoom retain the normal connected sample geometry.
            base.Render(render);
            return;
        }
        // Only measurement snapshots opt in: callers with mutable samples keep
        // the normal live-data behavior. Include every source transform and both
        // axes so zoom, pan, DPI/size changes and lazy window replacement rebuild.
        object key = (Axes.XAxis.Min, Axes.XAxis.Max, Axes.YAxis.Min, Axes.YAxis.Max,
            render.DataRect, Data.Count, Data.MinimumIndex, Data.MaximumIndex,
            Data.XOffset, Data.YOffset, Data.XScale, Data.YScale, Data.Rotated, ConnectStyle);
        if (cacheGeometry && ReferenceEquals(Data, _cachedSource) && key.Equals(_cacheKey) && _cachedLines != null)
        {
            LineStyle.ApplyToPaint(render.Paint);
            render.Canvas.DrawPoints(SKPointMode.Lines, _cachedLines, render.Paint.SKPaint);
            return;
        }
        var pixels = Data.GetPixelsToDraw(render, Axes, ConnectStyle);
        if (pixels.Count == 0) return;
        var lines = new SKPoint[pixels.Count * 2];
        int count = 0;
        for (int i = 0; i < pixels.Count;)
        {
            float x = pixels[i].X, low = pixels[i].Y, high = low;
            do
            {
                low = Math.Min(low, pixels[i].Y); high = Math.Max(high, pixels[i].Y); i++;
            } while (i < pixels.Count && pixels[i].X == x);
            // A zero-length butt-capped stroke is invisible. Constant ETC/IR
            // sections still need one pixel at their actual level.
            if (high - low < .25f)
            {
                lines[count++] = new(x - .5f, low); lines[count++] = new(x + .5f, high);
            }
            else
            {
                lines[count++] = new(x, low); lines[count++] = new(x, high);
            }
        }
        Array.Resize(ref lines, count);
        if (cacheGeometry)
        {
            _cachedSource = Data; _cacheKey = key; _cachedLines = lines;
        }
        LineStyle.ApplyToPaint(render.Paint);
        render.Canvas.DrawPoints(SKPointMode.Lines, lines, render.Paint.SKPaint);
    }
}
