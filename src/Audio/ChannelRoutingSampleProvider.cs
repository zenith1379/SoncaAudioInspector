using NAudio.Wave;

namespace SoncaAudioInspector;

public class ChannelRoutingSampleProvider : ISampleProvider
{
	private readonly ISampleProvider _source;

	private readonly WaveFormat _waveFormat;

	private readonly int? _targetChannel;

	public WaveFormat WaveFormat => _waveFormat;

	public ChannelRoutingSampleProvider(ISampleProvider source, int? targetChannel = null)
	{
		_source = source;
		_targetChannel = targetChannel;
		if (source.WaveFormat.Channels == 1 && targetChannel.HasValue)
		{
			_waveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
		}
		else
		{
			_waveFormat = source.WaveFormat;
		}
	}

	public int Read(float[] buffer, int offset, int count)
	{
		if (_source.WaveFormat.Channels == 1 && _waveFormat.Channels == 2)
		{
			int num = count / 2;
			float[] array = new float[num];
			int num2 = _source.Read(array, 0, num);
			for (int i = 0; i < num2; i++)
			{
				if (!_targetChannel.HasValue)
				{
					buffer[offset + i * 2] = array[i];
					buffer[offset + i * 2 + 1] = array[i];
				}
				else if (_targetChannel.Value == 0)
				{
					buffer[offset + i * 2] = array[i];
					buffer[offset + i * 2 + 1] = 0f;
				}
				else
				{
					buffer[offset + i * 2] = 0f;
					buffer[offset + i * 2 + 1] = array[i];
				}
			}
			return num2 * 2;
		}
		if (_waveFormat.Channels >= 2 && _targetChannel.HasValue)
		{
			int num3 = _source.Read(buffer, offset, count);
			int channels = _waveFormat.Channels;
			int num4 = num3 / channels;
			for (int j = 0; j < num4; j++)
			{
				for (int k = 0; k < channels; k++)
				{
					if (k != _targetChannel.Value)
					{
						buffer[offset + j * channels + k] = 0f;
					}
				}
			}
			return num3;
		}
		return _source.Read(buffer, offset, count);
	}
}
