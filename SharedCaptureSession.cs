using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoncaAudioInspector;

/// <summary>
/// Keeps FastTrack Pro's shared capture stream open within one AudioEngine lifetime.
/// This stream drains input only; it never renders sound or supplies measurement samples.
/// The engine must dispose it on shutdown. Playback remains separately stopped per test.
/// </summary>
public sealed class SharedCaptureSession : IDisposable
{
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private Exception? _failure;
    private int _stopping;
    private bool _disposed;

    public bool IsActive => _capture != null;

    public void Ensure(MMDevice device, bool exclusive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (exclusive || !device.FriendlyName.Contains("FastTrack Pro", StringComparison.OrdinalIgnoreCase))
        {
            Release();
            return;
        }
        if (_device?.ID == device.ID && Volatile.Read(ref _failure) == null
            && _capture?.WaveFormat.SampleRate == FastTrackDeviceSetup.ReadMixSampleRate(device.ID))
        {
            return;
        }
        // Replugging USB or changing Default Format can retain the endpoint ID.
        // Reopen the stream when its format no longer matches Windows.
        Release();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(device.ID);
            _failure = null;
            Volatile.Write(ref _stopping, 0);
            _capture = new WasapiCapture(_device, false, 100);
            _capture.RecordingStopped += (_, e) =>
            {
                if (Volatile.Read(ref _stopping) == 0)
                    Interlocked.CompareExchange(ref _failure,
                        e.Exception ?? new IOException("Luồng thu FastTrack Pro đã dừng ngoài dự kiến."), null);
            };
            _capture.StartRecording();
            ThrowIfStopped();
        }
        catch
        {
            Release();
            throw;
        }
    }

    public void ThrowIfStopped()
    {
        Exception? failure = Volatile.Read(ref _failure);
        if (failure != null)
            throw new IOException("Mất phiên thu FastTrack Pro; không sử dụng kết quả đo. Kiểm tra kết nối USB.", failure);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();
        _failure = null;
    }

    private void Release()
    {
        Volatile.Write(ref _stopping, 1);
        WasapiCapture? capture = _capture;
        MMDevice? device = _device;
        _capture = null;
        _device = null;
        try { capture?.Dispose(); }
        finally { device?.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }
}
