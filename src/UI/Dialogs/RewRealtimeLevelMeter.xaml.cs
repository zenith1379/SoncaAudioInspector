using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class RewRealtimeLevelMeter : UserControl
{
    private static readonly SolidColorBrush BrushBlue = new(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush BrushAmber = new(Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush BrushOrange = new(Color.FromRgb(249, 115, 22));
    private static readonly SolidColorBrush BrushRed = new(Color.FromRgb(239, 68, 68));

    private static readonly SolidColorBrush BgBlue = new(Color.FromRgb(24, 34, 52));
    private static readonly SolidColorBrush BgGreen = new(Color.FromRgb(6, 78, 59));
    private static readonly SolidColorBrush BgAmber = new(Color.FromRgb(56, 38, 14));
    private static readonly SolidColorBrush BgOrange = new(Color.FromRgb(58, 30, 13));
    private static readonly SolidColorBrush BgRed = new(Color.FromRgb(69, 20, 20));

    static RewRealtimeLevelMeter()
    {
        BrushBlue.Freeze();
        BrushGreen.Freeze();
        BrushAmber.Freeze();
        BrushOrange.Freeze();
        BrushRed.Freeze();

        BgBlue.Freeze();
        BgGreen.Freeze();
        BgAmber.Freeze();
        BgOrange.Freeze();
        BgRed.Freeze();
    }

    private double _estimatedNoiseFloorDb = -75.0;
    private bool _isHoldingCapturedResult = false;
    private DateTime _holdExpiry = DateTime.MinValue;

    public RewRealtimeLevelMeter()
    {
        InitializeComponent();
        Reset();
    }

    public void DisplayCapturedResult(float[] samples, double peakDbFs, double rmsDbFs, double snrDb, bool isClipped = false)
    {
        _isHoldingCapturedResult = true;
        _holdExpiry = DateTime.UtcNow.AddSeconds(3.5);

        if (samples != null && samples.Length > 0)
        {
            int numSlices = 70;
            int sliceLen = Math.Max(16, samples.Length / numSlices);
            var slices = new List<RewLevelGraphCanvas.LevelSlice>(numSlices);

            for (int s = 0; s < numSlices; s++)
            {
                int start = s * sliceLen;
                int end = Math.Min(samples.Length, start + sliceLen);
                if (start >= samples.Length) break;

                double maxVal = 0.0;
                double sumSq = 0.0;
                int count = end - start;

                for (int i = start; i < end; i++)
                {
                    double v = Math.Abs((double)samples[i]);
                    if (v > maxVal) maxVal = v;
                    sumSq += v * v;
                }

                double pDb = 20.0 * Math.Log10(Math.Max(1e-6, maxVal));
                double rDb = 20.0 * Math.Log10(Math.Max(1e-6, Math.Sqrt(sumSq / count)));
                bool clip = maxVal >= 0.995 || pDb > -0.5;

                slices.Add(new RewLevelGraphCanvas.LevelSlice
                {
                    PeakDb = (float)pDb,
                    RmsDb = (float)rDb,
                    IsClipped = clip
                });
            }

            GraphCanvas.SetSlices(slices);
        }

        double headroom = isClipped ? 0.0 : Math.Clamp(-peakDbFs, 0.0, 99.0);
        TxtHeadroom.Text = $"{headroom:F1} dB";

        if (isClipped)
        {
            TxtHeadroom.Foreground = BrushRed;
            BorderStatusBadge.Background = BgRed;
            BorderStatusBadge.BorderBrush = BrushRed;
            TxtStatusBadge.Text = "⛔ CLIPPING";
            TxtStatusBadge.Foreground = BrushRed;
        }
        else if (peakDbFs < -40.0)
        {
            TxtHeadroom.Foreground = BrushAmber;
            BorderStatusBadge.Background = BgAmber;
            BorderStatusBadge.BorderBrush = BrushAmber;
            TxtStatusBadge.Text = "⚠ MỨC QUÁ YẾU";
            TxtStatusBadge.Foreground = BrushAmber;
        }
        else if (snrDb < 15.0 && double.IsFinite(snrDb))
        {
            TxtHeadroom.Foreground = BrushAmber;
            BorderStatusBadge.Background = BgAmber;
            BorderStatusBadge.BorderBrush = BrushAmber;
            TxtStatusBadge.Text = "⚠ NHIỄU CAO (SNR THẤP)";
            TxtStatusBadge.Foreground = BrushAmber;
        }
        else if (headroom < 6.0)
        {
            TxtHeadroom.Foreground = BrushOrange;
            BorderStatusBadge.Background = BgOrange;
            BorderStatusBadge.BorderBrush = BrushOrange;
            TxtStatusBadge.Text = "⚠ MỨC RẤT LỚN (HOT)";
            TxtStatusBadge.Foreground = BrushOrange;
        }
        else
        {
            TxtHeadroom.Foreground = BrushGreen;
            BorderStatusBadge.Background = BgGreen;
            BorderStatusBadge.BorderBrush = BrushGreen;
            TxtStatusBadge.Text = "✔ ĐÃ THU (OK)";
            TxtStatusBadge.Foreground = BrushGreen;
        }

        string snrStr = double.IsFinite(snrDb) ? $"{snrDb:F0} dB" : "-- dB";
        TxtStatsDetail.Text = $"Peak: {peakDbFs:F1} | RMS {rmsDbFs:F1} | SNR {snrStr}";
    }

    public void PrepareForLiveCapture()
    {
        _isHoldingCapturedResult = false;
    }

    public void PushSamples(float[] samples, int count = -1)
    {
        if (samples == null || samples.Length == 0) return;
        if (_isHoldingCapturedResult)
        {
            if (DateTime.UtcNow < _holdExpiry) return;
            _isHoldingCapturedResult = false;
        }

        int n = count <= 0 || count > samples.Length ? samples.Length : count;
        double maxAbs = 0.0;
        double sumSq = 0.0;

        for (int i = 0; i < n; i++)
        {
            double val = Math.Abs((double)samples[i]);
            if (val > maxAbs) maxAbs = val;
            sumSq += val * val;
        }

        double peakDb = 20.0 * Math.Log10(Math.Max(1e-6, maxAbs));
        double rms = Math.Sqrt(sumSq / n);
        double rmsDb = 20.0 * Math.Log10(Math.Max(1e-6, rms));
        bool isClipped = maxAbs >= 0.995 || peakDb > -0.5;

        PushLevel(peakDb, rmsDb, isClipped);
    }

    public void PushLevel(double peakDb, double rmsDb, bool isClipped = false)
    {
        if (_isHoldingCapturedResult)
        {
            if (DateTime.UtcNow < _holdExpiry) return;
            _isHoldingCapturedResult = false;
        }

        double headroom;
        if (isClipped)
        {
            headroom = 0.0;
        }
        else if (peakDb <= -90.0)
        {
            headroom = 99.0;
        }
        else
        {
            headroom = Math.Clamp(0.0 - peakDb, 0.0, 99.0);
        }

        // Noise floor tracker & SNR estimation
        if (rmsDb < -60.0)
        {
            _estimatedNoiseFloorDb = 0.95 * _estimatedNoiseFloorDb + 0.05 * rmsDb;
        }
        else if (rmsDb < _estimatedNoiseFloorDb)
        {
            _estimatedNoiseFloorDb = rmsDb;
        }
        double snr = Math.Max(0.0, rmsDb - _estimatedNoiseFloorDb);

        // Send to scrolling graph canvas
        GraphCanvas.AddSlice((float)peakDb, (float)rmsDb, isClipped);

        // Update Headroom Readout & Color
        TxtHeadroom.Text = $"{headroom:F1} dB";

        if (isClipped)
        {
            TxtHeadroom.Foreground = BrushRed;
            BorderStatusBadge.Background = BgRed;
            BorderStatusBadge.BorderBrush = BrushRed;
            TxtStatusBadge.Text = "⛔ QUÁ CAO (CLIPPING)";
            TxtStatusBadge.Foreground = BrushRed;
        }
        else if (headroom >= 70.0 || peakDb < -55.0)
        {
            TxtHeadroom.Foreground = BrushBlue;
            BorderStatusBadge.Background = BgBlue;
            BorderStatusBadge.BorderBrush = BrushBlue;
            TxtStatusBadge.Text = "ℹ CHỜ TÍN HIỆU";
            TxtStatusBadge.Foreground = BrushBlue;
        }
        else if (headroom > 25.0 || peakDb < -25.0)
        {
            TxtHeadroom.Foreground = BrushAmber;
            BorderStatusBadge.Background = BgAmber;
            BorderStatusBadge.BorderBrush = BrushAmber;
            TxtStatusBadge.Text = "⚠ MỨC QUÁ YẾU (LOW)";
            TxtStatusBadge.Foreground = BrushAmber;
        }
        else if (snr < 15.0 && peakDb >= -25.0)
        {
            TxtHeadroom.Foreground = BrushAmber;
            BorderStatusBadge.Background = BgAmber;
            BorderStatusBadge.BorderBrush = BrushAmber;
            TxtStatusBadge.Text = "⚠ NHIỄU CAO (SNR THẤP)";
            TxtStatusBadge.Foreground = BrushAmber;
        }
        else if (headroom < 6.0)
        {
            TxtHeadroom.Foreground = BrushOrange;
            BorderStatusBadge.Background = BgOrange;
            BorderStatusBadge.BorderBrush = BrushOrange;
            TxtStatusBadge.Text = "⚠ MỨC RẤT LỚN (HOT)";
            TxtStatusBadge.Foreground = BrushOrange;
        }
        else
        {
            TxtHeadroom.Foreground = BrushGreen;
            BorderStatusBadge.Background = BgGreen;
            BorderStatusBadge.BorderBrush = BrushGreen;
            TxtStatusBadge.Text = "✔ MỨC THU PHÙ HỢP";
            TxtStatusBadge.Foreground = BrushGreen;
        }

        TxtStatsDetail.Text = $"Peak: {(double.IsFinite(peakDb) ? $"{peakDb:F1}" : "-∞")} | RMS: {(double.IsFinite(rmsDb) ? $"{rmsDb:F1}" : "-∞")} | SNR: {snr:F0} dB";
    }

    public void Reset()
    {
        _isHoldingCapturedResult = false;
        _estimatedNoiseFloorDb = -75.0;
        GraphCanvas.Clear();
        TxtHeadroom.Text = "-- dB";
        TxtHeadroom.Foreground = BrushBlue;
        BorderStatusBadge.Background = BgBlue;
        BorderStatusBadge.BorderBrush = BrushBlue;
        TxtStatusBadge.Text = "ℹ CHỜ TÍN HIỆU";
        TxtStatusBadge.Foreground = BrushBlue;
        TxtStatsDetail.Text = "Peak: -- | RMS: --";
    }

    public void ShowUnavailable(string message)
    {
        Reset();
        TxtStatusBadge.Text = message;
        TxtStatsDetail.Text = "Chưa có dữ liệu thu mới";
    }
}

public class RewLevelGraphCanvas : FrameworkElement
{
    public struct LevelSlice
    {
        public float RmsDb;
        public float PeakDb;
        public bool IsClipped;
    }

    private readonly List<LevelSlice> _history = new(256);
    private const double AxisWidth = 34.0;
    private const double ColumnWidth = 2.0;
    private const double TopMargin = 6.0;
    private const double BottomMargin = 6.0;

    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x30)), 1.0);
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A)), 1.0);
    private static readonly Pen MinorTickPen = new(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x4C)), 1.0);
    private static readonly SolidColorBrush AxisTextBrush = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private static readonly LinearGradientBrush RmsGradientBrush = new(
        Color.FromRgb(0x00, 0x22, 0xAA),
        Color.FromRgb(0x00, 0x99, 0xFF),
        new Point(0, 1),
        new Point(0, 0));

    private static readonly Pen PeakPen = new(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), 1.8);
    private static readonly Pen ClipPen = new(new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)), 2.2);

    private static readonly Typeface AxisTypeface = new(
        new FontFamily("Segoe UI, Arial, sans-serif"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    static RewLevelGraphCanvas()
    {
        GridPen.Freeze();
        AxisPen.Freeze();
        MinorTickPen.Freeze();
        AxisTextBrush.Freeze();
        RmsGradientBrush.Freeze();
        PeakPen.Freeze();
        ClipPen.Freeze();
    }

    public void AddSlice(float peakDb, float rmsDb, bool isClipped)
    {
        double availableWidth = Math.Max(20.0, ActualWidth - AxisWidth - 4.0);
        int maxColumns = Math.Max(30, (int)(availableWidth / ColumnWidth));

        _history.Add(new LevelSlice
        {
            RmsDb = rmsDb,
            PeakDb = peakDb,
            IsClipped = isClipped
        });

        if (_history.Count > maxColumns)
        {
            _history.RemoveRange(0, _history.Count - maxColumns);
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _history.Clear();
        InvalidateVisual();
    }

    public void SetSlices(IEnumerable<LevelSlice> slices)
    {
        _history.Clear();
        if (slices != null)
        {
            _history.AddRange(slices);
        }
        InvalidateVisual();
    }

    private double DbToY(double db, double usableHeight)
    {
        double clamped = Math.Clamp(db, -90.0, 0.0);
        double norm = (0.0 - clamped) / 90.0; // 0 dB -> 0, -90 dB -> 1.0
        return TopMargin + norm * usableHeight;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Background
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));

        double usableHeight = Math.Max(10.0, height - TopMargin - BottomMargin);
        double rightEdgeX = width - 2.0;
        double plotStartX = AxisWidth;

        // Draw Minor ticks every 5 dB: -5, -10, -15, -25, -30, -35, -45, -50, -55, -65, -70, -75, -80, -85
        for (int db = -5; db >= -85; db -= 5)
        {
            if (db == 0 || db == -20 || db == -40 || db == -60) continue;
            double y = DbToY(db, usableHeight);
            dc.DrawLine(MinorTickPen, new Point(plotStartX - 3, y), new Point(plotStartX, y));
        }

        // Major markings: 0, -20, -40, -60, -90 dB
        int[] majorMarks = [0, -20, -40, -60, -90];
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (int db in majorMarks)
        {
            double y = DbToY(db, usableHeight);

            // Horizontal Grid Line across plot
            dc.DrawLine(GridPen, new Point(plotStartX, y), new Point(rightEdgeX, y));

            // Axis Tick
            dc.DrawLine(AxisPen, new Point(plotStartX - 5, y), new Point(plotStartX, y));

            // Axis Label text
            string labelText = db.ToString();
            var ft = new FormattedText(
                labelText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                8.5,
                AxisTextBrush,
                dpi);

            dc.DrawText(ft, new Point(plotStartX - 7 - ft.Width, y - ft.Height / 2.0));
        }

        // Draw Axis dividing line
        dc.DrawLine(AxisPen, new Point(plotStartX, TopMargin), new Point(plotStartX, TopMargin + usableHeight));

        // Draw Live Scrolling Level Slices (from newest at rightEdgeX scrolling left)
        if (_history.Count == 0) return;

        double bottomY = DbToY(-90.0, usableHeight);

        for (int i = _history.Count - 1; i >= 0; i--)
        {
            double x = rightEdgeX - (_history.Count - 1 - i) * ColumnWidth;
            if (x < plotStartX) break;

            var slice = _history[i];
            double yPeak = DbToY(slice.PeakDb, usableHeight);
            double yRms = DbToY(slice.RmsDb, usableHeight);

            // Blue filled area for RMS
            if (slice.RmsDb > -89.5)
            {
                double barHeight = Math.Max(1.0, bottomY - yRms);
                dc.DrawRectangle(RmsGradientBrush, null, new Rect(x, yRms, ColumnWidth, barHeight));
            }

            // Red Peak Mark
            if (slice.PeakDb > -89.5)
            {
                Pen pen = slice.IsClipped ? ClipPen : PeakPen;
                dc.DrawLine(pen, new Point(x - 0.5, yPeak), new Point(x + ColumnWidth + 0.5, yPeak));
            }
        }
    }
}
