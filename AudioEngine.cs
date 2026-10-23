using System;

using System.Collections.Generic;

using System.IO;

using System.Globalization;

using System.Linq;

using System.Runtime.InteropServices;

using System.Threading;

using System.Threading.Tasks;

using NAudio.CoreAudioApi;

using NAudio.Wave;

using NAudio.Wave.SampleProviders;

using Microsoft.Win32;



namespace SoncaAudioInspector;



public partial class AudioEngine : IDisposable

{

	public sealed record DualCaptureResult(float[] DutSamples, int DutSampleRate, float[]? AmbientSamples, int AmbientSampleRate, string? AmbientError);
	


	public static bool flagSaveFile = false;



	public static bool flagGenerateSeperateSine = false;



	public static bool flagSaveData = false;



	public static bool flagExportImageSingleLine = false;



	public static bool flagExportImageAutoLine = true;



	private MMDeviceEnumerator _enumerator;



	private WasapiOut? _wasapiOut;



	// NAudio 2.3.0 does not close its event-sync wait handle in Dispose.
	// Polling Shared capture avoids native handle accumulation between GC runs.
	private WasapiCapture? _wasapiCapture;



	private SignalSampleProvider? _signalProvider;



	private List<float> _recordedSamples;



	private object _lock = new object();



	private WasapiOut? _continuousWasapiOut;



	private ContinuousSignalProvider? _continuousSignalProvider;



	private WasapiCapture? _continuousCapture;
	private MMDevice? _continuousPlaybackDevice;
	private MMDevice? _continuousRecordingDevice;
	private readonly object _lifecycleLock = new();
	private bool _measurementActive;
	private bool _disposed;
	public bool IsAnyPlaybackActive
	{
		get
		{
			lock (_lifecycleLock)
				return _wasapiOut?.PlaybackState == PlaybackState.Playing
					|| _continuousWasapiOut?.PlaybackState == PlaybackState.Playing;
		}
	}

	private IDisposable BeginMeasurement()
	{
		lock (_lifecycleLock)
		{
				ThrowIfUnavailable();
				if (UseExclusivePlayback) CaptureSession.Reset();
			StopAllAudio();
			_measurementActive = true;
			return new MeasurementLease(this);
		}
	}

	private void ThrowIfUnavailable()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_measurementActive)
			throw new InvalidOperationException("Một phiên đo âm thanh đang chạy hoặc đang dừng. Chờ phiên hiện tại kết thúc rồi đo lại.");
	}

	private sealed class MeasurementLease(AudioEngine owner) : IDisposable
	{
		public void Dispose()
		{
			lock (owner._lifecycleLock)
				owner._measurementActive = false;
		}
	}



	private double _playbackVolume = 0.6;



	private double _recordingVolume = 0.5;



	private int _playbackSampleRate = 44100;



	public bool IsContinuousPlaybackActive

	{

		get

		{

			if (_continuousWasapiOut != null)

			{

				return _continuousWasapiOut.PlaybackState == PlaybackState.Playing;

			}

			return false;

		}

	}



	public bool IsContinuousCaptureActive

	{

		get

		{

			if (_continuousCapture != null)

			{

				return _continuousCapture.CaptureState == CaptureState.Capturing;

			}

			return false;

		}

	}



	public double PlaybackVolume

	{

		get

		{

			return _playbackVolume;

		}

		set

		{

			_playbackVolume = Math.Clamp(double.IsFinite(value) ? value : 0.6, 0.0, 1.0);

		}

	}



	public bool UseExclusivePlayback
	{
		get => false;
		set { }
	}
	public string? LastExclusiveCaptureFormat { get; private set; }



	public double RecordingVolume

	{

		get

		{

			return _recordingVolume;

		}

		set

		{

			_recordingVolume = Math.Clamp(double.IsFinite(value) ? value : 0.5, 0.0, 1.0);

		}

	}



	public int PlaybackSampleRate

	{

		get

		{

			return _playbackSampleRate;

		}

		set

		{

			_playbackSampleRate = ((value == 44100) ? 44100 : 48000);

		}

	}



	public double RecordingGain

	{

		get

		{

			return 1.0;

		}

		set

		{

		}

	}



	public int? RecordingChannel { get; set; }



	public int? PlaybackChannel { get; set; }



	public int RecordingSampleRate { get; private set; } = 48000;



	// FastTrack Shared input stays open between workflows to avoid repeated USB
	// stream reinitialization. It drains input only; StopAllAudio still stops all renderers.
	public SharedCaptureSession CaptureSession { get; } = new();

	public AudioEngine()

	{

		_enumerator = new MMDeviceEnumerator();

		_recordedSamples = new List<float>();

	}



	public List<MMDevice> GetPlaybackDevices()

	{

		using MMDeviceEnumerator mMDeviceEnumerator = new MMDeviceEnumerator();

		return mMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

	}



	public List<MMDevice> GetRecordingDevices()

	{

		using MMDeviceEnumerator mMDeviceEnumerator = new MMDeviceEnumerator();

		return mMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

	}



	public double? TryGetWindowsPlaybackVolume(MMDevice? playbackDevice)

	{

		if (playbackDevice == null)

		{

			return null;

		}

		try

		{

			using MMDevice mMDevice = GetCurrentActiveDevice(playbackDevice, "ngo ph\ufffdt");

			return mMDevice.AudioEndpointVolume.MasterVolumeLevelScalar;

		}

		catch

		{

			return null;

		}

	}



	public (double Volume, string DeviceName, string DeviceId)? TryGetWindowsDefaultPlaybackVolume()

	{

		try

		{

			using MMDeviceEnumerator mMDeviceEnumerator = new MMDeviceEnumerator();

			using MMDevice mMDevice = mMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

			return (mMDevice.AudioEndpointVolume.MasterVolumeLevelScalar, mMDevice.FriendlyName, mMDevice.ID);

		}

		catch

		{

			return null;

		}

	}

	private WaveFormat ResolveExclusiveCaptureFormat(MMDevice device)

	{

		int sampleRate = PlaybackSampleRate;

		using AudioClient formatClient = device.AudioClient;
		int channels = Math.Max(1, formatClient.MixFormat.Channels);

		WaveFormat[] candidates =

		{

			new WaveFormatExtensible(sampleRate, 24, channels),

			new WaveFormat(sampleRate, 24, channels),

			new WaveFormat(sampleRate, 16, channels),

			new WaveFormatExtensible(sampleRate, 16, channels),

			WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels),

			new WaveFormat(sampleRate, 32, channels)

		};
		foreach (WaveFormat candidate in candidates)

		{

			if (formatClient.IsFormatSupported(AudioClientShareMode.Exclusive, candidate))

			{
				LastExclusiveCaptureFormat = candidate.ToString();

				return candidate;

			}

		}

		throw new NotSupportedException($"Thiết bị thu '{device.FriendlyName}' không hỗ trợ WASAPI Exclusive ở {sampleRate} Hz.");

	}



	public static bool IsWindowsMonoAudioEnabled()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Multimedia\Audio");
			object? value = key?.GetValue("AccessibilityMonoMixState");
			return value != null && Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
		}
		catch
		{
			return false;
		}
	}



	public double? TryGetWindowsPlaybackPeak(MMDevice? playbackDevice)

	{

		if (playbackDevice == null)

		{

			return null;

		}

		try

		{

			using MMDevice mMDevice = GetCurrentActiveDevice(playbackDevice, "ngo ph t");

			return mMDevice.AudioMeterInformation.MasterPeakValue;

		}

		catch

		{

			return null;

		}

	}



	public bool TryValidateAudioDevices(MMDevice? playbackDevice, MMDevice? recordingDevice, out string error)

	{

		if (playbackDevice == null || recordingDevice == null)

		{

			error = "Chưa chọn đủ ngõ phát và ngõ thu.";

			return false;

		}

		try

		{

			using (GetCurrentActiveDevice(playbackDevice, "Ngõ phát"))

			{

				using (GetCurrentActiveDevice(recordingDevice, "Ngo thu"))

				{

					error = string.Empty;

					return true;

				}

			}

		}

		catch (Exception ex)

		{

			error = ex.Message;

			return false;

		}

	}



	public static bool IsAudioDeviceBusy(Exception? exception)

	{

		for (Exception ex = exception; ex != null; ex = ex.InnerException)

		{

			int hResult = ex.HResult;

			if (hResult == -2004287478 || hResult == -2004287474)

			{

				return true;

			}

		}

		return false;

	}



	public double? TryGetWindowsRecordingVolume(MMDevice? recordingDevice)

	{

		if (recordingDevice == null)

		{

			return null;

		}

		try

		{

			using MMDevice mMDevice = GetCurrentActiveDevice(recordingDevice, "ngo thu");

			return mMDevice.AudioEndpointVolume.MasterVolumeLevelScalar;

		}

		catch

		{

			return null;

		}

	}



	public bool? TryGetWindowsRecordingMute(MMDevice? recordingDevice)

	{

		if (recordingDevice == null)

		{

			return null;

		}

		try

		{

			using MMDevice mMDevice = GetCurrentActiveDevice(recordingDevice, "ngo thu");

			return mMDevice.AudioEndpointVolume.Mute;

		}

		catch

		{

			return null;

		}

	}



	public bool ApplyWindowsRecordingVolume(MMDevice? recordingDevice)
	{
		// Không can thiệp Master Volume / Mute của Windows để tránh làm kẹt PGA/Gain thanh ghi phần cứng soundcard
		return true;
	}



	public MMDevice? AutoDetectPlaybackDevice()

	{

		return GetPlaybackDevices().FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf("MI_LCD", StringComparison.OrdinalIgnoreCase) >= 0 || d.FriendlyName.IndexOf("MI_SAM", StringComparison.OrdinalIgnoreCase) >= 0);

	}



	public MMDevice? AutoDetectRecordingDevice()

	{

		return GetRecordingDevices().FirstOrDefault((MMDevice d) => d.FriendlyName.IndexOf("SONCA", StringComparison.OrdinalIgnoreCase) >= 0);

	}



		public async Task<float[]> PlayAndRecordAsync(MMDevice playbackDevice, MMDevice recordingDevice, SignalType signalType, double frequency, double durationSeconds, Action<float[]>? realTimeRecordedCallback = null, bool forceSaveFiles = false, double[]? multitoneFrequencies = null, double[]? multitoneAmplitudes = null, double multitoneSampleScale = 1.0, CancellationToken cancellationToken = default(CancellationToken), int? recordingChannel = null, float[]? customSweepSamples = null, double signalSampleScale = 1.0, Action<float[], float[]>? stereoObserver = null)

	{
		using IDisposable measurement = BeginMeasurement();


		Stop();

		await Task.Delay(200, cancellationToken);

		CancellationTokenSource captureCancellation;

		Exception captureError;

		int stopping;

		using (MMDevice activePlaybackDevice = GetCurrentActiveDevice(playbackDevice, "ngo ph\ufffdt"))

		{

				using MMDevice activeRecordingDevice = GetCurrentActiveDevice(recordingDevice, "ngo thu");
				CaptureSession.Ensure(activeRecordingDevice, UseExclusivePlayback);

			_recordedSamples.Clear();

			captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

			try

			{

				captureError = null;

				stopping = 0;

				List<float> capturedSamples = new List<float>();

				string openingStage = "ngo thu '" + activeRecordingDevice.FriendlyName + "'";

				try

				{

					// Generate the stimulus at the sample rate selected by the app. WASAPI
					// shared mode performs any endpoint conversion without changing the
					// frequency/time base used by the measurement pipeline.
					int sampleRate = PlaybackSampleRate;

					_wasapiCapture = new WasapiCapture(activeRecordingDevice, useEventSync: UseExclusivePlayback, UseExclusivePlayback ? 50 : 150) { ShareMode = UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared };

					if (UseExclusivePlayback)

					{

						_wasapiCapture.WaveFormat = ResolveExclusiveCaptureFormat(activeRecordingDevice);

					}

					WaveFormat deviceFormat = _wasapiCapture.WaveFormat;

					RecordingSampleRate = deviceFormat.SampleRate;

					int channels = deviceFormat.Channels;
						if (stereoObserver != null && channels < 2) throw new InvalidOperationException("Impedance cần hai kênh thu đồng thời, độc lập.");

					int? num2 = recordingChannel;

					if (!num2.HasValue)

					{

						recordingChannel = RecordingChannel;

					}

					_wasapiCapture.DataAvailable += delegate (object? s, WaveInEventArgs e)

					{

						try

						{

							if (Volatile.Read(in stopping) == 0)

							{

								float[] array3 = CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, deviceFormat, 1.0, recordingChannel);
								if (stereoObserver != null) stereoObserver(
									CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, deviceFormat, 1.0, 0),
									CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, deviceFormat, 1.0, 1));

								lock (_lock)

								{

									capturedSamples.AddRange(array3);

								}

								realTimeRecordedCallback?.Invoke(array3);

							}

						}

						catch (Exception error)

						{

							FailCapture(error);

						}

					};

					_wasapiCapture.RecordingStopped += delegate (object? _, StoppedEventArgs e)

					{

						if (e.Exception != null || Volatile.Read(in stopping) == 0)

						{

							FailCapture(e.Exception ?? new IOException("Thiết bị thu dừng trước khi đo xong."));

						}

					};

					List<float> playedSamplesList = (flagSaveFile ? new List<float>() : null);

					_signalProvider = new SignalSampleProvider(sampleRate, signalType, frequency, 1.0, delegate (float[] samples)

					{

						if (flagSaveFile && playedSamplesList != null)

						{

							lock (playedSamplesList)

							{

								playedSamplesList.AddRange(samples);

							}

						}

					}, multitoneFrequencies, multitoneAmplitudes, multitoneSampleScale, customSweepSamples, signalSampleScale);

					ISampleProvider playbackProvider = _signalProvider;
					if (PlaybackChannel.HasValue)
					{
						playbackProvider = new ChannelRoutingSampleProvider(playbackProvider, PlaybackChannel.Value);
					}
					else if (UseExclusivePlayback && playbackProvider.WaveFormat.Channels == 1)
					{
						playbackProvider = new MonoToStereoSampleProvider(playbackProvider);
					}

					openingStage = "ngo ph\ufffdt '" + activePlaybackDevice.FriendlyName + "'";

					_wasapiOut = new WasapiOut(activePlaybackDevice, UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, useEventSync: false, UseExclusivePlayback ? 50 : 150);

					_wasapiOut.Init(playbackProvider);

					_wasapiOut.PlaybackStopped += delegate (object? _, StoppedEventArgs e)

					{

						if (e.Exception != null || Volatile.Read(in stopping) == 0)

						{

							FailCapture(e.Exception ?? new IOException("Thiết bị phát dừng trước khi đo xong."));

						}

					};

					try

					{

						if (recordingChannel.HasValue)

						{

							int valueOrDefault = recordingChannel.GetValueOrDefault();

							if (valueOrDefault < 0 || valueOrDefault >= channels)

							{

								throw new ArgumentOutOfRangeException("recordingChannel", "Kênh thu không tồn tại trên thiết bị.");

							}

						}

						cancellationToken.ThrowIfCancellationRequested();

						openingStage = "ngo thu '" + activeRecordingDevice.FriendlyName + "'";

						_wasapiCapture.StartRecording();

						openingStage = "ngo ph\ufffdt '" + activePlaybackDevice.FriendlyName + "'";

						_wasapiOut.Play();

						openingStage = "phi\ufffdn do \ufffdm thanh";

						await Task.Delay((int)(durationSeconds * 1000.0) + 600, captureCancellation.Token);

					}

					catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)

					{

						throw;

					}

					catch (OperationCanceledException) when (captureError != null)

					{

						throw new IOException("Lỗi thiết bị âm thanh. Cắm lại thiết bị và đo lại.", captureError);

					}

					catch (COMException ex3) when (IsDeviceInvalidated(ex3))

					{

						throw new IOException("Windows vừa ngắt hoặc khởi tạo lại " + openingStage + ". Nhấn Làm mới thiết bị, chọn lại ngõ phát/ngõ thu rồi đo lại.", ex3);

					}

					finally

					{

						Interlocked.Exchange(ref stopping, 1);

						Stop();

					}

					if (captureError != null)

					{

						throw new IOException("Thiết bị âm thanh báo lỗi; không sử dụng bản thu này.", captureError);

					}

					lock (_lock)

					{

						if (flagSaveFile && (!flagGenerateSeperateSine | forceSaveFiles))

						{

							try

							{

								string value = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

								string filename = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"played_{signalType}_{frequency}Hz_{value}.wav");

								string filename2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"recorded_{signalType}_{frequency}Hz_{value}.wav");

								if (playedSamplesList != null)

								{

									using WaveFileWriter waveFileWriter = new WaveFileWriter(filename, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));

									float[] array;

									lock (playedSamplesList)

									{

										array = playedSamplesList.ToArray();

									}

									waveFileWriter.WriteSamples(array, 0, array.Length);

								}

								using WaveFileWriter waveFileWriter2 = new WaveFileWriter(filename2, WaveFormat.CreateIeeeFloatWaveFormat(deviceFormat.SampleRate, 1));

								float[] array2 = capturedSamples.ToArray();

								waveFileWriter2.WriteSamples(array2, 0, array2.Length);

							}

							catch

							{

							}

						}

						float[] result = capturedSamples.ToArray();
						ValidateFastTrackCaptureIntegrity(activeRecordingDevice, result);
						return result;

					}

				}

				catch (COMException ex4) when (IsDeviceInvalidated(ex4))

				{

					throw new IOException("Windows vừa ngắt hoặc khởi tạo lại " + openingStage + ". Nhấn Làm mới thiết bị, chọn lại ngõ phát/ngõ thu rồi đo lại.", ex4);

				}

				finally

				{

					Interlocked.Exchange(ref stopping, 1);

					Stop();

				}

			}

			finally

			{

				if (captureCancellation != null)

				{

					((IDisposable)captureCancellation).Dispose();

				}

			}

		}

		void FailCapture(Exception error)

		{

			if (Volatile.Read(in stopping) != 0)

			{

				return;

			}

			Interlocked.CompareExchange(ref captureError, error, null);

			try

			{

				captureCancellation.Cancel();

			}

			catch (ObjectDisposedException)

			{

			}

		}


	}



	private MMDevice GetCurrentActiveDevice(MMDevice selectedDevice, string role)

	{

		string iD;

		string text;

		try

		{

			iD = selectedDevice.ID;

			text = iD;

		}

		catch (COMException ex) when (IsDeviceInvalidated(ex))

		{

			throw new IOException(role + " đã bị Windows ngắt hoặc khởi tạo lại. Nhấn Làm mới thiết bị và chọn lại.", ex);

		}

		MMDevice device;

		try

		{

			using MMDeviceEnumerator mMDeviceEnumerator = new MMDeviceEnumerator();

			device = mMDeviceEnumerator.GetDevice(iD);

		}

		catch (COMException ex2) when (IsDeviceInvalidated(ex2))

		{

			throw new IOException(role + " '" + text + "' không còn khả dụng. Nhấn Làm mới thiết bị và chọn lại.", ex2);

		}

		if (device.State != DeviceState.Active)

		{

			DeviceState state = device.State;

			device.Dispose();

			throw new IOException($"{role} '{text}' hiện không hoạt động ({state}). " + "Kiểm tra kết nối, nhấn Làm mới thiết bị và chọn lại.");

		}

		return device;

	}



	private static bool IsDeviceInvalidated(COMException exception)

	{

		return exception.HResult == -2004287484;

	}



	public async Task<float[]> PlayFileAndRecordAsync(string filePath, MMDevice playbackDevice, MMDevice recordingDevice, double durationSeconds)

	{
		using IDisposable measurement = BeginMeasurement();
		using MMDevice activePlaybackDevice = GetCurrentActiveDevice(playbackDevice, "ngõ phát");
		using MMDevice activeRecordingDevice = GetCurrentActiveDevice(recordingDevice, "ngõ thu");
		CaptureSession.Ensure(activeRecordingDevice, UseExclusivePlayback);
		using AudioFileReader fileReader = new AudioFileReader(filePath);
		try
		{


			Stop();

			_recordedSamples.Clear();

			_wasapiCapture = new WasapiCapture(activeRecordingDevice, useEventSync: UseExclusivePlayback, UseExclusivePlayback ? 50 : 150) { ShareMode = UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared };

			if (UseExclusivePlayback)

			{

				_wasapiCapture.WaveFormat = ResolveExclusiveCaptureFormat(activeRecordingDevice);

			}

			WaveFormat deviceFormat = _wasapiCapture.WaveFormat;

			RecordingSampleRate = deviceFormat.SampleRate;

			int channels = deviceFormat.Channels;

			int? fileChannel = RecordingChannel;

			if (fileChannel.HasValue)

			{

				int valueOrDefault = fileChannel.GetValueOrDefault();

				if (valueOrDefault < 0 || valueOrDefault >= channels)

				{

					Stop();

					throw new ArgumentOutOfRangeException("RecordingChannel", "Kênh thu không tồn tại trên thiết bị.");

				}

			}

			_wasapiCapture.DataAvailable += delegate (object? s, WaveInEventArgs e)

			{

				lock (_lock)

				{

					float[] collection = CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, deviceFormat, 1.0, fileChannel);

					_recordedSamples.AddRange(collection);

				}

			};



			VolumeSampleProvider volumeSampleProvider = new VolumeSampleProvider(fileReader)

			{

				Volume = 1f

			};

			ISampleProvider sampleProvider = volumeSampleProvider;

			if (UseExclusivePlayback && sampleProvider.WaveFormat.SampleRate != PlaybackSampleRate)

			{

				sampleProvider = new WdlResamplingSampleProvider(sampleProvider, PlaybackSampleRate);

			}

			if (PlaybackChannel.HasValue)

			{

				sampleProvider = new ChannelRoutingSampleProvider(sampleProvider, PlaybackChannel.Value);

			}

			else if (UseExclusivePlayback && sampleProvider.WaveFormat.Channels == 1)

			{

				sampleProvider = new MonoToStereoSampleProvider(sampleProvider);

			}

			_wasapiOut = new WasapiOut(activePlaybackDevice, UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, useEventSync: false, UseExclusivePlayback ? 50 : 150);

			_wasapiOut.Init(sampleProvider);

			_wasapiCapture.StartRecording();

			_wasapiOut.Play();

			await Task.Delay((int)(durationSeconds * 1000.0) + 350);

			Stop();



			lock (_lock)

			{

				if (flagSaveFile)

				{

					try

					{

						string path = "recorded_file_sweep_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".wav";

						using WaveFileWriter waveFileWriter = new WaveFileWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path), WaveFormat.CreateIeeeFloatWaveFormat(deviceFormat.SampleRate, 1));

						float[] array = _recordedSamples.ToArray();

						waveFileWriter.WriteSamples(array, 0, array.Length);

					}

					catch

					{

					}

				}

				float[] result = _recordedSamples.ToArray();
				ValidateFastTrackCaptureIntegrity(activeRecordingDevice, result);
				return result;

			}

		}
		finally
		{
			// Join audio workers before disposing the reader and endpoint owners.
			Stop();
		}
	}



	/// <summary>Stop every app-owned source and join/reset its WASAPI stream before the next test.</summary>
	public void StopAllAudio()
	{
		lock (_lifecycleLock)
		{
			StopContinuousPlayback();
			Stop();
			StopContinuousCapture();
		}
	}

	public void Stop()

	{
		lock (_lifecycleLock)
		{

			WasapiOut? output = _wasapiOut;
			_wasapiOut = null;
			if (output != null)
			{
				try { output.Stop(); } catch { }
				try { output.Dispose(); } catch { }
			}
			WasapiCapture? capture = _wasapiCapture;
			_wasapiCapture = null;
			if (capture != null)
			{
				try { capture.StopRecording(); } catch { }
				try { capture.Dispose(); } catch { }
			}
			_signalProvider = null;
		}
	}



	public static float[] ConvertCaptureBufferToMono(byte[] buffer, int bytesRecorded, WaveFormat format, double gain)

	{

		return CaptureSampleDecoder.Decode(buffer, bytesRecorded, format, gain);

	}



	public async Task<DualCaptureResult> CaptureSilenceAsync(MMDevice recordingDevice, MMDevice? ambientRecordingDevice, double durationSeconds = 0.4, CancellationToken cancellationToken = default(CancellationToken))

	{
		using IDisposable measurement = BeginMeasurement();


		List<float> dutSamples = new List<float>();

		List<float> ambientSamples = new List<float>();

		object dutLock = new object();

		object ambientLock = new object();

		string ambientError = null;

		using MMDevice activeRecordingDevice = GetCurrentActiveDevice(recordingDevice, "ngo thu");
		CaptureSession.Ensure(activeRecordingDevice, UseExclusivePlayback);

		using WasapiCapture dutCapture = new WasapiCapture(activeRecordingDevice, useEventSync: UseExclusivePlayback, UseExclusivePlayback ? 50 : 100) { ShareMode = UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared };

		if (UseExclusivePlayback)

		{

			dutCapture.WaveFormat = ResolveExclusiveCaptureFormat(activeRecordingDevice);

		}

		WaveFormat dutFormat = dutCapture.WaveFormat;

		int? dutChannel = RecordingChannel;

		if (dutChannel.HasValue)

		{

			int valueOrDefault = dutChannel.GetValueOrDefault();

			if (valueOrDefault < 0 || valueOrDefault >= dutFormat.Channels)

			{

				throw new ArgumentOutOfRangeException("RecordingChannel", "Kênh thu không tồn tại trên thiết bị.");

			}

		}

		dutCapture.DataAvailable += delegate (object? _, WaveInEventArgs e)

		{

			float[] collection = CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, dutFormat, 1.0, dutChannel);

			lock (dutLock)

			{

				dutSamples.AddRange(collection);

			}

		};

		WasapiCapture ambientCapture = null;

		MMDevice activeAmbientDevice = null;

		WaveFormat ambientFormat = null;

		bool useAmbient = false;

		if (ambientRecordingDevice != null)

		{

			try

			{

				useAmbient = !string.Equals(ambientRecordingDevice.ID, activeRecordingDevice.ID, StringComparison.OrdinalIgnoreCase);

			}

			catch (COMException exception) when (IsDeviceInvalidated(exception))

			{

				ambientError = "Mic môi trường đã bị Windows ngắt hoặc khởi tạo lại.";

			}

		}

		if (useAmbient)

		{

			try

			{

				activeAmbientDevice = GetCurrentActiveDevice(ambientRecordingDevice, "mic môi trường");

				ambientCapture = new WasapiCapture(activeAmbientDevice, useEventSync: UseExclusivePlayback, UseExclusivePlayback ? 50 : 100) { ShareMode = UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared };

				if (UseExclusivePlayback)

				{

					ambientCapture.WaveFormat = ResolveExclusiveCaptureFormat(activeAmbientDevice);

				}

				ambientFormat = ambientCapture.WaveFormat;

				ambientCapture.DataAvailable += delegate (object? _, WaveInEventArgs e)

				{

					float[] collection = ConvertCaptureBufferToMono(e.Buffer, e.BytesRecorded, ambientFormat, 1.0);

					lock (ambientLock)

					{

						ambientSamples.AddRange(collection);

					}

				};

			}

			catch (Exception ex)

			{

				ambientError = ex.Message;

				ambientCapture?.Dispose();

				ambientCapture = null;

				activeAmbientDevice?.Dispose();

				activeAmbientDevice = null;

				ambientFormat = null;

			}

		}

		try

		{

			ambientCapture?.StartRecording();

			dutCapture.StartRecording();

			await Task.Delay(Math.Max(250, (int)Math.Round(durationSeconds * 1000.0)), cancellationToken);

		}

		catch (COMException ex2) when (IsDeviceInvalidated(ex2))

		{

			throw new IOException("Windows vừa ngắt hoặc khởi tạo lại ngõ thu '" + activeRecordingDevice.FriendlyName + "' trong bước kiểm tra noise. Nhấn Làm mới thiết bị, chọn lại Mic KT rồi đo lại.", ex2);

		}

		finally

		{

			try

			{

				dutCapture.StopRecording();

			}

			catch

			{

			}

			try

			{

				ambientCapture?.StopRecording();

			}

			catch

			{

			}

			// StopRecording only requests a stop. Dispose joins callbacks before ToArray.
			try { dutCapture.Dispose(); }
			finally
			{
				try { ambientCapture?.Dispose(); }
				finally { activeAmbientDevice?.Dispose(); }
			}

		}

		float[] dutResult = dutSamples.ToArray();
		ValidateFastTrackCaptureIntegrity(activeRecordingDevice, dutResult);
		return new DualCaptureResult(dutResult, AmbientSamples: useAmbient ? ambientSamples.ToArray() : null, DutSampleRate: dutFormat.SampleRate, AmbientSampleRate: ambientFormat?.SampleRate ?? 0, AmbientError: ambientError);


	}



	private static void ValidateFastTrackCaptureIntegrity(MMDevice device, IReadOnlyList<float> samples)
	{
		if (!device.FriendlyName.Contains("FastTrack Pro", StringComparison.OrdinalIgnoreCase))
			return;

		double endpointGain;
		try { endpointGain = device.AudioEndpointVolume.MasterVolumeLevelScalar; }
		catch { return; }

		if (CaptureDataIntegrity.HasRepeatedPcm16Bytes(samples, endpointGain))
			throw new IOException(
				"FASTTRACK_CAPTURE_INTEGRITY: dữ liệu thu có mẫu byte PCM16 lặp bất thường. " +
				"Không sử dụng kết quả; rút/cắm lại FastTrack Pro rồi đo lại.");
	}

	public void StartContinuousPlayback(MMDevice? playbackDevice, SignalType type, double frequency, double volume, int? playbackChannel = null)

	{
		lock (_lifecycleLock)
		{
			ThrowIfUnavailable();
			try
			{


				StopContinuousPlayback();

				MMDevice currentActiveDevice = _continuousPlaybackDevice = GetCurrentActiveDevice(playbackDevice, "ngõ phát");

				int channels = 2;

				int sampleRate = PlaybackSampleRate;

				if (!UseExclusivePlayback)
				{
					// Every MMDevice.AudioClient access activates a new COM client.
					using AudioClient formatClient = currentActiveDevice.AudioClient;
					WaveFormat mixFormat = formatClient.MixFormat;
					channels = Math.Max(1, mixFormat.Channels);
					sampleRate = mixFormat.SampleRate;
				}

				int? playbackChannel2 = playbackChannel ?? PlaybackChannel;

				_continuousSignalProvider = new ContinuousSignalProvider(sampleRate, channels, playbackChannel2)

				{

					Type = type,

					Frequency = frequency,

					Volume = volume

				};

				_continuousWasapiOut = new WasapiOut(currentActiveDevice, UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, useEventSync: false, UseExclusivePlayback ? 50 : 100);

				_continuousWasapiOut.Init(_continuousSignalProvider);

				_continuousWasapiOut.Play();

			}
			catch
			{
				StopContinuousPlayback();
				throw;
			}
		}
	}



	public void UpdateContinuousPlayback(SignalType type, double frequency, double volume, int? playbackChannel = null)

	{

		if (_continuousSignalProvider != null)

		{

			_continuousSignalProvider.Type = type;

			_continuousSignalProvider.Frequency = frequency;

			_continuousSignalProvider.Volume = volume;

			if (playbackChannel.HasValue || !playbackChannel.HasValue)

			{

				_continuousSignalProvider.PlaybackChannel = playbackChannel ?? PlaybackChannel;

			}

		}

	}



	public void SetContinuousPlaybackChannel(int? channel)

	{

		PlaybackChannel = channel;

		if (_continuousSignalProvider != null)

		{

			_continuousSignalProvider.PlaybackChannel = channel;

		}

	}



	public void StopContinuousPlayback()

	{
		lock (_lifecycleLock)
		{


			WasapiOut? output = _continuousWasapiOut;
			_continuousWasapiOut = null;
			if (output != null)

			{

				try

				{

					output.Stop();

				}

				catch

				{

				}

				try { output.Dispose(); } catch { }

			}

			_continuousSignalProvider = null;

			MMDevice? device = _continuousPlaybackDevice;
			_continuousPlaybackDevice = null;
			device?.Dispose();
		}
	}



	public void StartContinuousCapture(MMDevice? recordingDevice, Action<float[], int> onSamplesAvailable) =>
		StartContinuousCapture(recordingDevice, onSamplesAvailable, null);

	public void StartContinuousCapture(MMDevice? recordingDevice, Action<float[], int> onSamplesAvailable,
		Action<Exception>? onCaptureFailed)

	{
		lock (_lifecycleLock)
		{
			ThrowIfUnavailable();
			try
			{


				StopContinuousCapture();

				MMDevice currentActiveDevice = _continuousRecordingDevice = GetCurrentActiveDevice(recordingDevice, "ngõ thu");
				// Scope, level checks and TestRunner share the same engine-owned
				// FastTrack session, including when Scope is opened first.
				CaptureSession.Ensure(currentActiveDevice, UseExclusivePlayback);

				_continuousCapture = new WasapiCapture(currentActiveDevice, useEventSync: UseExclusivePlayback, UseExclusivePlayback ? 50 : 100) { ShareMode = UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared };

				if (UseExclusivePlayback)

				{

					_continuousCapture.WaveFormat = ResolveExclusiveCaptureFormat(currentActiveDevice);

				}

				WaveFormat format = _continuousCapture.WaveFormat;

				int sampleRate = format.SampleRate;

				WasapiCapture capture = _continuousCapture;
				capture.RecordingStopped += (_, e) =>
				{
					if (ReferenceEquals(_continuousCapture, capture))
						onCaptureFailed?.Invoke(e.Exception ?? new IOException("Luồng theo dõi ngõ thu đã dừng."));
				};
				_continuousCapture.DataAvailable += delegate (object? s, WaveInEventArgs e)

				{

					if (e.BytesRecorded > 0)

					{

						float[] array = CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, format, 1.0, RecordingChannel);

						if (array.Length != 0)

						{

							onSamplesAvailable(array, sampleRate);

						}

					}

				};

				_continuousCapture.StartRecording();

			}
			catch
			{
				StopContinuousCapture();
				throw;
			}
		}
	}



	public void StopContinuousCapture()

	{
		lock (_lifecycleLock)
		{


			WasapiCapture? capture = _continuousCapture;
			_continuousCapture = null;
			if (capture != null)

			{

				try

				{

					capture.StopRecording();

				}

				catch

				{

				}

				try { capture.Dispose(); } catch { }

			}

			MMDevice? device = _continuousRecordingDevice;
			_continuousRecordingDevice = null;
			device?.Dispose();
		}
	}



	public void Dispose()

	{
		lock (_lifecycleLock)
		{
			if (_disposed) return;
			_disposed = true;


			Stop();

			StopContinuousPlayback();

			StopContinuousCapture();

				try { CaptureSession.Dispose(); }
				finally { _enumerator?.Dispose(); }

		}
	}

}
