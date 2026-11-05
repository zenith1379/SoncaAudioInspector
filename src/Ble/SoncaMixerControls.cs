namespace SoncaAudioInspector;

public sealed record SoncaMixerControl(ushort Register, string Group, string Name, string Description,
    double Minimum, double Maximum, double Step, bool Decibels = false, bool QuantizedFeedback = false, bool CompensateWrite = false)
{
    public double Decode(ushort raw) => Decibels ? (raw - 500) / 10d : raw;
    public ushort Encode(double value) => checked((ushort)Math.Round(Decibels ? value * 10 + 500 : value));
    public bool Accepts(ushort raw) => raw != ushort.MaxValue && Decode(raw) >= Minimum && Decode(raw) <= Maximum;
    public string Display(ushort raw) => Decibels ? $"{Decode(raw):+0.0;-0.0;0.0} dB" : $"{raw} / 100";
    public ushort CommandFor(ushort requested) => CompensateWrite && requested is >0 and <100 ? (ushort)(requested+1) : requested;
    public bool MatchesReadback(ushort command,ushort actual) => actual==command
        || QuantizedFeedback && command>0 && actual==command-1;
}

/// <summary>Mappings from Sonca mobile F1.j/F1.o and E1.d/E1.l/E1 echo screen.</summary>
public static class SoncaMixerControls
{
    // F1.o.J: these Main48 models use the 0..100 effect tables, not the
    // Q15 feedback, Hz high-cut or sample-count predelay used by older boards.
    public static bool UsesIndexedEffects(ushort model) => model is 1153 or 1154 or 1058 or 1060 or 1061 or 1063
        or 1064 or 1071 or 1072 or 1065 or 1066 or 1073 or 1074 or 1075 or 1078 or 1079 or 1080 or 1081 or 1082;

    public static IReadOnlyList<SoncaMixerControl> ForFirmware(ReadOnlySpan<byte> info)
    {
        if (!SoncaBleMixerProfile.TryReadModelId(info, out ushort model) || !UsesIndexedEffects(model)) return Array.Empty<SoncaMixerControl>();
        return model==1071 && System.Text.Encoding.ASCII.GetString(info).Contains("C(chip BP):1.2.1-AT",StringComparison.Ordinal)
            ? Acoustic3:Indexed;
    }
    private static SoncaMixerControl Tone(ushort reg, string group, string name) => new(reg, group, name,
        "Tăng/giảm dải âm tương ứng; thang gain Sonca Mixer, 0 dB là mức trung tính.", -6, 6, .1, true);
    private static SoncaMixerControl Effect(ushort reg, string group, string name, string description) =>
        new(reg, group, name, description + " Thang firmware 0–100.", 0, 100, 1);
    public static IReadOnlyList<SoncaMixerControl> Indexed { get; } = new SoncaMixerControl[]
    {
        Tone(0x3040,"MIC","Bass"), Tone(0x3042,"MIC","Mid"), Tone(0x3041,"MIC","Treble"),
        Tone(0x3C40,"MUSIC","Bass"), Tone(0x3C42,"MUSIC","Mid"), Tone(0x3C41,"MUSIC","Treble"),
        Effect(0x6405,"ECHO","High cut","Giảm thành phần cao tần của tiếng echo."),
        Effect(0x6406,"ECHO","Repeat","Điều chỉnh độ lặp/phản hồi của echo."),
        Effect(0x6407,"ECHO","Delay","Điều chỉnh khoảng cách giữa các tiếng lặp; không quy đổi mức này thành ms."),
        Effect(0x6408,"ECHO","Dry","Mức tín hiệu trực tiếp trong đường echo."),
        Effect(0x6409,"ECHO","Wet","Mức tín hiệu đã qua echo."),
        Effect(0x644B,"REVERB","High cut","Giảm thành phần cao tần của reverb."),
        Effect(0x644D,"REVERB","Pre-delay","Khoảng trễ trước khi đuôi reverb bắt đầu."),
        Effect(0x644E,"REVERB","Diffusion","Độ dày và độ phân tán các phản xạ reverb."),
        Effect(0x6450,"REVERB","Damping","Độ tắt dần của thành phần cao tần trong đuôi reverb."),
        Effect(0x6451,"REVERB","Wet / Dry","Tỷ lệ tín hiệu đã qua reverb so với tín hiệu trực tiếp."),
        Effect(0x644F,"REVERB","Decay","Độ dài đuôi vang reverb."),
        Tone(0x5A40,"RECORD","Bass"), Tone(0x5A42,"RECORD","Mid"), Tone(0x5A41,"RECORD","Treble")
    };
    // On the captured ACOUSTIC3 BP firmware, Repeat's percentage is converted
    // through a fixed-point feedback value: SET 54 reads 53, SET 55 reads 54.
    // Compensate this one control; other parameters still require exact readback.
    private static IReadOnlyList<SoncaMixerControl> Acoustic3 { get; } = Indexed.Select(c => c.Register switch
    {
        0x6406 => c with {QuantizedFeedback=true,CompensateWrite=true},
        0x644F or 0x6451 => c with {QuantizedFeedback=true}, _ => c
    }).ToArray();
    public static string Fingerprint(IReadOnlyDictionary<ushort,ushort> state,ReadOnlySpan<byte> info,int captureRate)
    {
        var registers=new ushort[]{0x6102,0x6103,0x6400,0x6402,0x6403,0x6467,0x6254}
            .Concat(ForFirmware(info).Select(c=>c.Register)).Distinct().Order().ToArray();
        return "SONCA-DSP2;FW:"+Convert.ToHexString(info)+";"+string.Join(";",registers.Select(reg=>
            $"{reg:X4}:"+(state.TryGetValue(reg,out ushort raw)?raw.ToString():"NA")))+(captureRate>0?";CAPTURE_RATE:"+captureRate:"");
    }
    public static bool IsAdditionalRegister(ushort register) => Indexed.Any(c => c.Register == register);
    public static bool CanWrite(ReadOnlySpan<byte> info, IReadOnlyDictionary<ushort,ushort> state, ushort register, ushort value) =>
        ForFirmware(info).Any(c => c.Register == register && c.Accepts(value)
            && state.TryGetValue(register, out ushort current) && c.Accepts(current));
}
