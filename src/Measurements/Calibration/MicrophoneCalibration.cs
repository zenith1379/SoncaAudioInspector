using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SoncaAudioInspector
{
    /// <summary>
    /// Represents a microphone calibration profile (e.g. REW / miniDSP / UMIK-1 format).
    /// Contains 1kHz sensitivity offset and frequency-dependent dB correction curve.
    /// </summary>
    public class MicrophoneCalibration
    {
        public string Name { get; set; } = "None";
        public string FilePath { get; set; } = "";
        public double Sensitivity1kHzDbFs { get; set; } = 0.0;
        public bool HasSensitivity { get; set; } = false;

        public List<(double Frequency, double GainCorrectionDb, double PhaseDeg)> Points { get; } = new();

        public bool IsLoaded => Points.Count > 0;

        /// <summary>
        /// Reads a standard microphone calibration .txt file.
        /// Supports lines with "*1000Hz -37.4" and frequency/dB pairs separated by tab, comma, or spaces.
        /// </summary>
        public static MicrophoneCalibration LoadFromFile(string path)
        {
            return LoadFromText(File.Exists(path) ? File.ReadAllText(path) : "", Path.GetFileName(path), path);
        }

        public static MicrophoneCalibration LoadFromText(string content, string name, string identity = "")
        {
            var profile = new MicrophoneCalibration
            {
                FilePath = identity,
                Name = name
            };
            string[] lines = content.Replace("\r", "").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Check for 1kHz sensitivity header: e.g. "*1000Hz -37.4" or "Sens Factor = -37.4"
                if (line.StartsWith("*") || line.StartsWith("#") || line.StartsWith("\"") || line.Contains("1000Hz", StringComparison.OrdinalIgnoreCase))
                {
                    string cleaned = line.TrimStart('*', '#', '"', ' ');
                    var parts = cleaned.Split(new[] { ' ', '\t', ',', ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        if (parts[i].Contains("1000", StringComparison.OrdinalIgnoreCase) || parts[i].Contains("sens", StringComparison.OrdinalIgnoreCase))
                        {
                            if (double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sens))
                            {
                                profile.Sensitivity1kHzDbFs = sens;
                                profile.HasSensitivity = true;
                                break;
                            }
                        }
                    }
                    continue;
                }

                // Parse frequency-gain data line
                var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2)
                {
                    if (double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double freq) &&
                        double.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
                    {
                        double phase = 0.0;
                        if (tokens.Length >= 3)
                        {
                            double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out phase);
                        }

                        profile.Points.Add((freq, gain, phase));
                    }
                }
            }

            profile.Points.Sort((a, b) => a.Frequency.CompareTo(b.Frequency));
            return profile;
        }

        /// <summary>
        /// Interpolates the microphone frequency correction (in dB) for a given frequency.
        /// Positive value means mic is more sensitive at this frequency; subtracting it flattens the mic response.
        /// </summary>
        public double GetGainCorrectionDb(double frequency)
        {
            if (Points.Count == 0 || frequency <= 0)
                return 0.0;

            if (frequency <= Points[0].Frequency)
                return Points[0].GainCorrectionDb;

            if (frequency >= Points[^1].Frequency)
                return Points[^1].GainCorrectionDb;

            // Binary search for interval
            int low = 0;
            int high = Points.Count - 1;
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (Points[mid].Frequency <= frequency)
                    low = mid;
                else
                    high = mid;
            }

            var p1 = Points[low];
            var p2 = Points[high];

            if (p2.Frequency <= p1.Frequency)
                return p1.GainCorrectionDb;

            // Logarithmic interpolation across frequency
            double logF = Math.Log10(frequency);
            double logF1 = Math.Log10(p1.Frequency);
            double logF2 = Math.Log10(p2.Frequency);
            double alpha = (logF - logF1) / (logF2 - logF1);

            return p1.GainCorrectionDb + alpha * (p2.GainCorrectionDb - p1.GainCorrectionDb);
        }

        /// <summary>
        /// Scans standard locations for microphone calibration files.
        /// </summary>
        public static MicrophoneCalibration? ForKtUsbDevice(string deviceName)
        {
            if (!deviceName.Contains("KT USB", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (string directory in new[] { AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(typeof(MicrophoneCalibration).Assembly.Location) ?? "" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.Combine(directory, "calibrations", "99-00192.txt");
                if (!File.Exists(path)) continue;
                var profile = LoadFromFile(path);
                if (profile.IsLoaded) return profile;
            }
            return ScanAvailableCalibrations().FirstOrDefault(p => p.Name.Equals("99-00192.txt", StringComparison.OrdinalIgnoreCase));
        }

        public static List<MicrophoneCalibration> ScanAvailableCalibrations()
        {
            var results = new List<MicrophoneCalibration>();
            var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var candidateDirs = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "calibrations"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "standards"),
                Path.Combine(Directory.GetCurrentDirectory(), "calibrations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };

            foreach (var dir in candidateDirs)
            {
                if (!Directory.Exists(dir)) continue;

                var files = Directory.GetFiles(dir, "*99-*.txt")
                    .Concat(Directory.GetFiles(dir, "*cal*.txt"))
                    .Concat(Directory.GetFiles(dir, "*mic*.txt"))
                    .Distinct();

                foreach (var file in files)
                {
                    if (scannedPaths.Add(file))
                    {
                        var cal = LoadFromFile(file);
                        if (cal.IsLoaded)
                        {
                            results.Add(cal);
                        }
                    }
                }
            }

            return results;
        }
    }
}
