using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class C360BleAfterAutoTestChecks
{
    public static void Run(Assembly assembly, object ble, string output, string cleanupPath)
    {
        using var cleanup=JsonDocument.Parse(File.ReadAllText(cleanupPath));
        if(!cleanup.RootElement.GetProperty("Success").GetBoolean())throw new Exception("Audio cleanup was not confirmed before BLE verification");
        var type=ble.GetType();
        var packet=(byte[])assembly.GetType("SoncaAudioInspector.SoncaBleProtocol")!.GetMethod("BuildReadMixerPacket")!.Invoke(null,null)!;
        var read=(Task<bool>)type.GetMethod("SendPacketAsync")!.Invoke(ble,new object[]{packet,"Đối chiếu BLE sau Auto Test"})!;
        Wait(()=>read.IsCompleted,15);
        bool confirmed=read.GetAwaiter().GetResult();
        bool connected=(bool)type.GetProperty("IsConnected")!.GetValue(ble)!;
        ulong? address=(ulong?)type.GetProperty("ConnectedBluetoothAddress")!.GetValue(ble);
        File.WriteAllText(Path.Combine(output,"ble-after-autotest.json"),JsonSerializer.Serialize(new {connected,confirmed,address=address?.ToString("X12"),firmware=type.GetProperty("FirmwareIdentity")!.GetValue(ble)},new JsonSerializerOptions{WriteIndented=true}));
        if(!connected || !confirmed || address==null)throw new Exception("BLE not usable after audio-only cleanup");
        var mixer=(Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!,new object[]{"C360"})!;
        try
        {
            mixer.Show();
            var combo=(ComboBox)mixer.FindName("CmbDevices");
            Wait(()=>combo.Items.OfType<ComboBoxItem>().Any(item=>Equals(item.Tag,address.Value)),12);
            var screenshot=new RenderTargetBitmap((int)mixer.ActualWidth,(int)mixer.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            screenshot.Render(mixer);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(screenshot));
            using(var file=File.Create(Path.Combine(output,"ctrl-shift-h-after.png")))png.Save(file);
            Console.WriteLine("PASS real BLE readback after audio-only Auto Test cleanup; current controller remains visible in Ctrl+Shift+H.");
        }
        finally{mixer.Close();}
    }
    private static void Wait(Func<bool> done,int seconds)
    {
        var deadline=DateTime.UtcNow.AddSeconds(seconds);
        while(!done() && DateTime.UtcNow<deadline)
        {var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(5);}
        if(!done())throw new TimeoutException("BLE post-Auto-Test readback/UI timed out");
    }
}
