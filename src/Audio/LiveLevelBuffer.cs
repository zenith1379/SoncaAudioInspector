namespace SoncaAudioInspector;

/// <summary>One bounded aggregate per live session; never retains audio arrays or UI work items.</summary>
public sealed class LiveLevelBuffer
{
    private readonly object _sync = new();
    private long _session, _count;
    private bool _active, _invalid;
    private double _peak, _sumSquares;

    public long Start()
    {
        lock (_sync) { _active = true; Clear(); return ++_session; }
    }

    public void Stop()
    {
        lock (_sync) { _active = false; ++_session; Clear(); }
    }

    public bool IsCurrent(long session)
    {
        lock (_sync) return _active && session == _session;
    }

    public void Push(float[] samples, long session)
    {
        double peak = 0, sum = 0;
        bool invalid = false;
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample)) { invalid = true; continue; }
            peak = Math.Max(peak, Math.Abs((double)sample));
            sum += (double)sample * sample;
        }
        lock (_sync)
        {
            if (!_active || session != _session) return;
            _peak = Math.Max(_peak, peak);
            _sumSquares += sum;
            _count += samples.Length;
            _invalid |= invalid;
        }
    }

    public bool TryRead(out double peakDb, out double rmsDb, out bool clipped, out bool invalid)
    {
        lock (_sync)
        {
            peakDb = rmsDb = double.NaN;
            clipped = false;
            invalid = _invalid;
            if (!_active || _count == 0) return false;
            peakDb = 20 * Math.Log10(Math.Max(1e-6, _peak));
            rmsDb = 20 * Math.Log10(Math.Max(1e-6, Math.Sqrt(_sumSquares / _count)));
            clipped = _peak >= .995 || peakDb > -.5;
            Clear();
            return true;
        }
    }

    private void Clear() { _peak = _sumSquares = 0; _count = 0; _invalid = false; }
}
