using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class BleMixerUiChecks
{
    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI task did not finish.");
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    public static void Performance(Assembly assembly, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        ConstructorInfo constructor = type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2);
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        writer.Connected = writer.Success = true;
        var window = (Window)constructor.Invoke(new object?[] { null, writer });
        void Drain() => window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var snapshot = Enumerable.Range(0x6000, 214).ToDictionary(i => (ushort)i, _ => (ushort)0);
        snapshot[0x6242] = 0; snapshot[0x6254] = 21960;
        writer.ConnectionReads = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        type.GetMethod("OnMixerStateReceived", hidden)!.Invoke(window, new object[] { snapshot }); Drain();
        double snapshotMs = clock.Elapsed.TotalMilliseconds;
        int connectionReadsPerSnapshot = writer.ConnectionReads;
        bool immediatelySelectable = new[] { "RadioInputBt", "RadioInputUsb", "RadioInputLineIn", "RadioInputKaraoke" }
            .All(name => ((RadioButton)window.FindName(name)).IsEnabled);
        Type peerType = assembly.GetType("SoncaAudioInspector.BluetoothAudioPeer")!;
        Type peersType = typeof(IReadOnlyList<>).MakeGenericType(peerType);
        Type pendingType = typeof(TaskCompletionSource<>).MakeGenericType(peersType);
        object pending = Activator.CreateInstance(pendingType)!;
        FieldInfo reader = type.GetField("ReadConnectedAudioPeers", hidden)!;
        reader.SetValue(window, System.Linq.Expressions.Expression.Lambda(reader.FieldType,
            System.Linq.Expressions.Expression.Constant(pendingType.GetProperty("Task")!.GetValue(pending), reader.FieldType.GenericTypeArguments[0])).Compile());
        int beforeRefresh = writer.Packets.Count;
        clock.Restart();
        Task refresh = (Task)type.GetMethod("RefreshAudioPeersAsync", hidden)!.Invoke(window, null)!;
        double refreshDispatchMs = clock.Elapsed.TotalMilliseconds;
        bool mixerReadBeforeBluetooth = writer.Packets.Count > beforeRefresh;
        pendingType.GetMethod("SetResult")!.Invoke(pending, new object[] { Array.CreateInstance(peerType, 0) });
        Complete(refresh); Drain();
        int beforeReads = writer.Packets.Count;
        MethodInfo read = type.GetMethod("ReadMixerStateAsync", hidden)!;
        for (int i = 0; i < 10; i++) Complete((Task)read.Invoke(window, null)!);
        int duplicateReads = writer.Packets.Count - beforeReads;
        int SourceWrites() => writer.Packets.Count(p => p.Length == 9 && p[2] == 4 && p[4] == 0x62 && p[5] == 0x42);
        window.Close();
        window = (Window)constructor.Invoke(new object?[] { null, writer });
        type.GetMethod("OnMixerStateReceived", hidden)!.Invoke(window, new object[] { snapshot }); Drain();
        bool reopenedImmediately = ((RadioButton)window.FindName("RadioInputUsb")).IsEnabled;
        window.Close();
        var report = new { connectionReadsPerSnapshot, snapshotMs, refreshDispatchMs, mixerReadBeforeBluetooth,
            duplicateReads, immediatelySelectable, reopenedImmediately, automaticSourceWrites = SourceWrites(),
            synthetic = true, sourceWritesAreInjected = true };
        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "ble-startup-performance.json"), json); Console.WriteLine(json);
        if (connectionReadsPerSnapshot > 10 || !mixerReadBeforeBluetooth || duplicateReads != 0
            || !immediatelySelectable || !reopenedImmediately || SourceWrites() != 0)
            throw new Exception("BLE startup regression: immediate flags, independent mixer read, quiet startup or duplicate suppression failed.");
        Console.WriteLine("PASS immediate BLE input flags and startup performance; no automatic source commands or physical hardware writes.");
    }

    public static void Live(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var window = (Window)Activator.CreateInstance(type, new object?[] { null })!;
        var manager = assembly.GetType("SoncaAudioInspector.SoncaBleManager")!.GetProperty("Instance")!.GetValue(null)!;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(40);
        bool ready = false;
        timer.Tick += (_, _) =>
        {
            ready = ((Button)window.FindName("BtnMuteMusic")).IsEnabled;
            if (ready || DateTime.UtcNow > deadline) { timer.Stop(); frame.Continue = false; }
        };
        try
        {
            window.Show(); timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "ble-mixer-live.png"))) png.Save(stream);
            var values = new[] { "TxtStatus", "TxtMicVal", "TxtEchoVal", "TxtReverbVal", "TxtMusicVal", "TxtMasterVal", "TxtLiveVal" }
                .ToDictionary(n => n, n => ((TextBlock)window.FindName(n)).Text);
            File.WriteAllText(Path.Combine(output, "ble-mixer-live.json"), System.Text.Json.JsonSerializer.Serialize(new { ready, values }));
            if (!ready) throw new Exception("Live mixer did not auto-connect and read speaker state; see screenshot/log.");
            Console.WriteLine("PASS live WPF mixer auto-associated Windows audio with Sonca BLE, completed INFO/config and enabled actual readback controls. No control values changed.");
        }
        finally { timer.Stop(); window.Close(); manager.GetType().GetMethod("Disconnect")!.Invoke(manager, null); }
    }

    public static void Run(Assembly assembly, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2);
        var window = (Window)constructor.Invoke(new object[] { "TEST MODEL", writer });
        var mute = (Button)window.FindName("BtnMuteMusic");
        var update = type.GetMethod("UpdateConnectionUi", hidden)!;
        var click = type.GetMethod("BtnMuteChannel_Click", hidden)!;
        var states = (Dictionary<string, bool>)type.GetField("_channelMuted", hidden)!.GetValue(window)!;
        if (writer.Packets.Count != 0 || mute.IsEnabled) throw new Exception("Mixer initialization wrote hardware or enabled offline controls.");
        click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (writer.Packets.Count != 0 || states["Music"]) throw new Exception("Offline mute changed state or sent a packet.");
        writer.Connected = true; update.Invoke(window, new object[] { true, "Fake GATT ready" });
        var readback = type.GetMethod("ApplyParameterReadback", hidden)!;
        var sourceButtons = new[] { "RadioInputBt", "RadioInputUsb", "RadioInputOpt", "RadioInputHdmi", "RadioInputLineIn", "RadioInputKaraoke" }
            .Select(n => (RadioButton)window.FindName(n)).ToArray();
        if (window.FindName("BtnProbeInputSources") != null) throw new Exception("Removed manual input-check button remains visible.");
        if (sourceButtons[5].Content.ToString() != "THẺ NHỚ (SD/TF)")
            throw new Exception("C360 model 1075 source 5 was mislabeled as Karaoke/MIC instead of SD/TF.");
        if (sourceButtons.Any(b => b.IsEnabled || b.IsChecked == true)) throw new Exception("Source controls enabled/guessed before readback.");
        readback.Invoke(window, new object[] { (ushort)0x6242, (ushort)0 });
        readback.Invoke(window, new object[] { (ushort)0x6254, (ushort)30152 });
        type.GetField("_suppressSliderEvents", hidden)!.SetValue(window, false);
        if (sourceButtons.Where((_, i) => new[] { 0, 1, 4, 5 }.Contains(i)).Any(b => !b.IsEnabled || b.Opacity != 1)
            || sourceButtons[2].IsEnabled || sourceButtons[3].IsEnabled || sourceButtons[2].Opacity != .4 || sourceButtons[3].Opacity != .4
            || sourceButtons[0].IsChecked != true || writer.Packets.Count != 0)
            throw new Exception("Verified BLE capability flags did not immediately light only selectable inputs without source commands.");
        writer.Success = false;
        sourceButtons[1].IsChecked = true;
        if (sourceButtons[0].IsChecked != true || sourceButtons[1].IsChecked == true || !sourceButtons[1].IsEnabled
            || !((TextBlock)window.FindName("TxtInputSourceStatus")).Text.Contains("Chưa xác nhận"))
            throw new Exception("Failed source write falsely selected USB, hid its failure or revoked firmware selectability.");
        writer.Success = true;
        sourceButtons[1].IsChecked = true;
        byte[] sourcePacket = writer.Packets.Last();
        if (sourceButtons[1].IsChecked != true || sourcePacket[4] != 0x62 || sourcePacket[5] != 0x42 || sourcePacket[7] != 1)
            throw new Exception("Confirmed source did not select USB with the verified command.");
        int sourceCount = writer.Packets.Count;
        sourceButtons[2].IsChecked = true;
        if (writer.Packets.Count != sourceCount || sourceButtons[1].IsChecked != true) throw new Exception("Unsupported optical source reached hardware.");
        type.GetMethod("OnMixerParametersReceived", hidden)!.Invoke(window, new object[] { new Dictionary<ushort, ushort> { [0x6242] = 0 } });
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (sourceButtons[0].IsChecked != true || !sourceButtons[1].IsEnabled || writer.Packets.Count != sourceCount
            || !((TextBlock)window.FindName("TxtInputSourceStatus")).Text.Contains("không giữ ngõ USB"))
            throw new Exception("Late fallback did not show the real source/warning while retaining the speaker's selectable-input flags.");
        writer.Pending = new TaskCompletionSource<bool>();
        sourceButtons[1].IsChecked = true;
        if (sourceButtons.Any(b => b.IsEnabled) || sourceButtons[0].IsChecked != true)
            throw new Exception("Pending source command was optimistic or did not lock source/probe controls.");
        writer.Connected = false; update.Invoke(window, new object[] { false, "Disconnect source transaction" });
        writer.Connected = true; update.Invoke(window, new object[] { true, "Another speaker connection" });
        readback.Invoke(window, new object[] { (ushort)0x6242, (ushort)4 });
        readback.Invoke(window, new object[] { (ushort)0x6254, (ushort)30152 });
        writer.Pending.SetResult(true); writer.Pending = null;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (sourceButtons[4].IsChecked != true || sourceButtons[1].IsChecked == true) throw new Exception("Old source completion overwrote the new connection.");
        CheckImmediateInputs(assembly, type, window, writer);
        // Render only injected state. Detach device discovery before showing.
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, type.GetMethod("SoncaBleMixerWindow_Loaded", hidden)!);
        window.Closing -= (System.ComponentModel.CancelEventHandler)Delegate.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), window, type.GetMethod("SoncaBleMixerWindow_Closing", hidden)!);
        window.Show(); window.UpdateLayout();
        var sourceImage = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        sourceImage.Render(window);
        var sourcePng = new System.Windows.Media.Imaging.PngBitmapEncoder(); sourcePng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sourceImage));
        using (var stream = File.Create(Path.Combine(output, "input-source-controls.png"))) sourcePng.Save(stream);
        Console.WriteLine("PASS input source UI: immediate firmware-selectable inputs, unsupported inputs dimmed, no startup probing, verified manual selection, failure rollback, fallback warning, pending lock and stale-completion guard; no real control writes.");
        var masterText = (TextBlock)window.FindName("TxtMasterVal");
        var masterSlider = (Slider)window.FindName("SliderMasterVol");
        readback.Invoke(window, new object[] { (ushort)0x6102, (ushort)0 });
        if (masterText.Text == "0" || masterSlider.Visibility == Visibility.Visible)
            throw new Exception("Unverified config zero was shown as the speaker's physical Master level.");
        type.GetMethod("OnMixerParametersReceived", hidden)!.Invoke(window, new object[] { new Dictionary<ushort, ushort> { [0x6102] = 8192 } });
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (masterText.Text != "100" || masterSlider.Value != 100) throw new Exception("Live Master update did not display 100.");
        readback.Invoke(window, new object[] { (ushort)0x6102, (ushort)0 });
        if (masterText.Text != "100") throw new Exception("Cached zero overwrote live Master feedback.");
        if (mute.IsEnabled) throw new Exception("Mute enabled before the speaker state was read.");
        readback.Invoke(window, new object[] { (ushort)0x6448, (ushort)0 });
        string? before = mute.Content.ToString();
        writer.Success = false; click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (mute.Content.ToString() != before || states["Music"] || !mute.IsEnabled) throw new Exception("Failed GATT write falsely changed mute UI.");
        writer.Success = true; click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (!states["Music"] || mute.Content.ToString() == before) throw new Exception("Successful mute request did not update its requested state.");
        click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (states["Music"]) throw new Exception("Successful unmute request did not clear its requested state.");

        var echo = (Button)window.FindName("BtnMuteEcho");
        if (echo.IsEnabled) throw new Exception("An unknown effect state can be overwritten.");
        readback.Invoke(window, new object[] { (ushort)0x6404, (ushort)1 });
        click.Invoke(window, new object[] { echo, new RoutedEventArgs() });
        click.Invoke(window, new object[] { echo, new RoutedEventArgs() });
        byte[] restored = writer.Packets.Last();
        if (restored[4] != 0x64 || restored[5] != 0x04 || restored[6] != 0 || restored[7] != 1)
            throw new Exception("Echo unmute did not use the dedicated enable register; level must stay untouched.");
        readback.Invoke(window, new object[] { (ushort)0x6400, (ushort)4096 });
        var slider = (Slider)window.FindName("SliderMicVol");
        if (slider.Value != 50 || slider.Maximum != 100) throw new Exception("C360 fader display does not match the 0..100 mobile scale.");
        type.GetMethod("QueueCommand", hidden)!.Invoke(window, new object[] { "MicVol", (byte)2, (ushort)0x6400, (ushort)1515, "Mic" });
        writer.Connected = false; update.Invoke(window, new object[] { false, "Disconnected" });
        if (((System.Collections.IDictionary)type.GetField("_pendingCommands", hidden)!.GetValue(window)!).Count != 0)
            throw new Exception("Disconnect retained a pending command for the next speaker.");

        writer.Connected = true; update.Invoke(window, new object[] { true, "Reconnected" });
        readback.Invoke(window, new object[] { (ushort)0x6448, (ushort)0 });
        writer.Pending = new TaskCompletionSource<bool>();
        click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (mute.IsEnabled || states["Music"]) throw new Exception("Pending command updated or re-enabled mute.");
        writer.Connected = false; update.Invoke(window, new object[] { false, "Disconnected while awaiting write" });
        writer.Connected = true; update.Invoke(window, new object[] { true, "Another connection" });
        readback.Invoke(window, new object[] { (ushort)0x6448, (ushort)0 });
        writer.Pending.SetResult(true); writer.Pending = null;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (states["Music"]) throw new Exception("Old completion changed the next connection's mute state.");

        readback.Invoke(window, new object[] { (ushort)0x6400, (ushort)4096 });
        var queue = type.GetMethod("QueueCommand", hidden)!;
        var tick = type.GetMethod("OnSliderDebounceTick", hidden)!;
        int sentBeforeDrag = writer.Packets.Count;
        writer.Pending = new TaskCompletionSource<bool>();
        queue.Invoke(window, new object[] { "MicVol", (byte)2, (ushort)0x6400, (ushort)3000, "Mic" });
        tick.Invoke(window, new object?[] { null, EventArgs.Empty });
        queue.Invoke(window, new object[] { "MicVol", (byte)2, (ushort)0x6400, (ushort)3500, "Mic" });
        tick.Invoke(window, new object?[] { null, EventArgs.Empty });
        if (writer.Packets.Count != sentBeforeDrag + 1) throw new Exception("Dragging queued concurrent slider transactions.");
        writer.Pending.SetResult(true); writer.Pending = null;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        tick.Invoke(window, new object?[] { null, EventArgs.Empty });
        if (writer.Packets.Count != sentBeforeDrag + 2 || writer.Packets.Last()[6] != 0x0D || writer.Packets.Last()[7] != 0xAC)
            throw new Exception("Latest slider value was lost instead of coalesced.");

        foreach (string name in new[] { "BtnSaveSpeakerConfig", "BtnResetSpeakerConfig", "ChkBassBoost", "SliderMusicBass" })
            if (((FrameworkElement)window.FindName(name)).IsEnabled) throw new Exception("Unverified firmware control was enabled: " + name);

        var infoType = assembly.GetType("SoncaAudioInspector.BleDeviceInfo")!;
        object Info(ulong address, short rssi) => Activator.CreateInstance(infoType, address, "Sonca test", rssi, true, DateTime.Now)!;
        var discovered = type.GetMethod("OnDeviceDiscovered", hidden)!;
        discovered.Invoke(window, new[] { Info(1, -20) }); discovered.Invoke(window, new[] { Info(2, -60) });
        var combo = (ComboBox)window.FindName("CmbDevices");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => (ulong)i.Tag == 2);
        discovered.Invoke(window, new[] { Info(3, -10) });
        if ((ulong)((ComboBoxItem)combo.SelectedItem).Tag != 2) throw new Exception("An advertisement changed the operator's selected device.");
        window.Close();
        Console.WriteLine("PASS BLE mixer: no initialization/offline write, readback required, failed mute unchanged, dedicated effect enable, Q12 volume, stale completion rejected, queue cleared and stable device selection.");
    }

    private static void CheckImmediateInputs(Assembly assembly, Type type, Window window, MixerWriterProxy writer)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        bool busy = false;
        type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window, (Func<bool>)(() => busy));
        var readback = type.GetMethod("ApplyParameterReadback", hidden)!;
        var update = type.GetMethod("UpdateInputSourceUi", hidden)!;
        if (window.FindName("ChkMicInput") != null || window.FindName("ChkUsbRecording") != null || window.FindName("TxtRecordingPathStatus") != null
            || type.GetMethod("ProbeInputSourcesAsync", hidden) != null)
            throw new Exception("Removed MIC/recording section or automatic source probing remains in the BLE console.");
        Type peer = assembly.GetType("SoncaAudioInspector.BluetoothAudioPeer")!;
        Type peers = typeof(IReadOnlyList<>).MakeGenericType(peer);
        object peersTask = typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(peers).Invoke(null, new object[] { Array.CreateInstance(peer, 0) })!;
        FieldInfo reader = type.GetField("ReadConnectedAudioPeers", hidden)!;
        reader.SetValue(window, System.Linq.Expressions.Expression.Lambda(reader.FieldType,
            System.Linq.Expressions.Expression.Constant(peersTask, reader.FieldType.GenericTypeArguments[0])).Compile());
        var clock = type.GetMethod("RefreshAudioPeersAsync", hidden)!;
        int SourceWrites() => writer.Packets.Count(p => p.Length == 9 && p[2] == 4 && p[4] == 0x62 && p[5] == 0x42);
        int before = SourceWrites();
        for (int i = 0; i < 3; i++) Complete((Task)clock.Invoke(window, null)!);
        if (SourceWrites() != before) throw new Exception("Ordinary BLE refresh switched a speaker input automatically.");
        type.GetMethod("OnMixerParametersReceived", hidden)!.Invoke(window, new object[] { new Dictionary<ushort, ushort> { [0x6254] = 64 } });
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var usb = (RadioButton)window.FindName("RadioInputUsb");
        var line = (RadioButton)window.FindName("RadioInputLineIn");
        if (usb.IsEnabled || usb.Opacity != .4 || !line.IsEnabled || line.IsChecked != true || SourceWrites() != before)
            throw new Exception("BLE input-flag notification did not immediately update the lights without switching sources.");
        readback.Invoke(window, new object[] { (ushort)0x6254, (ushort)30152 });
        if (!usb.IsEnabled || usb.Opacity != 1 || SourceWrites() != before) throw new Exception("Newly selectable source did not light immediately.");
        busy = true; update.Invoke(window, new object[] { true });
        usb.IsChecked = true;
        if (usb.IsEnabled || line.IsChecked != true || SourceWrites() != before) throw new Exception("Input switching was allowed during acquisition.");
        busy = false; update.Invoke(window, new object[] { true });
        readback.Invoke(window, new object[] { (ushort)0x6400, (ushort)4096 });
        Console.WriteLine("PASS music-input flags: immediate BLE updates, no automatic source writes, MIC/recording section removed and acquisition guarded.");
    }

}

public class MixerWriterProxy : DispatchProxy
{
    public readonly Dictionary<ushort,ushort> OptionState = new();
    public int ConnectionReads;
    public bool Connected, Success;
    public TaskCompletionSource<bool>? Pending;
    public List<byte[]> Packets { get; } = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "get_IsConnected") { ConnectionReads++; return Connected; }
        if (targetMethod?.Name == "get_SupportsMixerControls") return true;
        if (targetMethod?.Name == "get_MixerState") return OptionState;
        if (targetMethod?.Name == "SetOptionAsync")
        {
            ushort mask = args![0]!.ToString() switch { "MicFbx" => 1, "BassBoost" => 2, _ => 512 };
            if (Success) OptionState[0x6254] = (ushort)((bool)args[1]! ? OptionState.GetValueOrDefault((ushort)0x6254) | mask : OptionState.GetValueOrDefault((ushort)0x6254) & ~mask);
            return Pending?.Task ?? Task.FromResult(Success);
        }
        if (targetMethod?.Name == "SendPacketAsync")
        {
            byte[] packet = ((byte[])args![0]!).ToArray(); Packets.Add(packet);
            return Pending?.Task ?? Task.FromResult(Success);
        }
        throw new NotSupportedException(targetMethod?.Name);
    }
}
