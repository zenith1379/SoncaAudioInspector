using System;
using System.Collections.Generic;
using System.Linq;

namespace SoncaAudioInspector
{
    public sealed record FrequencyLimitPoint(
        double FrequencyHz,
        double TargetDb,
        double LowerDb,
        double UpperDb,
        bool Critical);

    public sealed record CriticalFrequencyZone(double MinHz, double MaxHz)
    {
        public bool Contains(double frequencyHz) =>
            frequencyHz >= Math.Min(MinHz, MaxHz) && frequencyHz <= Math.Max(MinHz, MaxHz);
    }

    public sealed record NoiseSpectrumMetrics(
        double BroadbandDb,
        string Unit,
        IReadOnlyDictionary<double, double> HumLevelsDb,
        double DominantHumHz,
        double DominantHumDb,
        double DcOffset = 0,
        double PeakSample = 0,
        double CrestFactor = 0,
        double HumProminenceDb = 0,
        double DominantSpectralPeakHz = 0,
        double DominantSpectralPeakDb = -180)
    {
        public double TotalRmsDb => BroadbandDb;
    }

    public sealed record NoiseAssessment(
        NoiseSpectrumMetrics DutMicrophone,
        NoiseSpectrumMetrics? AmbientMicrophone,
        bool EnvironmentTooLoud,
        bool LikelyDutOrFixtureNoise,
        string Classification,
        int Attempt)
    {
        public NoiseSpectrumMetrics DutMetrics => DutMicrophone;
    }

    public static class ProductionMeasurement
    {
        private static readonly double[] HumFrequencies =
            { 50, 60, 100, 120, 150, 180, 200, 240, 250, 300, 360 };

        public static bool TryGetFrequencyLimit(
            IReadOnlyDictionary<double, FrequencyLimitPoint>? limits,
            double frequencyHz,
            out FrequencyLimitPoint point)
        {
            point = new FrequencyLimitPoint(frequencyHz, 0, 0, 0, false);
            if (limits == null || limits.Count == 0 || frequencyHz <= 0 || !double.IsFinite(frequencyHz)) return false;
            try
            {
                if (limits.TryGetValue(frequencyHz, out FrequencyLimitPoint? exact))
                {
                    point = exact;
                    return true;
                }

                FrequencyLimitPoint[] values = limits.Values.ToArray();
                if (values.Length == 0) return false;

                FrequencyLimitPoint? lower = values
                    .Where(value => value.FrequencyHz < frequencyHz)
                    .OrderByDescending(value => value.FrequencyHz)
                    .FirstOrDefault();
                FrequencyLimitPoint? upper = values
                    .Where(value => value.FrequencyHz > frequencyHz)
                    .OrderBy(value => value.FrequencyHz)
                    .FirstOrDefault();
                if (lower == null || upper == null) return false;

                double lowerLog = Math.Log10(lower.FrequencyHz);
                double upperLog = Math.Log10(upper.FrequencyHz);
                double denom = upperLog - lowerLog;
                if (Math.Abs(denom) < 1e-9) return false;
                double ratio = (Math.Log10(frequencyHz) - lowerLog) / denom;
                point = new FrequencyLimitPoint(
                    frequencyHz,
                    Interpolate(lower.TargetDb, upper.TargetDb, ratio),
                    Interpolate(lower.LowerDb, upper.LowerDb, ratio),
                    Interpolate(lower.UpperDb, upper.UpperDb, ratio),
                    lower.Critical || upper.Critical);
                return true;
            }
            catch
            {
                return false;
            }
        }


        public static NoiseSpectrumMetrics AnalyzeNoise(
            float[] samples,
            int sampleRate,
            double? calibrationOffsetDb)
        {
            if (samples == null || samples.Length == 0 || sampleRate <= 0)
            {
                return new NoiseSpectrumMetrics(
                    -180,
                    calibrationOffsetDb.HasValue ? "dB SPL" : "dBFS",
                    new Dictionary<double, double>(),
                    0,
                    -180);
            }

            double rms = DspProcessor.CalculateRms(samples, 0, samples.Length);
            double broadbandDbFs = 20.0 * Math.Log10(rms + 1e-12);
            string unit = calibrationOffsetDb.HasValue ? "dB SPL" : "dBFS";
            double broadband = broadbandDbFs + (calibrationOffsetDb ?? 0.0);
            double dcOffset = samples.Average(value => (double)value);
            double peakSample = samples.Max(value => Math.Abs((double)value));
            double crestFactor = rms > 1e-12 ? peakSample / rms : 0;

            var humLevels = new Dictionary<double, double>();
            foreach (double frequency in HumFrequencies.Where(f => f < sampleRate / 2.0))
            {
                double toneRms = CalculateToneRms(samples, sampleRate, frequency);
                double level = 20.0 * Math.Log10(toneRms + 1e-12) + (calibrationOffsetDb ?? 0.0);
                humLevels[frequency] = level;
            }

            var dominant = humLevels.Count == 0
                ? new KeyValuePair<double, double>(0, -180)
                : humLevels.OrderByDescending(point => point.Value).First();

            FrequencySpectrum spectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(samples, sampleRate);
            double[] spectrumFrequencies = spectrum.FrequenciesHz;
            double dominantSpectralPeakHz = 0;
            double dominantSpectralPeakDb = -180;
            for (int index = 0; index < spectrumFrequencies.Length; index++)
            {
                double frequency = spectrumFrequencies[index];
                if (frequency < 20 || frequency > Math.Min(20000, sampleRate * 0.48)) continue;
                double level = 20.0 * Math.Log10(spectrum.Magnitudes[index] + 1e-12)
                    + (calibrationOffsetDb ?? 0.0);
                if (level <= dominantSpectralPeakDb) continue;
                dominantSpectralPeakDb = level;
                dominantSpectralPeakHz = frequency;
            }

            return new NoiseSpectrumMetrics(
                broadband,
                unit,
                humLevels,
                dominant.Key,
                dominant.Value,
                dcOffset,
                peakSample,
                crestFactor,
                dominant.Value - broadband,
                dominantSpectralPeakHz,
                dominantSpectralPeakDb);
        }

        public static NoiseAssessment CompareNoise(
            NoiseSpectrumMetrics dut,
            NoiseSpectrumMetrics? ambient,
            double ambientLimitDbSpl,
            double ambientLimitDbFs,
            double dutSeparationMarginDb,
            int attempt)
        {
            if (ambient == null)
            {
                return new NoiseAssessment(
                    dut,
                    null,
                    false,
                    false,
                    "Chỉ có mic DUT; đã đo noise/hum nhưng chưa thể tách nhiễu môi trường.",
                    attempt);
            }

            bool calibrated = string.Equals(ambient.Unit, "dB SPL", StringComparison.OrdinalIgnoreCase);
            double limit = calibrated ? ambientLimitDbSpl : ambientLimitDbFs;
            bool environmentTooLoud = ambient.BroadbandDb > limit;

            bool calibratedPair = string.Equals(dut.Unit, "dB SPL", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ambient.Unit, "dB SPL", StringComparison.OrdinalIgnoreCase);
            bool dutDominates = calibratedPair && dut.BroadbandDb >= ambient.BroadbandDb + dutSeparationMarginDb;
            bool dutHumDominates = false;
            if (calibratedPair && dut.DominantHumHz > 0 && ambient.HumLevelsDb.TryGetValue(dut.DominantHumHz, out double ambientHum))
            {
                dutHumDominates = dut.DominantHumDb >= ambientHum + dutSeparationMarginDb;
            }

            bool likelyDut = !environmentTooLoud && (dutDominates || dutHumDominates);
            string classification = environmentTooLoud
                ? "Nhiễu môi trường vượt ngưỡng; cần retry phép đo."
                : !calibratedPair
                    ? "Môi trường dưới ngưỡng dBFS; cần offset hiệu chuẩn cho cả hai mic để tách DUT và môi trường."
                : likelyDut
                    ? "Noise/hum tại mic DUT cao hơn mic môi trường; nghi DUT hoặc fixture."
                    : "Môi trường phù hợp; chưa thấy noise/hum riêng biệt từ DUT.";

            return new NoiseAssessment(
                dut,
                ambient,
                environmentTooLoud,
                likelyDut,
                classification,
                attempt);
        }

        private static double CalculateToneRms(float[] samples, int sampleRate, double frequency)
        {
            int count = samples.Length;
            if (count < 8) return 0;

            double cosine = 0;
            double sine = 0;
            double windowSum = 0;
            for (int i = 0; i < count; i++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (count - 1));
                double phase = 2.0 * Math.PI * frequency * i / sampleRate;
                double sample = samples[i] * window;
                cosine += sample * Math.Cos(phase);
                sine += sample * Math.Sin(phase);
                windowSum += window;
            }

            double peak = windowSum <= 0 ? 0 : 2.0 * Math.Sqrt(cosine * cosine + sine * sine) / windowSum;
            return peak / Math.Sqrt(2.0);
        }

        private static double Interpolate(double start, double end, double ratio) =>
            start + (end - start) * ratio;
    }
}
