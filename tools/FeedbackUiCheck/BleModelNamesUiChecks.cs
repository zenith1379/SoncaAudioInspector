using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class BleModelNamesUiChecks
{
    public static void Run(Assembly assembly, string output)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        Directory.CreateDirectory(output);
        const ulong acoustic = 0x439F3F264237, other = 0x423E3F752ECC;
        string data = Path.Combine(output, "data"); Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", data);
        File.WriteAllText(Path.Combine(data,"audio-bench-preferences.json"), JsonSerializer.Serialize(new
        {
            BleNameFilter = "Sonca BLE", BleModelFilter = "acoustic 3", BleFilterByModel = false,
            KnownDeviceModels = new Dictionary<ulong,ushort> { [acoustic] = 1071, [other] = 1075 }
        }));
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        var constructor = type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2);
        var window = (Window)constructor.Invoke(new object?[] { null, writer });
        try
        {
            var devices = (IList)type.GetField("_deviceList", hidden)!.GetValue(window)!;
            var deviceType = assembly.GetType("SoncaAudioInspector.BleDeviceInfo")!;
            object Device(ulong address) => Activator.CreateInstance(deviceType, address, $"Sonca BLE {address:X12}", (short)-54, true, DateTime.Now)!;
            devices.Add(Device(acoustic)); devices.Add(Device(other)); devices.Add(Device(0x439F3F264238));
            void Refresh() => type.GetMethod("UpdateDeviceCombo", hidden)!.Invoke(window, null);
            var filter = (TextBox)window.FindName("TxtBleNameFilter");
            var combo = (ComboBox)window.FindName("CmbDevices");
            Refresh();
            var toggle = (Button)window.FindName("BtnScan");
            void Toggle() => toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(toggle.Content.ToString()!="Lọc" || combo.Items.Count!=3) throw new Exception("Default BLE-name mode hid unrecognized devices or has wrong button");
            Toggle();
            if(toggle.Content.ToString()!="Dừng") throw new Exception("Model filter did not start");
            if (combo.Items.Count != 1 || (ulong)((ComboBoxItem)combo.SelectedItem).Tag != acoustic
                || !((TextBlock)window.FindName("TxtDetectedModel")).Text.Contains("ACOUSTIC 3"))
                throw new Exception("Generic advertisement could not be filtered by verified model name");
            foreach(string query in new[]{"1071","ID 1071","0x042F","ACOUSTIC3"})
            {filter.Text=query;if(combo.Items.Count!=1 || (ulong)((ComboBoxItem)combo.SelectedItem).Tag!=acoustic)throw new Exception("Model/ID query not applied: "+query);}
            filter.Text = "KBEATBOX";
            if (combo.Items.Count != 1 || (ulong)((ComboBoxItem)combo.SelectedItem).Tag != other) throw new Exception("Filter retained previous model selection");
            filter.Text = "nonexistent";
            if (combo.Items.Count != 0 || ((Button)window.FindName("BtnConnect")).IsEnabled) throw new Exception("Empty filter retained a connectable selection");
            filter.Text = "acoustic 3";
            Toggle();
            if(toggle.Content.ToString()!="Lọc" || filter.Text!="Sonca BLE" || combo.Items.Count!=3)throw new Exception("Stop did not restore separate BLE-name query");
            filter.Text="264238";
            if(combo.Items.Count!=1 || (ulong)((ComboBoxItem)combo.SelectedItem).Tag!=0x439F3F264238)throw new Exception("Unrecognized BLE device cannot be found in raw mode");
            Toggle();
            if(filter.Text!="acoustic 3" || combo.Items.Count!=1)throw new Exception("Switching modes lost model query");
            var dsp=(StackPanel)window.FindName("PanelDspControls"); var parent=(StackPanel)dsp.Parent;
            var save=(Button)window.FindName("BtnSaveBlePreferences");
            if(parent.Children.IndexOf(save)!=parent.Children.IndexOf(dsp)+1)throw new Exception("Save button is not below DSP controls");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using(var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(data,"audio-bench-preferences.json"))))
                if(!saved.RootElement.GetProperty("BleFilterByModel").GetBoolean() || saved.RootElement.GetProperty("BleNameFilter").GetString()!="264238"
                    || saved.RootElement.GetProperty("BleModelFilter").GetString()!="acoustic 3")throw new Exception("Mode and separate queries were not saved");
            writer.Connected = true;
            writer.FirmwarePayload = Encoding.ASCII.GetBytes("SCA_MAIN48_3x100A.ACOUSTIC3\0")
                .Concat(new byte[]{45,4,47}).Concat(" VER A:1.1.6"u8.ToArray()).ToArray();
            type.GetMethod("RefreshModelIdentity", hidden)!.Invoke(window, null);
            string detected = ((TextBlock)window.FindName("TxtDetectedModel")).Text;
            if (!detected.StartsWith("Model: ACOUSTIC 3") || detected.Contains("đã lưu")) throw new Exception("Live firmware model still displayed as cached");
            // Do not Show the window: that would start real BLE discovery. Render only its content.
            ((FrameworkElement)window.FindName("PanelSettings")).Visibility = Visibility.Visible;
            var content = (FrameworkElement)window.Content; window.Content = null;
            content.Measure(new Size(1400,900)); content.Arrange(new Rect(0,0,1400,900)); content.UpdateLayout();
            var image = new RenderTargetBitmap(1400,900,96,96,PixelFormats.Pbgra32); image.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(output,"model-name-filter.png"))) png.Save(file);
            if (writer.Packets.Count != 0) throw new Exception("Model discovery UI emitted a hardware command");
            Console.WriteLine("PASS Lọc/Dừng model-ID/name modes; decimal/hex/name queries; raw unrecognized BLE lookup; saved separate queries; Save below DSP; selection/empty filter; live/cache labels. Offline WPF render, zero hardware packets.");
        }
        finally { window.Close(); }
    }
}
