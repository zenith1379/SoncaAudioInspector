using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector
{
    public class TestStep
    {
        public string Name { get; set; }
        public string Status { get; set; } = "Waiting"; // Waiting, Running, Pass, Fail
        public string Details { get; set; } = "";
    }

    public class TestRunner
    {
        private readonly AudioEngine _audioEngine;
        
        // Target limits
        public double FreqResponseToleranceDb { get; set; } = 3.0; // +/- 3dB limit
        public double ThdLimitPercent { get; set; } = 0.5; // THD < 0.5% limit

        // Events for UI notification
        public event Action<List<TestStep>> OnStepsChanged;
        public event Action<string, string> OnLogMessage;
        public event Action<double, double> OnFrequencyResponsePoint; // frequency, dB
        public event Action<double[], double[], double> OnThdSpectrumReady; // frequencies, magnitudes, thdPercent
        public event Action<bool> OnTestCompleted;
        public event Action<string, string> OnTestSubstatusChanged; // type (Freq/THD), details (e.g. "Playing Sweep...")

        private List<TestStep> _steps;
        private bool _isCancelled = false;

        public TestRunner(AudioEngine audioEngine)
        {
            _audioEngine = audioEngine;
            InitializeSteps();
        }

        public void InitializeSteps()
        {
            _steps = new List<TestStep>
            {
                new TestStep { Name = "1. Device Connection & Check" },
                new TestStep { Name = "2. Frequency Response Analysis (20Hz - 20kHz)" },
                new TestStep { Name = "3. Total Harmonic Distortion (THD) Measurement" },
                new TestStep { Name = "4. Compile Final Verdict" }
            };
            OnStepsChanged?.Invoke(_steps);
        }

        public void Cancel()
        {
            _isCancelled = true;
            _audioEngine.Stop();
        }

        public async Task RunTestAsync(MMDevice playbackDevice, MMDevice recordingDevice)
        {
            _isCancelled = false;
            InitializeSteps();
            OnLogMessage?.Invoke("System", "Starting automated test procedure...");

            // ----------------------------------------------------
            // Step 1: Device Connection Check
            // ----------------------------------------------------
            _steps[0].Status = "Running";
            OnStepsChanged?.Invoke(_steps);
            OnLogMessage?.Invoke("Step 1", "Checking playback and recording hardware...");
            await Task.Delay(500);

            if (playbackDevice == null || recordingDevice == null)
            {
                _steps[0].Status = "Fail";
                _steps[0].Details = "Missing playback or recording device.";
                OnStepsChanged?.Invoke(_steps);
                OnLogMessage?.Invoke("Step 1 Error", "Error: Audio hardware not selected or disconnected.");
                OnTestCompleted?.Invoke(false);
                return;
            }

            _steps[0].Status = "Pass";
            _steps[0].Details = $"Out: {playbackDevice.FriendlyName}\nIn: {recordingDevice.FriendlyName}";
            OnStepsChanged?.Invoke(_steps);
            OnLogMessage?.Invoke("Step 1", $"Connected Out: {playbackDevice.FriendlyName}");
            OnLogMessage?.Invoke("Step 1", $"Connected In: {recordingDevice.FriendlyName}");

            if (_isCancelled) return;

            // ----------------------------------------------------
            // Step 2: Frequency Response
            // ----------------------------------------------------
            _steps[1].Status = "Running";
            OnStepsChanged?.Invoke(_steps);
            OnLogMessage?.Invoke("Step 2", "Starting frequency sweep from 20 Hz to 20 kHz...");
            OnTestSubstatusChanged?.Invoke("Freq", "Initializing...");

            // Frequencies to test (logarithmic spacing)
            double[] sweepFrequencies = new double[]
            {
                20, 30, 40, 60, 80, 100, 150, 200, 300, 400, 500, 700, 1000, 
                1500, 2000, 3000, 4000, 5000, 7000, 10000, 12000, 15000, 17000, 20000
            };

            Dictionary<double, double> rawDbResults = new Dictionary<double, double>();
            bool freqResponsePass = true;

            if (AudioEngine.flagGenerateSine)
            {
                double toneDuration = 0.8; // 800ms per tone to completely clear USB buffer start latency
                foreach (var freq in sweepFrequencies)
                {
                    if (_isCancelled) return;

                    if (freq < 200)
                    {
                        toneDuration = 0.8;
                    }
                    else 
                    {
                        toneDuration = 0.6;
                    }
                    
                    OnLogMessage?.Invoke("Step 2", $"Testing frequency: {freq} Hz");
                    OnTestSubstatusChanged?.Invoke("Freq", $"Testing: {freq} Hz");
                    
                    // Play and record the tone
                    float[] recorded = await _audioEngine.PlayAndRecordAsync(
                        playbackDevice, recordingDevice, SignalType.Sine, freq, toneDuration);

                    // Detect clipping
                    float maxSample = recorded.Length > 0 ? recorded.Select(Math.Abs).Max() : 0f;
                    if (maxSample > 0.95f)
                    {
                        OnLogMessage?.Invoke("Warning", "CRITICAL: Input clipping detected! Lower Playback Volume or Recording Gain.");
                    }

                    // Analyze the last 200ms of the 800ms window where audio is guaranteed to be fully settled and running
                    int sampleRate = 48000;
                    int analyzeCount = (int)(sampleRate * 0.2); // 200ms
                    int startOffset = Math.Max(0, recorded.Length - analyzeCount);
                    int countToAnalyze = Math.Min(analyzeCount, recorded.Length - startOffset);

                    double rms = DspProcessor.CalculateRms(recorded, startOffset, countToAnalyze);
                    double db = 20 * Math.Log10(rms + 1e-9); // Offset to prevent log of 0
                    rawDbResults[freq] = db;
                }
            }
            else
            {
                // Play file sweep
                string wavPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audiocheck.net_sweep_20Hz_20000Hz_0dBFS_10s.wav");
                if (!System.IO.File.Exists(wavPath))
                {
                    _steps[1].Status = "Fail";
                    _steps[1].Details = "Sweep WAV file not found.";
                    OnLogMessage?.Invoke("Step 2 Error", "Error: audiocheck.net_sweep_20Hz_20000Hz_0dBFS_10s.wav not found in app directory.");
                    OnTestCompleted?.Invoke(false);
                    return;
                }

                OnLogMessage?.Invoke("Step 2", "Playing sweep WAV file (10 seconds)...");
                OnTestSubstatusChanged?.Invoke("Freq", "Playing Sweep (10s)...");
                float[] recorded = await _audioEngine.PlayFileAndRecordAsync(wavPath, playbackDevice, recordingDevice, 10.5);

                OnLogMessage?.Invoke("Step 2", "Analyzing sweep recording...");
                OnTestSubstatusChanged?.Invoke("Freq", "Analyzing Sweep...");

                // Detect clipping
                float maxSample = recorded.Length > 0 ? recorded.Select(Math.Abs).Max() : 0f;
                if (maxSample > 0.95f)
                {
                    OnLogMessage?.Invoke("Warning", "CRITICAL: Input clipping detected! Lower Playback Volume or Recording Gain.");
                }

                // Detect when signal starts (threshold check) to adjust for hardware/OS delay
                int startSignalIndex = 0;
                for (int i = 0; i < recorded.Length; i++)
                {
                    if (Math.Abs(recorded[i]) > 0.005f)
                    {
                        startSignalIndex = i;
                        break;
                    }
                }

                double latencyMs = (double)startSignalIndex / 48000 * 1000;
                OnLogMessage?.Invoke("Step 2", $"Estimated latency: {latencyMs:F1} ms");

                int sampleRate = 48000;

                foreach (var freq in sweepFrequencies)
                {
                    // For log sweep f = 20 * 1000^(t/10) => t = 10 * log10(f/20) / 3
                    double t = 10.0 * Math.Log10(freq / 20.0) / 3.0;
                    int targetSampleIndex = startSignalIndex + (int)(t * sampleRate);

                    // 150ms analysis window centered around target time
                    int windowSize = (int)(sampleRate * 0.150);
                    int startOffset = Math.Max(0, targetSampleIndex - windowSize / 2);
                    int countToAnalyze = Math.Min(windowSize, recorded.Length - startOffset);

                    double rms = DspProcessor.CalculateRms(recorded, startOffset, countToAnalyze);
                    double db = 20 * Math.Log10(rms + 1e-9);
                    rawDbResults[freq] = db;
                }
            }

            // Normalize results such that 1 kHz (1000 Hz) is 0 dB
            double referenceDb = 0;
            if (rawDbResults.ContainsKey(1000))
            {
                referenceDb = rawDbResults[1000];
            }
            else
            {
                referenceDb = rawDbResults.Values.Max(); // fallback
            }

            bool isSilent = referenceDb < -70.0;
            if (isSilent)
            {
                freqResponsePass = false;
                OnLogMessage?.Invoke("Step 2 Error", $"Silent input detected ({referenceDb:F1} dBFS). Check playback/recording device connections.");
            }

            Dictionary<double, double> normalizedResults = new Dictionary<double, double>();
            double maxFreqDev = 0;

            foreach (var kvp in rawDbResults)
            {
                double normDb = isSilent ? kvp.Value : (kvp.Value - referenceDb);
                normalizedResults[kvp.Key] = normDb;

                // Fire point event so the UI chart updates in real-time
                OnFrequencyResponsePoint?.Invoke(kvp.Key, normDb);

                // Evaluate limits (only between 100 Hz and 15000 Hz, where typical device response is critical)
                if (kvp.Key >= 100 && kvp.Key <= 15000)
                {
                    double absoluteDev = Math.Abs(normDb);
                    if (absoluteDev > maxFreqDev)
                    {
                        maxFreqDev = absoluteDev;
                    }
                    if (isSilent || absoluteDev > FreqResponseToleranceDb)
                    {
                        freqResponsePass = false;
                    }
                }
            }

            if (freqResponsePass)
            {
                _steps[1].Status = "Pass";
                _steps[1].Details = $"Max Deviation: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB)";
                OnLogMessage?.Invoke("Step 2", $"Frequency response PASSED. Max deviation: {maxFreqDev:F2} dB");
            }
            else
            {
                _steps[1].Status = "Fail";
                _steps[1].Details = isSilent ? "No signal detected (silent input)." : $"Max Deviation: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB)";
                OnLogMessage?.Invoke("Step 2", isSilent ? "Frequency response FAILED: Silent input." : $"Frequency response FAILED. Max deviation: {maxFreqDev:F2} dB");
            }

            OnStepsChanged?.Invoke(_steps);
            await Task.Delay(500);

            if (_isCancelled) return;

            // ----------------------------------------------------
            // Step 3: THD Measurement
            // ----------------------------------------------------
            _steps[2].Status = "Running";
            OnStepsChanged?.Invoke(_steps);
            OnLogMessage?.Invoke("Step 3", "Measuring Total Harmonic Distortion (THD) at 1 kHz...");
            OnTestSubstatusChanged?.Invoke("Freq", ""); // Clear freq active status
            OnTestSubstatusChanged?.Invoke("THD", "Testing 1 kHz Tone (1.5s)...");

            float[] thdRecord = await _audioEngine.PlayAndRecordAsync(
                playbackDevice, recordingDevice, SignalType.Sine, 1000, 1.5); // 1.5 seconds

            // Detect clipping
            float maxThdSample = thdRecord.Length > 0 ? thdRecord.Select(Math.Abs).Max() : 0f;
            if (maxThdSample > 0.95f)
            {
                OnLogMessage?.Invoke("Warning", "CRITICAL: Input clipping detected during THD! Lower Playback Volume or Recording Gain.");
            }

            OnTestSubstatusChanged?.Invoke("THD", "Analyzing FFT...");

            // Calculate THD on the last 500ms captured buffer
            int sampleRateThd = 48000;
            int analyzeCountThd = (int)(sampleRateThd * 0.5); // 500ms
            int thdStart = Math.Max(0, thdRecord.Length - analyzeCountThd);
            float[] thdBuffer = thdRecord.Skip(thdStart).ToArray();

            double[] frequencies;
            var thdCalc = DspProcessor.CalculateThd(thdBuffer, 48000, out frequencies);

            OnThdSpectrumReady?.Invoke(frequencies, thdCalc.magnitudes, thdCalc.thdPercent);

            double thdRms = DspProcessor.CalculateRms(thdBuffer, 0, thdBuffer.Length);
            double thdDbFS = 20 * Math.Log10(thdRms + 1e-9);
            bool isThdSilent = thdDbFS < -70.0;

            bool thdPass = !isThdSilent && thdCalc.thdPercent <= ThdLimitPercent;

            if (thdPass)
            {
                _steps[2].Status = "Pass";
                _steps[2].Details = $"THD: {thdCalc.thdPercent:F3}% (Limit: < {ThdLimitPercent}%)";
                OnLogMessage?.Invoke("Step 3", $"THD measurement PASSED. THD: {thdCalc.thdPercent:F3}%");
            }
            else
            {
                _steps[2].Status = "Fail";
                _steps[2].Details = isThdSilent ? "No signal detected (silent input)." : $"THD: {thdCalc.thdPercent:F3}% (Limit: < {ThdLimitPercent}%)";
                OnLogMessage?.Invoke("Step 3", isThdSilent ? "THD measurement FAILED: Silent input." : $"THD measurement FAILED. THD: {thdCalc.thdPercent:F3}%");
            }

            OnTestSubstatusChanged?.Invoke("THD", "Finished");
            OnStepsChanged?.Invoke(_steps);
            await Task.Delay(500);

            if (_isCancelled) return;

            // ----------------------------------------------------
            // Step 4: Final Verdict
            // ----------------------------------------------------
            _steps[3].Status = "Running";
            OnStepsChanged?.Invoke(_steps);
            OnTestSubstatusChanged?.Invoke("Freq", "");
            OnTestSubstatusChanged?.Invoke("THD", "");

            bool overallSuccess = freqResponsePass && thdPass;

            if (overallSuccess)
            {
                _steps[3].Status = "Pass";
                _steps[3].Details = "Device matches all criteria.";
                OnLogMessage?.Invoke("Verdict", "TEST PASSED. Device is within specs.");
            }
            else
            {
                _steps[3].Status = "Fail";
                _steps[3].Details = "Device out of spec bounds.";
                OnLogMessage?.Invoke("Verdict", "TEST FAILED. Device does not meet specs.");
            }

            OnStepsChanged?.Invoke(_steps);
            OnTestCompleted?.Invoke(overallSuccess);
        }

        public async Task RunNoiseTestAsync(MMDevice playbackDevice, MMDevice recordingDevice)
        {
            _isCancelled = false;
            OnLogMessage?.Invoke("Noise Test", "Starting Noise Floor & Hum analysis...");
            OnTestSubstatusChanged?.Invoke("THD", "Capturing Silence (1.5s)...");

            if (recordingDevice == null || playbackDevice == null)
            {
                OnLogMessage?.Invoke("Noise Test Error", "Error: Missing playback or recording device.");
                return;
            }

            // Capture 1.5 seconds of silence (Sine wave of 0 Hz is absolute digital silence)
            float[] recorded = await _audioEngine.PlayAndRecordAsync(
                playbackDevice, recordingDevice, SignalType.Sine, 0, 1.5);

            if (_isCancelled) return;

            OnTestSubstatusChanged?.Invoke("THD", "Analyzing Noise Spectrum...");

            // Use the last 1.0 second of data
            int startOffset = recorded.Length / 3;
            int analyzeCount = recorded.Length - startOffset;
            float[] noiseBuffer = recorded.Skip(startOffset).ToArray();

            double rms = DspProcessor.CalculateRms(recorded, startOffset, analyzeCount);
            double dbFS = 20 * Math.Log10(rms + 1e-9);

            // Compute FFT on the noise buffer to show hum and switching noise
            double[] frequencies;
            var thdCalc = DspProcessor.CalculateThd(noiseBuffer, 48000, out frequencies);

            // Trigger the spectrum ready event to draw the noise FFT chart (pass 0.0 for THD indicator)
            OnThdSpectrumReady?.Invoke(frequencies, thdCalc.magnitudes, 0.0);

            OnLogMessage?.Invoke("Noise Test", $"Average Noise Level: {dbFS:F2} dBFS");

            if (dbFS > -55.0)
            {
                OnLogMessage?.Invoke("Noise Test Warning", "WARNING: High noise floor (> -55 dBFS)! Ground loop or USB isolation issue is highly likely.");
            }
            else
            {
                OnLogMessage?.Invoke("Noise Test", "Noise level is excellent. Signal routing is clean.");
            }
            OnTestSubstatusChanged?.Invoke("THD", "Finished");
        }
    }
}
