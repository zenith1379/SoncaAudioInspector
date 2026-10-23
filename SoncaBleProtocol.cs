using System;
using System.IO;

namespace SoncaAudioInspector;

/// <summary>
/// Giao thức BLE giao tiếp với các dòng loa / vang số của Soncamedia / Acnos (tương tự app Sonca Mixer).
/// </summary>
public static class SoncaBleProtocol
{
    // UUIDs tiêu chuẩn của dòng Soncamedia / Acnos DSP
    public static readonly Guid PrimaryServiceUuid = Guid.Parse("00005343-0000-1000-8000-00805f9b34fb"); // "SC" (SonCa)
    public static readonly Guid StandardUartServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb");

    public static readonly Guid WriteCharacteristicUuid1 = Guid.Parse("0000ff02-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharacteristicUuid2 = Guid.Parse("0000fffa-0000-1000-8000-00805f9b34fb");

    public static readonly Guid NotifyCharacteristicUuid1 = Guid.Parse("0000ff01-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyCharacteristicUuid2 = Guid.Parse("0000ffff-0000-1000-8000-00805f9b34fb");

    public const byte Stx = 0x02;
    public const byte Marker = 0x80;

    // Command Codes
    public const byte CmdSetParam = 0x04;
    public const byte CmdGetParam = 0x02;
    public const byte CmdInfo = 0x01;

    // Channel Types
    public const byte ChannelMusic = 0x01;
    public const byte ChannelMic = 0x02;
    public const byte ChannelMaster = 0x00;
    public const byte ChannelEffect = 0x03;

    // Fader/mute constants below refer to the verified C360 profile. Legacy
    // tone/FBX/PEQ addresses are unverified and rejected by the manager.
    public const ushort RegMasterVol = SoncaBleMixerProfile.MasterVolume;
    public const ushort RegInputSource = SoncaBleMixerProfile.InputSource;
    public const ushort RegMasterMute = SoncaBleMixerProfile.MasterMute;
    public const ushort RegBassBoost = 0x304A;   // 0: Off, 1: On
    public const ushort RegEnhancer = 0x304B;    // 0: Off, 1: On
    public const ushort RegLiveVol = SoncaBleMixerProfile.LiveVolume;

    public const ushort RegMusicMasterVol = SoncaBleMixerProfile.MusicVolume;
    public const ushort RegMusicBass = 0x3041;
    public const ushort RegMusicMid = 0x3042;
    public const ushort RegMusicTreble = 0x3043;
    public const ushort RegMusicMute = SoncaBleMixerProfile.MusicMute;

    public const ushort RegMicMasterVol = SoncaBleMixerProfile.MicVolume;
    public const ushort RegMicBass = 0x3141;
    public const ushort RegMicMid = 0x3142;
    public const ushort RegMicTreble = 0x3143;
    public const ushort RegFbxLevel = 0x3148; // 0: Off, 1: Cấp 1, 2: Cấp 2, 3: Cấp 3
    public const ushort RegMicMute = SoncaBleMixerProfile.MicMute;

    public const ushort RegEchoLevel = SoncaBleMixerProfile.EchoVolume;
    public const ushort RegReverbLevel = SoncaBleMixerProfile.ReverbVolume;
    public const ushort RegEchoDelay = 0x3442;
    public const ushort RegEchoRepeat = 0x3443;
    public const ushort RegReverbDecay = 0x3444;
    public const ushort RegReverbTone = 0x3445;
    public const ushort RegEffectMode = 0x3448; // 0: Echo, 1: Reverb, 2: Echo + Reverb

    // Parametric Equalizer (PEQ) Bands
    public const ushort RegPeqBand1 = 0x3240; // 80 Hz
    public const ushort RegPeqBand2 = 0x3241; // 250 Hz
    public const ushort RegPeqBand3 = 0x3242; // 1.0 kHz
    public const ushort RegPeqBand4 = 0x3243; // 4.0 kHz
    public const ushort RegPeqBand5 = 0x3244; // 12.0 kHz
    public const ushort RegPeqBypass = 0x324F; // 0: Active, 1: Bypass

    /// <summary>
    /// Tính CRC8 theo chuẩn CCITT / SMBus đa thức 0x07 (khởi tạo 0x00) như trong Sonca Mixer app.
    /// </summary>
    public static byte ComputeCrc8(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
            {
                if ((crc & 0x80) != 0)
                {
                    crc = (crc << 1) ^ 0x07;
                }
                else
                {
                    crc <<= 1;
                }
            }
        }
        return (byte)(crc & 0xFF);
    }

    /// <summary>
    /// Đóng gói một khung tin BLE Sonca theo chuẩn: [0x02, 0x80, cmd, len, payload..., crc8]
    /// </summary>
    public static byte[] BuildPacket(byte cmd, byte[]? payload)
    {
        using var bodyStream = new MemoryStream();
        bodyStream.WriteByte(cmd);

        if (cmd == CmdInfo)
        {
            byte[] infoBytes = System.Text.Encoding.ASCII.GetBytes("SCA=INFO");
            bodyStream.WriteByte((byte)infoBytes.Length);
            bodyStream.Write(infoBytes, 0, infoBytes.Length);
        }
        else
        {
            int len = payload?.Length ?? 0;
            bodyStream.WriteByte((byte)len);
            if (payload != null && len > 0)
            {
                bodyStream.Write(payload, 0, len);
            }
        }

        byte[] body = bodyStream.ToArray();
        byte crc = ComputeCrc8(body);

        using var packetStream = new MemoryStream();
        packetStream.WriteByte(Stx);
        packetStream.WriteByte(Marker);
        packetStream.Write(body, 0, body.Length);
        packetStream.WriteByte(crc);

        return packetStream.ToArray();
    }

    /// <summary>
    /// Tạo gói truy vấn thông tin máy "SCA=INFO".
    /// </summary>
    public static byte[] BuildInfoPacket()
    {
        return BuildPacket(CmdInfo, null);
    }

    /// <summary>
    /// Tạo gói điều chỉnh tham số DSP (Register address + Value).
    /// </summary>
    public static byte[] BuildParamPacket(byte channelType, ushort regAddr, ushort value)
    {
        // The NRPN address already identifies the channel. The wire payload is
        // a big-endian address/value pair, without an extra channel byte.
        byte[] payload = new byte[4]
        {
            (byte)((regAddr >> 8) & 0xFF),
            (byte)(regAddr & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF)
        };

        return BuildPacket(CmdSetParam, payload);
    }

    // GET reads the current config through 0x6209=1; arbitrary NRPN GETs
    // return empty acknowledgements on C360 and do not read that parameter.
    public static byte[] BuildReadMixerPacket() => BuildPacket(CmdGetParam, new byte[] { 0x62, 0x09, 0x00, 0x01 });

    /// <summary>
    /// Chuyển nguồn nhạc (0: Bluetooth, 1: USB nhạc, 2: Optical, 3: HDMI, 4: Line IN, 5: SD/TF trên C360).
    /// </summary>
    public static byte[] BuildInputSourcePacket(byte inputIndex)
    {
        if (inputIndex > 5) throw new ArgumentOutOfRangeException(nameof(inputIndex));
        return BuildParamPacket(ChannelMusic, RegInputSource, inputIndex);
    }

    /// <summary>
    /// Điều chỉnh chế độ chống hú FBX Micro (0: Tắt, 1: Nhẹ, 2: Tiêu chuẩn, 3: Mạnh).
    /// </summary>
    public static byte[] BuildFbxPacket(byte fbxLevel)
    {
        return BuildParamPacket(ChannelMic, RegFbxLevel, fbxLevel);
    }

    /// <summary>
    /// Bật/tắt Bass Boost.
    /// </summary>
    public static byte[] BuildBassBoostPacket(bool enable)
    {
        return BuildParamPacket(ChannelMusic, RegBassBoost, (ushort)(enable ? 1 : 0));
    }

    /// <summary>
    /// Bật/tắt Enhancer (Nâng tiếng).
    /// </summary>
    public static byte[] BuildEnhancerPacket(bool enable)
    {
        return BuildParamPacket(ChannelMusic, RegEnhancer, (ushort)(enable ? 1 : 0));
    }

    /// <summary>
    /// Điều chỉnh Mute tổng hoặc từng kênh.
    /// </summary>
    public static byte[] BuildMutePacket(byte channelType, ushort muteReg, bool isMute)
    {
        return BuildParamPacket(channelType, muteReg, (ushort)(isMute ? 1 : 0));
    }

    /// <summary>
    /// Điều chỉnh mức Gain (-12 đến +12 dB) cho dải tần PEQ (bandIndex 0..4).
    /// </summary>
    public static byte[] BuildPeqPacket(byte bandIndex, int gainDb)
    {
        ushort reg = (ushort)(RegPeqBand1 + Math.Clamp((int)bandIndex, 0, 4));
        int clampedGain = Math.Clamp(gainDb, -12, 12);
        ushort val = (ushort)(clampedGain + 24); // Offset encoding
        return BuildParamPacket(ChannelMusic, reg, val);
    }

    /// <summary>
    /// Giải mã gói tin nhận được từ loa.
    /// </summary>
    public static bool TryParsePacket(ReadOnlySpan<byte> buffer, out byte cmd, out byte[] payload)
    {
        cmd = 0;
        payload = Array.Empty<byte>();

        if (buffer.Length < 5) return false;
        if (buffer[0] != Stx || buffer[1] != Marker) return false;

        cmd = buffer[2];
        int len = buffer[3];
        if (buffer.Length < 4 + len + 1) return false;

        ReadOnlySpan<byte> body = buffer.Slice(2, 2 + len);
        byte expectedCrc = buffer[4 + len];
        byte calculatedCrc = ComputeCrc8(body);

        if (expectedCrc != calculatedCrc) return false;

        payload = buffer.Slice(4, len).ToArray();
        return true;
    }

    /// <summary>
    /// Kiểm tra xem tên thiết bị có khả năng là loa Soncamedia / Acnos hay không.
    /// </summary>
    public static bool IsLikelySoncaDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string upper = name.ToUpperInvariant();
        string normalized = upper.Replace(" ", "").Replace("-", "").Replace("_", "");
        return upper.Contains("ACNOS") ||
               upper.Contains("SONCA") ||
               upper.Contains("BLUZEK") ||
               upper.Contains("KBEATBOX") ||
               upper.Contains("CS36") ||
               upper.Contains("CS45") ||
               upper.Contains("CS55") ||
               upper.Contains("MI30") ||
               upper.Contains("MI 30") ||
               upper.Contains("MI SAM") ||
               normalized.Contains("MISAM") ||
               normalized.Contains("MI30") ||
               normalized.Contains("KBEATBOX");
    }
}
