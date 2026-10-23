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

    public static IReadOnlyList<BluetoothAudioPeer> ReadConnected()
    {
        var peers = new List<BluetoothAudioPeer>();
        var search = new Search { Size = Marshal.SizeOf<Search>(), Connected = true, Inquiry = false };
        var device = new Device { Size = Marshal.SizeOf<Device>(), Name = "" };
        IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
        if (find == IntPtr.Zero) return peers;
        try
        {
            do
            {
                if (device.Connected && device.Address != 0 && SoncaBleProtocol.IsLikelySoncaDevice(device.Name))
                    peers.Add(new(device.Address, device.Name.Trim()));
                device = new Device { Size = Marshal.SizeOf<Device>(), Name = "" };
            } while (BluetoothFindNextDevice(find, ref device));
        }
        finally { BluetoothFindDeviceClose(find); }
        return peers;
    }
}
