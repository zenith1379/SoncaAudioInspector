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

            // Frequencies to test (logarithmic spacing)
            double[] sweepFrequencies = new double[]
            {
                20, 30, 40, 60, 80, 100, 150, 200, 300, 400, 500, 700, 1000, 
                1500, 2000, 3000, 4000, 5000, 7000, 10000, 12000, 15000, 17000, 20000
            };

            Dictionary<double, double> rawDbResults = new Dictionary<double, double>();

            double toneDuration = 0.35; // 350ms per tone
            bool freqResponsePass = true;

            foreach (var freq in sweepFrequencies)
            {
                if (_isCancelled) return;

                OnLogMessage?.Invoke("Step 2", $"Testing frequency: {freq} Hz");
                
                // Play and record the tone
                float[] recorded = await _audioEngine.PlayAndRecordAsync(
                    playbackDevice, recordingDevice, SignalType.Sine, freq, toneDuration);

                // Use the last 50% of the recorded buffer to let the analog filters and AD/DA convertors settle
                int startOffset = recorded.Length / 2;
                int analyzeCount = recorded.Length - startOffset;

                double rms = DspProcessor.CalculateRms(recorded, startOffset, analyzeCount);
                double db = 20 * Math.Log10(rms + 1e-9); // Offset to prevent log of 0
                rawDbResults[freq] = db;
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

            Dictionary<double, double> normalizedResults = new Dictionary<double, double>();
            double maxFreqDev = 0;

            foreach (var kvp in rawDbResults)
            {
                double normDb = kvp.Value - referenceDb;
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
                    if (absoluteDev > FreqResponseToleranceDb)
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
                _steps[1].Details = $"Max Deviation: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB)";
                OnLogMessage?.Invoke("Step 2", $"Frequency response FAILED. Max deviation: {maxFreqDev:F2} dB");
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

            float[] thdRecord = await _audioEngine.PlayAndRecordAsync(
                playbackDevice, recordingDevice, SignalType.Sine, 1000, 1.2);

            // Calculate THD on the captured buffer
            int thdStart = thdRecord.Length / 2; // last half
            float[] thdBuffer = thdRecord.Skip(thdStart).ToArray();

            double[] frequencies;
            var thdCalc = DspProcessor.CalculateThd(thdBuffer, 48000, out frequencies);

            OnThdSpectrumReady?.Invoke(frequencies, thdCalc.magnitudes, thdCalc.thdPercent);

            bool thdPass = thdCalc.thdPercent <= ThdLimitPercent;

            if (thdPass)
            {
                _steps[2].Status = "Pass";
                _steps[2].Details = $"THD: {thdCalc.thdPercent:F3}% (Limit: < {ThdLimitPercent}%)";
                OnLogMessage?.Invoke("Step 3", $"THD measurement PASSED. THD: {thdCalc.thdPercent:F3}%");
            }
            else
            {
                _steps[2].Status = "Fail";
                _steps[2].Details = $"THD: {thdCalc.thdPercent:F3}% (Limit: < {ThdLimitPercent}%)";
                OnLogMessage?.Invoke("Step 3", $"THD measurement FAILED. THD: {thdCalc.thdPercent:F3}%");
            }

            OnStepsChanged?.Invoke(_steps);
            await Task.Delay(500);

            if (_isCancelled) return;

            // ----------------------------------------------------
            // Step 4: Final Verdict
            // ----------------------------------------------------
            _steps[3].Status = "Running";
            OnStepsChanged?.Invoke(_steps);

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
    }
}
