namespace SoncaAudioInspector;

/// <summary>A bounded live window that never reuses samples across stimulus changes.</summary>
public sealed class FreshCaptureWindow
{
    private readonly object _sync = new();
    private readonly float[] _buffer;
    private readonly Func<DateTime> _utcNow;
    private int _writeIndex, _count, _sampleRate = 48000, _discardSamples;
    private int _pendingDiscardMs = 350;
    private DateTime _lastDataUtc = DateTime.MinValue;

    public FreshCaptureWindow(int capacity = 16384, Func<DateTime>? utcNow = null)
    {
        _buffer = new float[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public void Reset(int discardMilliseconds = 350)
    {
        lock (_sync)
        {
            _writeIndex = _count = _discardSamples = 0;
            _pendingDiscardMs = Math.Max(0, discardMilliseconds);
            _lastDataUtc = DateTime.MinValue;
        }
    }

    public void Append(float[] samples, int sampleRate)
    {
        if (sampleRate <= 0 || samples.Length == 0) return;
        lock (_sync)
        {
            DateTime receivedAt = _utcNow();
            if (sampleRate != _sampleRate || (_lastDataUtc != DateTime.MinValue
                && receivedAt - _lastDataUtc > TimeSpan.FromMilliseconds(500)))
            {
                _writeIndex = _count = 0;
                _pendingDiscardMs = Math.Max(350, _pendingDiscardMs);
            }
            _sampleRate = sampleRate;
            if (_pendingDiscardMs >= 0)
            {
                _discardSamples = (int)Math.Ceiling(sampleRate * _pendingDiscardMs / 1000.0);
                _pendingDiscardMs = -1;
            }
            int start = Math.Min(samples.Length, _discardSamples);
            _discardSamples -= start;
            for (int i = start; i < samples.Length; i++)
            {
                _buffer[_writeIndex] = samples[i];
                _writeIndex = (_writeIndex + 1) % _buffer.Length;
                _count = Math.Min(_buffer.Length, _count + 1);
            }
            _lastDataUtc = receivedAt;
        }
    }

    public bool TryRead(int length, out float[] samples, out int sampleRate)
    {
        lock (_sync)
        {
            sampleRate = _sampleRate;
            samples = Array.Empty<float>();
            if (length <= 0 || length > _buffer.Length || _count < length
                || _utcNow() - _lastDataUtc > TimeSpan.FromMilliseconds(500)) return false;
            samples = new float[length];
            int start = (_writeIndex - length + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < length; i++) samples[i] = _buffer[(start + i) % _buffer.Length];
            return true;
        }
    }
}
