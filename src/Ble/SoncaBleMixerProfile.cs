namespace SoncaAudioInspector;

public enum SoncaMixerOption { MicFbx, BassBoost, Enhancer }

/// <summary>Sonca Mixer NRPN schema, enabled per register reported by the connected firmware.</summary>
public static class SoncaBleMixerProfile
{
    // Sonca Mi30_OptionView: these are bit flags in 0x6254, not EQ NRPNs.
    public static ushort OptionMask(SoncaMixerOption option) => option switch
    { SoncaMixerOption.MicFbx => 1, SoncaMixerOption.BassBoost => 2, SoncaMixerOption.Enhancer => 512, _ => 0 };
    public static bool SupportsOption(ushort flags, SoncaMixerOption option) => flags != ushort.MaxValue && option switch
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
        EchoEnable = 0x6404, ReverbEnable = 0x640A, ReverbEnableAlternate = 0x644A, LiveMute = 0x6468;
    // Sonca Mixer's Mi30_OptionView / E1.B: source 25154, capability flags 25172.
    public const ushort InputSource = 0x6242, InputCapabilities = 0x6254;

    public static bool SupportsInfo(SoncaBleReply reply) => reply.Command == SoncaBleProtocol.CmdInfo && reply.Status == 0
        && System.Text.Encoding.ASCII.GetString(reply.Payload).StartsWith("SCA_MAIN48_C360.C360", StringComparison.Ordinal);

    // The Sonca mobile Mi30 option screen uses this routing schema for several
    // boards, independently of the C360-only mixer/effect profile. Require both
    // the Sonca INFO signature and fresh source/capability registers.
    public static bool SupportsRoutingInfo(SoncaBleReply reply) => reply.Command == SoncaBleProtocol.CmdInfo
        && reply.Status == 0 && reply.Payload.AsSpan().StartsWith("SCA_"u8);

    public static bool HasMixerControlState(IReadOnlyDictionary<ushort, ushort> state) =>
        state.Any(pair => pair.Key != InputSource && CanWrite(pair.Key, pair.Value));

    public static bool CanWriteReportedParameter(IReadOnlyDictionary<ushort, ushort> state, ushort register, ushort value) =>
        state.TryGetValue(register, out ushort current) && CanWrite(register, current) && CanWrite(register, value);

    public static bool HasInputRoutingState(IReadOnlyDictionary<ushort, ushort> state) =>
        state.TryGetValue(InputSource, out ushort raw) && TryReadInputSource(raw, out _)
        && state.TryGetValue(InputCapabilities, out ushort flags) && flags != ushort.MaxValue;

    public static bool CanWrite(ushort register, ushort value) => register is MicVolume or MusicVolume or MasterVolume or EchoVolume or ReverbVolume or LiveVolume ? value <= 8192
        : register == InputSource ? value <= 5
        : register is MicMute or MusicMute or MasterMute or EchoEnable or ReverbEnable or ReverbEnableAlternate or LiveMute ? value <= 1
        : SoncaMixerControls.Indexed.Any(c => c.Register == register && c.Accepts(value));

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
        && state.TryGetValue(InputCapabilities, out ushort capabilities) && capabilities != ushort.MaxValue && SupportsInputSource(capabilities, source);

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

    public static bool TryMuteRegister(string channel, ReadOnlySpan<byte> info,
        IReadOnlyDictionary<ushort, ushort> state, out ushort register, out bool inverted)
    {
        if (!TryMuteRegister(channel, out register, out inverted)) return false;
        if (channel != "Reverb") return true;
        // Sonca mobile E1.e selects 0x644A for F1.o.O model IDs; other
        // models use 0x640A. A single reported enable register is unambiguous.
        if (TryReadModelId(info, out ushort model))
        {
            register = model is 1839 or 1854 or 1856 or 1857 or 1858 or 1867 or 1868 or 1869
                ? ReverbEnableAlternate : ReverbEnable;
            return true;
        }
        bool standard = state.TryGetValue(ReverbEnable, out ushort a) && a <= 1;
        bool alternate = state.TryGetValue(ReverbEnableAlternate, out ushort b) && b <= 1;
        if (standard == alternate) return false;
        register = alternate ? ReverbEnableAlternate : ReverbEnable;
        return true;
    }

    public static bool TryReadModelId(ReadOnlySpan<byte> info, out ushort model)
    {
        model = 0;
        int end = info.IndexOf((byte)0);
        if (end < 0 || info.Length < end + 4 || info[end + 1] != (byte)'-') return false;
        model = (ushort)((info[end + 2] << 8) | info[end + 3]);
        return true;
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
