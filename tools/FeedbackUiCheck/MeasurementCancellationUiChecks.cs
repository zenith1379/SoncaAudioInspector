using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class MeasurementCancellationUiChecks
{
    public static void Run(Assembly assembly, string fixture)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var routing = (FrameworkElement)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioRouting")!)!;
        var type = routing.GetType();
        object Field(string name) => type.GetField(name, hidden)!.GetValue(routing)!;
        void Set(string name, object? value) => type.GetField(name, hidden)!.SetValue(routing, value);
        var engine = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.AudioEngine")!)!;
        var runner = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.TestRunner")!, new[] { engine })!;
        Set("_audioEngine", engine); Set("_testRunner", runner);
        var details = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.MeasurementDetailsWindow")!)!;
        Set("_measurementDetailsWindow", details);
        bool Busy() => (bool)type.GetProperty("IsTestingBusy")!.GetValue(routing)!;
        object DetailField(string name) => details.GetType().GetField(name, hidden)!.GetValue(details)!;
        void Until(Func<bool> done)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < 5)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
            Check(done(), "UI cleanup timed out.");
        }
        var owned = runner.GetType().GetMethod("RunOwnedTestAsync", hidden)!;
        var observe = type.GetMethod("ObserveMeasurementTaskAsync", hidden)!;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            Set("_discardCurrentMeasurementRequested", false);
            var lease = (IDisposable)type.GetMethod("TryBeginRoutingWorkflow", hidden)!.Invoke(routing, null)!;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> worker = async token =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); await release.Task; throw; }
            };
            var work = (Task)owned.Invoke(runner, new object[] { worker })!;
            Set("_activeMeasurementTask", work);
            var tracking = (Task)observe.Invoke(routing, new object[] { work })!;
            type.GetMethod("UpdateMeasurementDetails", hidden)!.Invoke(routing, new object[] { true });
            Check((bool)details.GetType().GetProperty("IsMeasurementPending")!.GetValue(details)!, "Pending state missing.");
            type.GetMethod("CancelAndDiscardCurrentMeasurement")!.Invoke(routing, null);
            Until(() => cancelled.Task.IsCompleted);
            Check(Busy() && !tracking.IsCompleted, "UI released ownership while worker still running.");
            Check(!(bool)details.GetType().GetProperty("IsMeasurementPending")!.GetValue(details)!, "Discarded IR still says measuring.");
            Check(((TextBlock)DetailField("_status")).Text.Contains("Đã hủy"), "Discard status missing.");
            Check(((ContentControl)DetailField("_impulse")).Content is TextBlock t && !t.Text.Contains("Đang"), "Stale pending placeholder.");
            release.SetResult(); Until(() => tracking.IsCompleted); tracking.GetAwaiter().GetResult();
            lease.Dispose();
            Check(!Busy(), "Finished cancellation left UI busy.");
            Check(((Button)routing.FindName("BtnStart")).IsEnabled, "Measure button not restored.");
        }
        Console.WriteLine("PASS three cancel/discard/rerun cycles through runner ownership, tracked task cleanup, workflow lease, details placeholder and busy state; no audio emitted.");

        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var root = JsonNode.Parse(File.ReadAllText(fixture))!;
        root["Validity"] = "DIAGNOSTIC"; root["IsClipped"] = false;
        root["SignalLevelDbFs"] = -30; root["NoiseFloorDbFs"] = -40; root["EstimatedSnrDb"] = 10;
        root["SweepHarmonics"] = new JsonArray(new JsonObject { ["FundamentalFrequencyHz"] = 1000, ["FundamentalLevelDb"] = -30,
            ["H2Dbc"] = 20 * Math.Log10(.14), ["H3Dbc"] = -180, ["H4Dbc"] = -180, ["H5Dbc"] = -180, ["ThdPercent"] = 14 });
        object Summary()
        {
            object result = JsonSerializer.Deserialize(root.ToJsonString(), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, options)!;
            return runner.GetType().GetMethod("BuildSweepDistortionSummary")!.Invoke(runner, new object?[] { result, 1000.0 })!;
        }
        void Render(object summary) => type.GetMethod("UpdateSweepDistortionMetrics", hidden)!.Invoke(routing, new[] { summary });
        Set("_discardCurrentMeasurementRequested", false);
        object noisy = Summary();
        Check((bool)noisy.GetType().GetProperty("IsValid")!.GetValue(noisy)!, "Measured harmonics rejected by fabricated broadband SNR gate.");
        Check((double)noisy.GetType().GetProperty("ThdPercent")!.GetValue(noisy)! == 14, "Raw THD was hidden or changed.");
        Render(noisy);
        string metrics = ((TextBox)routing.FindName("TxtSweepDistortionMetrics")).Text;
        Check(metrics.Contains("14.000"), "Measured raw THD missing from UI.");
        root["SweepHarmonics"]![0]!["ThdPercent"] = .25;
        Check((bool)Summary().GetType().GetProperty("IsValid")!.GetValue(Summary())!, "Finite under-limit THD lost its SNR-warning acceptance.");
        root["SweepHarmonics"]![0]!["ThdPercent"] = 14;
        root["EstimatedSnrDb"] = 70; root["NoiseFloorDbFs"] = -100;
        Check((bool)Summary().GetType().GetProperty("IsValid")!.GetValue(Summary())!, "High-SNR distortion was suppressed.");
        var toneNode = new JsonObject { ["IsValid"] = true, ["FundamentalFrequencyHz"] = 1000,
            ["FundamentalRms"] = .25, ["SignalLevelDbFs"] = -12, ["ThdPercent"] = .216,
            ["SnrDb"] = 40, ["Harmonics"] = new JsonArray(), ["AnalysisSource"] = "LOG_SWEEP_FDW" };
        Type toneType = assembly.GetType("SoncaAudioInspector.ToneQualityMetrics")!;
        object tone = JsonSerializer.Deserialize(toneNode.ToJsonString(), toneType, options)!;
        var map = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(double), toneType))!;
        map.Add(1000.0, tone);
        runner.GetType().GetProperty("LastToneQualities")!.SetValue(runner, map);
        type.GetMethod("RefreshThdVerdictAfterMeasurement", hidden)!.Invoke(routing, null);
        Check(((TextBox)routing.FindName("TxtSweepDistortionMetrics")).Text.Contains("Log sweep · cửa sổ hài theo tần số"), "Sweep-derived THD is still labelled as a separate tone.");
        toneNode["IsValid"] = false; toneNode["ThdPercent"] = 14;
        map[1000.0] = JsonSerializer.Deserialize(toneNode.ToJsonString(), toneType, options)!;
        type.GetMethod("RefreshThdVerdictAfterMeasurement", hidden)!.Invoke(routing, null);
        Check(((TextBlock)routing.FindName("TxtThdStatus")).Text.Contains("chưa xác định")
            && !((TextBox)routing.FindName("TxtSweepDistortionMetrics")).Text.Contains("14.000"), "Invalid tone still presents its raw THD as a result.");
        string? lowPopup = null;
        Set("SignalWarningPresenter", new Action<string,string>((message, title) => lowPopup = message));
        root["EstimatedSnrDb"] = 10;
        object weak = JsonSerializer.Deserialize(root.ToJsonString(), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, options)!;
        runner.GetType().GetProperty("LastSweepResult")!.SetValue(runner, weak);
        type.GetMethod("ShowSignalLevelPopupIfNeeded", hidden)!.Invoke(routing, null);
        Check(lowPopup != null && lowPopup.Contains("THD") && lowPopup.Contains("30 dB"), "Low signal/SNR warning missing.");
        Check((double)Summary().GetType().GetProperty("ThdPercent")!.GetValue(Summary())! == 14, "Low-level warning changed numeric THD.");
        Console.WriteLine("PASS low signal warning retains raw THD and is separate from missing stimulus/clipping.");
        root["Validity"] = "INVALID";
        root["SignalLevelDbFs"] = -51.2; root["NoiseFloorDbFs"] = -51.6; root["EstimatedSnrDb"] = .4;
        root["Diagnostics"] = new JsonArray(new JsonObject { ["Code"] = "SWEEP_NOT_DETECTED", ["Severity"] = "INVALID", ["Message"] = "No known sweep" });
        object missing = JsonSerializer.Deserialize(root.ToJsonString(), assembly.GetType("SoncaAudioInspector.StandardAcousticResult")!, options)!;
        runner.GetType().GetProperty("LastSweepResult")!.SetValue(runner, missing);
        string? popup = null;
        Set("SignalWarningPresenter", new Action<string,string>((message, title) => popup = message));
        type.GetMethod("ShowSignalLevelPopupIfNeeded", hidden)!.Invoke(routing, null);
        Check(popup != null && popup.Contains("sweep"), "Noisy input without sweep did not show missing-stimulus popup.");
        Check(!(bool)Summary().GetType().GetProperty("IsValid")!.GetValue(Summary())!, "Missing sweep was accepted as THD.");
        Check(routing.FindName("ChkNormalizeOneKilohertz") == null, "Removed normalization checkbox still present.");
        Console.WriteLine("PASS noise-only input triggers transmit/receive popup; absent sweep cannot produce a THD verdict; normalization option removed.");
        details.Close();
        ((IDisposable)Field("_audioEngine")).Dispose();
        Console.WriteLine("PASS measured raw 14% THD displayed without invented broadband SNR gate; actual invalid capture still suppressed.");
        Console.WriteLine("PASS frequency-dependent sweep THD retains its source label in the main inspection panel.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
