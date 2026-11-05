namespace SoncaAudioInspector;

public sealed record SoncaBleReply(byte Command, byte Status, byte[] Payload);

/// <summary>RX uses a 16-bit body length, command, flag, payload and CRC8.</summary>
public sealed class SoncaBleReplyDecoder
{
    private readonly List<byte> _buffer = new();
    public void Clear() { lock (_buffer) _buffer.Clear(); }
    public IReadOnlyList<SoncaBleReply> Push(ReadOnlySpan<byte> chunk)
    {
        lock (_buffer)
        {
            _buffer.AddRange(chunk.ToArray());
            var replies = new List<SoncaBleReply>();
            while (_buffer.Count >= 2)
            {
                int bodyLength = (_buffer[0] << 8) | _buffer[1];
                if (bodyLength < 3 || bodyLength > 16000) { _buffer.RemoveAt(0); continue; }
                if (_buffer.Count < bodyLength + 2) break;
                byte[] body = _buffer.Skip(2).Take(bodyLength - 1).ToArray();
                if (SoncaBleProtocol.ComputeCrc8(body) == _buffer[bodyLength + 1])
                {
                    replies.Add(new(body[0], body[1], body.Skip(2).ToArray()));
                    _buffer.RemoveRange(0, bodyLength + 2);
                }
                else _buffer.RemoveRange(0, bodyLength + 2);
            }
            if (_buffer.Count > 32000) _buffer.Clear();
            return replies;
        }
    }
}
