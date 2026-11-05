using System;
using NAudio.Wave;

namespace SoncaAudioInspector;

public class ContinuousSignalProvider : ISampleProvider
{
	private readonly WaveFormat _waveFormat;

	private SignalType _type = SignalType.Sine;

	private double _frequency = 1000.0;

	private double _volume = 0.5;

	private double _phase = 0.0;

	private int _playbackChannel = -1;

	private readonly Random _random = new Random();

	private double b0;

	private double b1;

	private double b2;

	private double b3;

	private double b4;

	private double b5;

	private double b6;

	public WaveFormat WaveFormat => _waveFormat;

	public SignalType Type
	{
		get
		{
			return _type;
		}
		set
		{
			_type = value;
		}
	}

	public double Frequency
	{
		get
		{
			return _frequency;
		}
		set
		{
			_frequency = Math.Clamp(value, 10.0, 24000.0);
		}
	}

	public double Volume
	{
		get
		{
			return _volume;
		}
		set
		{
			_volume = Math.Clamp(value, 0.0, 1.0);
		}
	}

	public int? PlaybackChannel
	{
		get
		{
			return (_playbackChannel < 0) ? ((int?)null) : new int?(_playbackChannel);
		}
		set
		{
			_playbackChannel = (value.HasValue ? value.Value : (-1));
		}
	}

	public ContinuousSignalProvider(int sampleRate = 48000, int channels = 2, int? playbackChannel = null)
	{
		int channels2 = Math.Max(1, channels);
		_waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels2);
		PlaybackChannel = playbackChannel;
	}

	public int Read(float[] buffer, int offset, int count)
	{
		int channels = _waveFormat.Channels;
		int num = count / channels;
		double num2 = _waveFormat.SampleRate;
		double num3 = Math.PI * 2.0 * _frequency / num2;
		float num4 = (float)_volume;
		int? num5 = _playbackChannel;
		for (int i = 0; i < num; i++)
		{
			float num6 = 0f;
			switch (_type)
			{
			case SignalType.Sine:
				num6 = (float)Math.Sin(_phase) * num4;
				break;
			case SignalType.Square:
				num6 = ((_phase < Math.PI) ? 1f : (-1f)) * num4;
				break;
			case SignalType.Triangle:
				num6 = (float)(2.0 / Math.PI * Math.Asin(Math.Sin(_phase))) * num4;
				break;
			case SignalType.WhiteNoise:
				num6 = (float)(_random.NextDouble() * 2.0 - 1.0) * num4;
				break;
			case SignalType.PinkNoise:
			{
				double num7 = _random.NextDouble() * 2.0 - 1.0;
				b0 = 0.99886 * b0 + num7 * 0.0555179;
				b1 = 0.99332 * b1 + num7 * 0.0750759;
				b2 = 0.969 * b2 + num7 * 0.153852;
				b3 = 0.8665 * b3 + num7 * 0.3104856;
				b4 = 0.55 * b4 + num7 * 0.5329522;
				b5 = -0.7616 * b5 - num7 * 0.016898;
				double num8 = b0 + b1 + b2 + b3 + b4 + b5 + b6 + num7 * 0.5362;
				b6 = num7 * 0.115926;
				num6 = (float)(num8 * 0.11 * (double)num4);
				break;
			}
			default:
				num6 = (float)Math.Sin(_phase) * num4;
				break;
			}
			if (channels == 1)
			{
				buffer[offset + i] = num6;
			}
			else
			{
				for (int j = 0; j < channels; j++)
				{
					if (!num5.HasValue)
					{
						buffer[offset + i * channels + j] = num6;
					}
					else if (num5.Value == j)
					{
						buffer[offset + i * channels + j] = num6;
					}
					else
					{
						buffer[offset + i * channels + j] = 0f;
					}
				}
			}
			_phase += num3;
			if (_phase >= Math.PI * 2.0)
			{
				_phase -= Math.PI * 2.0;
			}
		}
		return count;
	}
}
