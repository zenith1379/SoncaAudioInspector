using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace SoncaAudioInspector;

public class SignalSampleProvider : ISampleProvider
{
	private readonly int _sampleRate;

	private readonly SignalType _type;

	private double _frequency;

	private double _phase;

	private readonly double _volume;

	private readonly Action<float[]>? _onSamplesGenerated;

	private readonly WaveFormat _waveFormat;

	private Random _random = new Random();

	private double b0;

	private double b1;

	private double b2;

	private double b3;

	private double b4;

	private double b5;

	private double b6;

	private readonly double[] _multitoneFrequencies;

	private readonly double[] _multitoneAmplitudes;

	private readonly double _multitoneSampleScale;

	private readonly double[] _multitonePhases;

	private readonly float[] _customSweepSamples;

	private readonly double _signalSampleScale;

	private int _customSweepIndex;

	public WaveFormat WaveFormat => _waveFormat;

	public SignalSampleProvider(int sampleRate, SignalType type, double frequency, double volume, Action<float[]>? onSamplesGenerated = null, double[]? multitoneFrequencies = null, double[]? multitoneAmplitudes = null, double multitoneSampleScale = 1.0, float[]? customSweepSamples = null, double signalSampleScale = 1.0)
	{
		_sampleRate = sampleRate;
		_type = type;
		_frequency = frequency;
		_volume = volume;
		_onSamplesGenerated = onSamplesGenerated;
		_waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
		_multitoneFrequencies = multitoneFrequencies ?? TestRunner.TestFrequencies;
		_multitoneAmplitudes = multitoneAmplitudes ?? Array.Empty<double>();
		_multitoneSampleScale = multitoneSampleScale;
		_customSweepSamples = customSweepSamples ?? Array.Empty<float>();
		_signalSampleScale = Math.Clamp(signalSampleScale, 0.01, 1.0);
		_multitonePhases = new double[_multitoneFrequencies.Length];
		for (int i = 0; i < _multitonePhases.Length; i++)
		{
			_multitonePhases[i] = Math.PI * (double)i * (double)i / (double)_multitoneFrequencies.Length;
		}
	}

	public static double CalculateCommonMultitoneSampleScale(int sampleRate, IEnumerable<double[]> bands)
	{
		double num = 0.0;
		foreach (double[] band in bands)
		{
			if (band.Length == 0)
			{
				continue;
			}
			double[] array = new double[band.Length];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = Math.PI * (double)i * (double)i / (double)band.Length;
			}
			double num2 = 0.0;
			double num3 = 1.0 / (double)sampleRate;
			int num4 = checked(sampleRate * 6);
			for (int j = 0; j < num4; j++)
			{
				double num5 = 0.0;
				for (int k = 0; k < band.Length; k++)
				{
					num5 += Math.Sin(array[k] + Math.PI * 2.0 * band[k] * (double)j * num3);
				}
				double num6 = Math.Abs(num5 / (double)band.Length);
				if (num6 > num2)
				{
					num2 = num6;
				}
			}
			if (num2 > num)
			{
				num = num2;
			}
		}
		if (!(num > 0.001))
		{
			return 1.0;
		}
		return 0.78 / (num * 0.85);
	}

	public int Read(float[] buffer, int offset, int count)
	{
		double num = 1.0 / (double)_sampleRate;
		float[] array = ((_onSamplesGenerated != null) ? new float[count] : null);
		for (int i = 0; i < count; i++)
		{
			float num2 = 0f;
			if (_type == SignalType.Sine)
			{
				num2 = (float)(_volume * 0.8 * _signalSampleScale * Math.Sin(_phase));
				_phase += Math.PI * 2.0 * _frequency * num;
				if (_phase > Math.PI * 2.0)
				{
					_phase -= Math.PI * 2.0;
				}
			}
			else if (_type == SignalType.PinkNoise)
			{
				double num3 = _random.NextDouble() * 2.0 - 1.0;
				b0 = 0.99886 * b0 + num3 * 0.0555179;
				b1 = 0.99332 * b1 + num3 * 0.0750759;
				b2 = 0.969 * b2 + num3 * 0.153852;
				b3 = 0.8665 * b3 + num3 * 0.3104856;
				b4 = 0.55 * b4 + num3 * 0.5329522;
				b5 = -0.7616 * b5 - num3 * 0.016898;
				double num4 = b0 + b1 + b2 + b3 + b4 + b5 + b6 + num3 * 0.5362;
				b6 = num3 * 0.115926;
				num2 = (float)(num4 * 0.08 * _volume);
			}
			else if (_type == SignalType.Multitone)
			{
				double num5 = 0.0;
				for (int j = 0; j < _multitoneFrequencies.Length; j++)
				{
					double num6 = ((j < _multitoneAmplitudes.Length) ? _multitoneAmplitudes[j] : 1.0);
					num5 += num6 * Math.Sin(_multitonePhases[j]);
					_multitonePhases[j] += Math.PI * 2.0 * _multitoneFrequencies[j] * num;
					if (_multitonePhases[j] > Math.PI * 2.0)
					{
						_multitonePhases[j] -= Math.PI * 2.0;
					}
				}
				double val = ((_multitoneAmplitudes.Length != 0) ? _multitoneAmplitudes.Sum() : ((double)_multitoneFrequencies.Length));
				num2 = (float)(num5 / Math.Max(1.0, val) * _multitoneSampleScale * _volume * 0.85);
			}
			else if (_type == SignalType.Sweep)
			{
				num2 = ((_customSweepIndex < _customSweepSamples.Length) ? ((float)((double)_customSweepSamples[_customSweepIndex++] * _volume)) : 0f);
			}
			buffer[offset + i] = num2;
			if (array != null)
			{
				array[i] = num2;
			}
		}
		if (array != null)
		{
			_onSamplesGenerated?.Invoke(array);
		}
		return count;
	}
}
