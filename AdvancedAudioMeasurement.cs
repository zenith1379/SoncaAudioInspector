using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector
{
    /// <summary>
    /// Kết quả đo chất lượng tone theo phương pháp sine-fit của MeasureLab.
    /// Tất cả đại lượng được tính trong miền thời gian để không phụ thuộc vị trí bin FFT.
    /// </summary>
    public sealed record HarmonicMeasurement(int Order, double FrequencyHz, double LevelDbc);

    public sealed record ToneQualityMetrics(
        bool IsValid,
        double FundamentalFrequencyHz,
        double FundamentalRms,
        double SignalLevelDbFs,
        double ThdPercent,
        double ThdNPercent,
        double SinadDb,
        double SnrDb,
        double DcOffset,
        double CrestFactor,
        double PeakSample,
        double ClippedSamplePercent,
        double LargestSpurFrequencyHz,
        double SfdrDb,
        IReadOnlyList<HarmonicMeasurement> Harmonics)
    {
        public double[] SpectrumFrequenciesHz { get; init; } = Array.Empty<double>();
        public double[] SpectrumMagnitudes { get; init; } = Array.Empty<double>();
        public string AnalysisSource { get; init; } = "SINE_FIT";
    }

    public sealed record FrequencySpectrum(double[] FrequenciesHz, double[] Magnitudes);

    public sealed record TransientRubBuzzEvent(
        double TimeSeconds,
        double DurationMs,
        double PeakDbc,
        double CrestFactor,
        double FundamentalPhaseDegrees);

    public sealed record TransientEnvelopePoint(double TimeSeconds, double LevelDbc);

    public sealed record RubBuzzMetrics(
        bool IsValid,
        double FundamentalFrequencyHz,
        double RubBuzzPercent,
        double ResidualPeakFrequencyHz,
        double ResidualPeakDbc,
        double PeakSample,
        double ClippedSamplePercent)
    {
        public double ResidualCrestFactor { get; init; }
        public double TransientThresholdDbc { get; init; } = -180;
        public double TransientPhaseConsistency { get; init; }
        public string TransientPattern { get; init; } = "NONE";
        public IReadOnlyList<TransientRubBuzzEvent> TransientEvents { get; init; } = Array.Empty<TransientRubBuzzEvent>();
        public IReadOnlyList<TransientEnvelopePoint> TransientEnvelope { get; init; } = Array.Empty<TransientEnvelopePoint>();
    }

    public sealed record RtaBandPoint(
        double CenterFrequencyHz,
        double LevelDbFs);

    public sealed record RtaSpectrumResult(
        bool IsValid,
        int SampleRate,
        double SignalLevelDbFs,
        double PeakSample,
        double ClippedSamplePercent,
        IReadOnlyList<RtaBandPoint> Bands,
        string Validity);

    public sealed record ImdProduct(
        double FrequencyHz,
        double LevelDbc);

    public sealed record ImdMeasurementResult(
        bool IsValid,
        double LowToneFrequencyHz,
        double HighToneFrequencyHz,
        double LowToneLevelDbFs,
        double HighToneLevelDbFs,
        double ImdPercent,
        double SignalLevelDbFs,
        double PeakSample,
        double ClippedSamplePercent,
        IReadOnlyList<ImdProduct> Products,
        string Validity);

    /// <summary>
    /// Lõi đo C# thuần, chuyển thể các nguyên tắc đo chuẩn từ MeasureLab:
    /// tối ưu tần số, hồi quy sine/cosine, tách harmonic và phần dư không điều hòa.
    /// </summary>
    public static class AdvancedAudioMeasurement
    {
        private const double Epsilon = 1e-15;

        public static ToneQualityMetrics AnalyzeTone(
            float[] samples,
            int sampleRate,
            double expectedFrequencyHz,
            int maxHarmonic = 10)
        {
            if (samples == null || samples.Length < 512 || sampleRate <= 0
                || !double.IsFinite(expectedFrequencyHz) || expectedFrequencyHz <= 0
                || expectedFrequencyHz >= sampleRate * 0.48 || maxHarmonic < 1
                || samples.Any(value => !float.IsFinite(value)))
            {
                return EmptyTone();
            }

            double frequency = OptimizeFundamentalFrequency(samples, sampleRate, expectedFrequencyHz);
            int harmonicCount = Math.Max(1, Math.Min(maxHarmonic, (int)((sampleRate * 0.48) / frequency)));
            double[] coefficients = FitHarmonicModel(samples, sampleRate, frequency, harmonicCount);
            if (coefficients.Length == 0) return EmptyTone();

            double fundamentalRms = ComponentRms(coefficients, 1);
            if (!double.IsFinite(fundamentalRms) || fundamentalRms <= 1e-9) return EmptyTone();

            var harmonics = new List<HarmonicMeasurement>();
            double harmonicPower = 0;
            for (int order = 2; order <= harmonicCount; order++)
            {
                double rms = ComponentRms(coefficients, order);
                harmonicPower += rms * rms;
                harmonics.Add(new HarmonicMeasurement(
                    order,
                    order * frequency,
                    ToDb(rms / fundamentalRms)));
            }

            double[] residual = BuildResidual(samples, sampleRate, frequency, harmonicCount, coefficients);
            int trim = Math.Min((int)(sampleRate * 0.1), residual.Length / 8);
            double residualRms = CalculateRms(residual, trim, residual.Length - (2 * trim));
            double totalRms = DspProcessor.CalculateRms(samples, 0, samples.Length);
            double acPower = Math.Max(0, totalRms * totalRms - coefficients[0] * coefficients[0]);
            // An ill-conditioned fit can assign more harmonic energy than the
            // entire captured AC signal. Such a ratio (e.g. thousands of %)
            // cannot be a trustworthy THD measurement.
            bool harmonicFitConsistent = double.IsFinite(harmonicPower)
                && harmonicPower <= acPower * 1.25;
            bool toneDetected = harmonicFitConsistent && ToDb(fundamentalRms) >= -75
                && fundamentalRms * fundamentalRms >= 0.1 * acPower;
            double peak = samples.Select(value => Math.Abs((double)value)).DefaultIfEmpty().Max();
            double clippedPercent = samples.Count(value => Math.Abs(value) >= 0.98f) * 100.0 / samples.Length;
            double thdRatio = Math.Sqrt(harmonicPower) / fundamentalRms;
            double noiseAndDistortionRms = Math.Sqrt(harmonicPower + residualRms * residualRms);
            // Match REW's displayed THD+N convention: noise+distortion power
            // relative to total input power. SINAD is its reciprocal.
            double thdnRatio = noiseAndDistortionRms
                / Math.Sqrt(fundamentalRms * fundamentalRms + noiseAndDistortionRms * noiseAndDistortionRms);

            SpectrumData fullSpectrum = CalculateSpectrum(samples.Select(value => (double)value).ToArray(), sampleRate);
            (double spurFrequency, double spurAmplitude) = FindLargestSpurExcluding(
                fullSpectrum, 20, Math.Min(20000, sampleRate * 0.48), frequency);
            double spurRms = spurAmplitude / Math.Sqrt(2.0);

            FrequencySpectrum displaySpectrum = AnalyzeSpectrum(samples, sampleRate);
            return new ToneQualityMetrics(
                toneDetected,
                frequency,
                fundamentalRms,
                ToDb(totalRms),
                harmonicCount >= 2 && harmonicFitConsistent ? thdRatio * 100.0 : double.NaN,
                thdnRatio * 100.0,
                -ToDb(thdnRatio),
                ToDb(fundamentalRms / Math.Max(residualRms, Epsilon)),
                coefficients[0],
                totalRms > Epsilon ? peak / totalRms : 0,
                peak,
                clippedPercent,
                spurFrequency,
                ToDb(fundamentalRms / Math.Max(spurRms, Epsilon)),
                harmonics)
            {
                SpectrumFrequenciesHz = displaySpectrum.FrequenciesHz,
                SpectrumMagnitudes = displaySpectrum.Magnitudes
            };
        }

        public static FrequencySpectrum AnalyzeSpectrum(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length < 512 || sampleRate <= 0
                || samples.Any(value => !float.IsFinite(value)))
                return new FrequencySpectrum(Array.Empty<double>(), Array.Empty<double>());
            SpectrumData spectrum = CalculateSpectrum(samples.Select(value => (double)value).ToArray(), sampleRate);
            return new FrequencySpectrum(spectrum.Frequencies, spectrum.Magnitudes);
        }

        public static RubBuzzMetrics AnalyzeRubBuzz(
            float[] samples,
            int sampleRate,
            double expectedFrequencyHz,
            double bandMinHz = 1000,
            double bandMaxHz = 8000)
        {
            ToneQualityMetrics tone = AnalyzeTone(samples, sampleRate, expectedFrequencyHz);
            if (!tone.IsValid) return new RubBuzzMetrics(false, expectedFrequencyHz, 0, 0, -180, 0, 0);

            if (!double.IsFinite(bandMinHz) || !double.IsFinite(bandMaxHz)
                || bandMinHz <= 0 || bandMaxHz <= bandMinHz || bandMinHz >= sampleRate * 0.48)
                return new RubBuzzMetrics(false, expectedFrequencyHz, 0, 0, -180, tone.PeakSample, tone.ClippedSamplePercent);

            // Keep higher-order and non-harmonic components for impulsive-defect
            // detection. Removing every available harmonic would erase part of
            // the very high-order signature used to diagnose rub and buzz.
            int harmonicCount = Math.Max(1, Math.Min(5, (int)((sampleRate * 0.48) / tone.FundamentalFrequencyHz)));
            double[] coefficients = FitHarmonicModel(samples, sampleRate, tone.FundamentalFrequencyHz, harmonicCount);
            double[] residual = BuildResidual(samples, sampleRate, tone.FundamentalFrequencyHz, harmonicCount, coefficients);
            double effectiveBandMax = Math.Min(bandMaxHz, sampleRate * 0.48);
            double[] bandResidual = BandLimit(residual, sampleRate, bandMinHz, effectiveBandMax);
            SpectrumData spectrum = CalculateFullRecordSpectrum(bandResidual, sampleRate);

            double energy = 0;
            double peakAmplitude = 0;
            double peakFrequency = 0;
            for (int index = 0; index < spectrum.Frequencies.Length; index++)
            {
                double frequency = spectrum.Frequencies[index];
                if (frequency < bandMinHz || frequency > Math.Min(bandMaxHz, sampleRate * 0.48)) continue;
                double amplitude = spectrum.Magnitudes[index];
                // Hann ENBW = 1,5 bin. Chia thêm 1,5 để tổng năng lượng phổ
                // khớp RMS miền thời gian, tránh báo Rub/Buzz cao hơn thực tế 22,5%.
                energy += amplitude * amplitude / 3.0;
                if (amplitude > peakAmplitude)
                {
                    peakAmplitude = amplitude;
                    peakFrequency = frequency;
                }
            }

            // RMS uses every retained sample. The spectrum remains useful for
            // locating a sustained buzz, but must not decide whether a short
            // event near a record boundary exists.
            double residualBandRms = CalculateRms(bandResidual, 0, bandResidual.Length);
            double residualPeak = bandResidual.Select(Math.Abs).DefaultIfEmpty(0).Max();
            (IReadOnlyList<TransientRubBuzzEvent> events,
                IReadOnlyList<TransientEnvelopePoint> envelope,
                double thresholdDbc) = DetectTransientEvents(
                    bandResidual, sampleRate, tone.FundamentalFrequencyHz, tone.FundamentalRms);
            double phaseConsistency = CalculatePhaseConsistency(events);
            return new RubBuzzMetrics(
                true,
                tone.FundamentalFrequencyHz,
                residualBandRms / Math.Max(tone.FundamentalRms, Epsilon) * 100.0,
                peakFrequency,
                ToDb((peakAmplitude / Math.Sqrt(2.0)) / Math.Max(tone.FundamentalRms, Epsilon)),
                tone.PeakSample,
                tone.ClippedSamplePercent)
            {
                ResidualCrestFactor = residualBandRms > Epsilon ? residualPeak / residualBandRms : 0,
                TransientThresholdDbc = thresholdDbc,
                TransientPhaseConsistency = phaseConsistency,
                TransientPattern = events.Count == 0 ? "NONE"
                    : events.Count == 1 ? "SINGLE_EVENT"
                    : phaseConsistency >= 0.75 ? "PHASE_LOCKED" : "IRREGULAR",
                TransientEvents = events,
                TransientEnvelope = envelope
            };
        }

        /// <summary>
        /// RTA snapshot in 1/12-octave bands. Levels are diagnostic dBFS values;
        /// calibrated SPL still requires a microphone calibration chain.
        /// </summary>
        public static RtaSpectrumResult AnalyzeRtaSpectrum(float[] samples, int sampleRate)
        {
            if (samples == null || samples.Length < 1024 || sampleRate <= 0)
            {
                return new RtaSpectrumResult(
                    false, sampleRate, -180, 0, 0,
                    Array.Empty<RtaBandPoint>(), "INVALID_NO_DATA");
            }
            if (samples.Any(value => !float.IsFinite(value)))
                return new RtaSpectrumResult(
                    false, sampleRate, -180, 0, 0,
                    Array.Empty<RtaBandPoint>(), "INVALID_NONFINITE_DATA");

            double totalRms = DspProcessor.CalculateRms(samples, 0, samples.Length);
            double signalLevel = ToDb(totalRms);
            double peak = samples.Select(value => Math.Abs((double)value)).DefaultIfEmpty().Max();
            double clippedPercent = samples.Count(value => Math.Abs(value) >= 0.98f) * 100.0 / samples.Length;
            SpectrumData spectrum = CalculateSpectrum(samples.Select(value => (double)value).ToArray(), sampleRate);
            double maximumFrequency = Math.Min(20000, sampleRate * 0.45);
            var bands = new List<RtaBandPoint>();

            for (int band = 0; ; band++)
            {
                double center = 20.0 * Math.Pow(2.0, band / 12.0);
                if (center > maximumFrequency) break;
                double low = center / Math.Pow(2.0, 1.0 / 24.0);
                double high = center * Math.Pow(2.0, 1.0 / 24.0);
                double energy = 0;
                int bins = 0;
                for (int index = 1; index < spectrum.Frequencies.Length; index++)
                {
                    double frequency = spectrum.Frequencies[index];
                    if (frequency < low || frequency >= high) continue;
                    energy += spectrum.Magnitudes[index] * spectrum.Magnitudes[index] / 3.0;
                    bins++;
                }
                if (bins > 0)
                    bands.Add(new RtaBandPoint(center, ToDb(Math.Sqrt(energy))));
            }

            string validity = signalLevel < -80
                ? "INVALID_LOW_SIGNAL"
                : peak >= 0.995 || clippedPercent > 0.01
                    ? "INVALID_CLIPPING"
                    : "DIAGNOSTIC";
            return new RtaSpectrumResult(
                validity == "DIAGNOSTIC",
                sampleRate,
                signalLevel,
                peak,
                clippedPercent,
                bands,
                validity);
        }

        /// <summary>
        /// SMPTE-style two-tone IMD measurement. The returned percentage is the
        /// RSS of sidebands around the high tone relative to the high-tone carrier.
        /// </summary>
        public static ImdMeasurementResult AnalyzeSmpteImd(
            float[] samples,
            int sampleRate,
            double lowToneFrequencyHz = 60,
            double highToneFrequencyHz = 7000,
            int sidebandOrder = 2)
        {
            if (samples == null || samples.Length < 1024 || sampleRate <= 0
                || lowToneFrequencyHz <= 0 || highToneFrequencyHz <= lowToneFrequencyHz
                || highToneFrequencyHz + sidebandOrder * lowToneFrequencyHz >= sampleRate * 0.48)
            {
                return EmptyImd(lowToneFrequencyHz, highToneFrequencyHz, "INVALID_CONFIGURATION");
            }
            if (samples.Any(value => !float.IsFinite(value)))
                return EmptyImd(lowToneFrequencyHz, highToneFrequencyHz, "INVALID_NONFINITE_DATA");

            SpectrumData spectrum = CalculateSpectrum(samples.Select(value => (double)value).ToArray(), sampleRate);
            (double lowFrequency, double lowAmplitude) = FindPeakNear(spectrum, lowToneFrequencyHz);
            (double highFrequency, double highAmplitude) = FindPeakNear(spectrum, highToneFrequencyHz);
            double totalRms = DspProcessor.CalculateRms(samples, 0, samples.Length);
            double peak = samples.Select(value => Math.Abs((double)value)).DefaultIfEmpty().Max();
            double clippedPercent = samples.Count(value => Math.Abs(value) >= 0.98f) * 100.0 / samples.Length;

            bool requestedTonesDetected = lowAmplitude / Math.Sqrt(2.0) >= totalRms * 0.02
                && highAmplitude / Math.Sqrt(2.0) >= totalRms * 0.02;
            if (lowAmplitude <= 1e-7 || highAmplitude <= 1e-7 || !requestedTonesDetected)
                return EmptyImd(lowToneFrequencyHz, highToneFrequencyHz, "INVALID_STIMULUS_NOT_DETECTED") with
                {
                    SignalLevelDbFs = ToDb(totalRms),
                    PeakSample = peak,
                    ClippedSamplePercent = clippedPercent
                };

            double productPower = 0;
            var products = new List<ImdProduct>();
            for (int order = 1; order <= Math.Max(1, sidebandOrder); order++)
            {
                foreach (double target in new[]
                {
                    highFrequency - order * lowFrequency,
                    highFrequency + order * lowFrequency
                })
                {
                    if (target <= 20 || target >= sampleRate * 0.48) continue;
                    (double measuredFrequency, double amplitude) = FindPeakNear(spectrum, target);
                    productPower += amplitude * amplitude;
                    products.Add(new ImdProduct(measuredFrequency, ToDb(amplitude / highAmplitude)));
                }
            }

            string validity = ToDb(totalRms) < -75
                ? "INVALID_LOW_SIGNAL"
                : peak >= 0.995 || clippedPercent > 0.01
                    ? "INVALID_CLIPPING"
                    : "DIAGNOSTIC";
            return new ImdMeasurementResult(
                validity == "DIAGNOSTIC",
                lowFrequency,
                highFrequency,
                ToDb(lowAmplitude / Math.Sqrt(2.0)),
                ToDb(highAmplitude / Math.Sqrt(2.0)),
                Math.Sqrt(productPower) / highAmplitude * 100.0,
                ToDb(totalRms),
                peak,
                clippedPercent,
                products,
                validity);
        }

        private static ToneQualityMetrics EmptyTone() => new(
            false, 0, 0, -180, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<HarmonicMeasurement>());

        private static ImdMeasurementResult EmptyImd(double lowTone, double highTone, string validity) => new(
            false, lowTone, highTone, -180, -180, 0, -180, 0, 0,
            Array.Empty<ImdProduct>(), validity);

        private static double OptimizeFundamentalFrequency(float[] samples, int sampleRate, double expectedFrequency)
        {
            double halfWidth = Math.Max(5.0, expectedFrequency * 0.02);
            SpectrumData spectrum = CalculateSpectrum(samples.Select(value => (double)value).ToArray(), sampleRate);
            (double peakFrequency, _) = FindLargestSpur(spectrum,
                Math.Max(1, expectedFrequency - halfWidth), expectedFrequency + halfWidth);
            double bestFrequency = peakFrequency > 0 ? peakFrequency : expectedFrequency;
            // FFT locates the main lobe even when the old 2%-wide grid skips it.
            halfWidth = spectrum.Frequencies.Length > 1 ? spectrum.Frequencies[1] : halfWidth;
            double bestError = double.PositiveInfinity;

            for (int pass = 0; pass < 5; pass++)
            {
                const int points = 11;
                double start = Math.Max(1, bestFrequency - halfWidth);
                double step = (2.0 * halfWidth) / (points - 1);
                for (int index = 0; index < points; index++)
                {
                    double candidate = start + index * step;
                    double error = FundamentalFitError(samples, sampleRate, candidate);
                    if (error < bestError)
                    {
                        bestError = error;
                        bestFrequency = candidate;
                    }
                }
                halfWidth = step * 1.5;
            }
            return bestFrequency;
        }

        private static double FundamentalFitError(float[] samples, int sampleRate, double frequency)
        {
            double[,] normal = new double[3, 3];
            double[] rhs = new double[3];
            double totalPower = 0;
            double angle = 2.0 * Math.PI * frequency / sampleRate;
            double stepSin = Math.Sin(angle), stepCos = Math.Cos(angle);
            double sine = 0, cosine = 1;
            for (int index = 0; index < samples.Length; index++)
            {
                // Re-anchor periodically to bound oscillator roundoff.
                if ((index & 1023) == 0) { sine = Math.Sin(angle * index); cosine = Math.Cos(angle * index); }
                double sample = samples[index];
                totalPower += sample * sample;
                rhs[0] += sample;
                rhs[1] += sine * sample;
                rhs[2] += cosine * sample;
                normal[0, 0] += 1;
                normal[1, 0] += sine;
                normal[1, 1] += sine * sine;
                normal[2, 0] += cosine;
                normal[2, 1] += cosine * sine;
                normal[2, 2] += cosine * cosine;
                (sine, cosine) = (sine * stepCos + cosine * stepSin, cosine * stepCos - sine * stepSin);
            }
            MirrorLowerTriangle(normal);
            double[] fit = Solve(normal, rhs);
            if (fit.Length == 0) return double.PositiveInfinity;
            double explained = fit[0] * rhs[0] + fit[1] * rhs[1] + fit[2] * rhs[2];
            return Math.Max(0, totalPower - explained);
        }

        private static double[] FitHarmonicModel(float[] samples, int sampleRate, double frequency, int harmonicCount)
        {
            int dimension = 1 + harmonicCount * 2;
            double[,] normal = new double[dimension, dimension];
            double[] rhs = new double[dimension];
            double[] basis = new double[dimension];
            for (int index = 0; index < samples.Length; index++)
            {
                basis[0] = 1;
                double phase = 2.0 * Math.PI * frequency * index / sampleRate;
                for (int order = 1; order <= harmonicCount; order++)
                {
                    basis[order * 2 - 1] = Math.Sin(order * phase);
                    basis[order * 2] = Math.Cos(order * phase);
                }
                AccumulateNormalEquation(normal, rhs, basis, samples[index]);
            }
            MirrorLowerTriangle(normal);
            return Solve(normal, rhs);
        }

        private static void AccumulateNormalEquation(double[,] normal, double[] rhs, double[] basis, double sample)
        {
            for (int row = 0; row < basis.Length; row++)
            {
                rhs[row] += basis[row] * sample;
                for (int column = 0; column <= row; column++)
                {
                    normal[row, column] += basis[row] * basis[column];
                }
            }
        }

        private static void MirrorLowerTriangle(double[,] matrix)
        {
            int size = matrix.GetLength(0);
            for (int row = 0; row < size; row++)
                for (int column = row + 1; column < size; column++)
                    matrix[row, column] = matrix[column, row];
        }

        private static double[] Solve(double[,] matrix, double[] vector)
        {
            int size = vector.Length;
            double[,] augmented = new double[size, size + 1];
            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++) augmented[row, column] = matrix[row, column];
                augmented[row, size] = vector[row];
            }

            for (int pivot = 0; pivot < size; pivot++)
            {
                int bestRow = pivot;
                for (int row = pivot + 1; row < size; row++)
                {
                    if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[bestRow, pivot])) bestRow = row;
                }
                if (Math.Abs(augmented[bestRow, pivot]) < 1e-12) return Array.Empty<double>();
                if (bestRow != pivot)
                {
                    for (int column = pivot; column <= size; column++)
                    {
                        (augmented[pivot, column], augmented[bestRow, column]) =
                            (augmented[bestRow, column], augmented[pivot, column]);
                    }
                }
                double divisor = augmented[pivot, pivot];
                for (int column = pivot; column <= size; column++) augmented[pivot, column] /= divisor;
                for (int row = 0; row < size; row++)
                {
                    if (row == pivot) continue;
                    double factor = augmented[row, pivot];
                    for (int column = pivot; column <= size; column++) augmented[row, column] -= factor * augmented[pivot, column];
                }
            }

            var result = new double[size];
            for (int index = 0; index < size; index++) result[index] = augmented[index, size];
            return result;
        }

        private static double ComponentRms(double[] coefficients, int harmonicOrder)
        {
            int sineIndex = harmonicOrder * 2 - 1;
            int cosineIndex = harmonicOrder * 2;
            if (cosineIndex >= coefficients.Length) return 0;
            return Math.Sqrt(coefficients[sineIndex] * coefficients[sineIndex]
                + coefficients[cosineIndex] * coefficients[cosineIndex]) / Math.Sqrt(2.0);
        }

        private static double[] BuildResidual(
            float[] samples,
            int sampleRate,
            double frequency,
            int harmonicCount,
            double[] coefficients)
        {
            var residual = new double[samples.Length];
            for (int index = 0; index < samples.Length; index++)
            {
                double phase = 2.0 * Math.PI * frequency * index / sampleRate;
                double fitted = coefficients[0];
                for (int order = 1; order <= harmonicCount; order++)
                {
                    fitted += coefficients[order * 2 - 1] * Math.Sin(order * phase)
                        + coefficients[order * 2] * Math.Cos(order * phase);
                }
                residual[index] = samples[index] - fitted;
            }
            return residual;
        }

        private static double CalculateRms(double[] samples, int start, int length)
        {
            if (length <= 0) return 0;
            double sum = 0;
            int end = Math.Min(samples.Length, start + length);
            for (int index = Math.Max(0, start); index < end; index++) sum += samples[index] * samples[index];
            return Math.Sqrt(sum / Math.Max(1, end - Math.Max(0, start)));
        }

        private static double[] BandLimit(double[] samples, int sampleRate, double lowHz, double highHz)
        {
            int fftSize = 1;
            while (fftSize < samples.Length * 2) fftSize <<= 1;
            var spectrum = new Complex[fftSize];
            for (int index = 0; index < samples.Length; index++) spectrum[index] = samples[index];
            Fourier.Forward(spectrum, FourierOptions.Matlab);
            double lowTransition = Math.Max(10.0, lowHz * 0.15);
            double highTransition = Math.Max(50.0, highHz * 0.10);
            for (int bin = 0; bin < fftSize; bin++)
            {
                double frequency = Math.Min(bin, fftSize - bin) * sampleRate / (double)fftSize;
                double gain = frequency < lowHz - lowTransition || frequency > highHz + highTransition ? 0.0 : 1.0;
                if (frequency >= lowHz - lowTransition && frequency < lowHz)
                    gain = 0.5 - 0.5 * Math.Cos(Math.PI * (frequency - lowHz + lowTransition) / lowTransition);
                else if (frequency > highHz && frequency <= highHz + highTransition)
                    gain = 0.5 + 0.5 * Math.Cos(Math.PI * (frequency - highHz) / highTransition);
                spectrum[bin] *= gain;
            }
            Fourier.Inverse(spectrum, FourierOptions.Matlab);
            var output = new double[samples.Length];
            for (int index = 0; index < output.Length; index++) output[index] = spectrum[index].Real;
            return output;
        }

        private static (IReadOnlyList<TransientRubBuzzEvent> Events,
            IReadOnlyList<TransientEnvelopePoint> Envelope,
            double ThresholdDbc) DetectTransientEvents(
            double[] residual,
            int sampleRate,
            double fundamentalHz,
            double fundamentalRms)
        {
            int window = Math.Max(8, (int)Math.Round(sampleRate * 0.001));
            var squaredPrefix = new double[residual.Length + 1];
            for (int index = 0; index < residual.Length; index++)
                squaredPrefix[index + 1] = squaredPrefix[index] + residual[index] * residual[index];
            var shortRms = new double[residual.Length];
            int half = window / 2;
            for (int index = 0; index < residual.Length; index++)
            {
                int start = Math.Max(0, index - half);
                int end = Math.Min(residual.Length, start + window);
                shortRms[index] = Math.Sqrt((squaredPrefix[end] - squaredPrefix[start]) / Math.Max(1, end - start));
            }

            double median = Median(shortRms);
            double mad = Median(shortRms.Select(value => Math.Abs(value - median)).ToArray());
            double threshold = Math.Max(median + 8.0 * 1.4826 * mad, fundamentalRms * 0.0005);
            int edge = Math.Min(residual.Length / 10, Math.Max(window, sampleRate / 100));
            int mergeGap = Math.Max(1, (int)Math.Round(sampleRate * 0.002));
            int minimumLength = Math.Max(1, (int)Math.Round(sampleRate * 0.00015));
            var regions = new List<(int Start, int End)>();
            int candidateStart = -1;
            for (int index = edge; index < residual.Length - edge; index++)
            {
                if (shortRms[index] >= threshold)
                {
                    if (candidateStart < 0) candidateStart = index;
                    continue;
                }
                if (candidateStart < 0) continue;
                int end = index - 1;
                if (end - candidateStart + 1 >= minimumLength)
                {
                    if (regions.Count > 0 && candidateStart - regions[^1].End <= mergeGap)
                        regions[^1] = (regions[^1].Start, end);
                    else
                        regions.Add((candidateStart, end));
                }
                candidateStart = -1;
            }
            if (candidateStart >= 0)
            {
                int end = residual.Length - edge - 1;
                if (end - candidateStart + 1 >= minimumLength)
                    regions.Add((candidateStart, end));
            }

            var events = new List<TransientRubBuzzEvent>();
            foreach ((int start, int end) in regions)
            {
                int peakIndex = start;
                double peak = 0;
                double power = 0;
                for (int index = start; index <= end; index++)
                {
                    double magnitude = Math.Abs(residual[index]);
                    power += residual[index] * residual[index];
                    if (magnitude > peak) { peak = magnitude; peakIndex = index; }
                }
                double rms = Math.Sqrt(power / Math.Max(1, end - start + 1));
                double phase = (peakIndex * fundamentalHz / sampleRate * 360.0) % 360.0;
                events.Add(new TransientRubBuzzEvent(
                    peakIndex / (double)sampleRate,
                    (end - start + 1) * 1000.0 / sampleRate,
                    ToDb(peak / Math.Max(fundamentalRms, Epsilon)),
                    rms > Epsilon ? peak / rms : 0,
                    phase));
            }

            int stride = Math.Max(1, residual.Length / 2000);
            var envelope = new List<TransientEnvelopePoint>();
            for (int start = 0; start < residual.Length; start += stride)
            {
                int end = Math.Min(residual.Length, start + stride);
                double maximum = 0;
                int maximumIndex = start;
                for (int index = start; index < end; index++)
                    if (shortRms[index] > maximum) { maximum = shortRms[index]; maximumIndex = index; }
                envelope.Add(new TransientEnvelopePoint(
                    maximumIndex / (double)sampleRate,
                    ToDb(maximum / Math.Max(fundamentalRms, Epsilon))));
            }
            return (events, envelope, ToDb(threshold / Math.Max(fundamentalRms, Epsilon)));
        }

        private static double CalculatePhaseConsistency(IReadOnlyList<TransientRubBuzzEvent> events)
        {
            if (events.Count < 2) return 0;
            double cosine = events.Sum(item => Math.Cos(item.FundamentalPhaseDegrees * Math.PI / 180.0));
            double sine = events.Sum(item => Math.Sin(item.FundamentalPhaseDegrees * Math.PI / 180.0));
            return Math.Sqrt(cosine * cosine + sine * sine) / events.Count;
        }

        private static double Median(double[] values)
        {
            if (values.Length == 0) return 0;
            double[] ordered = values.ToArray();
            Array.Sort(ordered);
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2.0 : ordered[middle];
        }

        private static SpectrumData CalculateSpectrum(double[] samples, int sampleRate)
        {
            int fftSize = 1;
            while (fftSize * 2 <= samples.Length && fftSize < 65536) fftSize *= 2;
            if (fftSize < 512) return new SpectrumData(Array.Empty<double>(), Array.Empty<double>());

            int hop = Math.Max(1, fftSize / 2);
            var starts = new List<int>();
            for (int start = 0; start + fftSize <= samples.Length; start += hop) starts.Add(start);
            int finalStart = Math.Max(0, samples.Length - fftSize);
            if (starts.Count == 0 || starts[^1] != finalStart) starts.Add(finalStart);
            var accumulatedPower = new double[fftSize / 2];
            double windowSum = 0;
            for (int index = 0; index < fftSize; index++)
                windowSum += 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / (fftSize - 1));
            foreach (int start in starts)
            {
                var buffer = new Complex[fftSize];
                for (int index = 0; index < fftSize; index++)
                {
                    double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / (fftSize - 1));
                    buffer[index] = new Complex(samples[start + index] * window, 0);
                }
                Fourier.Forward(buffer, FourierOptions.NoScaling);
                for (int index = 0; index < accumulatedPower.Length; index++)
                {
                    double amplitude = 2.0 * buffer[index].Magnitude / windowSum;
                    accumulatedPower[index] += amplitude * amplitude;
                }
            }

            int count = fftSize / 2;
            var frequencies = new double[count];
            var magnitudes = new double[count];
            for (int index = 0; index < count; index++)
            {
                frequencies[index] = (double)index * sampleRate / fftSize;
                magnitudes[index] = Math.Sqrt(accumulatedPower[index] / starts.Count);
            }
            return new SpectrumData(frequencies, magnitudes);
        }

        private static SpectrumData CalculateFullRecordSpectrum(double[] samples, int sampleRate)
        {
            int fftSize = 1;
            while (fftSize < samples.Length) fftSize <<= 1;
            if (fftSize < 512) return new SpectrumData(Array.Empty<double>(), Array.Empty<double>());
            var buffer = new Complex[fftSize];
            for (int index = 0; index < samples.Length; index++) buffer[index] = samples[index];
            Fourier.Forward(buffer, FourierOptions.NoScaling);
            int count = fftSize / 2;
            var frequencies = new double[count];
            var magnitudes = new double[count];
            for (int index = 0; index < count; index++)
            {
                frequencies[index] = index * sampleRate / (double)fftSize;
                magnitudes[index] = 2.0 * buffer[index].Magnitude / samples.Length;
            }
            return new SpectrumData(frequencies, magnitudes);
        }

        private static (double Frequency, double Amplitude) FindLargestSpurExcluding(
            SpectrumData spectrum,
            double minFrequency,
            double maxFrequency,
            double excludedFrequency)
        {
            double binWidth = spectrum.Frequencies.Length > 1 ? spectrum.Frequencies[1] : 1;
            double exclusion = Math.Max(binWidth * 4.0, excludedFrequency * 0.002);
            double frequency = 0;
            double amplitude = 0;
            for (int index = 1; index < spectrum.Frequencies.Length; index++)
            {
                double candidate = spectrum.Frequencies[index];
                if (candidate < minFrequency || candidate > maxFrequency
                    || Math.Abs(candidate - excludedFrequency) <= exclusion) continue;
                if (spectrum.Magnitudes[index] <= amplitude) continue;
                amplitude = spectrum.Magnitudes[index];
                frequency = candidate;
            }
            return (frequency, amplitude);
        }

        private static (double Frequency, double Amplitude) FindLargestSpur(
            SpectrumData spectrum,
            double minFrequency,
            double maxFrequency)
        {
            double frequency = 0;
            double amplitude = 0;
            for (int index = 0; index < spectrum.Frequencies.Length; index++)
            {
                if (spectrum.Frequencies[index] < minFrequency || spectrum.Frequencies[index] > maxFrequency) continue;
                if (spectrum.Magnitudes[index] <= amplitude) continue;
                amplitude = spectrum.Magnitudes[index];
                frequency = spectrum.Frequencies[index];
            }
            return (frequency, amplitude);
        }

        private static (double Frequency, double Amplitude) FindPeakNear(
            SpectrumData spectrum,
            double expectedFrequency)
        {
            if (spectrum.Frequencies.Length < 2) return (expectedFrequency, 0);
            double binWidth = spectrum.Frequencies[1] - spectrum.Frequencies[0];
            double halfWidth = Math.Max(binWidth * 4.0, expectedFrequency * 0.0015);
            return FindLargestSpur(
                spectrum,
                Math.Max(0, expectedFrequency - halfWidth),
                expectedFrequency + halfWidth);
        }

        private static double ToDb(double ratio) => 20.0 * Math.Log10(Math.Max(ratio, Epsilon));

        private sealed record SpectrumData(double[] Frequencies, double[] Magnitudes);
    }
}
