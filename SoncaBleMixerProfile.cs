namespace SoncaAudioInspector;

public enum SoncaMixerOption { MicFbx, BassBoost, Enhancer }

/// <summary>NRPN controls verified against the C360 INFO/config response.</summary>
public static class SoncaBleMixerProfile
{
    // Sonca Mi30_OptionView: these are bit flags in 0x6254, not EQ NRPNs.
    public static ushort OptionMask(SoncaMixerOption option) => option switch
    { SoncaMixerOption.MicFbx => 1, SoncaMixerOption.BassBoost => 2, SoncaMixerOption.Enhancer => 512, _ => 0 };
    public static bool SupportsOption(ushort flags, SoncaMixerOption option) => option switch
    { SoncaMixerOption.MicFbx => true, SoncaMixerOption.BassBoost => (flags & 128) != 0, SoncaMixerOption.Enhancer => (flags & 256) != 0, _ => false };
    public static ushort WithOption(ushort flags, SoncaMixerOption option, bool enabled)
    {
        ushort mask = OptionMask(option);
        if (mask == 0 || !SupportsOption(flags, option)) throw new ArgumentException("Loa không báo hỗ trợ tùy chọn này.");
        return (ushort)(enabled ? flags | mask : flags & ~mask);
    }
    public const ushort MicVolume = 0x6400, MusicVolume = 0x6103, MasterVolume = 0x6102,
        EchoVolume = 0x6402, ReverbVolume = 0x6403, LiveVolume = 0x6467;
    public const ushort MicMute = 0x6447, MusicMute = 0x6448, MasterMute = 0x6449,
        EchoEnable = 0x6404, ReverbEnable = 0x640A, LiveMute = 0x6468;
    // Sonca Mixer's Mi30_OptionView / E1.B: source 25154, capability flags 25172.
    public const ushort InputSource = 0x6242, InputCapabilities = 0x6254;

    public static bool SupportsInfo(SoncaBleReply reply) => reply.Command == SoncaBleProtocol.CmdInfo
        && System.Text.Encoding.ASCII.GetString(reply.Payload).StartsWith("SCA_MAIN48_C360.C360", StringComparison.Ordinal);

    public static bool CanWrite(ushort register, ushort value) => register == MicVolume ? value <= 8192
        : register == InputSource ? value <= 5
        : register is MicMute or MusicMute or MasterMute or EchoEnable or ReverbEnable or LiveMute && value <= 1;

    public static bool TryReadInputSource(ushort raw, out byte source)
    {
        source = (byte)(raw & 15);
        return source <= 5;
    }

    public static bool SupportsInputSource(ushort capabilities, byte source) => source switch
    {
        0 => true,
        1 => (capabilities & 8) != 0,
        2 => (capabilities & 32) != 0,
        3 => (capabilities & 16) != 0,
        4 => (capabilities & 64) != 0,
        5 => (capabilities & 16384) != 0,
        _ => false
    };

    public static bool CanSelectInputSource(IReadOnlyDictionary<ushort, ushort> state, byte source) =>
        state.TryGetValue(InputSource, out ushort current) && TryReadInputSource(current, out _)
        && state.TryGetValue(InputCapabilities, out ushort capabilities) && SupportsInputSource(capabilities, source);

    // C360 INFO model 0x0433 (1075): Sonca Mi30_OptionView labels mode 5
    // SDCARD. KARAOKE is the generic label used for other hardware models.
    public static string InputSourceName(byte source) => source switch
    { 0 => "Bluetooth", 1 => "USB A", 2 => "OPTICAL", 3 => "HDMI", 4 => "LINE IN", 5 => "THẺ NHỚ (SD/TF)", _ => "Chưa xác định" };

    // C360's mobile fader has 201 internal steps but labels step / 2:
    // raw 4096 is displayed as 50, raw 8192 as 100.
    public static ushort VolumeToRaw(double level) => (ushort)(Math.Clamp(level, 0, 100) * 8192 / 100);
    public static double VolumeFromRaw(ushort raw) => raw * 100.0 / 8192;

    public static bool TryReadParameterUpdate(SoncaBleReply reply, out IReadOnlyDictionary<ushort, ushort> parameters)
    {
        var result = new Dictionary<ushort, ushort>();
        parameters = result;
        if (reply.Command is not (0x03 or 0x84) || reply.Status != 0 || reply.Payload.Length == 0 || reply.Payload.Length % 4 != 0) return false;
        var data = reply.Payload;
        for (int i = 0; i < data.Length; i += 4)
            result[(ushort)((data[i] << 8) | data[i + 1])] = (ushort)((data[i + 2] << 8) | data[i + 3]);
        return true;
    }

    public static bool TryMuteRegister(string channel, out ushort register, out bool inverted)
    {
        inverted = channel is "Echo" or "Reverb";
        register = channel switch { "Mic" => MicMute, "Music" => MusicMute, "Master" => MasterMute,
            "Echo" => EchoEnable, "Reverb" => ReverbEnable, "Live" => LiveMute, _ => 0 };
        return register != 0;
    }

    public static bool TryReadSnapshot(SoncaBleReply reply, out IReadOnlyDictionary<ushort, ushort> parameters)
    {
        var result = new Dictionary<ushort, ushort>();
        parameters = result;
        // C360 CMD=02: flag 00, embedded length (pairs + 3), BE NRPN/value
        // pairs, embedded check byte. The decoder has verified outer CRC8.
        var data = reply.Payload;
        if (reply.Command != SoncaBleProtocol.CmdGetParam || reply.Status != 0 || data.Length < 7
            || ((data[0] << 8) | data[1]) != data.Length || (data.Length - 3) % 4 != 0) return false;
        for (int i = 2; i < data.Length - 1; i += 4)
        {
            ushort address = (ushort)((data[i] << 8) | data[i + 1]);
            if (!result.TryAdd(address, (ushort)((data[i + 2] << 8) | data[i + 3]))) return false;
        }
        return result.Count > 0;
    }
}
