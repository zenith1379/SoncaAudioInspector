using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoncaAudioInspector;

/// <summary>Owns only the scope's WAV output. It never changes AudioEngine or endpoint settings.</summary>
public sealed class ScopeWavPlayer : IDisposable
{
    private WasapiOut? _output;
    private MMDevice? _device;
    private RepeatingWaveFile? _file;
    private Exception? _failure;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public Exception? Failure => Volatile.Read(ref _failure);
    public static string DefaultFilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "sine-1000Hz-minus12dBFS-RMS.wav");

    public void Play(string deviceId, int? channel)
    {
        Stop();
        Interlocked.Exchange(ref _failure, null);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(deviceId);
            using var client = _device.AudioClient;
            int channels = client.MixFormat.Channels;
            if (channel.HasValue && (channel.Value < 0 || channel.Value >= channels))
                throw new ArgumentException("Kênh phát WAV không tồn tại trên thiết bị đã chọn.", nameof(channel));
            _file = new RepeatingWaveFile(DefaultFilePath);
            ISampleProvider source = _file.ToSampleProvider();
            if (channels >= 2)
                source = channel.HasValue ? new ChannelRoutingSampleProvider(source, channel)
                    : new MonoToStereoSampleProvider(source);
            var output = new WasapiOut(_device, AudioClientShareMode.Shared, false, 100);
            _output = output;
            output.PlaybackStopped += (_, e) =>
            {
                if (ReferenceEquals(_output, output))
                    Interlocked.CompareExchange(ref _failure, e.Exception
                        ?? new IOException("Phát WAV đã dừng ngoài dự kiến. Kiểm tra kết nối ngõ phát."), null);
            };
            output.Init(source);
            output.Play();
        }
        catch { Stop(); throw; }
    }

    public void Stop()
    {
        var output = _output;
        _output = null; // Ignore PlaybackStopped caused by our own stop.
        try
        {
            try { output?.Stop(); }
            finally { output?.Dispose(); } // Join the playback worker before disposing the reader.
        }
        finally
        {
            try { _file?.Dispose(); }
            finally { _file = null; _device?.Dispose(); _device = null; }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>The supplied WAV contains exactly 10,000 sine periods, so repeats are phase-continuous.</summary>
public sealed class RepeatingWaveFile : WaveStream
{
    private readonly WaveFileReader _reader;
    public RepeatingWaveFile(string path)
    {
        _reader = new WaveFileReader(path);
        if (_reader.Length == 0) { _reader.Dispose(); throw new InvalidDataException("File WAV không có dữ liệu âm thanh."); }
    }
    public override WaveFormat WaveFormat => _reader.WaveFormat;
    public override long Length => _reader.Length;
    public override long Position { get => _reader.Position; set => _reader.Position = value; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = _reader.Read(buffer, offset + total, count - total);
            total += read;
            if (read == 0)
            {
                if (_reader.Position == 0) break;
                _reader.Position = 0;
            }
        }
        return total;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}
