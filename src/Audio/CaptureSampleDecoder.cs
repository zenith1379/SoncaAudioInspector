using NAudio.Wave;

namespace SoncaAudioInspector;

/// <summary>Decode supported WASAPI formats, with optional channel isolation before any averaging.</summary>
public static class CaptureSampleDecoder
{
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static float[] Decode(byte[] buffer, int count, WaveFormat format, double gain, int? channel = null)
    {
        if (count < 0 || count > buffer.Length || !double.IsFinite(gain) || gain < 0)
            throw new ArgumentException("Dữ liệu hoặc mức thu không hợp lệ.");
        int channels = format.Channels, bits = format.BitsPerSample, bytes = bits / 8;
        if (channels < 1 || bytes < 1 || format.BlockAlign < 1
            || format.BlockAlign != channels * bytes || count % format.BlockAlign != 0
            || channel is int selected && (selected < 0 || selected >= channels))
            throw new ArgumentException("Kênh thu hoặc khung dữ liệu âm thanh không hợp lệ.");
        bool pcm = format.Encoding == WaveFormatEncoding.Pcm;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible extended)
        {
            pcm = extended.SubFormat == PcmSubtype;
            floating = extended.SubFormat == FloatSubtype;
        }
        if (!(pcm && bits is 16 or 24 or 32) && !(floating && bits == 32))
            throw new NotSupportedException("Định dạng thu chưa hỗ trợ. Chọn PCM 16/24/32-bit hoặc Float 32-bit.");
        var result = new float[count / format.BlockAlign];
        int first = channel ?? 0, last = channel.HasValue ? first + 1 : channels;
        for (int frame = 0; frame < result.Length; frame++)
        {
            double sum = 0;
            for (int c = first; c < last; c++)
            {
                int offset = frame * format.BlockAlign + c * bytes;
                sum += floating ? BitConverter.ToSingle(buffer, offset) : bits switch
                {
                    16 => BitConverter.ToInt16(buffer, offset) / 32768.0,
                    24 => ((sbyte)buffer[offset + 2] << 16 | buffer[offset + 1] << 8 | buffer[offset]) / 8388608.0,
                    _ => BitConverter.ToInt32(buffer, offset) / 2147483648.0
                };
            }
            result[frame] = (float)(sum / (last - first) * gain);
        }
        return result;
    }
}
