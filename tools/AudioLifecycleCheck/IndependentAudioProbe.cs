using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// This probe does not call Sonca's engine, decoder, signal generator or analyzer.
internal static class IndependentAudioProbe
{
    public static async Task Run(string[] args)
    {
        string Opt(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "="))?.Split('=', 2)[1] ?? fallback;
        string mode = Opt("--mode", "capture");
        double amplitude = double.Parse(Opt("--amplitude", ".1"), System.Globalization.CultureInfo.InvariantCulture);
        int outputChannel = int.Parse(Opt("--output-channel", "1")) - 1;
        if (outputChannel < 0 || outputChannel > 1) throw new ArgumentException("Output channel must be 1 or 2.");
        bool faultStimulus = args.Contains("--allow-fault-stimulus");
        if (faultStimulus && (mode != "tone" || amplitude > .05 || Opt("--rounds", "10") != "1"))
            throw new ArgumentException("Fault-state diagnostic stimulus is limited to one tone at amplitude <= .05.");
        string save = Opt("--save", "");
        if (string.IsNullOrWhiteSpace(save)) throw new ArgumentException("Provide --save=<dated artifact directory> for diagnostic captures.");
        string directory = Path.GetFullPath(save);
        Directory.CreateDirectory(directory);
        using var enumerator = new MMDeviceEnumerator();
        using var input = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Single(d => d.FriendlyName.Contains("FastTrack Pro"));
        using var output = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Single(d => d.FriendlyName.Contains("Analog Connector 1/2") && d.FriendlyName.Contains("FastTrack Pro"));
        using var capture = new WasapiCapture(input, false, 100);
        if (args.Contains("--pcm16")) capture.WaveFormat = new WaveFormat(capture.WaveFormat.SampleRate, 16, 2);
        using var loopback = new WasapiLoopbackCapture(output);
        var outputBlocks = new List<byte>();
        object outputGate = new();
        loopback.DataAvailable += (_, e) => { lock (outputGate) outputBlocks.AddRange(e.Buffer.Take(e.BytesRecorded)); };
        loopback.StartRecording();
        int sampleRate = capture.WaveFormat.SampleRate;
        int bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
        if ((bytesPerSample != 4 && bytesPerSample != 2) || capture.WaveFormat.Channels != 2) throw new Exception("Expected stereo PCM16 or float32.");
        var blocks = new List<byte>();
        object gate = new();
        capture.DataAvailable += (_, e) => { lock (gate) blocks.AddRange(e.Buffer.Take(e.BytesRecorded)); };
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Console.Error.WriteLine(e.Exception); };
        capture.StartRecording();
        bool collapsed = false;
        async Task<double> Window(string label, int milliseconds)
        {
            lock (gate) blocks.Clear();
            lock (outputGate) outputBlocks.Clear();
            await Task.Delay(milliseconds);
            byte[] data; lock (gate) data = blocks.ToArray();
            using (var writer = new WaveFileWriter(Path.Combine(directory, label + ".wav"), capture.WaveFormat)) writer.Write(data, 0, data.Length);
            byte[] rendered; lock (outputGate) rendered = outputBlocks.ToArray();
            using (var writer = new WaveFileWriter(Path.Combine(directory, label + "-loopback.wav"), loopback.WaveFormat)) writer.Write(rendered, 0, rendered.Length);
            var levels = new List<object>(); double selectedDb = -200;
            for (int c = 0; c < 2; c++)
            {
                var samples = new List<double>();
                for (int i = c * bytesPerSample; i + bytesPerSample <= data.Length; i += 2 * bytesPerSample)
                    samples.Add(bytesPerSample == 4 ? BitConverter.ToSingle(data, i) : BitConverter.ToInt16(data, i) / 32768.0);
                double rms = Math.Sqrt(samples.Select(x => x*x).DefaultIfEmpty().Average());
                double db = 20*Math.Log10(Math.Max(rms, 1e-20));
                if (c == 1) selectedDb = db;
                int distinct = samples.Distinct().Count();
                if (c == 1 && label.EndsWith("playing") && db > -70 && distinct <= 64) collapsed = true;
                levels.Add(new { channel = c+1, rmsDbFs = db, peak = samples.Select(Math.Abs).DefaultIfEmpty().Max(), samples = samples.Count, distinct });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { mode, label, outputChannel = outputChannel + 1, levels, rate = sampleRate, bits = capture.WaveFormat.BitsPerSample, inputVolume = input.AudioEndpointVolume.MasterVolumeLevelScalar, inputVolumeDb = input.AudioEndpointVolume.MasterVolumeLevel, outputVolume = output.AudioEndpointVolume.MasterVolumeLevelScalar }));
            return selectedDb;
        }
        double baseline = await Window("initial", 1500);
        if (mode != "capture" && baseline > -70 && !faultStimulus) { Console.WriteLine("Baseline already abnormal; no stimulus emitted."); return; }
        if (baseline > -70 && faultStimulus) Console.WriteLine("DIAGNOSTIC ONLY: abnormal baseline retained; do not use this capture as a speaker verdict.");
        try
        {
            int rounds = int.Parse(Opt("--rounds", "10"));
            for (int round = 1; round <= rounds; round++)
            {
                if (mode != "capture")
                {
                    using var playback = new WasapiOut(output, AudioClientShareMode.Shared, false, 150);
                    playback.Init(new Stimulus(sampleRate, mode, amplitude, outputChannel));
                    playback.Play();
                    await Window($"{round:00}-playing", 4000);
                    playback.Stop();
                }
                await Task.Delay(800);
                double after = await Window($"{round:00}-silence", 1500);
                if (collapsed) { Console.WriteLine("FAULT: captured signal has <=64 distinct values; saved raw input/output WAVs."); break; }
                if (after > -70 && mode != "capture") { Console.WriteLine("FAULT: stopped at first abnormal post-playback capture."); break; }
            }
        }
        finally { capture.StopRecording(); }
    }
    private sealed class Stimulus(int sampleRate, string mode, double amplitude, int outputChannel) : ISampleProvider
    {
        private long frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i=0; i<count; i+=2, frame++)
            {
                double t = frame / (double)sampleRate;
                double phase = mode == "sweep" ? 2*Math.PI*20*3/Math.Log(1000)*(Math.Exp(t*Math.Log(1000)/3)-1) : 2*Math.PI*1000*t;
                float value = t >= 3 || mode == "silence" ? 0 : (float)(amplitude*Math.Sin(phase)*Math.Min(1,t/.01)*Math.Min(1,(3-t)/.01));
                buffer[offset+i] = outputChannel == 0 ? value : 0;
                buffer[offset+i+1] = outputChannel == 1 ? value : 0;
            }
            return count;
        }
    }
}
