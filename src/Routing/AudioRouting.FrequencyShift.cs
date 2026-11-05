namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private void ShowC360FrequencyShiftPlot(AutoTestCaseItem row)
    {
        _frequencyChartRefreshTimer.Stop();
        ShowInlineImpulse(null);
        HideFrequencyHover();
        TxtFrequencyChartHeading.Text = row.Id + " · MIC IN · DI TẦN FBX TẮT / BẬT";
        var plot = PlotFreqResponse.Plot; plot.Clear();
        ConfigureLogarithmicXAxis(plot);
        plot.Axes.Bottom.Label.Text = "Tần số tone (Hz)";
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#18181B");
        plot.Legend.FontColor = ScottPlot.Color.FromHex("#E4E4E7");
        plot.Legend.OutlineColor = ScottPlot.Color.FromHex("#52525B");
        plot.Title(row.FrequencyShift?.FbxChangeDetected == false ? "Di tần MIC · hai đường trùng: độ dịch Hz không đổi theo FBX" : "MIC IN · di tần sau tách sai lệch clock"); plot.Axes.Left.Label.Text = "Độ dịch tần (Hz)";
        if (row.ResponseFrequencies.Length > 0)
        {
            var line = plot.Add.Scatter(row.ResponseFrequencies.Select(Math.Log10).ToArray(), row.ResponseLevels);
            line.Color = ScottPlot.Color.FromHex("#10B981"); line.LineWidth = 2; line.MarkerSize = 9;
            line.LegendText = "FBX bật";
            if (row.FrequencyShift is { Off.Count: 3 } shift)
            {
                var before = plot.Add.Scatter(row.ResponseFrequencies.Select(Math.Log10).ToArray(), shift.Off.Select(p => p.MeasuredHz - p.NominalHz * (1 + shift.OffClockPpm / 1e6)).ToArray());
                before.Color = ScottPlot.Color.FromHex("#FACC15"); before.LineWidth = 1; before.MarkerSize = 4; before.LegendText = "FBX tắt";
                plot.ShowLegend();
            }
            var levels = row.ResponseLevels.Concat(row.FrequencyShift is { Off.Count: 3 } data
                ? data.Off.Select(p => p.MeasuredHz - p.NominalHz * (1 + data.OffClockPpm / 1e6)) : []);
            double low = levels.Min(), high = levels.Max(), margin = Math.Max(.5, (high - low) * .2);
            plot.Axes.SetLimits(Math.Log10(600), Math.Log10(4200), low - margin, high + margin);
        }
        else plot.Axes.SetLimits(Math.Log10(600), Math.Log10(4200), -1, 1);
        PlotFreqResponse.Refresh();
    }
}
