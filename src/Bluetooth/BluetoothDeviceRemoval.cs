using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoncaAudioInspector;

internal static class BluetoothDeviceRemoval
{
    internal static bool IsMeasuredAddress(ulong candidate, ulong controller) => SoncaBleDiscovery.IsSameSpeaker(candidate, controller);
    internal static Task<BluetoothRemovalReport> RemoveMeasuredSpeakerAsync(ulong controller, CancellationToken token = default)
        => WindowsBluetoothPairing.RunExclusiveAsync(() => RemoveCoreAsync(controller, token), token);

    private static async Task<BluetoothRemovalReport> RemoveCoreAsync(ulong controller, CancellationToken token)
    {
        if (!IsMeasuredAddress(controller, controller)) throw new ArgumentException("Địa chỉ loa không hợp lệ.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var records = new Dictionary<string, DeviceInformation>();
        var containers = new HashSet<Guid>();
        string[] properties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.ContainerId", "System.Devices.Aep.IsConnected" };
        async Task<BluetoothRemovalSnapshot> Read(CancellationToken ct)
        {
            records.Clear();
            var paired = new List<BluetoothPairingRecord>();
            bool connected = false;
            // Classic audio only. Never enumerate/unpair BluetoothLEDevice here.
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            foreach (var device in await DeviceInformation.FindAllAsync(selector, properties, DeviceInformationKind.AssociationEndpoint).AsTask(ct))
            {
                if (!device.Properties.TryGetValue(properties[0], out var raw)) continue;
                string hex = raw?.ToString()?.Replace(":", "").Replace("-", "") ?? "";
                if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong address) || !IsMeasuredAddress(address, controller)) continue;
                if (device.Properties.TryGetValue(properties[1], out var value) && Guid.TryParse(value?.ToString(), out Guid container)
                    && BluetoothPlaybackMatcher.IsPhysicalContainer(container)) containers.Add(container);
                if (device.Properties.TryGetValue(properties[2], out var active) && active is true) connected = true;
                // Some Windows cached selectors also return a remembered AEP
                // after unpairing. Its actual pairing state is authoritative.
                if (!device.Pairing.IsPaired) continue;
                records[device.Id] = device;
                paired.Add(new(device.Id, device.Name, address));
            }
            // Pairing records can disappear before the audio driver disconnects.
            // Confirm both radio state and the exact physical render container.
            connected |= NativeBluetoothAudioPeers.IsConnected(controller) || NativeBluetoothAudioPeers.IsConnected(controller ^ 0x010000000000UL);
            connected |= WindowsBluetoothPlayback.Read().Any(endpoint => endpoint.Address is ulong address
                ? IsMeasuredAddress(address, controller) : endpoint.ContainerId is Guid container && containers.Contains(container));
            return new(paired, connected);
        }
        async Task Unpair(BluetoothPairingRecord record, CancellationToken ct)
        {
            var result = await records[record.Id].Pairing.UnpairAsync().AsTask(ct);
            if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                throw new InvalidOperationException(result.Status.ToString());
        }
        return await BluetoothRemovalWorkflow.RunAsync(controller, Read, Unpair, timeout.Token);
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static bool MatchesModel(string deviceName, string modelName)
    {
        static string ModelKey(string name)
        {
            string endpointName = Regex.Match(name, @"\(([^)]+)\)\s*$").Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(endpointName)) name = endpointName;
            name = Regex.Replace(name.Trim(), @"^\d+\s*-\s*", "");
            string key = Normalize(name);
            return key is "MISAM" or "MI30SAM" ? "MI30SAM" : key;
        }

        string modelKey = ModelKey(modelName);
        return modelKey.Length >= 4 && ModelKey(deviceName) == modelKey;
    }

    internal static async Task<(bool Success, int Removed, int Matched, string Message)> RemovePairedDevicesForModelAsync(string modelName)
    {
        // Query pairing records so disconnected devices are included. Removal is
        // called only by the manual button, never by measurement completion.
        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            Array.Empty<string>(),
            DeviceInformationKind.AssociationEndpoint);
        var byId = paired.ToDictionary(device => device.Id, StringComparer.Ordinal);
        return await RemoveMatchingDevicesAsync(modelName,
            paired.Select(device => new KeyValuePair<string, string>(device.Id, device.Name)),
            async id =>
            {
                var result = await byId[id].Pairing.UnpairAsync();
                if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                    throw new InvalidOperationException(result.Status.ToString());
            });
    }

    internal static async Task<(bool Success, int Removed, int Matched, string Message)> RemoveMatchingDevicesAsync(
        string modelName, IEnumerable<KeyValuePair<string, string>> pairedDevices, Func<string, Task> unpair)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, 0, 0, "Chọn model trước khi xóa thiết bị Bluetooth đã ghép.");

        var targets = pairedDevices.Where(device => MatchesModel(device.Value, modelName))
            .DistinctBy(device => device.Key).ToArray();
        if (targets.Length == 0)
            return (true, 0, 0, $"Không còn thiết bị Bluetooth đã ghép thuộc model '{modelName}'.");

        int removed = 0;
        var failures = new List<string>();
        foreach (var device in targets)
        {
            try
            {
                await unpair(device.Key);
                removed++;
            }
            catch (Exception ex) { failures.Add($"{device.Value}: {ex.Message}"); }
        }

        return (failures.Count == 0, removed, targets.Length,
            $"Đã xóa {removed}/{targets.Length} thiết bị Bluetooth đã ghép thuộc model '{modelName}'."
            + (failures.Count == 0 ? "" : " Chưa xóa được: " + string.Join("; ", failures)));
    }
}
