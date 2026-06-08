using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoncaAudioInspector
{
    public class AudioEngine : IDisposable
    {
        // Global flags for testing
        public static bool flagSaveFile = true; // TODO TEST
        public static bool flagGenerateSine = false; // TODO TEST

        private MMDeviceEnumerator _enumerator;
        private WasapiOut _wasapiOut;
        private WasapiCapture _wasapiCapture;
        private SignalSampleProvider _signalProvider;
        private List<float> _recordedSamples;
        private object _lock = new object();

        // Volume and Gain controls (0.0 to 1.0 / 2.0)
        public double PlaybackVolume { get; set; } = 0.8;
        public double RecordingGain { get; set; } = 1.0;

        public AudioEngine()
        {
            _enumerator = new MMDeviceEnumerator();
            _recordedSamples = new List<float>();
        }

        public List<MMDevice> GetPlaybackDevices()
        {
            return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }

        public List<MMDevice> GetRecordingDevices()
        {
            return _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        }

        public MMDevice AutoDetectPlaybackDevice()
        {
            var devices = GetPlaybackDevices();
            return devices.FirstOrDefault(d => 
                d.FriendlyName.IndexOf("MI_LCD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                d.FriendlyName.IndexOf("MI_SAM", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public MMDevice AutoDetectRecordingDevice()
        {
            var devices = GetRecordingDevices();
            return devices.FirstOrDefault(d => 
                d.FriendlyName.IndexOf("SONCA", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Plays a sine wave on the selected device and captures the response from the recording device.
        /// </summary>
        public async Task<float[]> PlayAndRecordAsync(
            MMDevice playbackDevice, 
            MMDevice recordingDevice, 
            SignalType signalType, 
            double frequency, 
            double durationSeconds,
            Action<float[]> realTimeRecordedCallback = null)
        {
            Stop();

            _recordedSamples.Clear();

            int sampleRate = 48000; // Standard professional audio sample rate
            int channels = 1;

            // Setup recording
            _wasapiCapture = new WasapiCapture(recordingDevice);
            _wasapiCapture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _wasapiCapture.ShareMode = AudioClientShareMode.Shared;
            
            _wasapiCapture.DataAvailable += (s, e) =>
            {
                lock (_lock)
                {
                    int sampleCount = e.BytesRecorded / 4;
                    float[] buffer = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        // Apply recording gain digitally
                        buffer[i] = (float)(BitConverter.ToSingle(e.Buffer, i * 4) * RecordingGain);
                    }
                    _recordedSamples.AddRange(buffer);
                    realTimeRecordedCallback?.Invoke(buffer);
                }
            };

            // Setup playback and played audio tracking for testing
            List<float> playedSamplesList = flagSaveFile ? new List<float>() : null;
            
            _signalProvider = new SignalSampleProvider(sampleRate, signalType, frequency, PlaybackVolume, samples =>
            {
                if (flagSaveFile && playedSamplesList != null)
                {
                    lock (playedSamplesList)
                    {
                        playedSamplesList.AddRange(samples);
                    }
                }
            });

            _wasapiOut = new WasapiOut(playbackDevice, AudioClientShareMode.Shared, false, 60);
            _wasapiOut.Init(_signalProvider);

            // Start capture and playback
            _wasapiCapture.StartRecording();
            _wasapiOut.Play();

            // Wait for duration
            await Task.Delay((int)(durationSeconds * 1000));

            // Stop
            Stop();

            lock (_lock)
            {
                // Save output and input files if testing flag is enabled
                if (flagSaveFile)
                {
                    try
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        string playedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"played_{signalType}_{frequency}Hz_{timestamp}.wav");
                        string recordedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"recorded_{signalType}_{frequency}Hz_{timestamp}.wav");

                        if (playedSamplesList != null)
                        {
                            using (var writer = new WaveFileWriter(playedPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)))
                            {
                                float[] playedArr;
                                lock (playedSamplesList) { playedArr = playedSamplesList.ToArray(); }
                                writer.WriteSamples(playedArr, 0, playedArr.Length);
                            }
                        }

                        using (var writer = new WaveFileWriter(recordedPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)))
                        {
                            float[] recordedArr = _recordedSamples.ToArray();
                            writer.WriteSamples(recordedArr, 0, recordedArr.Length);
                        }
                    }
                    catch { }
                }

                return _recordedSamples.ToArray();
            }
        }

        /// <summary>
        /// Plays a WAV audio file on the selected playback device and captures the response on the recording device.
        /// </summary>
        public async Task<float[]> PlayFileAndRecordAsync(
            string filePath,
            MMDevice playbackDevice,
            MMDevice recordingDevice,
            double durationSeconds)
        {
            Stop();

            _recordedSamples.Clear();

            int sampleRate = 48000;
            int channels = 1;

            // Setup recording
            _wasapiCapture = new WasapiCapture(recordingDevice);
            _wasapiCapture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _wasapiCapture.ShareMode = AudioClientShareMode.Shared;
            
            _wasapiCapture.DataAvailable += (s, e) =>
            {
                lock (_lock)
                {
                    int sampleCount = e.BytesRecorded / 4;
                    float[] buffer = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        buffer[i] = (float)(BitConverter.ToSingle(e.Buffer, i * 4) * RecordingGain);
                    }
                    _recordedSamples.AddRange(buffer);
                }
            };

            // Setup file playback
            var fileReader = new AudioFileReader(filePath);
            var volumeProvider = new VolumeSampleProvider(fileReader);
            volumeProvider.Volume = (float)PlaybackVolume;

            _wasapiOut = new WasapiOut(playbackDevice, AudioClientShareMode.Shared, false, 60);
            _wasapiOut.Init(volumeProvider);

            // Start capture and playback
            _wasapiCapture.StartRecording();
            _wasapiOut.Play();

            // Wait for duration
            await Task.Delay((int)(durationSeconds * 1000));

            // Stop
            Stop();
            fileReader.Dispose();

            lock (_lock)
            {
                // Save recorded file if testing flag is enabled
                if (flagSaveFile)
                {
                    try
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        string recordedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"recorded_file_sweep_{timestamp}.wav");

                        using (var writer = new WaveFileWriter(recordedPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)))
                        {
                            float[] recordedArr = _recordedSamples.ToArray();
                            writer.WriteSamples(recordedArr, 0, recordedArr.Length);
                        }
                    }
                    catch { }
                }

                return _recordedSamples.ToArray();
            }
        }

        public void Stop()
        {
            try
            {
                if (_wasapiOut != null)
                {
                    _wasapiOut.Stop();
                    _wasapiOut.Dispose();
                    _wasapiOut = null;
                }
            }
            catch { }

            try
            {
                if (_wasapiCapture != null)
                {
                    _wasapiCapture.StopRecording();
                    _wasapiCapture.Dispose();
                    _wasapiCapture = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _enumerator?.Dispose();
        }
    }

    public enum SignalType
    {
        Sine,
        PinkNoise,
        Sweep
    }

    public class SignalSampleProvider : ISampleProvider
    {
        private readonly int _sampleRate;
        private readonly SignalType _type;
        private double _frequency;
        private double _phase;
        private readonly double _volume;
        private readonly Action<float[]> _onSamplesGenerated;
        private readonly WaveFormat _waveFormat;
        private Random _random = new Random();

        // Pink noise filter state variables
        private double b0, b1, b2, b3, b4, b5, b6;

        public SignalSampleProvider(int sampleRate, SignalType type, double frequency, double volume, Action<float[]> onSamplesGenerated = null)
        {
            _sampleRate = sampleRate;
            _type = type;
            _frequency = frequency;
            _volume = volume;
            _onSamplesGenerated = onSamplesGenerated;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat => _waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            double samplePeriod = 1.0 / _sampleRate;
            float[] tempTrack = _onSamplesGenerated != null ? new float[count] : null;

            for (int i = 0; i < count; i++)
            {
                float sampleValue = 0f;
                if (_type == SignalType.Sine)
                {
                    sampleValue = (float)(_volume * 0.8 * Math.Sin(_phase));
                    _phase += 2.0 * Math.PI * _frequency * samplePeriod;
                    if (_phase > 2.0 * Math.PI)
                    {
                        _phase -= 2.0 * Math.PI;
                    }
                }
                else if (_type == SignalType.PinkNoise)
                {
                    // Voss-McCartney algorithm for pink noise
                    double white = _random.NextDouble() * 2.0 - 1.0;
                    b0 = 0.99886 * b0 + white * 0.0555179;
                    b1 = 0.99332 * b1 + white * 0.0750759;
                    b2 = 0.96900 * b2 + white * 0.1538520;
                    b3 = 0.86650 * b3 + white * 0.3104856;
                    b4 = 0.55000 * b4 + white * 0.5329522;
                    b5 = -0.7616 * b5 - white * 0.0168980;
                    double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362;
                    b6 = white * 0.115926;
                    sampleValue = (float)(pink * 0.08 * _volume);
                }

                buffer[offset + i] = sampleValue;
                if (tempTrack != null)
                {
                    tempTrack[i] = sampleValue;
                }
            }

            if (tempTrack != null)
            {
                _onSamplesGenerated?.Invoke(tempTrack);
            }

            return count;
        }
    }
}
