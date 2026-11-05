using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class BleBluetoothAudioLiveChecks
{
    public static void Run(Assembly assembly,string output,ulong address)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR",Path.Combine(output,"data"));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var manager=Task.Run(()=>assembly.GetType("SoncaAudioInspector.SoncaBleManager")!.GetProperty("Instance")!.GetValue(null)!).GetAwaiter().GetResult();
        var logs=new Action<string>(message=>{if(message.StartsWith("[BLE]")||message.StartsWith("[BT]"))Console.WriteLine(message);});
        manager.GetType().GetEvent("LogMessage")!.AddEventHandler(manager,logs);
        manager.GetType().GetMethod("StartScanning")!.Invoke(manager,null);
        var connect=Task.Run(async()=>await (Task<bool>)manager.GetType().GetMethod("ConnectAsync")!.Invoke(manager,new object[]{address})!);
        Until(()=>connect.IsCompleted,20);
        if(!connect.GetAwaiter().GetResult())throw new Exception("Live BLE target not available");
        var window=(Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!,new object?[]{"C360"})!;
        Application.Current.MainWindow=window;
        string Text()=>((TextBlock)window.FindName("TxtBluetoothAudioStatus")).Text;
        try
        {
            window.Show();
            Until(()=>Text().StartsWith("Bluetooth âm thanh đã kết nối:")||Text().StartsWith("Chưa nối được Bluetooth âm thanh:"),65);
            window.FindName("PanelSettings").GetType().GetProperty("Visibility")!.SetValue(window.FindName("PanelSettings"),Visibility.Visible);
            window.UpdateLayout();
            File.WriteAllText(Path.Combine(output,"status.txt"),Text());
            var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(output,"live-mixer.png")))png.Save(file);
            Console.WriteLine("LIVE STATUS "+Text());
        }
        finally{window.Close();manager.GetType().GetMethod("Disconnect")!.Invoke(manager,null);manager.GetType().GetEvent("LogMessage")!.RemoveEventHandler(manager,logs);}
    }
    private static void Until(Func<bool> condition,int seconds)
    {
        var deadline=DateTime.UtcNow.AddSeconds(seconds);
        while(!condition())
        {
            if(DateTime.UtcNow>deadline)throw new TimeoutException("Live Windows audio connection timed out");
            var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);Thread.Sleep(2);
        }
    }
}
