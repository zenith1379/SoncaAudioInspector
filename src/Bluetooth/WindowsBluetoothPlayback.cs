using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public static class WindowsBluetoothPlayback
{
    private static readonly PropertyKey ContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    public static IReadOnlyList<BluetoothPlaybackEndpoint> Read(DeviceState state = DeviceState.Active)
    {
        using var devices = new MMDeviceEnumerator();
        var result = new List<BluetoothPlaybackEndpoint>();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, state))
        using (device)
        {
            try
            {
                string instance = device.Properties.Contains(PropertyKeys.PKEY_Device_InstanceId)
                    ? device.Properties[PropertyKeys.PKEY_Device_InstanceId].Value?.ToString() ?? "" : "";
                var ancestry = Ancestors(instance);
                var addresses = ancestry.Select(BluetoothPlaybackMatcher.AddressFromInstanceId).Where(a => a.HasValue).Distinct().ToArray();
                bool bluetooth = ancestry.Any(id => id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase));
                bool handsFree = ancestry.Any(id => id.StartsWith("BTHHF", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("{0000111E-", StringComparison.OrdinalIgnoreCase) || id.Contains("{0000111F-", StringComparison.OrdinalIgnoreCase));
                using var client = device.AudioClient;
                bool stereo = !handsFree && client.MixFormat.Channels >= 2;
                Guid? container = device.Properties.Contains(ContainerKey)
                    && Guid.TryParse(device.Properties[ContainerKey].Value?.ToString(), out Guid id) ? id : null;
                // Offloaded Bluetooth drivers can expose an Intel/USB ancestry.
                // Keep their container for an exact match after Windows pairing.
                if (bluetooth || container.HasValue) result.Add(new(device.ID, device.FriendlyName, addresses.Length == 1 ? addresses[0] : null, stereo, container));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            { System.Diagnostics.Debug.WriteLine("Bluetooth endpoint not identified: " + ex.Message); }
        }
        return result;
    }

    private static string[] Ancestors(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return Array.Empty<string>();
        var result = new List<string> { instance };
        if (CM_Locate_DevNode(out uint node, instance, 0) != 0) return result.ToArray();
        var seen = new HashSet<uint> { node };
        for (int depth = 0; depth < 16 && CM_Get_Parent(out uint parent, node, 0) == 0 && seen.Add(parent); depth++)
        {
            if (CM_Get_Device_ID_Size(out uint size, parent, 0) != 0 || size > 4096) break;
            var text = new StringBuilder((int)size + 1);
            if (CM_Get_Device_ID(parent, text, text.Capacity, 0) != 0) break;
            result.Add(text.ToString()); node = parent;
        }
        return result.ToArray();
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint CM_Locate_DevNode(out uint node, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Device_ID_Size(out uint size, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint node, StringBuilder text, int length, uint flags);
}
