using System.Text;
using SoncaAudioInspector;

internal static class BleModelNameChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static byte[] Info(ushort id) => Encoding.ASCII.GetBytes("SCA_MAIN48_3x100A.ACOUSTIC3\0")
            .Concat(new byte[] { 45, (byte)(id >> 8), (byte)id }).Concat(" VER A:1.1.6"u8.ToArray()).ToArray();
        var catalogue = SoncaFirmwareModels.Current;
        Check(catalogue.EntryCount == 121 && catalogue.Find(1071).SequenceEqual(new[] { "ACOUSTIC 3" }), "Bundled official model names missing");
        Check(catalogue.Find(1075).Single() == "KBEATBOX", "Catalogue name was replaced by an app-specific model alias");
        Check(catalogue.Find(1824).SequenceEqual(new[] { "CS448NS", "QUAD 64" }), "Duplicate ID lost a vendor name");
        Check(catalogue.Find(65535).Count == 0, "Unknown model invented a name");
        Check(SoncaBleDiscovery.IsSameSpeaker(0x439F3F264237, 0x429F3F264237)
            && !SoncaBleDiscovery.IsSameSpeaker(0x439F3F264237, 0x439F3F264236)
            && !SoncaBleDiscovery.IsSameSpeaker(0, 0) && !SoncaBleDiscovery.IsSameSpeaker(0x1000000000000, 1), "XOR octet or address validation wrong");
        var device = new BleDeviceInfo(0x439F3F264237, "Sonca BLE 439F3F264237", -55, true, DateTime.Now);
        var peers = new[] { new BluetoothAudioPeer(0x429F3F264237, "Acoustic studio"), new BluetoothAudioPeer(0x429F3F264236, "Wrong speaker") };
        var preferences = new AudioBenchPreferences { BleFilterByModel = true, BleModelFilter = "acoustic 3", KnownDeviceModels = new() { [device.Address] = 1075 } };
        var live = SoncaBleDeviceIdentity.Resolve(device, peers, preferences, Info(1071));
        Check(live.FromLiveInfo && live.ModelId == 1071 && live.ModelNames.Single() == "ACOUSTIC 3"
            && live.AudioName == "Acoustic studio" && preferences.MatchesBleDevice(device, peers, Info(1071)), "Live INFO did not override stale cached model / match name");
        Check(!preferences.MatchesBleDevice(device, peers) && !preferences.MatchesBleDevice(device, peers, Info(65535)), "Stale/unknown model passed requested model filter");
        Check((preferences with { BleFilterByModel = false, BleNameFilter = "wrong; ACOUSTIC STUDIO" }).MatchesBleDevice(device, peers), "XOR-associated Bluetooth name not searchable");
        Check(!(preferences with { BleFilterByModel = false, BleNameFilter = "Wrong speaker" }).MatchesBleDevice(device, peers), "Other Bluetooth peer leaked into name match");
        Check((preferences with { BleFilterByModel = false, BleNameFilter = "sonca ble" }).MatchesBleDevice(device, peers), "Advertised-name fallback lost");
        foreach (string query in new[] { "1071", "ID:1071", "0x042F", "acoustic3", "unknown; ACOUSTIC 3" })
            Check(SoncaBleModelFilter.Matches(live,query), "Model/ID query failed: " + query);
        Check(!SoncaBleModelFilter.Matches(live,"107") && !SoncaBleModelFilter.Matches(live,"1075")
            && !SoncaBleModelFilter.Matches(live,"Sonca BLE") && !SoncaBleModelFilter.Matches(live with {ModelNames=Array.Empty<string>()},"1071"),
            "Model-only filter accepted a partial ID, BLE name or ID absent from vendor table");
        var malformed = SoncaBleDeviceIdentity.Resolve(device, peers, preferences, "SCA_MAIN48.X\0-\x04"u8.ToArray());
        Check(malformed.ModelId == null && malformed.ModelNames.Count == 0, "Truncated INFO reused stale model");
        Check(!SoncaBleMixerProfile.TryReadModelId(Info(1071)[..^15], out _), "Truncated INFO accepted");
        var parsed = SoncaFirmwareModels.Parse("{\"model\":[{\"model_id\":1071,\"model_name\":\"ACOUSTIC 3\",\"profile\":[{\"data\":\"ignored preset\"}]}]}");
        Check(parsed.Find(1071).Single() == "ACOUSTIC 3", "Vendor profile not accepted as name-only catalogue");
        foreach (string invalid in new[] { "{}", "{\"model\":[]}", "{\"model\":[{\"model_id\":70000,\"model_name\":\"X\"}]}" })
        {
            bool rejected = false;
            try { SoncaFirmwareModels.Parse(invalid); } catch (System.IO.InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid catalogue accepted");
        }
        Console.WriteLine("PASS official 121 model names, duplicate-ID ambiguity, exact XOR radio association, live INFO over cache, model/BLE/BT name filtering, malformed INFO and catalogue rejection. No radio commands.");
    }
}
