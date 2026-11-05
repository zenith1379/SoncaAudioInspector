using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

internal static class BleModelControlChecks
{
    public static async Task Restore(string baselinePath,string output)
    {
        Directory.CreateDirectory(output);
        using var saved=JsonDocument.Parse(File.ReadAllText(baselinePath));
        ulong address=Convert.ToUInt64(saved.RootElement.GetProperty("address").GetString(),16);
        var baseline=saved.RootElement.GetProperty("parameters").EnumerateObject().ToDictionary(p=>Convert.ToUInt16(p.Name,16),p=>p.Value.GetUInt16());
        using var manager=new SoncaBleManager();manager.LogMessage+=Console.WriteLine;
        if(!await manager.ConnectAsync(address))throw new Exception("Restore connection unavailable; no writes.");
        foreach(var p in baseline.Where(p=>p.Key!=SoncaBleMixerProfile.InputSource && SoncaBleMixerProfile.CanWrite(p.Key,p.Value)))
        {
            if(manager.MixerState.TryGetValue(p.Key,out ushort current) && current==p.Value)continue;
            var definition=SoncaMixerControls.ForFirmware(manager.FirmwareInfoPayload).FirstOrDefault(c=>c.Register==p.Key);
            if(!await manager.SetParamAsync(0,p.Key,definition?.CommandFor(p.Value)??p.Value,"Restore prior verified baseline"))throw new Exception("Baseline restore not confirmed "+p.Key.ToString("X4"));
        }
        foreach(var option in Enum.GetValues<SoncaMixerOption>())
            if(SoncaBleMixerProfile.SupportsOption(baseline[0x6254],option)
                && !await manager.SetOptionAsync(option,(baseline[0x6254]&SoncaBleMixerProfile.OptionMask(option))!=0))throw new Exception("Option restore failed");
        await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket());
        var diff=baseline.Where(p=>manager.MixerState.GetValueOrDefault(p.Key)!=p.Value).ToDictionary(p=>p.Key.ToString("X4"),p=>new{before=p.Value,after=manager.MixerState.GetValueOrDefault(p.Key)});
        File.WriteAllText(Path.Combine(output,"restoration.json"),JsonSerializer.Serialize(new {diff},new JsonSerializerOptions{WriteIndented=true}));
        if(diff.Count>0)throw new Exception("Fresh baseline differs");
        Console.WriteLine("PASS fresh reconnect/readback; all "+baseline.Count+" parameters match prior baseline.");
    }
    public static async Task Run(string output, ulong address, bool exercise, bool extra = false,ushort? only=null)
    {
        Directory.CreateDirectory(output);
        using var manager = new SoncaBleManager();
        manager.LogMessage += Console.WriteLine;
        void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name + ".json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        if (!await manager.ConnectAsync(address)) throw new Exception("Requested BLE controller could not connect; no control writes sent.");
        if (!await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Read model controls"))
            throw new Exception("No fresh mixer snapshot; no control writes sent.");
        var baseline = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
        Save("before", new { address = address.ToString("X12"), manager.FirmwareIdentity,
            infoPayloadHex = Convert.ToHexString(manager.FirmwareInfoPayload), manager.SupportsMixerControls,
            manager.SupportsInputRouting, parameters = baseline.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) });
        Console.WriteLine($"Firmware: {manager.FirmwareIdentity}; mixer={manager.SupportsMixerControls}; parameters={baseline.Count}");
        if (!exercise) return;
        var results = new List<object>();
        try
        {
            var registers = new List<ushort> { SoncaBleMixerProfile.MicVolume };
            if (extra) registers.AddRange(new ushort[] {0x6102,0x6103,0x6402,0x6403,0x6467});
            foreach (string channel in new[] { "Mic", "Music", "Master", "Echo", "Reverb", "Live" })
                if (manager.TryMuteRegister(channel, out ushort register, out _)) registers.Add(register);
            if (extra) registers.AddRange(SoncaMixerControls.ForFirmware(manager.FirmwareInfoPayload).Select(c => c.Register));
            foreach (ushort register in registers)
            {
                if(only.HasValue && register!=only)continue;
                if (!baseline.TryGetValue(register, out ushort original) || !SoncaBleMixerProfile.CanWrite(register, original)) continue;
                var definition = SoncaMixerControls.ForFirmware(manager.FirmwareInfoPayload).FirstOrDefault(c => c.Register==register);
                ushort target = definition != null ? definition.Encode(definition.Decode(original)>definition.Minimum
                    ? definition.Decode(original)-definition.Step : definition.Decode(original)+definition.Step)
                    : register is 0x6400 or 0x6102 or 0x6103 or 0x6402 or 0x6403 or 0x6467
                        ? (ushort)Math.Max(0, original - (only.HasValue?410:41)) : (ushort)(1 - original);
                try
                {
                    bool confirmed = await manager.SetParamAsync(0, register, definition?.CommandFor(target)??target, "Reversible model control check");
                    if(!confirmed && extra && definition!=null)
                    {
                        target=definition.Encode(Math.Clamp(definition.Decode(original)-definition.Step*5,definition.Minimum,definition.Maximum));
                        confirmed=await manager.SetParamAsync(0,register,definition.CommandFor(target),"Check larger supported control step");
                    }
                    results.Add(new { register = register.ToString("X4"), original, target, confirmed,
                        actual = manager.MixerState.GetValueOrDefault(register) });
                    if (!confirmed && !extra) throw new Exception($"Control not confirmed: 0x{register:X4}");
                    Console.WriteLine($"{(confirmed?"VERIFIED":"NOT_CONFIRMED")} 0x{register:X4}: {original} -> {target}");
                }
                finally
                {
                    await manager.SetParamAsync(0, register, definition?.CommandFor(original)??original, "Restore original model control");
                    bool Exact()=>manager.IsConnected && manager.MixerState.TryGetValue(register,out ushort actual) && actual==original;
                    if(!Exact() && definition is {Decibels:false} && original<100 && manager.IsConnected
                        && manager.MixerState.TryGetValue(register,out ushort quantized) && quantized==original-1)
                        await manager.SetParamAsync(0,register,(ushort)(original+1),"Restore exact percentage after firmware quantization");
                    if(!Exact()) await manager.SetParamAsync(0,register,definition?.CommandFor(original)??original,"Retry model control restoration");
                    if(!Exact())
                        throw new Exception($"RESTORE FAILED 0x{register:X4}; original={original}");
                }
            }
            foreach (var option in Enum.GetValues<SoncaMixerOption>())
            {
                if(only.HasValue)continue;
                if (!baseline.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort flags)
                    || !SoncaBleMixerProfile.SupportsOption(flags, option)) continue;
                bool original = (flags & SoncaBleMixerProfile.OptionMask(option)) != 0;
                try
                {
                    bool confirmed = await manager.SetOptionAsync(option, !original);
                    results.Add(new { option = option.ToString(), original, target = !original, confirmed });
                    if (!confirmed) throw new Exception("Option not confirmed: " + option);
                    Console.WriteLine("VERIFIED " + option);
                }
                finally
                {
                    if (!await manager.SetOptionAsync(option, original) && !await manager.SetOptionAsync(option, original))
                        throw new Exception("RESTORE FAILED option " + option);
                }
            }
        }
        finally
        {
            bool refreshed = await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket());
            var diff = baseline.Where(p => !manager.MixerState.TryGetValue(p.Key, out ushort value) || value != p.Value)
                .ToDictionary(p => p.Key.ToString("X4"), p => new { before = p.Value, after = manager.MixerState.GetValueOrDefault(p.Key) });
            Save("controls", new { results, refreshed, diff, parameters = manager.MixerState.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) });
            Console.WriteLine($"Restoration refreshed={refreshed}, diff={diff.Count}");
            if (!refreshed || diff.Count > 0) throw new Exception("Final mixer snapshot differs from baseline; inspect controls.json.");
        }
        int replies = 0;
        manager.ReplyReceived += _ => replies++;
        if (!await manager.EnsureConnectedAsync(address) || !manager.SupportsMixerControls || replies != 0)
            throw new Exception("Existing common-model connection was lost or handshaken again.");
        int unconfirmed=results.Count(r=>!JsonSerializer.SerializeToElement(r).GetProperty("confirmed").GetBoolean());
        Console.WriteLine($"{(unconfirmed==0?"PASS":"PARTIAL")} model controls: {results.Count-unconfirmed}/{results.Count} confirmed; exact baseline restoration. No PCM, EEPROM or reset.");
    }
}
