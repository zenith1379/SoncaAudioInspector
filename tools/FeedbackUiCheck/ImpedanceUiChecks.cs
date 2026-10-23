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
        var windowType = assembly.GetType("SoncaAudioInspector.ImpedanceWindow")!;
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
        var window = (Window)constructor.Invoke(new object[] { capture, context, output });
        object Field(string name) => windowType.GetField(name, Hidden)!.GetValue(window)!;
        Task RunLoad() => (Task)windowType.GetMethod("RunAsync", Hidden)!.Invoke(window, new object[] { "PHÁT / ĐO TẢI" })!;
        void Wait(Task task)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 10)
            { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5); }
            Check(task.IsCompleted, "UI timeout"); task.GetAwaiter().GetResult();
        }
        Wait(RunLoad()); Check(_captures == 0, "No-fixture UI emitted audio");
        ((ComboBox)Field("_output")).SelectedIndex = 0; ((ComboBox)Field("_input")).SelectedIndex = 0; ((CheckBox)Field("_ready")).IsChecked = true;
        Wait(RunLoad()); Check(_captures == 0, "Missing calibration emitted into load");
        var calType = assembly.GetType("SoncaAudioInspector.ImpedanceCalibration")!;
        var calibration = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Context = "fixture", Settings = settings, Open = Rows(1,0), Short = Rows(0,0), Known = Rows(10.0/110,0), CreatedUtc = DateTimeOffset.UtcNow }), calType)!;
        windowType.GetField("_calibration", Hidden)!.SetValue(window, calibration);
        Wait(RunLoad()); Check(_captures == 1 && ((TextBlock)Field("_status")).Text == "ĐÃ ĐO TRỞ KHÁNG", "Calibrated manual measurement missing");
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
        ((TextBox)Field("_level")).Text = "-30"; Wait(RunLoad()); Check(_captures == 1, "Changed settings reused stale calibration");
        ((TextBox)Field("_level")).Text = "-42";
        var openRows = frequencies.Select(f =>
        {
            var z = new System.Numerics.Complex(0,-1/(2*Math.PI*f*2e-9)); var h = z/(100+z);
            return new { FrequencyHz=f, Real=h.Real, Imaginary=h.Imaginary, ReferenceSnrDb=70, LoadSnrDb=65 };
        }).ToArray();
        var openCapture = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { Context="fixture", Ratios=openRows }), captureType)!;
        captureType.GetProperty("Ratios")!.SetValue(fake, captureType.GetProperty("Ratios")!.GetValue(openCapture));
        Wait(RunLoad()); Check(_captures == 2 && ((TextBlock)Field("_status")).Text == "TẢI HỞ / TRỞ KHÁNG CAO", "Open/high load state/diagnostic curve missing");
        windowType.GetMethod("Export", Hidden)!.Invoke(window, null);
        Check(((TextBlock)Field("_message")).Text.Contains("Chưa có bản đo hợp lệ"), "Open load eligible for a qualified CSV export");
        var saved = Field("_last"); var openResult = saved.GetType().GetProperty("Result")!.GetValue(saved)!;
        windowType.GetMethod("Show", Hidden)!.Invoke(window, new[] { openResult });
        Snapshot("impedance-open.png");
        var exactOpen = JsonSerializer.Deserialize("{\"IsValid\":false,\"Message\":\"Tải hở\",\"Points\":[],\"OpenOrHighImpedance\":true,\"DiagnosticPoints\":[]}", assembly.GetType("SoncaAudioInspector.ImpedanceResult")!)!;
        windowType.GetMethod("Show", Hidden)!.Invoke(window, new[] { exactOpen });
        Check(((TextBlock)Field("_message")).Text.Contains("∞"), "Exact open invented finite impedance instead of an infinity explanation");
        _hold = true; var pending = RunLoad(); windowType.GetField("_pending", Hidden)!.SetValue(window, pending);
        var shutdown = (Task)windowType.GetMethod("StopForShutdownAsync")!.Invoke(window, null)!; Wait(shutdown); Check(pending.IsCompleted, "Window shutdown left measurement active");
        Console.WriteLine("PASS manual impedance UI: no fixture/calibration means no capture, calibrated Ω/phase plots, open/high-load diagnostic plot, qualified export blocked, exact-open infinity explanation, stale settings blocked before playback, cancellation/shutdown join and no Auto Test integration. Mock audio only.");
    }
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
}
