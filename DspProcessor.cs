using System;
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

        /// <summary>
        /// Calculates THD (Total Harmonic Distortion) of a recorded sine wave signal.
        /// Returns THD as a percentage (e.g. 0.15 for 0.15%) and the FFT magnitude spectrum.
        /// </summary>
        public static (double thdPercent, double[] magnitudes, double fundamentalFreq) CalculateThd(
            float[] samples, int sampleRate, out double[] frequencies)
        {
            // Use the nearest power of 2 for FFT
            int fftSize = 1;
            while (fftSize < samples.Length)
            {
                fftSize *= 2;
            }
            // If the buffer is large, cap it to a reasonable size for FFT, e.g. 16384 or 32768
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

            // Copy to input buffer and apply window
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

            // Compute magnitudes (only the positive frequency half)
            // Scaling for window loss (Hann window average is 0.5, so we scale by 2.0 / fftSize)
            double scalingFactor = 2.0 / fftSize;
            for (int i = 0; i < halfSize; i++)
            {
                frequencies[i] = i * binWidth;
                double mag = fftBuffer[i].Magnitude * scalingFactor;
                magnitudes[i] = mag;

                // Find fundamental peak frequency (exclude DC and very low frequencies under 100Hz)
                if (frequencies[i] >= 100 && mag > maxMag)
                {
                    maxMag = mag;
                    fundBin = i;
                }
            }

            if (fundBin == -1 || maxMag <= 1e-6)
            {
                return (0, magnitudes, 0);
            }

            double fundamentalFreq = fundBin * binWidth;

            // For THD, we sum the energy around the fundamental, and then sum the energy around the harmonics.
            // Summing energy in a small band (e.g. ±3 bins) accounts for any spectral leakage.
            double fundEnergy = SumBinEnergy(magnitudes, fundBin, 4);

            double harmonicEnergySum = 0;
            // Measure up to 10 harmonics or up to Nyquist frequency
            for (int h = 2; h <= 10; h++)
            {
                double targetFreq = fundamentalFreq * h;
                int targetBin = (int)Math.Round(targetFreq / binWidth);

                if (targetBin >= halfSize - 4)
                    break;

                double hEnergy = SumBinEnergy(magnitudes, targetBin, 4);
                harmonicEnergySum += hEnergy;
            }

            if (fundEnergy <= 1e-12)
            {
                return (0, magnitudes, fundamentalFreq);
            }

            double thd = Math.Sqrt(harmonicEnergySum) / Math.Sqrt(fundEnergy);
            double thdPercent = thd * 100.0;

            return (thdPercent, magnitudes, fundamentalFreq);
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
    }
}
