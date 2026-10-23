using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class SampleRateSnapshot
{
    // Read current formats and open capture only: no stimulus or endpoint setting writes.
    public static async Task RunAsync()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToList();
        try
        {
            foreach (var device in devices)
            {
                int currentRate = FastTrackDeviceSetup.ReadMixSampleRate(device.ID);
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "endpoint", device.ID, device.FriendlyName,
                    flow = device.DataFlow.ToString(), currentRate }));
                foreach (var entry in new[] { ("DeviceFormat", PropertyKeys.PKEY_AudioEngine_DeviceFormat),
                    ("OEMFormat", PropertyKeys.PKEY_AudioEngine_OEMFormat) })
                {
                    try
                    {
                        object value = device.Properties[entry.Item2].Value;
                        if (value is byte[] bytes)
                        {
                            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                            try { LogFormat(device.FriendlyName, entry.Item1, WaveFormat.MarshalFromPtr(pinned.AddrOfPinnedObject())); }
                            finally { pinned.Free(); }
                        }
                        else Console.WriteLine(JsonSerializer.Serialize(new { stage = entry.Item1, type = value?.GetType().FullName }));
                    }
                    catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { stage = entry.Item1, error = ex.Message })); }
                }
                ReadPolicyFormats(device);
                if (device.DataFlow != DataFlow.Capture) continue;
                ReadRawClientFormats(device);
                await ReadRawCaptureAsync(device);
                try
                {
                    using var current = enumerator.GetDevice(device.ID);
                    using var capture = new WasapiCapture(current, false, 50);
                    long bytes = 0;
                    Exception? failure = null;
                    capture.DataAvailable += (_, e) => Interlocked.Add(ref bytes, e.BytesRecorded);
                    capture.RecordingStopped += (_, e) => failure = e.Exception;
                    capture.StartRecording();
                    await Task.Delay(500);
                    capture.StopRecording();
                    Console.WriteLine(JsonSerializer.Serialize(new { stage = "actual-capture", device.FriendlyName,
                        endpointRate = currentRate, actualRate = capture.WaveFormat.SampleRate,
                        bytes = Interlocked.Read(ref bytes), error = failure?.Message }));
                    if (failure != null || bytes == 0) throw new InvalidOperationException("No healthy capture packets received.");
                    if (capture.WaveFormat.SampleRate != currentRate) throw new InvalidOperationException("Fresh capture differs from current endpoint rate.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { stage = "capture-unverified", device.FriendlyName, error = ex.Message }));
                }
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }

    private static void LogFormat(string device, string source, WaveFormat format) =>
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "format", device, source,
            rate = format.SampleRate, channels = format.Channels, bits = format.BitsPerSample, encoding = format.Encoding.ToString() }));

    private static void ReadPolicyFormats(MMDevice device)
    {
        var type = typeof(AudioEngine).Assembly.GetType("SoncaAudioInspector.WindowsEndpointPolicy")!;
        object? policy = null;
        try
        {
            policy = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            var contract = type.GetNestedType("IPolicyConfig", BindingFlags.NonPublic)!;
            foreach (bool defaults in new[] { false, true })
            {
                object[] parameters = { device.ID, defaults, IntPtr.Zero };
                Marshal.ThrowExceptionForHR((int)contract.GetMethod("GetDeviceFormat")!.Invoke(policy, parameters)!);
                IntPtr format = (IntPtr)parameters[2];
                try { LogFormat(device.FriendlyName, "PolicyDeviceFormat defaults=" + defaults, WaveFormat.MarshalFromPtr(format)); }
                finally { Marshal.FreeCoTaskMem(format); }
            }
            object[] mix = { device.ID, IntPtr.Zero };
            Marshal.ThrowExceptionForHR((int)contract.GetMethod("GetMixFormat")!.Invoke(policy, mix)!);
            IntPtr mixFormat = (IntPtr)mix[1];
            try { LogFormat(device.FriendlyName, "PolicyMixFormat", WaveFormat.MarshalFromPtr(mixFormat)); }
            finally { Marshal.FreeCoTaskMem(mixFormat); }
        }
        catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { stage = "policy-format-error", error = ex.ToString() })); }
        finally { if (policy != null) Marshal.ReleaseComObject(policy); }
    }

    private static void ReadRawClientFormats(MMDevice device)
    {
        foreach (var options in new[] { AudioClientStreamOptions.None, AudioClientStreamOptions.Raw })
        {
            try
            {
                using var client = device.AudioClient;
                object native = typeof(AudioClient).GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
                var properties = new AudioClientProperties { cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                    eCategory = AudioStreamCategory.Other, Options = options };
                IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientProperties>());
                try { Marshal.StructureToPtr(properties, pointer, false); SetClientProperties(native, pointer); }
                finally { Marshal.FreeHGlobal(pointer); }
                LogFormat(device.FriendlyName, "ClientProperties " + options, client.MixFormat);
            }
            catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { stage = "client-properties-error", options = options.ToString(), error = ex.Message })); }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetClientPropertiesCall(IntPtr client, IntPtr properties);

    private static void SetClientProperties(object native, IntPtr properties)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(native), client2 = IntPtr.Zero;
        try
        {
            Guid iid = new("726778CD-F60A-4eda-82DE-E47610CD78AA");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out client2));
            IntPtr vtable = Marshal.ReadIntPtr(client2);
            var call = Marshal.GetDelegateForFunctionPointer<SetClientPropertiesCall>(Marshal.ReadIntPtr(vtable, 16 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(call(client2, properties));
        }
        finally { if (client2 != IntPtr.Zero) Marshal.Release(client2); Marshal.Release(unknown); }
    }

    private static async Task ReadRawCaptureAsync(MMDevice device)
    {
        try
        {
            using var capture = new WasapiCapture(device, false, 50);
            var client = (AudioClient)typeof(WasapiCapture).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
            object native = typeof(AudioClient).GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            var properties = new AudioClientProperties { cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                eCategory = AudioStreamCategory.Other, Options = AudioClientStreamOptions.Raw };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientProperties>());
            try { Marshal.StructureToPtr(properties, pointer, false); SetClientProperties(native, pointer); }
            finally { Marshal.FreeHGlobal(pointer); }
            typeof(AudioClient).GetField("mixFormat", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, null);
            capture.WaveFormat = client.MixFormat;
            long bytes = 0;
            Exception? failure = null;
            capture.DataAvailable += (_, e) => Interlocked.Add(ref bytes, e.BytesRecorded);
            capture.RecordingStopped += (_, e) => failure = e.Exception;
            capture.StartRecording();
            await Task.Delay(500);
            capture.StopRecording();
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "actual-raw-capture", device.FriendlyName,
                actualRate = capture.WaveFormat.SampleRate, channels = capture.WaveFormat.Channels,
                bits = capture.WaveFormat.BitsPerSample, bytes = Interlocked.Read(ref bytes), error = failure?.Message }));
        }
        catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { stage = "raw-capture-unverified", error = ex.ToString() })); }
    }
}
