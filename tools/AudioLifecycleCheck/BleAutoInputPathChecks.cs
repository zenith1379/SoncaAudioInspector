using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

internal static class BleAutoInputPathChecks
{
    public static async Task WindowsState(string output)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        foreach (var query in new[] { ("classic", Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelector()), ("classic-connected", Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)), ("LE", Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelector()) })
        {
            var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(query.Item2,
                new[] { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired" });
            foreach (var info in infos.Where(i => SoncaBleProtocol.IsLikelySoncaDevice(i.Name)))
            {
                string? state = null, address = null, error = null;
                try { using var device = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id); state = device?.ConnectionStatus.ToString(); address = device?.BluetoothAddress.ToString("X12"); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                rows.Add(new { query = query.Item1, info.Name, info.Id, kind = info.Kind.ToString(), paired = info.Pairing.IsPaired, state, address, error, properties = info.Properties });
            }
        }
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "windows-bt-state.json"), json); Console.WriteLine(json);
    }

    public static async Task Run(string output, bool connect)
    {
        Directory.CreateDirectory(output);
        var usb = SoncaUsbRecordingEndpoint.Read();
        var peers = await SoncaBleManager.ConnectedAudioPeersAsync();
        var known = await SoncaBleManager.KnownAssociatedControllersAsync(peers);
        var selected = SoncaBleDiscovery.Preferred(known, peers);
        bool connected = false, read = false;
        using var manager = new SoncaBleManager();
        manager.LogMessage += Console.WriteLine;
        if (selected == null && peers.Count > 0)
        {
            manager.StartScanning(); await Task.Delay(6000); manager.StopScanning();
            selected = SoncaBleDiscovery.Preferred(manager.DiscoveredDevices, peers);
        }
        if (connect && selected != null)
        {
            connected = await manager.ConnectAsync(selected.Address);
            if (connected) read = await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Read automatically associated speaker");
        }
        var report = new { usb, peers, known = known.Select(d => new { d.Name, d.FormattedAddress }),
            selected = selected?.FormattedAddress, connected, read, source = read ? (int?)manager.MixerState.GetValueOrDefault(SoncaBleMixerProfile.InputSource) : null,
            micMute = read ? (int?)manager.MixerState.GetValueOrDefault(SoncaBleMixerProfile.MicMute) : null, controlWrites = false };
        File.WriteAllText(Path.Combine(output, "auto-input-paths.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (usb == null || (connect && (!connected || !read))) throw new Exception("Live automatic connection/USB-C verification incomplete; see report.");
    }
}
