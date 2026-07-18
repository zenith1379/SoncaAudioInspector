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
        // Configuration options for frequency bands count
        public static int BassCount = 15;
        public static int MidCount = 15;
        public static int TrebleCount = 15;

        // Band boundaries
        public const double BassMin = 20;
        public const double BassMax = 250;
        public const double MidMin = 250;
        public const double MidMax = 4000;
        public const double TrebleMin = 4000;
        public const double TrebleMax = 20000;

        public static double[] TestFrequencies
        {
            get
            {
                var freqs = new List<double>();
                GenerateLogFrequencies(BassMin, BassMax, BassCount, freqs);
                GenerateLogFrequencies(MidMin, MidMax, MidCount, freqs);
                GenerateLogFrequencies(TrebleMin, TrebleMax, TrebleCount, freqs);
                if (!freqs.Contains(1000.0))
                {
                    freqs.Add(1000.0);
                }
                return freqs.Distinct().OrderBy(f => f).ToArray();
            }
        }

        private static void GenerateLogFrequencies(double min, double max, int count, List<double> list)
        {
            if (count <= 0) return;
            if (count == 1)
            {
                list.Add(Math.Round(min));
                return;
            }
            double factor = Math.Log(max / min);
            for (int i = 0; i < count; i++)
            {
                double val = min * Math.Exp(factor * i / (count - 1));
                list.Add(Math.Round(val));
            }
        }

        private readonly AudioEngine _audioEngine;
        
        // Target limits
        public double FreqResponseToleranceDb { get; set; } = 3.0; // +/- 3dB limit
        public double ThdLimitPercent { get; set; } = 0.5; // THD < 0.5% limit
        public Dictionary<double, double> StandardCurve { get; set; } = null;
        public double LastMaxDevPercent { get; private set; } = 0;
        public double LastAvgDevPercent { get; private set; } = 0;
        public bool HasComparedToStandard { get; private set; } = false;

        public bool BassPassed { get; private set; } = true;
        public bool MidPassed { get; private set; } = true;
        public bool TreblePassed { get; private set; } = true;
        public bool ThdPassed { get; private set; } = true;

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

        public async Task RunTestAsync(MMDevice playbackDevice, MMDevice recordingDevice, bool runFeqThreeTimes = false)
        {
            _isCancelled = false;
            InitializeSteps();
            OnLogMessage?.Invoke("System", "Starting automated test procedure...");

            LastMaxDevPercent = 0;
            LastAvgDevPercent = 0;
            HasComparedToStandard = false;
            BassPassed = true;
            MidPassed = true;
            TreblePassed = true;
            ThdPassed = true;

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
            bool freqResponsePass = true;
            double maxFreqDev = 0;
            string compareMsg = "";
            bool isSilent = false;

            int totalRuns = runFeqThreeTimes ? 3 : 1;
            int passedRuns = 0;
            int failedRuns = 0;

            for (int run = 1; run <= totalRuns; run++)
            {
                if (_isCancelled) return;

                OnLogMessage?.Invoke("Step 2", $"--- FEQ Run {run}/{totalRuns} ---");
                OnTestSubstatusChanged?.Invoke("Freq", $"Run {run}/{totalRuns}...");

                Dictionary<double, double> rawDbResults = new Dictionary<double, double>();

                if (AudioEngine.flagGenerateSeperateSine)
                {
                    double toneDuration = 0.5; // 500ms per tone
                    foreach (var freq in sweepFrequencies)
                    {
                        if (_isCancelled) return;

                        if (freq > 10000) 
                        {
                            toneDuration = 1.0;
                        }

                        OnLogMessage?.Invoke("Step 2", $"Run {run} - Testing: {freq} Hz");
                        OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - {freq} Hz");
                        
                        // Play and record the tone
                        float[] recorded = await _audioEngine.PlayAndRecordAsync(
                            playbackDevice, recordingDevice, SignalType.Sine, freq, toneDuration);

                        // Detect clipping
                        float maxSample = recorded.Length > 0 ? recorded.Select(Math.Abs).Max() : 0f;
                        if (maxSample > 0.95f)
                        {
                            OnLogMessage?.Invoke("Warning", "CRITICAL: Input clipping detected! Lower Playback Volume or Recording Gain.");
                        }

                        // Analyze the last 200ms
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
                    OnLogMessage?.Invoke("Step 2", $"Run {run} - Playing Multitone signal (1.5 seconds)...");
                    OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - Multitone (1.5s)...");

                    float[] recorded = await _audioEngine.PlayAndRecordAsync(
                        playbackDevice, recordingDevice, SignalType.Multitone, 1000, 1.5);

                    if (_isCancelled) return;

                    OnLogMessage?.Invoke("Step 2", "Analyzing multitone response...");
                    OnTestSubstatusChanged?.Invoke("Freq", $"Run {run} - Analyzing...");

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

                isSilent = referenceDb < -70.0;
                if (isSilent)
                {
                    OnLogMessage?.Invoke("Step 2 Error", $"Run {run} - Silent input detected ({referenceDb:F1} dBFS).");
                }

                Dictionary<double, double> normalizedResults = new Dictionary<double, double>();
                double runMaxFreqDev = 0;
                int checkedPointsCount = 0;
                int failedPointsCount = 0;

                bool runBassPassed = true;
                bool runMidPassed = true;
                bool runTreblePassed = true;

                foreach (var kvp in rawDbResults)
                {
                    double normDb = isSilent ? kvp.Value : (kvp.Value - referenceDb);
                    normalizedResults[kvp.Key] = normDb;

                    // Fire point event so the UI chart updates in real-time
                    OnFrequencyResponsePoint?.Invoke(kvp.Key, normDb);

                    // Evaluate limits (only between 100 Hz and 15000 Hz)
                    if (kvp.Key >= 100 && kvp.Key <= 15000)
                    {
                        checkedPointsCount++;

                        double targetDb = 0;
                        bool hasStandard = StandardCurve != null && StandardCurve.Count > 0;
                        if (hasStandard && StandardCurve.ContainsKey(kvp.Key))
                        {
                            targetDb = StandardCurve[kvp.Key];
                        }

                        double absoluteDev = Math.Abs(normDb - targetDb);
                        if (absoluteDev > runMaxFreqDev)
                        {
                            runMaxFreqDev = absoluteDev;
                        }

                        bool isLimitExceeded = hasStandard && (absoluteDev > FreqResponseToleranceDb);
                        if (isSilent || isLimitExceeded)
                        {
                            failedPointsCount++;
                            if (kvp.Key < MidMin) runBassPassed = false;
                            else if (kvp.Key < TrebleMin) runMidPassed = false;
                            else runTreblePassed = false;
                        }
                    }
                }

                // If isSilent, force all to failed
                if (isSilent)
                {
                    runBassPassed = false;
                    runMidPassed = false;
                    runTreblePassed = false;
                }

                // Pass the run if fewer than or equal to 10% of the points are out of bounds
                double runFailedRatio = checkedPointsCount > 0 ? (double)failedPointsCount / checkedPointsCount : 0.0;
                bool runPassed = !isSilent && (runFailedRatio <= 0.10);

                if (runPassed)
                {
                    passedRuns++;
                    OnLogMessage?.Invoke("Step 2", $"Run {run} PASSED. Out-of-bounds: {failedPointsCount}/{checkedPointsCount} ({runFailedRatio * 100.0:F1}%), Max Dev: {runMaxFreqDev:F2} dB");
                }
                else
                {
                    failedRuns++;
                    OnLogMessage?.Invoke("Step 2", $"Run {run} FAILED. Out-of-bounds: {failedPointsCount}/{checkedPointsCount} ({runFailedRatio * 100.0:F1}%), Max Dev: {runMaxFreqDev:F2} dB");
                }

                // Update representative fields based on current run
                maxFreqDev = runMaxFreqDev;
                BassPassed = runBassPassed;
                MidPassed = runMidPassed;
                TreblePassed = runTreblePassed;

                // Compare with standard curve if available
                double maxDevPercent = 0;
                double sumDevPercent = 0;
                int comparedCount = 0;

                if (StandardCurve != null && StandardCurve.Count > 0)
                {
                    foreach (var kvp in normalizedResults)
                    {
                        double freq = kvp.Key;
                        if (freq >= 100 && freq <= 15000 && StandardCurve.ContainsKey(freq))
                        {
                            double currDb = kvp.Value;
                            double stdDb = StandardCurve[freq];
                            double diffDb = currDb - stdDb;
                            double ratio = Math.Pow(10, diffDb / 20.0);
                            double devPercent = Math.Abs(ratio - 1.0) * 100.0;

                            sumDevPercent += devPercent;
                            if (devPercent > maxDevPercent)
                            {
                                maxDevPercent = devPercent;
                            }
                            comparedCount++;
                        }
                    }
                }

                if (comparedCount > 0)
                {
                    double avgDevPercent = sumDevPercent / comparedCount;
                    LastMaxDevPercent = maxDevPercent;
                    LastAvgDevPercent = avgDevPercent;
                    HasComparedToStandard = true;
                    compareMsg = $" | Dev to Std: Max {maxDevPercent:F1}%, Avg {avgDevPercent:F1}%";
                }

                // Export FEQ results to a CSV file if flagSaveData is true
                if (AudioEngine.flagSaveData)
                {
                    try
                    {
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string fileName = $"feq_results_run{run}_{timestamp}.csv";
                        string filePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                        
                        using (var writer = new System.IO.StreamWriter(filePath))
                        {
                            writer.WriteLine($"Sonca Audio Inspector - FEQ Test Results (Run {run})");
                            writer.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            writer.WriteLine($"Test Mode: {(AudioEngine.flagGenerateSeperateSine ? "Sine Sweep" : "Multitone")}");
                            writer.WriteLine($"Reference Level (1000 Hz): {referenceDb:F2} dBFS");
                            writer.WriteLine($"Tolerance Limit: ±{FreqResponseToleranceDb:F1} dB");
                            writer.WriteLine($"Overall Run Result: {(runPassed ? "PASS" : "FAIL")}");
                            writer.WriteLine($"Max Deviation: {runMaxFreqDev:F2} dB");
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
                                    double targetDb = 0;
                                    if (StandardCurve != null && StandardCurve.ContainsKey(freq)) targetDb = StandardCurve[freq];
                                    status = Math.Abs(normDb - targetDb) <= FreqResponseToleranceDb ? "PASS" : "FAIL";
                                }
                                writer.WriteLine($"{freq},{rawDb:F2},{normDb:F2},{status}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLogMessage?.Invoke("Step 2 Error", $"Failed to save FEQ CSV for Run {run}: {ex.Message}");
                    }
                }

                // Short-circuit evaluations
                if (passedRuns >= (runFeqThreeTimes ? 2 : 1))
                {
                    freqResponsePass = true;
                    OnLogMessage?.Invoke("Step 2", $"FEQ Decided: PASS ({passedRuns} Passes, {failedRuns} Fails)");
                    break;
                }
                if (failedRuns >= (runFeqThreeTimes ? 2 : 1))
                {
                    freqResponsePass = false;
                    OnLogMessage?.Invoke("Step 2", $"FEQ Decided: FAIL ({passedRuns} Passes, {failedRuns} Fails)");
                    break;
                }

                // Brief cool-down delay between sweeps for hardware stability
                await Task.Delay(300);
            }

            if (freqResponsePass)
            {
                _steps[1].Status = "Pass";
                _steps[1].Details = $"Max Deviation: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB){compareMsg}";
                OnLogMessage?.Invoke("Step 2", $"Frequency response PASSED. Max deviation: {maxFreqDev:F2} dB{compareMsg}");
            }
            else
            {
                _steps[1].Status = "Fail";
                string failedBands = "";
                if (!BassPassed) failedBands += "Bass ";
                if (!MidPassed) failedBands += "Middle ";
                if (!TreblePassed) failedBands += "Treble ";
                string failedBandsMsg = string.IsNullOrEmpty(failedBands) ? "" : $" | Failed: {failedBands.Trim()}";

                _steps[1].Details = isSilent ? "No signal detected (silent input)." : $"Max Dev: {maxFreqDev:F2} dB (Limit: ±{FreqResponseToleranceDb} dB){failedBandsMsg}{compareMsg}";
                OnLogMessage?.Invoke("Step 2", isSilent ? "Frequency response FAILED: Silent input." : $"Frequency response FAILED. Max dev: {maxFreqDev:F2} dB{failedBandsMsg}{compareMsg}");
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
            ThdPassed = thdPass;

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
