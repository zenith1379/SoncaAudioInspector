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
        public static readonly double[] TestFrequencies = new double[]
        {
            20, 50, 100, 150, 200, 250,
            500, 750, 1000, 1250, 1500, 
            3000, 4000, 5000, 6000, 7000,
            10000, 15000, 20000
        };

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

            double[] sweepFrequencies = TestFrequencies;

            Dictionary<double, double> rawDbResults = new Dictionary<double, double>();
            bool freqResponsePass = true;

            if (AudioEngine.flagGenerateSeperateSine)
            {
                double toneDuration = 0.5; // 500ms per tone (down from 800ms) to speed up testing while ensuring stability
                foreach (var freq in sweepFrequencies)
                {
                    if (_isCancelled) return;

                    if (freq > 10000) 
                    {
                        toneDuration = 1.0;
                    }

                    //OnLogMessage?.Invoke("Step 2", $"Testing frequency: " + toneDuration);

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
                    int sampleRate = _audioEngine.RecordingSampleRate;
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
                OnLogMessage?.Invoke("Step 2", "Playing and recording Multitone signal (1.5 seconds)...");
                OnTestSubstatusChanged?.Invoke("Freq", "Running Multitone (1.5s)...");

                float[] recorded = await _audioEngine.PlayAndRecordAsync(
                    playbackDevice, recordingDevice, SignalType.Multitone, 1000, 1.5);

                if (_isCancelled) return;

                OnLogMessage?.Invoke("Step 2", "Analyzing multitone response...");
                OnTestSubstatusChanged?.Invoke("Freq", "Analyzing Multitone...");

                // Detect clipping
                float maxSample = recorded.Length > 0 ? recorded.Select(Math.Abs).Max() : 0f;
                if (maxSample > 0.95f)
                {
                    OnLogMessage?.Invoke("Warning", "CRITICAL: Input clipping detected! Lower Playback Volume or Recording Gain.");
                }

                int sampleRate = _audioEngine.RecordingSampleRate;
                var multitoneResults = DspProcessor.CalculateMultitoneResponse(recorded, sampleRate, sweepFrequencies);

                foreach (var freq in sweepFrequencies)
                {
                    rawDbResults[freq] = multitoneResults[freq];
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

            // Export FEQ results to a CSV file if flagSaveData is true
            if (AudioEngine.flagSaveData)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string fileName = $"feq_results_{timestamp}.csv";
                    string filePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                    
                    using (var writer = new System.IO.StreamWriter(filePath))
                    {
                        writer.WriteLine("Sonca Audio Inspector - FEQ Test Results");
                        writer.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        writer.WriteLine($"Test Mode: {(AudioEngine.flagGenerateSeperateSine ? "Sine Sweep" : "Multitone")}");
                        writer.WriteLine($"Reference Level (1000 Hz): {referenceDb:F2} dBFS");
                        writer.WriteLine($"Tolerance Limit: ±{FreqResponseToleranceDb:F1} dB");
                        writer.WriteLine($"Overall Result: {(freqResponsePass ? "PASS" : "FAIL")}");
                        writer.WriteLine($"Max Deviation: {maxFreqDev:F2} dB");
                        writer.WriteLine();
                        writer.WriteLine("Frequency (Hz),Raw Level (dBFS),Normalized Level (dBr),Limit Status");
                        
                        foreach (var kvp in rawDbResults)
                        {
                            double freq = kvp.Key;
                            double rawDb = kvp.Value;
                            double normDb = normalizedResults[freq];
                            string status = "N/A";
                            if (freq >= 100 && freq <= 15000)
                            {
                                status = Math.Abs(normDb) <= FreqResponseToleranceDb ? "PASS" : "FAIL";
                            }
                            writer.WriteLine($"{freq},{rawDb:F2},{normDb:F2},{status}");
                        }
                    }
                    OnLogMessage?.Invoke("Step 2", $"Saved FEQ results to: {fileName}");
                }
                catch (Exception ex)
                {
                    OnLogMessage?.Invoke("Step 2 Error", $"Failed to save FEQ CSV: {ex.Message}");
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
                playbackDevice, recordingDevice, SignalType.Sine, 1000, 1.5, null, true); // 1.5 seconds, save files enabled

            // Detect clipping
            float maxThdSample = thdRecord.Length > 0 ? thdRecord.Select(Math.Abs).Max() : 0f;
            if (maxThdSample > 0.95f)
            {
                OnLogMessage?.Invoke("Warning", "CRITICAL: Input clipping detected during THD! Lower Playback Volume or Recording Gain.");
            }

            OnTestSubstatusChanged?.Invoke("THD", "Analyzing FFT...");

            // Calculate THD on the last 500ms captured buffer
            int sampleRateThd = _audioEngine.RecordingSampleRate;
            int analyzeCountThd = (int)(sampleRateThd * 0.5); // 500ms
            int thdStart = Math.Max(0, thdRecord.Length - analyzeCountThd);
            float[] thdBuffer = thdRecord.Skip(thdStart).ToArray();

            double[] frequencies;
            var thdCalc = DspProcessor.CalculateThd(thdBuffer, sampleRateThd, out frequencies);

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
            var thdCalc = DspProcessor.CalculateThd(noiseBuffer, _audioEngine.RecordingSampleRate, out frequencies);

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
