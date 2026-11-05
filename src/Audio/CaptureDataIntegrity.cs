namespace SoncaAudioInspector;

/// <summary>
/// Detects the repeated-byte PCM16 pattern observed when a FastTrack Pro stream
/// is decoded with the wrong byte order. It rejects data and never swaps bytes.
/// </summary>
public static class CaptureDataIntegrity
{
    public static bool HasRepeatedPcm16Bytes(IReadOnlyList<float> samples, double endpointGain)
    {
        if (samples.Count < 2048 || !double.IsFinite(endpointGain) || endpointGain <= 0 || endpointGain > 1.01)
            return false;
        for (int start = 0; start + 2048 <= samples.Count; start += 4096)
        {
            if (WindowHasRepeatedBytes(samples, start, Math.Min(4096, samples.Count - start), endpointGain))
                return true;
        }
        return false;
    }

    private static bool WindowHasRepeatedBytes(IReadOnlyList<float> samples, int start, int count, double endpointGain)
    {
        int informative = 0;
        int matches = 0;
        double sumSquares = 0;
        var distinct = new HashSet<int>();
        for (int index = start; index < start + count; index++)
        {
            float sample = samples[index];
            if (!float.IsFinite(sample)) return false;
            sumSquares += (double)sample * sample;
            double scaled = sample * 32768.0 / endpointGain;
            if (scaled < short.MinValue || scaled > short.MaxValue) return false;
            int code = (int)Math.Round(scaled);
            if (Math.Abs(code) < 32) continue;
            informative++;
            int word = code & 0xffff;
            if (Math.Abs(scaled - code) < 0.01 && (word & 255) == (word >> 8))
            {
                matches++;
                distinct.Add(code);
            }
        }
        return sumSquares / count >= 1e-7
            && informative >= 512
            && distinct.Count >= 2
            && matches >= informative * 0.999;
    }
}
