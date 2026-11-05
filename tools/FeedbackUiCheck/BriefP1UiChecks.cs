using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class BriefP1UiChecks
{
    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        // Do not host/show the view or call InitializeRouting: Loaded/device discovery stays inactive.
        var routing = (FrameworkElement)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioRouting")!)!;
        Type type = routing.GetType();
        object Field(string name) => type.GetField(name, hidden)!.GetValue(routing)!;
        void Set(string name, object? value) => type.GetField(name, hidden)!.SetValue(routing, value);
        object? Call(string name, params object?[] values) => type.GetMethod(name, hidden)!.Invoke(routing, values);
        Type runnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
        var engine = (IDisposable)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        var runner = Activator.CreateInstance(runnerType, new object[] { engine })!;
        void RunnerSet(string name, object? value) => runnerType.GetProperty(name)!.SetValue(runner, value);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks.Add(why); }
        string Status(int index) => ((IList)runnerType.GetProperty("Steps")!.GetValue(runner)!)[index]!.GetType().GetProperty("Status")!
            .GetValue(((IList)runnerType.GetProperty("Steps")!.GetValue(runner)!)[index])!.ToString()!;
        void Step(int index, string status, string details)
        {
            var value = ((IList)runnerType.GetProperty("Steps")!.GetValue(runner)!)[index]!;
            value.GetType().GetProperty("Status")!.SetValue(value, status);
            value.GetType().GetProperty("Details")!.SetValue(value, details);
        }
        Set("_audioEngine", engine); Set("_testRunner", runner);
        try
        {
            Call("InitCharts");
            ((IList)Field("_freqs")).Add(1000.0); ((IList)Field("_dbValues")).Add(-20.0);
            Step(0, "Pass", "fixture"); Step(1, "Pass", "fixture");
            RunnerSet("LastMeasuredThdPercent", .2);
            foreach (string reason in new[] { "80 Hz", "4000 Hz", "THD+N", "SINAD", "SNR", "DC offset" })
            {
                Step(2, "Fail", reason + " vượt giới hạn");
                Check(!(bool)type.GetProperty("AutoTestDistortionPassed", hidden)!.GetValue(routing)!, "D1 preserves failed " + reason + " with low 1 kHz");
                Call("SetFinalVerdict", false);
                var verdict = (TextBlock)routing.FindName("LblVerdict");
                Check(verdict.Text.Contains("KHÔNG ĐẠT") && verdict.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(248, 113, 113),
                    "D1 failed verdict remains red: " + reason);
            }
            Step(2, "Pass", "ĐẠT");
            Check((bool)type.GetProperty("AutoTestDistortionPassed", hidden)!.GetValue(routing)!, "D1 valid passing distortion remains PASS");
            RunnerSet("ThdAcquisitionInvalid", true); Step(2, "Invalid", "80 Hz chưa hợp lệ");
            Check((bool)type.GetProperty("AutoTestAcquisitionInvalid", hidden)!.GetValue(routing)!, "D1 finite 1 kHz does not hide invalid optional THD");
            RunnerSet("ThdAcquisitionInvalid", false);

            RunnerSet("IsRubBuzzTest", true); runnerType.GetMethod("InitializeSteps")!.Invoke(runner, null);
            Step(0, "Pass", "fixture");
            runnerType.GetMethod("SetRubBuzzInvalid", hidden)!.Invoke(runner, new object[] { "Không khóa được tone." });
            Call("SetFinalVerdict", false);
            Check(Status(1) == "Invalid" && Status(3) == "Invalid", "D2 invalid legacy steps and final step");
            Check(((TextBlock)routing.FindName("LblVerdict")).Text.Contains("CHƯA CÓ PHÉP ĐO HỢP LỆ")
                && ((TextBlock)routing.FindName("LblVerdict")).Foreground == Brushes.Gold, "D2 invalid legacy verdict is yellow");
            Set("_isExecutingAutoSuite", true); Set("_autoTestSessionFolder", output);
            Call("SaveFailureScreenshots", "INVALID", true);
            Check(!File.Exists(Path.Combine(output, "FRA-INVALID.png")) && ((IList)Field("_autoTestGraphPaths")).Count == 0, "D2 invalid legacy result produces no QA graph");

            Type rowType = assembly.GetType("SoncaAudioInspector.AutoTestCaseItem")!;
            Type configType = assembly.GetType("SoncaAudioInspector.TestConfig")!;
            Type metricType = assembly.GetType("SoncaAudioInspector.RubBuzzMetrics")!;
            Type spectrumType = assembly.GetType("SoncaAudioInspector.FrequencySpectrum")!;
            RunnerSet("RubBuzzAcquisitionInvalid", false); RunnerSet("RubBuzzTestFreq", 70.0);
            Step(1, "Pass", "Rè/rung đạt giới hạn"); Step(2, "Pass", "Không áp dụng cho bài đo rè/rung");
            Step(3, "Pass", "ĐẠT");
            var metrics = JsonSerializer.Deserialize("{\"IsValid\":true,\"FundamentalFrequencyHz\":70,\"RubBuzzPercent\":0.5,\"ResidualPeakFrequencyHz\":3000,\"ResidualPeakDbc\":-46,\"PeakSample\":0.5,\"ClippedSamplePercent\":0}", metricType)!;
            RunnerSet("LastRubBuzzMetrics", metrics);
            var spectrum = Activator.CreateInstance(spectrumType, new object[] { new[] { 70d, 1000, 3000, 8000 }, new[] { .5, .00001, .0025, .00001 } })!;
            RunnerSet("LastRubBuzzSpectrum", spectrum);
            var row = Activator.CreateInstance(rowType)!;
            var config = Activator.CreateInstance(configType)!;
            configType.GetProperty("RubBuzzTestFreq")!.SetValue(config, 70.0);
            rowType.GetProperty("Config")!.SetValue(row, config);
            rowType.GetProperty("Id")!.SetValue(row, "IO001"); rowType.GetProperty("Name")!.SetValue(row, "Rè/rung fixture");
            rowType.GetProperty("Status")!.SetValue(row, "PASS"); rowType.GetProperty("ThdStatus")!.SetValue(row, "Không áp dụng cho bài đo rè/rung");
            ((IList)Field("_autoTestCases")).Add(row);
            Call("CaptureAutoTestResultForReview", row);
            Check(((double[])rowType.GetProperty("ResponseFrequencies")!.GetValue(row)!).Length == 0, "Legacy snapshot cannot inherit the previous FEQ response");
            object savedSpectrum = rowType.GetProperty("RubBuzzSpectrum")!.GetValue(row)!;
            Check(!ReferenceEquals(spectrum, savedSpectrum)
                && !ReferenceEquals(spectrumType.GetProperty("Magnitudes")!.GetValue(spectrum), spectrumType.GetProperty("Magnitudes")!.GetValue(savedSpectrum)), "QA spectrum snapshot owns independent arrays");
            Call("DisplayAutoTestResult", row); Call("SaveFailureScreenshots", "IO001", true);
            string path = Path.Combine(output, "FRA-IO001.png");
            Check(File.Exists(path) && new FileInfo(path).Length > 1000, "QA saves populated legacy spectrum after review clears previous plots");
            using (var stream = File.OpenRead(path))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(image.PixelWidth == 800 && image.PixelHeight == 450, "QA spectrum keeps existing 800 by 450 export contract");
            }
            Check(((IList)Field("_autoTestGraphPaths")).Count == ((IList)Field("_autoTestCases")).Count, "QA legacy graph count satisfies existing per-step requirement without upload");

            var failed = Activator.CreateInstance(rowType)!;
            rowType.GetProperty("Config")!.SetValue(failed, config); rowType.GetProperty("Id")!.SetValue(failed, "IO002");
            rowType.GetProperty("Name")!.SetValue(failed, "Rè/rung vượt giới hạn"); rowType.GetProperty("Status")!.SetValue(failed, "FAIL");
            rowType.GetProperty("ThdStatus")!.SetValue(failed, "Rè/rung 4% vượt giới hạn 1,5%");
            RunnerSet("ThdPassed", false); Step(1, "Fail", "Rè/rung 4% vượt giới hạn 1,5%");
            var failedMetrics = JsonSerializer.Deserialize("{\"IsValid\":true,\"FundamentalFrequencyHz\":70,\"RubBuzzPercent\":4,\"ResidualPeakFrequencyHz\":3000,\"ResidualPeakDbc\":-28,\"PeakSample\":0.5,\"ClippedSamplePercent\":0}", metricType)!;
            RunnerSet("LastRubBuzzMetrics", failedMetrics); ((IList)Field("_autoTestCases")).Add(failed);
            RunnerSet("LastRubBuzzSpectrum", Activator.CreateInstance(spectrumType,
                new object[] { new[] { 70d, 1000, 3000, 8000 }, new[] { .5, .00001, .02, .00001 } }));
            Call("CaptureAutoTestResultForReview", failed); Call("DisplayAutoTestResult", failed); Call("SaveFailureScreenshots", "IO002", true);
            Check(File.Exists(Path.Combine(output, "FRA-IO002.png")) && ((IList)Field("_autoTestGraphPaths")).Count == 2,
                "QA valid failed legacy result also has exactly one spectrum image per step");

            RunnerSet("IsRubBuzzTest", false); RunnerSet("LastRubBuzzMetrics", null); RunnerSet("LastRubBuzzSpectrum", null);
            Call("DisplayAutoTestResult", row);
            Check(((TextBlock)routing.FindName("TxtQualityChartHeading")).Text == "RÈ / RUNG", "Review uses saved legacy type even after runner switches to another test");
            Check(!routing.IsLoaded, "Offline UI test never triggers Loaded/device enumeration");
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks.Prepend($"PASS {checks.Count} P1 WPF assertions; no device/BLE/server operations."));
            Console.WriteLine($"PASS {checks.Count} P1 WPF assertions and QA spectrum PNG; no audio/BLE/server.");
        }
        finally
        {
            Set("_isExecutingAutoSuite", false);
            foreach (string name in new[] { "_frequencyChartRefreshTimer", "_meterUiTimer" })
                if (type.GetField(name, hidden)?.GetValue(routing) is System.Windows.Threading.DispatcherTimer timer) timer.Stop();
            engine.Dispose();
        }
    }
}
