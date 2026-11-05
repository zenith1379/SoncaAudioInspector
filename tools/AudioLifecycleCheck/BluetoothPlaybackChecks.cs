using System.Text.Json;
using System.IO;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class BluetoothPlaybackChecks
{
    public static async Task Run()
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        const ulong ble = 0x423E3F752ECC, audio = 0x433E3F752ECC, other = 0x433E3F752ED6;
        var correct = new BluetoothPlaybackEndpoint("new-PC-endpoint", "Headphones (KBEATBOX)", audio, true);
        var unrelated = new BluetoothPlaybackEndpoint("wrong-speaker", correct.Name, other, true);
        var mono = new BluetoothPlaybackEndpoint("HFP", "Hands-Free (KBEATBOX)", audio, false);
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { unrelated, mono, correct }, "Old computer / ACOUSTIC 3") == correct,
            "Old configured name or same-name speaker defeated hardware matching");
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { unrelated, mono }) == null, "Wrong speaker/HFP was accepted");
        var sameSuffix = unrelated with { Address = 0x993E3F752ECC };
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { sameSuffix }, correct.Name) == null,
            "A same-name device sharing the MAC suffix was accepted instead of the connected BLE device");
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { correct with { Id = "third-PC", Name = "Loa đã đổi tên" } })?.Id == "third-PC",
            "Fresh endpoint ID/name on another PC was not rediscovered");
        var second = correct with { Id = "second-render", Name = "Stereo speakers" };
        bool ambiguous = false;
        try { BluetoothPlaybackMatcher.Select(ble, new[] { correct, second }); } catch (InvalidOperationException) { ambiguous = true; }
        Check(ambiguous && BluetoothPlaybackMatcher.Select(ble, new[] { correct, second }, second.Name) == second,
            "Ambiguous same-device render endpoints silently chose one or ignored explicit tie-breaker");
        Guid container = Guid.NewGuid();
        var offloaded = new BluetoothPlaybackEndpoint("offload", "Renamed offloaded speaker", null, true, container);
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { unrelated with { ContainerId = container }, offloaded }, containerId: container) == offloaded,
            "Container fallback ignored explicit wrong MAC or failed for an offloaded driver");
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { offloaded }) == null, "Unknown-address endpoint accepted by name alone");
        Guid systemContainer = new("00000000-0000-0000-ffff-ffffffffffff");
        Check(BluetoothPlaybackMatcher.Select(ble, new[] { offloaded with { ContainerId = systemContainer } }, containerId: systemContainer) == null,
            "Generic system container selected a built-in speaker");
        Check(BluetoothPlaybackMatcher.AddressFromInstanceId(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&74ec4ef&0&433E3F752ECC_C00000000") == audio
            && BluetoothPlaybackMatcher.AddressFromInstanceId(@"BTHENUM\DEV_433E3F752ECC\7&74EC4EF&0&BLUETOOTHDEVICE_433E3F752ECC") == audio
            && BluetoothPlaybackMatcher.AddressFromInstanceId(@"USB\VID_433E&PID_2ECC") == null,
            "Recorded Windows PnP ancestry was parsed incorrectly");
        int pairs = 0;
        var selected = await BluetoothPlaybackConnection.EnsureAsync(ble, "stale name", true, () => new[] { correct, unrelated },
            (_, _) => { pairs++; return Task.FromResult<Guid?>(null); }, default);
        Check(selected == correct && pairs == 0, "Already connected speaker was paired again");
        bool available = false;
        selected = await BluetoothPlaybackConnection.EnsureAsync(ble, "Old name", true,
            () => available ? new[] { offloaded, unrelated } : new[] { unrelated },
            (allow, _) => { if (allow) pairs++; available = true; return Task.FromResult<Guid?>(container); }, default,
            timeout: TimeSpan.FromMilliseconds(20), interval: TimeSpan.FromMilliseconds(1));
        Check(selected == offloaded && pairs == 1, "New-PC pairing repeated or selected another speaker");
        bool missing = false;
        try
        {
            await BluetoothPlaybackConnection.EnsureAsync(ble, null, false, () => new[] { unrelated },
                (allow, _) => { if (allow) pairs++; return Task.FromResult<Guid?>(null); }, default,
                timeout: TimeSpan.FromMilliseconds(2), interval: TimeSpan.FromMilliseconds(1));
        }
        catch (InvalidOperationException ex) { missing = ex.Message.Contains("Chưa phát tín hiệu đo"); }
        Check(missing && pairs == 1, "AutoPair=false still paired or fell back to another output");
        selected = await BluetoothPlaybackConnection.EnsureAsync(ble, null, false, () => new[] { offloaded },
            (allow, _) => { Check(!allow,"Disabled pairing enabled by metadata lookup"); return Task.FromResult<Guid?>(container); }, default);
        Check(selected == offloaded && pairs == 1,"Already connected offloaded endpoint required re-pairing");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { await BluetoothPlaybackConnection.EnsureAsync(ble, null, true, () => new[] { correct },
                (_, _) => { pairs++; return Task.FromResult<Guid?>(null); }, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && pairs == 1, "Cancelled preparation paired a device");
        }
        using (var changedBoard = new CancellationTokenSource())
        {
            bool stopped = false;
            try
            {
                await BluetoothPlaybackConnection.EnsureAsync(ble, null, true, () => new[] { unrelated },
                    (_, _) => { changedBoard.Cancel(); return Task.FromResult<Guid?>(container); }, changedBoard.Token);
            }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "A BLE change during Windows preparation continued to select an audio endpoint");
        }
        var advertised = new BleDeviceInfo(ble,"Sonca BLE",-50,true,DateTime.Now) { ServiceUuids = new[] { SoncaBleProtocol.PrimaryServiceUuid } };
        var preferences = new AudioBenchPreferences();
        Check(SoncaBleDiscovery.UniqueNewController(new[] { advertised },1071,preferences) == advertised
            && SoncaBleDiscovery.UniqueNewController(new[] { advertised, advertised with { Address = 0x439F3F264237 } },1071,preferences) == null
            && SoncaBleDiscovery.UniqueNewController(new[] { advertised with { LastSeen = DateTime.Now.AddMinutes(-1) } },1071,preferences) == null,
            "New-PC BLE discovery guessed among multiple or stale controllers");
        var model = C360ModelDefinition.Create(); var config = model.testItems.InOut;
        config.Tests.Single(t=>t.SpeakerStage=="Bluetooth").Enable=true;
        var inventory = config.Devices.Input.Select(pair => new C360ModelEndpoint(pair.Key,pair.Value,false))
            .Concat(config.Devices.Output.Select(pair => new C360ModelEndpoint(pair.Key,pair.Value,true))).ToArray();
        var bt = config.Tests.Single(t=>t.SpeakerStage=="Bluetooth");
        config.Devices.Input[bt.PlaybackOut] = "Saved name from another PC";
        config.BluetoothAutoPair=false;
        var plan = C360ModelPlan.Create(config, inventory, new("a","u","c"));
        plan.SetupFor(bt).Validate();
        Check(plan.Setup.BluetoothPlaybackId == "" && !plan.Setup.BluetoothAutoPair && plan.PlaybackIds[C360ElectronicStage.Bluetooth] == "",
            "Model plan relied on old BT name/endpoint before BLE/source switching");
        Check(JsonSerializer.Deserialize<InOutConfig>("{}")!.BluetoothAutoPair && !JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(config))!.BluetoothAutoPair,
            "Auto pair default or model persistence is wrong");
        Console.WriteLine("PASS Bluetooth playback: exact MAC/XOR, duplicate-name speakers, renamed/new-PC endpoints, HFP rejection, ambiguity, offloaded container, recorded PnP paths, single pairing, no repair for connected speaker, disabled pairing, cancellation, unique fresh BLE and portable model policy. Injected inventory/pairing only.");
    }

    public static void Snapshot(string output)
    {
        Directory.CreateDirectory(output);
        var snapshot = new { utc = DateTimeOffset.UtcNow, active = WindowsBluetoothPlayback.Read(), all = WindowsBluetoothPlayback.Read(DeviceState.All) };
        File.WriteAllText(Path.Combine(output,"bluetooth-endpoints.json"), JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"READ ONLY Windows render inventory: {snapshot.active.Count} active / {snapshot.all.Count} known endpoints. No pairing, playback or level writes.");
    }
}
