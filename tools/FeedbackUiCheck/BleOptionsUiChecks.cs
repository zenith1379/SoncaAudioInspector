using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class BleOptionsUiChecks
{
    public static void Run(Assembly assembly)
    {
        string data = System.IO.Path.Combine(AppContext.BaseDirectory, "ble-preferences-check");
        System.IO.Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", data);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        writer.Connected = writer.Success = true; writer.OptionState[0x6254] = 30152;
        var window = (Window)type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2).Invoke(new object?[] { null, writer });
        var readback = type.GetMethod("ApplyParameterReadback", hidden)!;
        readback.Invoke(window, new object[] { (ushort)0x6254, (ushort)30152 });
        var fbx = (CheckBox)window.FindName("ChkFbx");
        if (!fbx.IsEnabled || fbx.IsChecked != false || !((CheckBox)window.FindName("ChkBassBoost")).IsEnabled
            || !((CheckBox)window.FindName("ChkEnhancer")).IsEnabled) throw new Exception("Verified C360 options remain disabled.");
        if (!((TextBlock)window.FindName("TxtAppBuild")).Text.StartsWith("APP ")) throw new Exception("Build identity not visible.");
        object option = Enum.Parse(assembly.GetType("SoncaAudioInspector.SoncaMixerOption")!, "MicFbx");
        var change = type.GetMethod("ChangeOptionAsync", hidden)!;
        void Complete(Task task)
        {
            while (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        Complete((Task)change.Invoke(window, new[] { option, (object)true })!);
        if (fbx.IsChecked != true || writer.OptionState[0x6254] != 30153) throw new Exception("Option readback not reflected.");
        writer.Success = false;
        Complete((Task)change.Invoke(window, new[] { option, (object)false })!);
        if (fbx.IsChecked != true || writer.OptionState[0x6254] != 30153) throw new Exception("Rejected option appeared applied.");
        writer.Success = true; writer.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = (Task)change.Invoke(window, new[] { option, (object)false })!;
        if (fbx.IsEnabled || fbx.IsChecked != true) throw new Exception("Pending option was optimistic or unlocked.");
        writer.Pending.SetResult(true); writer.Pending = null; Complete(pending);
        if (!fbx.IsEnabled || fbx.IsChecked != false) throw new Exception("Option completion stuck.");
        type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window, new Func<bool>(() => true));
        Complete((Task)change.Invoke(window, new[] { option, (object)true })!);
        if (writer.OptionState[0x6254] != 30152 || fbx.IsEnabled) throw new Exception("Option changed during measurement.");
        type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window, new Func<bool>(() => false));
        if(window.FindName("ChkRemoveMeasuredBluetooth")!=null)throw new Exception("Model Bluetooth cleanup is still in BLE settings");
        if(window.FindName("TxtBenchRuns")!=null || window.FindName("TxtBenchPasses")!=null)throw new Exception("Run policy remains in BLE settings");
        ((TextBox)window.FindName("TxtBleNameFilter")).Text = "C360, KBEAT";
        type.GetMethod("SaveBenchPreferences", hidden)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
        var prefType = assembly.GetType("SoncaAudioInspector.AudioBenchPreferences")!;
        var saved = prefType.GetMethod("Load")!.Invoke(null, null)!;
        if(System.Text.Json.JsonSerializer.Serialize(saved).Contains("BluetoothAutoRemove"))throw new Exception("Bluetooth cleanup still persists as a global BLE preference");
        var devices = (System.Collections.IList)type.GetField("_deviceList", hidden)!.GetValue(window)!;
        var infoType = assembly.GetType("SoncaAudioInspector.BleDeviceInfo")!;
        object Device(ulong address, string name, short rssi, DateTime seen) => Activator.CreateInstance(infoType, address, name, rssi, true, seen)!;
        devices.Add(Device(1,"C360 test",-65,DateTime.Now)); devices.Add(Device(2,"Unrelated",-20,DateTime.Now));
        type.GetMethod("UpdateDeviceCombo", hidden)!.Invoke(window, null);
        type.GetMethod("UpdateSignalStrength", hidden)!.Invoke(window, null);
        if (((ComboBox)window.FindName("CmbDevices")).Items.Count != 1 || ((ProgressBar)window.FindName("BleSignalBar")).Value != 3) throw new Exception("BLE name filter/RSSI not rendered");
        devices[0] = Device(1,"C360 test",0,DateTime.Now);
        type.GetMethod("UpdateSignalStrength", hidden)!.Invoke(window, null);
        if (((ProgressBar)window.FindName("BleSignalBar")).Value != 0) throw new Exception("Unknown RSSI rendered strong");
        devices[0] = Device(1,"C360 test",-40,DateTime.Now.AddSeconds(-30));
        type.GetMethod("UpdateSignalStrength", hidden)!.Invoke(window, null);
        if (((ProgressBar)window.FindName("BleSignalBar")).Value != 0) throw new Exception("Stale RSSI rendered current");
        devices[0] = Device(1,"C360 test",-65,DateTime.Now); type.GetMethod("UpdateSignalStrength", hidden)!.Invoke(window, null);
        ((FrameworkElement)window.FindName("PanelSettings")).Visibility = Visibility.Visible;
        var content = (FrameworkElement)window.Content; window.Content = null;
        content.Measure(new Size(1400,900)); content.Arrange(new Rect(0,0,1400,900)); content.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(1400,900,96,96,System.Windows.Media.PixelFormats.Pbgra32); image.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var file = System.IO.File.Create(System.IO.Path.Combine(data,"ble-settings.png"))) png.Save(file);
        window.Close();
        Console.WriteLine("PASS BLE filter persistence; model cleanup and per-test voting removed from BLE settings; live/unknown/stale RSSI display. Mock advertisements only.");
        Console.WriteLine("PASS firmware-controlled Mic FBX/Bass boost/Enhancer UI; build identity; confirmed readback; rejected write rollback; pending lock; measurement ownership. No physical writes.");
        CommonModels(assembly, data);
    }

    private static void CommonModels(Assembly assembly, string output)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        writer.Connected = writer.Success = true; writer.C360Profile = false;
        using var fixture = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Acoustic3-controls.json")));
        writer.FirmwarePayload=Convert.FromHexString(fixture.RootElement.GetProperty("infoPayloadHex").GetString()!);
        foreach (var p in fixture.RootElement.GetProperty("parameters").EnumerateObject()) writer.OptionState[Convert.ToUInt16(p.Name, 16)] = p.Value.GetUInt16();
        var original = new Dictionary<ushort,ushort>(writer.OptionState);
        var window = (Window)type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2).Invoke(new object?[] { null, writer });
        void Snapshot(Dictionary<ushort,ushort> state)
        {
            writer.OptionState.Clear(); foreach(var p in state) writer.OptionState[p.Key] = p.Value;
            type.GetMethod("OnMixerStateReceived", hidden)!.Invoke(window, new object[] { state });
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        Snapshot(new(writer.OptionState));
        var faders=new[]{"SliderMicVol","SliderEcho","SliderReverb","SliderMusicVol","SliderMasterVol","SliderLiveVol"};
        if(faders.Any(n=>!((Slider)window.FindName(n)).IsEnabled || !((Slider)window.FindName(n)).IsHitTestVisible))
            throw new Exception("A reported fader remained locked after the user enabled all volume controls.");
        var dsp=(System.Collections.IDictionary)type.GetField("_dspControls",hidden)!.GetValue(window)!;
        if(dsp.Count!=20) throw new Exception("Mapped tone/effect controls are missing.");
        var tick=type.GetMethod("OnSliderDebounceTick",hidden)!;
        type.GetField("_suppressSliderEvents",hidden)!.SetValue(window,false);
        var music=(Slider)window.FindName("SliderMusicVol");
        double musicOriginal=music.Value; music.Value=musicOriginal-1;
        tick.Invoke(window,new object?[]{null,EventArgs.Empty});
        if(writer.Packets.Last()[4]!=0x61 || writer.Packets.Last()[5]!=0x03)throw new Exception("Music fader wrote the wrong register.");
        type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window,new Func<bool>(()=>true));
        type.GetMethod("SetControlAvailability",hidden)!.Invoke(window,new object[]{true});
        if(faders.Any(n=>((Slider)window.FindName(n)).IsEnabled))throw new Exception("Additional faders stayed enabled during capture.");
        type.GetProperty("IsAudioMeasurementBusy")!.SetValue(window,new Func<bool>(()=>false));
        Snapshot(original);
        string[] channels = { "Mic", "Music", "Master", "Echo", "Reverb", "Live" };
        if (channels.Any(c => !((Button)window.FindName("BtnMute" + c)).IsEnabled)
            || !((Slider)window.FindName("SliderMicVol")).IsEnabled
            || new[] { "ChkFbx", "ChkBassBoost", "ChkEnhancer" }.Any(c => !((CheckBox)window.FindName(c)).IsEnabled)
            || ((RadioButton)window.FindName("RadioInputKaraoke")).Content.ToString() != "KARAOKE")
            throw new Exception("Recorded ACOUSTIC3 controls/source label were blocked or changed to the C360 label.");
        var click = type.GetMethod("BtnMuteChannel_Click", hidden)!;
        foreach (string channel in channels)
        {
            int before = writer.Packets.Count;
            var button = (Button)window.FindName("BtnMute" + channel);
            click.Invoke(window, new object[] { button, new RoutedEventArgs() });
            click.Invoke(window, new object[] { button, new RoutedEventArgs() });
            if (writer.Packets.Count != before + 2) throw new Exception("Common-model mute toggle failed: " + channel);
        }
        // A fresh full snapshot must revoke missing controls on the same link.
        Snapshot(new() { [0x6400] = 65535, [0x6447] = 65535, [0x6254] = 65535 });
        if (channels.Any(c => ((Button)window.FindName("BtnMute" + c)).IsEnabled)
            || ((Slider)window.FindName("SliderMicVol")).IsEnabled
            || new[] { "ChkFbx", "ChkBassBoost", "ChkEnhancer" }.Any(c => ((CheckBox)window.FindName(c)).IsEnabled))
            throw new Exception("Missing/sentinel mixer controls borrowed old enable state.");
        if(faders.Any(n=>((Slider)window.FindName(n)).IsEnabled)) throw new Exception("A missing volume fader borrowed another snapshot.");
        writer.AlternateReverb = true;
        Snapshot(new() { [0x644A] = 1, [0x640A] = 1 });
        var reverb = (Button)window.FindName("BtnMuteReverb");
        click.Invoke(window, new object[] { reverb, new RoutedEventArgs() });
        if (writer.Packets.Last()[5] != 0x4A || writer.Packets.Last()[7] != 0)
            throw new Exception("Alternate reverb firmware did not write inverted enable 0x644A.");
        writer.AlternateReverb = false; Snapshot(original);
        type.GetMethod("UpdateConnectionUi", hidden)!.Invoke(window, new object[] { true, "Fixture readback" });
        object reply = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.SoncaBleReply")!, (byte)1, (byte)0,
            Convert.FromHexString(fixture.RootElement.GetProperty("infoPayloadHex").GetString()!))!;
        ((TextBlock)window.FindName("TxtFirmwareIdentity")).Text = (string)assembly.GetType("SoncaAudioInspector.SoncaBuildIdentity")!.GetMethod("Firmware")!.Invoke(null, new[] { reply })!;
        var content = (FrameworkElement)window.Content; window.Content = null;
        content.Measure(new Size(1200,800)); content.Arrange(new Rect(0,0,1200,800)); content.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap(1200,800,96,96,System.Windows.Media.PixelFormats.Pbgra32); image.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var file = System.IO.File.Create(System.IO.Path.Combine(output,"acoustic3-mixer-ui.png"))) png.Save(file);
        window.Close();
        Console.WriteLine("PASS recorded ACOUSTIC3 UI: six mute/effect controls, MIC gain, options and generic source label; stale/missing/sentinel revocation; alternate reverb register. Injected state only.");
    }
}
