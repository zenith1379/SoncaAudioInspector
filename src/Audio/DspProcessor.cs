using System;
using System.Collections.Generic;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector
{
    public static class DspProcessor
    {
        /// <summary>
        /// Calculates the Root Mean Square (RMS) value of a signal.
        /// </summary>
        public static double CalculateRms(float[] samples, int startIndex, int length)
        {
            if (samples == null || samples.Length == 0 || length <= 0)
                return 0;

            double sumSq = 0;
            int end = Math.Min(startIndex + length, samples.Length);
            int count = end - startIndex;
            if (count <= 0) return 0;

            for (int i = startIndex; i < end; i++)
            {
                sumSq += samples[i] * samples[i];
            }

            return Math.Sqrt(sumSq / count);
        }

        /// <summary>
        /// Applies Hann Window to the samples to reduce spectral leakage.
        /// </summary>
        public static void ApplyHannWindow(float[] input, Complex[] output)
        {
            int n = input.Length;
            for (int i = 0; i < n; i++)
            {
                double windowValue = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
                output[i] = new Complex(input[i] * windowValue, 0.0);
            }
        }

        /// <summary>
        /// Performs FFT on the input complex array. The array is modified in-place.
        /// </summary>
        public static void PerformFft(Complex[] samples)
        {
            Fourier.Forward(samples, FourierOptions.NoScaling);
        }

        private static double SumBinEnergy(double[] magnitudes, int centerBin, int span)
        {
            double sumSq = 0;
            int start = Math.Max(0, centerBin - span);
            int end = Math.Min(magnitudes.Length - 1, centerBin + span);
            for (int i = start; i <= end; i++)
            {
                sumSq += magnitudes[i] * magnitudes[i];
            }
            return sumSq;
        }

        private static double CalculateMedian(List<double> values)
        {
            if (values.Count == 0) return -100.0;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 1
                ? values[middle]
                : (values[middle - 1] + values[middle]) / 2.0;
        }

        public static System.Collections.Generic.Dictionary<double, double> CalculateMultitoneResponse(
            float[] samples, int sampleRate, double[] targetFrequencies)
        {
            var results = new System.Collections.Generic.Dictionary<double, double>();

            if (sampleRate <= 0 || samples.Any(sample => !float.IsFinite(sample))
                || targetFrequencies.Any(f => !double.IsFinite(f) || f <= 0 || f >= sampleRate * 0.48))
                throw new ArgumentException("Bản thu không hợp lệ hoặc sample rate không đủ cho dải đo. Chọn ngõ thu 44,1/48 kHz trở lên để đo đến 20 kHz.");

            int fftSize = 1;
            while (fftSize < samples.Length)
            {
                fftSize *= 2;
            }
            // The denser Bass grid needs sub-Hz bin resolution from 50 Hz upward
            // so adjacent multitone points do not share the same FFT energy bins.
            fftSize = Math.Min(fftSize, 65536);
            if (fftSize > samples.Length)
            {
                fftSize /= 2;
            }

            if (fftSize < 512)
            {
                foreach (var f in targetFrequencies) results[f] = -100.0;
                return results;
            }

            int halfSize = fftSize / 2;
            double binWidth = (double)sampleRate / fftSize;
            double scalingFactor = 2.0 / fftSize;

            const int requestedFrameCount = 5;
            int maxFrameStart = Math.Max(0, samples.Length - fftSize);
            int frameCount = maxFrameStart == 0 ? 1 : requestedFrameCount;
            var frameValues = new Dictionary<double, List<double>>();
            foreach (double targetFrequency in targetFrequencies)
            {
                frameValues[targetFrequency] = new List<double>(frameCount);
            }

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                int analysisStart = frameCount == 1
                    ? maxFrameStart / 2
                    : (int)Math.Round((double)maxFrameStart * frameIndex / (frameCount - 1));

                float[] windowedInput = new float[fftSize];
                Array.Copy(samples, analysisStart, windowedInput, 0, fftSize);

                Complex[] fftBuffer = new Complex[fftSize];
                ApplyHannWindow(windowedInput, fftBuffer);
                PerformFft(fftBuffer);

                for (int i = 0; i < targetFrequencies.Length; i++)
                {
                    double targetFreq = targetFrequencies[i];
                    int centerBin = (int)Math.Round(targetFreq / binWidth);

                    // Restrict search width so it doesn't overlap with adjacent tones in Multitone
                    double distLeft = i > 0 ? (targetFreq - targetFrequencies[i - 1]) / 2.0 : targetFreq * 0.1;
                    double distRight = i < targetFrequencies.Length - 1 ? (targetFrequencies[i + 1] - targetFreq) / 2.0 : targetFreq * 0.1;
                    double maxSearch = Math.Min(distLeft, distRight) * 0.8;

                    // Shared playback/capture clocks can drift, but a 1.5% window was
                    // unnecessarily wide (225 Hz at 15 kHz) and could lock onto a spur.
                    // Allow 0.2% clock mismatch plus a few FFT bins instead.
                    double clockSearchHz = targetFreq * 0.002 + binWidth * 3.0;
                    double searchWidthHz = Math.Min(maxSearch, Math.Max(binWidth * 3.0, clockSearchHz));
                    int binSpan = (int)Math.Ceiling(searchWidthHz / binWidth);

                    int startBin = Math.Max(0, centerBin - binSpan);
                    int endBin = Math.Min(halfSize - 1, centerBin + binSpan);

                    int peakBin = centerBin;
                    double maxBinMag = -1.0;

                    for (int b = startBin; b <= endBin; b++)
                    {
                        double mag = fftBuffer[b].Magnitude;
                        if (mag > maxBinMag)
                        {
                            maxBinMag = mag;
                            peakBin = b;
                        }
                    }

                    // Restrict energy integration radius so it doesn't overlap adjacent tones in dense multitone grids
                    int maxEnergyBinRadius = Math.Max(1, (int)Math.Floor(maxSearch / binWidth));
                    int energyRadius = Math.Clamp(maxEnergyBinRadius, 1, 2);
                    int energyStart = Math.Max(0, peakBin - energyRadius);
                    int energyEnd = Math.Min(halfSize - 1, peakBin + energyRadius);
                    double sumSq = 0;
                    for (int b = energyStart; b <= energyEnd; b++)
                    {
                        double scaledMag = fftBuffer[b].Magnitude * scalingFactor;
                        sumSq += scaledMag * scaledMag;
                    }

                    double estimatedAmp = Math.Sqrt(sumSq * 1.3333333333333333);
                    double dbFS = 20 * Math.Log10(estimatedAmp + 1e-9);
                    frameValues[targetFreq].Add(dbFS);
                }
            }

            foreach (double targetFrequency in targetFrequencies)
            {
                results[targetFrequency] = CalculateMedian(frameValues[targetFrequency]);
            }

            return results;
        }

        public static (double rubBuzzPercent, double[] magnitudes, double fundamentalFreq) CalculateRubBuzz(
            float[] samples, int sampleRate, double targetFundamental, out double[] frequencies)
        {
            int fftSize = 1;
            while (fftSize < samples.Length)
            {
                fftSize *= 2;
            }
            fftSize = Math.Min(fftSize, 32768);
            if (fftSize > samples.Length)
            {
                fftSize /= 2;
            }

            if (fftSize < 512)
            {
                frequencies = new double[0];
                return (0, new double[0], 0);
            }

            float[] windowedInput = new float[fftSize];
            Array.Copy(samples, samples.Length - fftSize, windowedInput, 0, fftSize);

            Complex[] fftBuffer = new Complex[fftSize];
            ApplyHannWindow(windowedInput, fftBuffer);
            PerformFft(fftBuffer);

            int halfSize = fftSize / 2;
            double[] magnitudes = new double[halfSize];
            frequencies = new double[halfSize];

            double binWidth = (double)sampleRate / fftSize;
            double maxMag = 0;
            int fundBin = -1;

            double scalingFactor = 2.0 / fftSize;
            for (int i = 0; i < halfSize; i++)
            {
                frequencies[i] = i * binWidth;
                double mag = fftBuffer[i].Magnitude * scalingFactor;
                magnitudes[i] = mag;
            }

            // Find actual fundamental near targetFundamental
            int targetCenterBin = (int)Math.Round(targetFundamental / binWidth);
            int binSearchRange = (int)Math.Ceiling(10.0 / binWidth); // search within 10Hz
            int startBin = Math.Max(0, targetCenterBin - binSearchRange);
            int endBin = Math.Min(halfSize - 1, targetCenterBin + binSearchRange);

            for (int i = startBin; i <= endBin; i++)
            {
                if (magnitudes[i] > maxMag)
                {
                    maxMag = magnitudes[i];
                    fundBin = i;
                }
            }

            if (fundBin == -1 || maxMag <= 1e-6)
            {
                return (0, magnitudes, targetFundamental);
            }

            double fundamentalFreq = fundBin * binWidth;
            double fundEnergy = SumBinEnergy(magnitudes, fundBin, 4);

            // Calculate high-frequency energy (1000 Hz to 8000 Hz)
            double highFreqEnergySum = 0;
            for (int i = 0; i < halfSize; i++)
            {
                if (frequencies[i] >= 1000.0 && frequencies[i] <= 8000.0)
                {
                    highFreqEnergySum += magnitudes[i] * magnitudes[i];
                }
            }

            if (fundEnergy <= 1e-12)
            {
                return (0, magnitudes, fundamentalFreq);
            }

            double rubBuzz = Math.Sqrt(highFreqEnergySum) / Math.Sqrt(fundEnergy);
            double rubBuzzPercent = rubBuzz * 100.0;

            return (rubBuzzPercent, magnitudes, fundamentalFreq);
        }
    }
}
