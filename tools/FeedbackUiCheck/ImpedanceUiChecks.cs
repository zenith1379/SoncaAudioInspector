using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ImpedanceUiChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _captures;
    private static bool _hold;
    public static async Task<T> Capture<T>(object value, CancellationToken token)
    { _captures++; if (_hold) await Task.Delay(Timeout.Infinite, token); return (T)value; }
    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output); SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var windowType = assembly.GetType("SoncaAudioInspector.ImpedanceView")!;
        var settingsType = assembly.GetType("SoncaAudioInspector.ImpedanceSettings")!;
        var settings = JsonSerializer.Deserialize("{\"SenseOhms\":100,\"KnownOhms\":10,\"StartHz\":2000,\"EndHz\":18000,\"LevelDbFs\":-42,\"Seconds\":3}", settingsType)!;
        var frequencies = (double[])settingsType.GetMethod("Frequencies")!.Invoke(settings, null)!;
        object[] Rows(double real, double imaginary) => frequencies.Select(f => (object)new { FrequencyHz = f, Real = real, Imaginary = imaginary, ReferenceSnrDb = 70, LoadSnrDb = 65 }).ToArray();
        var ratio = new System.Numerics.Complex(8,2) / new System.Numerics.Complex(108,2);
        var captureType = assembly.GetType("SoncaAudioInspector.ImpedanceCapture")!;
        object fake = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Context = "fixture", Ratios = Rows(ratio.Real, ratio.Imaginary) }), captureType)!;
        var constructor = windowType.GetConstructors().Single(); var signature = constructor.GetParameters();
        var captureDelegateType = signature[0].ParameterType; var parameters = captureDelegateType.GenericTypeArguments.Take(6).Select(Expression.Parameter).ToArray();
        var body = Expression.Call(typeof(ImpedanceUiChecks).GetMethod(nameof(Capture))!.MakeGenericMethod(captureType), Expression.Constant(fake, typeof(object)), parameters[5]);
        var capture = Expression.Lambda(captureDelegateType, body, parameters).Compile();
        var contextDelegateType = signature[1].ParameterType; var contextParameters = contextDelegateType.GenericTypeArguments.Take(4).Select(Expression.Parameter).ToArray();
        var context = Expression.Lambda(contextDelegateType, Expression.Constant("fixture"), contextParameters).Compile();
        var view = (FrameworkElement)constructor.Invoke(new object[] { capture, context, output });
        var window = new Window { Content = view, Width = 1200, Height = 840 };
        object Field(string name) => windowType.GetField(name, Hidden)!.GetValue(view)!;
        Task RunLoad() => (Task)windowType.GetMethod("RunAsync", Hidden)!.Invoke(view, new object[] { "PHÁT / ĐO TẢI" })!;
        void Wait(Task task)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 10)
            { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5); }
            Check(task.IsCompleted, "UI timeout"); task.GetAwaiter().GetResult();
        }
        Wait(RunLoad()); Check(_captures == 0, "Missing endpoints emitted audio");
        ((ComboBox)Field("_output")).SelectedIndex = 0; ((ComboBox)Field("_input")).SelectedIndex = 0;
        ((TextBox)Field("_start")).Text="2000"; ((TextBox)Field("_end")).Text="18000"; ((TextBox)Field("_seconds")).Text="3";
        Wait(RunLoad()); Check(_captures == 1 && ((TextBlock)Field("_status")).Text.Contains("CHƯA HIỆU CHUẨN"), "First measurement did not capture an uncalibrated diagnostic");
        var diagnosticSaved=Field("_last");var diagnostic=diagnosticSaved.GetType().GetProperty("Result")!.GetValue(diagnosticSaved)!;
        Check(!(bool)diagnostic.GetType().GetProperty("IsValid")!.GetValue(diagnostic)! && ((Array)diagnostic.GetType().GetProperty("DiagnosticPoints")!.GetValue(diagnostic)!).Length>0,
            "Uncalibrated measurement was qualified or lost its curve");
        windowType.GetMethod("Export",Hidden)!.Invoke(view,null);
        Check(((TextBlock)Field("_message")).Text.Contains("Chưa có bản đo hợp lệ"),"Uncalibrated diagnostic exported as qualified impedance");
        var calType = assembly.GetType("SoncaAudioInspector.ImpedanceCalibration")!;
        var calibration = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Context = "fixture", Settings = settings, Open = Rows(1,0), Short = Rows(0,0), Known = Rows(10.0/110,0), CreatedUtc = DateTimeOffset.UtcNow }), calType)!;
        windowType.GetField("_calibration", Hidden)!.SetValue(view, calibration);
        Wait(RunLoad()); Check(_captures == 2 && ((TextBlock)Field("_status")).Text == "ĐÃ ĐO TRỞ KHÁNG", "Calibrated manual measurement missing");
        window.ShowInTaskbar = false; window.Show();
        var renderWait = System.Diagnostics.Stopwatch.StartNew();
        while (renderWait.ElapsedMilliseconds < 250) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5); }
        void Snapshot(string name)
        {
            var content = (FrameworkElement)window.Content; content.Measure(new Size(1200,840)); content.Arrange(new Rect(0,0,1200,840)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1200,840,96,96,PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
        }
        Snapshot("impedance.png");
        var basicCalibration = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Context = "fixture", Settings = settings, Open = Rows(1,0), Short=Array.Empty<object>(), Known=Array.Empty<object>(), CreatedUtc=DateTimeOffset.UtcNow }),calType)!;
        windowType.GetField("_calibration",Hidden)!.SetValue(view,basicCalibration);
        Wait(RunLoad()); Check(_captures==3 && ((TextBlock)Field("_status")).Text=="ĐÃ ĐO TRỞ KHÁNG","Open-only calibration still required all three steps");
        Snapshot("impedance-basic.png");
        windowType.GetField("_calibration",Hidden)!.SetValue(view,calibration);
        ((TextBox)Field("_level")).Text = "-30"; Wait(RunLoad()); Check(_captures == 3, "Changed settings reused stale calibration");
        ((TextBox)Field("_level")).Text = "-42";
        var openRows = frequencies.Select(f =>
        {
            var z = new System.Numerics.Complex(0,-1/(2*Math.PI*f*2e-9)); var h = z/(100+z);
            return new { FrequencyHz=f, Real=h.Real, Imaginary=h.Imaginary, ReferenceSnrDb=70, LoadSnrDb=65 };
        }).ToArray();
        var openCapture = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Context="fixture", Ratios=openRows }), captureType)!;
        captureType.GetProperty("Ratios")!.SetValue(fake, captureType.GetProperty("Ratios")!.GetValue(openCapture));
        Wait(RunLoad()); Check(_captures == 4 && ((TextBlock)Field("_status")).Text == "TẢI HỞ / TRỞ KHÁNG CAO", "Open/high load state/diagnostic curve missing");
        windowType.GetMethod("Export", Hidden)!.Invoke(view, null);
        Check(((TextBlock)Field("_message")).Text.Contains("Chưa có bản đo hợp lệ"), "Open load eligible for a qualified CSV export");
        var saved = Field("_last"); var openResult = saved.GetType().GetProperty("Result")!.GetValue(saved)!;
        windowType.GetMethod("Show", Hidden)!.Invoke(view, new[] { openResult });
        Snapshot("impedance-open.png");
        windowType.GetField("_calibration",Hidden)!.SetValue(view,null);
        Wait(RunLoad()); Check(_captures==5 && ((TextBlock)Field("_status")).Text.Contains("CHƯA HIỆU CHUẨN"),"Diagnostic capture was blocked without calibration");
        Snapshot("impedance-uncalibrated.png");
        windowType.GetField("_calibration",Hidden)!.SetValue(view,calibration);
        var exactOpen = JsonSerializer.Deserialize("{\"IsValid\":false,\"Message\":\"Tải hở\",\"Points\":[],\"OpenOrHighImpedance\":true,\"DiagnosticPoints\":[]}", assembly.GetType("SoncaAudioInspector.ImpedanceResult")!)!;
        windowType.GetMethod("Show", Hidden)!.Invoke(view, new[] { exactOpen });
        Check(((TextBlock)Field("_message")).Text.Contains("∞"), "Exact open invented finite impedance instead of an infinity explanation");
        _hold = true; var pending = RunLoad(); windowType.GetField("_pending", Hidden)!.SetValue(view, pending);
        var shutdown = (Task)windowType.GetMethod("StopForShutdownAsync")!.Invoke(view, null)!; Wait(shutdown); Check(pending.IsCompleted, "Window shutdown left measurement active");
        Console.WriteLine("PASS manual impedance UI: endpoints required, direct first capture produces an uncalibrated curve, qualified export blocked, open-only/full calibration, combined Ω/phase plot, open/high-load curve, no invented finite exact-open values, stale calibration blocked, cancellation/shutdown join. Mock audio only.");
    }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
}
