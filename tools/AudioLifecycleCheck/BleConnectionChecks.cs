using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

internal static class BleConnectionChecks
{
    private static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    public static async Task ReadinessAsync(string output, ulong address)
    {
        Directory.CreateDirectory(output);
        using var manager = new SoncaAudioInspector.SoncaBleManager();
        Task<bool>? concurrent = null;
        bool? returnedBeforeSnapshot = null;
        int registersAtInfo = -1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        manager.LogMessage += message =>
        {
            if (!message.StartsWith("[BLE] INFO được loa xác nhận:") || concurrent != null) return;
            registersAtInfo = manager.MixerState.Count;
            concurrent = manager.EnsureConnectedAsync(address, timeout.Token, expectedModelId: 1075);
            returnedBeforeSnapshot = concurrent.IsCompleted;
        };
        bool ready = await manager.EnsureConnectedAsync(address, timeout.Token, userInitiated: true, expectedModelId: 1075);
        bool secondReady = concurrent != null && await concurrent;
        File.WriteAllText(Path.Combine(output,"readiness.json"),JsonSerializer.Serialize(new {
            address=manager.ConnectedBluetoothAddress?.ToString("X12"),manager.FirmwareIdentity,
            ready,secondReady,returnedBeforeSnapshot,registersAtInfo,finalRegisters=manager.MixerState.Count,
            noAudioOrParameterWrites=true
        },new JsonSerializerOptions{WriteIndented=true}));
        Require(ready && secondReady && manager.ConnectedBluetoothAddress == address && manager.MixerState.Count >= 200,
            "Connection did not finish with a complete mixer snapshot");
        Require(returnedBeforeSnapshot == false,"Concurrent BLE waiter returned at INFO before the initial mixer snapshot");
        Console.WriteLine("PASS real concurrent BLE readiness: INFO waiter waited for the first mixer snapshot; no audio/parameter writes.");
    }
    public static void Logic()
    {
        Protocol();
        const ulong audio = 0x433E3F752ED6, ble = 0x423E3F752ED6;
        var services = new[] { SoncaAudioInspector.SoncaBleProtocol.PrimaryServiceUuid };
        var unnamed = SoncaAudioInspector.SoncaBleDiscovery.Merge(ble, "", -50, services)
            ?? throw new Exception("Unnamed Sonca service advertisement was discarded.");
        var response = SoncaAudioInspector.SoncaBleDiscovery.Merge(ble, "", -48, Array.Empty<Guid>(), unnamed)!;
        if (!response.ServiceUuids.Contains(services[0]) || !response.IsLikelySonca)
            throw new Exception("Scan response erased the service identification.");
        if (SoncaAudioInspector.SoncaBleDiscovery.Merge(123, "", -20, Array.Empty<Guid>()) != null)
            throw new Exception("Unidentified nearby radio became a Sonca controller.");
        var other = SoncaAudioInspector.SoncaBleDiscovery.Merge(0x423E3F752E09, "", -20, services)!;
        var preferred = SoncaAudioInspector.SoncaBleDiscovery.Preferred(new[] { other, response }, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "KBEATBOX") });
        if (preferred?.Address != ble || SoncaAudioInspector.SoncaBleDiscovery.Preferred(new[] { other }, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "KBEATBOX") }) != null)
            throw new Exception("Controller selection followed RSSI instead of the connected peer address.");
        if (!SoncaAudioInspector.SoncaBleDiscovery.MatchesAudioPeer(ble, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "KBEATBOX") })
            || SoncaAudioInspector.SoncaBleDiscovery.MatchesAudioPeer(0, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "KBEATBOX") })
            || SoncaAudioInspector.SoncaBleDiscovery.Preferred(new[] { response, other }, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "A"), new SoncaAudioInspector.BluetoothAudioPeer(other.Address ^ 0x010000000000UL, "B") }) != null
            || !SoncaAudioInspector.SoncaUsbRecordingEndpoint.MatchesC360("Headset Microphone (C360 usb audio)")
            || SoncaAudioInspector.SoncaUsbRecordingEndpoint.MatchesC360("Microphone (6- USB Audio Device)"))
            throw new Exception("Automatic peer/USB-C selection accepted an ambiguous or unrelated endpoint.");
        Console.WriteLine("PASS unnamed Sonca service, merged scan response, unrelated-radio rejection and unique connected-peer association.");
    }

    private static void Protocol()
    {
        byte[] config = Convert.FromHexString(string.Concat(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "C360-config.hex")).Where(c => !char.IsWhiteSpace(c))));
        var decoder = new SoncaAudioInspector.SoncaBleReplyDecoder();
        for (int split = 0; split <= config.Length; split++)
        {
            decoder.Clear();
            var replies = decoder.Push(config.AsSpan(0, split)).Concat(decoder.Push(config.AsSpan(split))).ToArray();
            if (replies.Length != 1 || !SoncaAudioInspector.SoncaBleMixerProfile.TryReadSnapshot(replies[0], out var state)
                || state.Count != 216 || state[0x6400] != 4096 || state[0x6102] != 8192 || state[0x6404] != 1)
                throw new Exception($"Captured C360 config failed at split {split}.");
        }
        byte[] damaged = config.ToArray(); damaged[50] ^= 1;
        decoder.Clear();
        if (decoder.Push(damaged.Concat(config).ToArray()).Count != 1) throw new Exception("CRC failure accepted or prevented next frame recovery.");
        decoder.Clear();
        var ack = Convert.FromHexString("000302012D");
        if (decoder.Push(ack.Concat(config).Concat(ack).ToArray()).Count != 3) throw new Exception("Coalesced frames failed.");
        if (!SoncaAudioInspector.SoncaBleProtocol.BuildReadMixerPacket().AsSpan().SequenceEqual(Convert.FromHexString("028002046209000199"))) throw new Exception("GET config wire packet mismatch.");
        var name=System.Text.Encoding.ASCII.GetBytes("SCA_MAIN48_C360.C360\0");
        var version=System.Text.Encoding.ASCII.GetBytes(" VER A:1.1.5, C:1.6.7-AT 2026_09_30:18:08:13\n");
        var identity=SoncaAudioInspector.SoncaBuildIdentity.Firmware(new(SoncaAudioInspector.SoncaBleProtocol.CmdInfo,0,name.Concat(new byte[]{45,4,51}).Concat(version).ToArray()));
        Require(identity=="SCA_MAIN48_C360.C360 · VER A:1.1.5, C:1.6.7-AT 2026_09_30:18:08:13","Firmware name/version were embellished or lost raw content");
        Require(SoncaAudioInspector.SoncaBuildIdentity.Firmware(new(1,0,System.Text.Encoding.ASCII.GetBytes("SCA_OTHER.X\0")))=="SCA_OTHER.X","Missing version invented a model/version label");
        var anotherInfo = new SoncaAudioInspector.SoncaBleReply(SoncaAudioInspector.SoncaBleProtocol.CmdInfo,0,System.Text.Encoding.ASCII.GetBytes("SCA_MAIN48_OTHER.BOARD\0"));
        Require(!SoncaAudioInspector.SoncaBleMixerProfile.SupportsInfo(anotherInfo)
            && SoncaAudioInspector.SoncaBleMixerProfile.SupportsRoutingInfo(anotherInfo),"Common Sonca routing must not require the C360-only mixer identity");
        Require(!SoncaAudioInspector.SoncaBleMixerProfile.HasInputRoutingState(new Dictionary<ushort,ushort>{[0x6242]=0})
            && !SoncaAudioInspector.SoncaBleMixerProfile.HasInputRoutingState(new Dictionary<ushort,ushort>{[0x6242]=15,[0x6254]=72})
            && SoncaAudioInspector.SoncaBleMixerProfile.CanSelectInputSource(new Dictionary<ushort,ushort>{[0x6242]=4,[0x6254]=72},1)
            && !SoncaAudioInspector.SoncaBleMixerProfile.CanSelectInputSource(new Dictionary<ushort,ushort>{[0x6242]=4,[0x6254]=72},2),
            "Unreported/invalid/unsupported sources became writable on another board");
        var updates = new SoncaAudioInspector.SoncaBleReply(0x84, 0, new byte[] { 0x61, 0x02, 0x20, 0 });
        if (!SoncaAudioInspector.SoncaBleMixerProfile.TryReadParameterUpdate(updates, out var live) || live[0x6102] != 8192
            || SoncaAudioInspector.SoncaBleMixerProfile.TryReadParameterUpdate(updates with { Status = 1 }, out _))
            throw new Exception("Live parameter update framing/status failed.");
        decoder.Clear();
        var buttonPress = decoder.Push(Convert.FromHexString("00070300610319C021")).Single();
        if (!SoncaAudioInspector.SoncaBleMixerProfile.TryReadParameterUpdate(buttonPress, out var musicUpdate)
            || musicUpdate[0x6103] != 6592 || Math.Round(SoncaAudioInspector.SoncaBleMixerProfile.VolumeFromRaw(musicUpdate[0x6103])) != 80)
            throw new Exception("Captured C360 MUSIC button feedback was lost or scaled incorrectly.");
        if (SoncaAudioInspector.SoncaBleMixerProfile.VolumeToRaw(100) != 8192 || SoncaAudioInspector.SoncaBleMixerProfile.VolumeFromRaw(8192) != 100
            || SoncaAudioInspector.SoncaBleMixerProfile.VolumeFromRaw(4096) != 50
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x3140, 50) || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x6400, 8193)
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x6448, 2)) throw new Exception("Unsafe legacy address or gain encoding accepted.");
        Console.WriteLine("PASS captured 216-parameter C360 frame at every split, coalesced packets, CRC recovery, config query and control bounds.");
        decoder.Clear();
        SoncaAudioInspector.SoncaBleMixerProfile.TryReadSnapshot(decoder.Push(config).Single(), out var snapshot);
        var available = Enumerable.Range(0, 6).Where(i => SoncaAudioInspector.SoncaBleMixerProfile.CanSelectInputSource(snapshot, (byte)i)).ToArray();
        if (!available.SequenceEqual(new[] { 0, 1, 4, 5 })
            || SoncaAudioInspector.SoncaBleMixerProfile.CanSelectInputSource(new Dictionary<ushort, ushort>(), 0)
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x3048, 1)
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x6254, 1)
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x6242, 6)
            || !SoncaAudioInspector.SoncaBleMixerProfile.TryReadInputSource(0x101, out byte packed) || packed != 1
            || SoncaAudioInspector.SoncaBleMixerProfile.TryReadInputSource(0x107, out _))
            throw new Exception("Input source capability/readback bounds failed.");
        var infoFrame = Convert.FromHexString("004801005343415F4D41494E34385F433336302E43333630002D04332056455220413A312E312E352C20433A312E362E372D415420323032365F30395F33303A31383A30383A31330A85");
        decoder.Clear();
        var info = decoder.Push(infoFrame).Single();
        int modelMarker = Array.IndexOf(info.Payload, (byte)'-');
        if (!SoncaAudioInspector.SoncaBleMixerProfile.SupportsInfo(info) || modelMarker < 0
            || ((info.Payload[modelMarker + 1] << 8) | info.Payload[modelMarker + 2]) != 1075
            || SoncaAudioInspector.SoncaBleMixerProfile.InputSourceName(5) != "THẺ NHỚ (SD/TF)")
            throw new Exception("Captured C360 INFO model/source-5 label mismatch.");
        string firmware = SoncaAudioInspector.SoncaBuildIdentity.Firmware(info);
        if (!firmware.Contains("A:1.1.5") || !firmware.Contains("C:1.6.7") || !firmware.Contains("2026_09_30")) throw new Exception("Firmware INFO version not decoded.");
        foreach (var option in Enum.GetValues<SoncaAudioInspector.SoncaMixerOption>())
        {
            ushort flags = 30152, mask = SoncaAudioInspector.SoncaBleMixerProfile.OptionMask(option);
            ushort on = SoncaAudioInspector.SoncaBleMixerProfile.WithOption(flags, option, true), off = SoncaAudioInspector.SoncaBleMixerProfile.WithOption(on, option, false);
            if (off != flags || (on ^ flags) != mask) throw new Exception("Option changed unrelated bits.");
        }
        Console.WriteLine("PASS firmware A/C/build identity and masked Mic FBX/Bass boost/Enhancer preserve all input flags.");
        Console.WriteLine("PASS captured C360 INFO model 1075 and source capabilities: BT/USB music/LINE IN/SD-TF; blocked unsupported ports/legacy address/flags write/invalid mode.");
        CommonMixerControls();
    }

    private static void CommonMixerControls()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Acoustic3-controls.json")));
        var info = new SoncaAudioInspector.SoncaBleReply(1, 0, Convert.FromHexString(json.RootElement.GetProperty("infoPayloadHex").GetString()!));
        var state = json.RootElement.GetProperty("parameters").EnumerateObject().ToDictionary(p => Convert.ToUInt16(p.Name, 16), p => p.Value.GetUInt16());
        Require(!SoncaAudioInspector.SoncaBleMixerProfile.SupportsInfo(info)
            && SoncaAudioInspector.SoncaBleMixerProfile.SupportsRoutingInfo(info)
            && SoncaAudioInspector.SoncaBleMixerProfile.HasMixerControlState(state), "Recorded ACOUSTIC3 mixer is still tied to C360 identity");
        foreach (ushort register in new ushort[] { 0x6447, 0x6448, 0x6449, 0x6404, 0x640A, 0x6468, 0x6400 })
            Require(SoncaAudioInspector.SoncaBleMixerProfile.CanWriteReportedParameter(state, register, state[register]), "Recorded common-model control blocked: " + register.ToString("X4"));
        Require(!SoncaAudioInspector.SoncaBleMixerProfile.SupportsRoutingInfo(info with { Status = 1 })
            && !SoncaAudioInspector.SoncaBleMixerProfile.SupportsRoutingInfo(info with { Payload = "OTHER_DEVICE"u8.ToArray() })
            && !SoncaAudioInspector.SoncaBleMixerProfile.HasMixerControlState(new Dictionary<ushort,ushort> { [0x6242] = 0, [0x6254] = 72 })
            && !SoncaAudioInspector.SoncaBleMixerProfile.HasMixerControlState(new Dictionary<ushort,ushort> { [0x6400] = 65535, [0x6447] = 2 }), "Invalid/absent mixer controls were treated as supported");
        Require(!SoncaAudioInspector.SoncaBleMixerProfile.CanWriteReportedParameter(new Dictionary<ushort,ushort>(), 0x6447, 1)
            && !SoncaAudioInspector.SoncaBleMixerProfile.CanWriteReportedParameter(new Dictionary<ushort,ushort> { [0x6447] = 65535 }, 0x6447, 1)
            && !SoncaAudioInspector.SoncaBleMixerProfile.CanWriteReportedParameter(state, 0x3141, 50)
            && !SoncaAudioInspector.SoncaBleMixerProfile.CanWriteReportedParameter(state, 0x6400, 8193), "Unreported/invalid/guessed controls became writable");
        foreach (var option in Enum.GetValues<SoncaAudioInspector.SoncaMixerOption>())
            Require(!SoncaAudioInspector.SoncaBleMixerProfile.SupportsOption(65535, option), "Unsupported option sentinel was accepted");
        Require(SoncaAudioInspector.SoncaBleMixerProfile.TryReadModelId(info.Payload, out ushort model) && model == 1071
            && SoncaAudioInspector.SoncaBleMixerProfile.TryMuteRegister("Reverb", info.Payload, state, out ushort normal, out bool inverted)
            && normal == 0x640A && inverted, "Recorded ACOUSTIC3 reverb mapping/inversion is wrong");
        byte[] alternate = System.Text.Encoding.ASCII.GetBytes("SCA_OTHER.BOARD\0").Concat(new byte[] { 45, 7, 47 }).ToArray(); // 1839: Sonca mobile F1.o.O
        var both = new Dictionary<ushort,ushort> { [0x640A] = 1, [0x644A] = 0 };
        Require(SoncaAudioInspector.SoncaBleMixerProfile.TryMuteRegister("Reverb", alternate, both, out ushort alt, out _) && alt == 0x644A
            && !SoncaAudioInspector.SoncaBleMixerProfile.TryMuteRegister("Reverb", Array.Empty<byte>(), both, out _, out _)
            && SoncaAudioInspector.SoncaBleMixerProfile.TryMuteRegister("Reverb", Array.Empty<byte>(), new Dictionary<ushort,ushort> { [0x644A] = 1 }, out alt, out _) && alt == 0x644A,
            "Alternate/ambiguous reverb schema selected the wrong register");
        Console.WriteLine("PASS captured ACOUSTIC3 common mixer, per-register bounds, missing/sentinel rejection, alternate firmware reverb mapping and INFO validation.");
    }

    public static async Task ConnectControlAsync(bool exerciseControls = false)
    {
        using var manager = new SoncaAudioInspector.SoncaBleManager();
        manager.LogMessage += message => Console.WriteLine(message);
        manager.ConnectionStatusChanged += (ready, status) => Console.WriteLine($"READY={ready}: {status}");
        int notifications = 0;
        manager.DataReceived += (data, description) => { Interlocked.Increment(ref notifications); Console.WriteLine("REPLY " + description); };
        var peers = await SoncaAudioInspector.SoncaBleManager.ConnectedAudioPeersAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "connected-audio", peers }));
        manager.StartScanning(); await Task.Delay(10000); manager.StopScanning();
        foreach (var info in manager.DiscoveredDevices.Where(d => d.IsLikelySonca)) Console.WriteLine(JsonSerializer.Serialize(info));
        var preferred = SoncaAudioInspector.SoncaBleDiscovery.Preferred(manager.DiscoveredDevices, peers);
        if (preferred == null) { Console.WriteLine("INCONCLUSIVE: no unique Sonca BLE radio associated with the connected Windows speaker; no commands sent."); return; }
        bool connected = await manager.ConnectAsync(preferred.Address);
        Console.WriteLine($"Connection ready={connected}; service={manager.ActiveServiceUuid}; write={manager.ActiveWriteCharUuid}; notify={manager.ActiveNotifyCharUuid}");
        if (connected)
        {
            if (!await manager.SendPacketAsync(SoncaAudioInspector.SoncaBleProtocol.BuildReadMixerPacket(), "READ ONLY config query")) throw new Exception("Config readback failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "snapshot", manager.SupportsMixerControls, parameters = manager.MixerState.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) }));
            if (exerciseControls) await ExerciseAsync(manager);
        }
        Console.WriteLine($"Notifications={notifications}; controlExercise={exerciseControls}; no EEPROM or reset commands were emitted.");
    }

    private static async Task ExerciseAsync(SoncaAudioInspector.SoncaBleManager manager)
    {
        if (!manager.SupportsMixerControls) throw new Exception("No verified device profile: no writes allowed.");
        var baseline = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
        foreach (ushort register in new ushort[] { 0x6447, 0x6448, 0x6449, 0x6404, 0x640A, 0x6468, 0x6400 })
        {
            if (!baseline.TryGetValue(register, out ushort original)) throw new Exception($"Missing original 0x{register:X4}; no write allowed.");
            ushort changed = register == 0x6400 ? (ushort)(original > 41 ? original - 41 : original) : (ushort)(original == 0 ? 1 : 0);
            bool attempted = false;
            try
            {
                attempted = true;
                if (!await manager.SetParamAsync(0, register, changed, "Reversible hardware check")) throw new Exception($"Write/readback failed 0x{register:X4}.");
                Console.WriteLine($"VERIFIED 0x{register:X4}: {original} -> {manager.MixerState[register]} (DSP readback).");
            }
            finally
            {
                if (attempted)
                {
                    bool restored = await manager.SetParamAsync(0, register, original, "Restore exact original setting");
                    if (!restored) restored = await manager.SetParamAsync(0, register, original, "Retry original setting restore");
                    if (!restored) throw new Exception($"RESTORE FAILED 0x{register:X4}; original={original}.");
                    Console.WriteLine($"RESTORED 0x{register:X4}={original} (DSP readback).");
                }
            }
        }
        var changes = baseline.Where(p => !manager.MixerState.TryGetValue(p.Key, out var actual) || actual != p.Value).ToArray();
        if (changes.Length != 0) throw new Exception("Final config differs: " + JsonSerializer.Serialize(changes));
        Console.WriteLine($"PASS all six mute/enable controls and MIC gain, restored all {baseline.Count} parameters to the initial readback; no audible-output claim.");
    }

    public static async Task SnapshotAsync()
    {
        void Write(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        Write(new { stage = "adapter", adapter?.IsLowEnergySupported, adapter?.IsClassicSupported, adapter?.IsCentralRoleSupported });
        foreach (var pair in new[] { ("classic-connected", BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)),
            ("ble-known", BluetoothLEDevice.GetDeviceSelector()) })
        {
            var devices = await DeviceInformation.FindAllAsync(pair.Item2);
            foreach (var info in devices)
            {
                try
                {
                if (pair.Item1 == "classic-connected")
                {
                    using var device = await BluetoothDevice.FromIdAsync(info.Id);
                    Write(new { stage = pair.Item1, info.Name, info.Pairing.IsPaired, address = device?.BluetoothAddress.ToString("X12"), state = device?.ConnectionStatus.ToString(),
                        properties = device?.DeviceInformation.Properties.Where(p => p.Key.Contains("Container")).ToDictionary(p => p.Key, p => p.Value?.ToString()) });
                }
                else Write(new { stage = pair.Item1, info.Name, info.Pairing.IsPaired });
                }
                catch (ArgumentException ex) { Write(new { stage = pair.Item1, info.Name, error = ex.Message }); }
            }
        }
        var adverts = new ConcurrentDictionary<ulong, (string Name, short Rssi, string[] Services, string Type)>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) => adverts.AddOrUpdate(e.BluetoothAddress,
            _ => (e.Advertisement.LocalName, e.RawSignalStrengthInDBm, e.Advertisement.ServiceUuids.Select(u => u.ToString()).ToArray(), e.AdvertisementType.ToString()),
            (_, old) => (string.IsNullOrWhiteSpace(e.Advertisement.LocalName) ? old.Name : e.Advertisement.LocalName,
                e.RawSignalStrengthInDBm, old.Services.Union(e.Advertisement.ServiceUuids.Select(u => u.ToString())).ToArray(), e.AdvertisementType.ToString()));
        watcher.Stopped += (_, e) => Write(new { stage = "scan-stopped", error = e.Error.ToString() });
        watcher.Start(); await Task.Delay(12000); watcher.Stop();
        Write(new { stage = "scan-summary", count = adverts.Count });
        foreach (var pair in adverts.OrderByDescending(p => p.Value.Rssi))
        {
            // Discovery only. No application packets, volume/mute writes or pairing changes.
            Write(new { stage = "advertisement", address = pair.Key.ToString("X12"), name = pair.Value.Name,
                rssi = pair.Value.Rssi, services = pair.Value.Services, type = pair.Value.Type });
            if (!SoncaAudioInspector.SoncaBleProtocol.IsLikelySoncaDevice(pair.Value.Name)
                && !pair.Value.Services.Contains(SoncaAudioInspector.SoncaBleProtocol.PrimaryServiceUuid.ToString())) continue;
            try
            {
                using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(pair.Key);
                if (device == null) continue;
                Write(new { stage = "ble-device", device.Name, device.DeviceId, address = device.BluetoothAddress.ToString("X12"),
                    properties = device.DeviceInformation.Properties.Where(p => p.Key.Contains("Container") || p.Key.Contains("Address")).ToDictionary(p => p.Key, p => p.Value?.ToString()) });
                var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
                Write(new { stage = "gatt-services", device.Name, status = services.Status.ToString() });
                foreach (var service in services.Services)
                {
                    using (service)
                    {
                        var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                        Write(new { stage = "gatt-characteristics", service = service.Uuid, status = chars.Status.ToString(),
                            characteristics = chars.Characteristics.Select(c => new { c.Uuid, properties = c.CharacteristicProperties.ToString() }) });
                        foreach (var characteristic in chars.Characteristics.Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)))
                        {
                            var value = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                            if (value.Value == null) continue;
                            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(value.Value);
                            var bytes = new byte[value.Value.Length]; reader.ReadBytes(bytes);
                            Write(new { stage = "gatt-read", characteristic = characteristic.Uuid, status = value.Status.ToString(), length = bytes.Length,
                                hex = Convert.ToHexString(bytes.Take(256).ToArray()), ascii = System.Text.Encoding.ASCII.GetString(bytes.Take(256).Select(b => b >= 32 && b < 127 ? b : (byte)'.').ToArray()) });
                        }
                    }
                }
            }
            catch (Exception ex) { Write(new { stage = "gatt-probe-error", name = pair.Value.Name, error = ex.Message }); }
        }
    }
}
