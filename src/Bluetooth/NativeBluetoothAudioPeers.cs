using System.Runtime.InteropServices;

namespace SoncaAudioInspector;

internal static class NativeBluetoothAudioPeers
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Search
    {
        public int Size;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        [MarshalAs(UnmanagedType.Bool)] public bool Unknown;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Inquiry;
        public byte Timeout;
        public IntPtr Radio;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Millisecond; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device
    {
        public int Size;
        public ulong Address;
        public uint DeviceClass;
        [MarshalAs(UnmanagedType.Bool)] public bool Connected;
        [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
        public SystemTime LastSeen, LastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstDevice(ref Search search, ref Device device);
    [DllImport("bthprops.cpl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(IntPtr find, ref Device device);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(IntPtr find);
    [StructLayout(LayoutKind.Sequential)] private struct RadioSearch { public int Size; }
    [DllImport("bthprops.cpl")] private static extern IntPtr BluetoothFindFirstRadio(ref RadioSearch search, out IntPtr radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindRadioClose(IntPtr find);
    [DllImport("bthprops.cpl")] private static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref Device device);
    [DllImport("bthprops.cpl")] private static extern uint BluetoothSetServiceState(IntPtr radio, ref Device device, ref Guid service, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

    internal static uint EnableStereoService(ulong address)
    {
        if (!SoncaBleDiscovery.IsSameSpeaker(address, address)) throw new ArgumentException("Địa chỉ Bluetooth không hợp lệ.");
        var search = new RadioSearch { Size = Marshal.SizeOf<RadioSearch>() };
        IntPtr find = BluetoothFindFirstRadio(ref search, out IntPtr radio);
        if (find == IntPtr.Zero) return 1168; // ERROR_NOT_FOUND
        try
        {
            do
            {
                try
                {
                    var device = new Device { Size = Marshal.SizeOf<Device>(), Address = address, Name = "" };
                    if (BluetoothGetDeviceInfo(radio, ref device) != 0 || device.Address != address) continue;
                    Guid audioSink = new("0000110B-0000-1000-8000-00805F9B34FB");
                    // Only enable the documented stereo service. Do not disable
                    // an existing profile or remove/recreate a user's pairing.
                    return BluetoothSetServiceState(radio, ref device, ref audioSink, 1);
                }
                finally { CloseHandle(radio); }
            } while (BluetoothFindNextRadio(find, out radio));
        }
        finally { BluetoothFindRadioClose(find); }
        return 1168;
    }

    public static IReadOnlyList<BluetoothAudioPeer> ReadConnected() => ReadConnectedDevices()
        .Where(device => SoncaBleProtocol.IsLikelySoncaDevice(device.Name))
        .Select(device => new BluetoothAudioPeer(device.Address, device.Name.Trim())).ToArray();

    internal static bool IsConnected(ulong address) => ReadConnectedDevices().Any(device => device.Address == address);

    private static IReadOnlyList<Device> ReadConnectedDevices()
    {
        var peers = new List<Device>();
        var search = new Search { Size = Marshal.SizeOf<Search>(), Connected = true, Inquiry = false };
        var device = new Device { Size = Marshal.SizeOf<Device>(), Name = "" };
        IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
        if (find == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (0 or 259)) throw new System.ComponentModel.Win32Exception(error);
            return peers;
        }
        try
        {
            do
            {
                if (device.Connected && device.Address != 0) peers.Add(device);
                device = new Device { Size = Marshal.SizeOf<Device>(), Name = "" };
            } while (BluetoothFindNextDevice(find, ref device));
        }
        finally { BluetoothFindDeviceClose(find); }
        return peers;
    }

}
