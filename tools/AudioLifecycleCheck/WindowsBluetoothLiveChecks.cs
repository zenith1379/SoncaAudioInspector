using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using SoncaAudioInspector;

internal static class WindowsBluetoothLiveChecks
{
    public static async Task Unpair(string output, ulong controller)
    {
        Directory.CreateDirectory(output);
        using var manager = new SoncaBleManager();
        manager.LogMessage += Console.WriteLine;
        await Task.Run(() => manager.StartScanning()).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1500);
        try
        {
            if (!await Task.Run(() => manager.ConnectAsync(controller)).WaitAsync(TimeSpan.FromSeconds(25)) || manager.ConnectedBluetoothAddress != controller
                || manager.FirmwareInfoPayload.Length == 0)
                throw new InvalidOperationException("Cannot verify the current BLE speaker; no Windows removal attempted");
            File.WriteAllText(Path.Combine(output, "identity-before.json"), JsonSerializer.Serialize(new
            { controller = controller.ToString("X12"), manager.FirmwareIdentity, before = WindowsBluetoothPlayback.Read() }));
            manager.SuppressAutomaticConnection();
            var remove = typeof(SoncaBleManager).Assembly.GetType("SoncaAudioInspector.BluetoothDeviceRemoval")!
                .GetMethod("RemoveMeasuredSpeakerAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var report = await (Task<BluetoothRemovalReport>)remove.Invoke(null, new object[] { controller, CancellationToken.None })!;
            File.WriteAllText(Path.Combine(output, "removal.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "endpoints-after.json"), JsonSerializer.Serialize(WindowsBluetoothPlayback.Read(), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            if (!report.Success) throw new Exception(report.Message);
        }
        finally { manager.SuppressAutomaticConnection(); }
    }

    public static async Task Discover(string output, ulong controller)
    {
        Directory.CreateDirectory(output);
        string[] properties={"System.Devices.Aep.DeviceAddress","System.Devices.Aep.IsPaired","System.Devices.Aep.IsConnected","System.Devices.Aep.ContainerId"};
        var reports=new ConcurrentQueue<object>();
        bool Target(DeviceInformation info)=>info.Name.Contains("KBEAT",StringComparison.OrdinalIgnoreCase)
            || info.Properties.TryGetValue(properties[0],out object? raw) && ulong.TryParse(raw?.ToString()?.Replace(":", ""),System.Globalization.NumberStyles.HexNumber,null,out ulong address)
                && SoncaBleDiscovery.IsSameSpeaker(controller,address);
        foreach(var (name,selector) in new[]{("default",BluetoothDevice.GetDeviceSelector()),("unpaired",BluetoothDevice.GetDeviceSelectorFromPairingState(false)),("paired",BluetoothDevice.GetDeviceSelectorFromPairingState(true))})
        {
            Console.WriteLine(name+" SELECTOR "+selector);
            using var queryTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            DeviceInformationCollection found;
            try{found=await DeviceInformation.FindAllAsync(selector,properties).AsTask(queryTimeout.Token);}
            catch(OperationCanceledException){reports.Enqueue(new{phase="query-timeout",name,selector});Console.WriteLine(name+" QUERY TIMEOUT");continue;}
            var rows=found.Where(Target).Select(info=>new {info.Id,info.Name,info.Pairing.IsPaired,info.Pairing.CanPair,properties=info.Properties.ToDictionary(p=>p.Key,p=>p.Value?.ToString())}).ToArray();
            reports.Enqueue(new {phase="known",name,selector,count=found.Count,targets=rows});
            Console.WriteLine(name+" FOUND "+JsonSerializer.Serialize(rows));
        }
        async Task Watch(string name,string selector)
        {
            var devices=new ConcurrentDictionary<string,DeviceInformation>();
            var watcher=DeviceInformation.CreateWatcher(selector,properties,DeviceInformationKind.AssociationEndpoint);
            watcher.Added+=(_,info)=>{devices[info.Id]=info;if(Target(info))reports.Enqueue(new{phase="added",watch=name,info.Id,info.Name,info.Pairing.IsPaired,info.Pairing.CanPair,properties=info.Properties.ToDictionary(p=>p.Key,p=>p.Value?.ToString())});};
            watcher.Updated+=(_,update)=>{if(devices.TryGetValue(update.Id,out var info)){info.Update(update);if(Target(info))reports.Enqueue(new{phase="updated",watch=name,info.Id,info.Name,info.Pairing.IsPaired,info.Pairing.CanPair,properties=info.Properties.ToDictionary(p=>p.Key,p=>p.Value?.ToString())});}};
            watcher.Start();try{await Task.Delay(10000);}finally{watcher.Stop();}
            reports.Enqueue(new{phase="watch-end",name,count=devices.Count,status=watcher.Status.ToString()});
        }
        await Watch("all-classic","System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8B-4647-BB8A-263B43F0F974}\"");
        string json=JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(output,"discovery.json"),json);
        Console.WriteLine(json);
    }

    public static async Task Pair(string output,ulong controller)
    {
        Directory.CreateDirectory(output);
        using var manager=new SoncaBleManager();
        manager.LogMessage+=message=>{if(message.StartsWith("[BLE]"))Console.WriteLine(message);};
        manager.StartScanning();await Task.Delay(1500);
        if(!await manager.ConnectAsync(controller))throw new Exception("Cannot verify BLE target; no Windows pairing attempted");
        if(!SoncaBleMixerProfile.TryReadModelId(manager.FirmwareInfoPayload,out ushort model)||model!=1075)
            throw new Exception("Requested KBEATBOX/C360 model 1075 not confirmed; no pairing");
        File.WriteAllText(Path.Combine(output,"ble-identity.json"),JsonSerializer.Serialize(new{controller=controller.ToString("X12"),manager.FirmwareIdentity,model,source=manager.MixerState.GetValueOrDefault(SoncaBleMixerProfile.InputSource)}));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(55));
        try
        {
            var endpoint=await BluetoothPlaybackConnection.EnsureAsync(controller,null,true,()=>WindowsBluetoothPlayback.Read(),
                (allow,token)=>WindowsBluetoothPairing.PrepareSpeakerAsync(controller,allow,token,Console.WriteLine),timeout.Token,Console.WriteLine);
            File.WriteAllText(Path.Combine(output,"connected-endpoint.json"),JsonSerializer.Serialize(endpoint,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("CONNECTED "+JsonSerializer.Serialize(endpoint));
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Console.WriteLine("FAILED "+ex);
        }
        finally{manager.Disconnect();}
    }
}
