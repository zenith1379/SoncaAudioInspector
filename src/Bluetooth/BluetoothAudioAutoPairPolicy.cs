using System.Collections.Concurrent;

namespace SoncaAudioInspector;

/// <summary>Keep a removed audio bond removed until the next explicit audio request.</summary>
public sealed class BluetoothAudioAutoPairPolicy
{
    private readonly ConcurrentDictionary<ulong, byte> _removed = new();
    private static ulong Key(ulong controller) => controller & ~0x010000000000UL;
    public bool IsSuppressed(ulong controller) => _removed.ContainsKey(Key(controller));
    public void Suppress(ulong controller)
    {
        if (!SoncaBleDiscovery.IsSameSpeaker(controller, controller)) throw new ArgumentException("Địa chỉ BLE không hợp lệ.");
        _removed[Key(controller)] = 0;
    }
    public void Allow(ulong controller) => _removed.TryRemove(Key(controller), out _);
}
