using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class Acoustic3RouteProbe
{
    public static object Analyze(float[] samples,int rate,int frequency,bool mic)
    {
        int length=Math.Min(samples.Length,rate*2/5),start=0; double best=-1;
        for(int index=0;index+length<=samples.Length;index+=Math.Max(1,rate/20))
        {
            double power=0;for(int n=index;n<index+length;n++)power+=samples[n]*samples[n];
            if(power>best){best=power;start=index;}
        }
        var quality=AdvancedAudioMeasurement.AnalyzeTone(samples.AsSpan(start,length).ToArray(),rate,frequency,9);
        bool valid=quality.IsValid && double.IsFinite(quality.SnrDb) && quality.SnrDb>=20 && quality.ClippedSamplePercent<.01
            && Math.Abs(quality.FundamentalFrequencyHz-frequency)<8;
        // Route discovery accepts a DSP-shifted carrier. This is not a THD,
        // clock or product PASS gate; those stay in the production measurement.
        return new {frequency,valid,quality.SignalLevelDbFs,quality.SnrDb,quality.FundamentalFrequencyHz,
            shiftHz=quality.FundamentalFrequencyHz-frequency,quality.ThdPercent,windowStart=start,windowSamples=length};
    }
    public static void Reanalyze(string directory)
    {
        var rows=new List<object>();
        foreach(string route in new[]{"MIC IN","LINE IN","USB A"})
        {
            var tones=new List<object>();
            foreach(int frequency in new[]{997,1603})
            {
                using var reader=new AudioFileReader(Path.Combine(directory,route.Replace(" ","-")+"-"+frequency+".wav"));
                var samples=new float[reader.Length/sizeof(float)];int count=reader.Read(samples,0,samples.Length);
                tones.Add(Analyze(samples.AsSpan(0,count).ToArray(),reader.WaveFormat.SampleRate,frequency,route=="MIC IN"));
            }
            rows.Add(new {route,tones});
        }
        string json=JsonSerializer.Serialize(rows,C360Reference.Json);File.WriteAllText(Path.Combine(directory,"reanalyzed.json"),json);Console.WriteLine(json);
    }
    public static void Qualify(string directory)
    {
        var rows=JsonNode.Parse(File.ReadAllText(Path.Combine(directory,"routes.json")))!.AsArray();
        foreach(var row in rows)
        {
            var tones=new JsonArray(); bool detected=true;
            foreach(int frequency in new[]{997,1603})
            {
                string stem=row!["route"]!.GetValue<string>().Replace(" ","-");
                string path=Path.Combine(directory,stem+"-"+frequency+".wav");
                if(!File.Exists(path))path=Path.Combine(directory,stem+"-"+(row["render"]!.GetValue<string>().Contains("CS3600")?"CS3600":"C360record")+"-"+frequency+".wav");
                using var reader=new AudioFileReader(path);var samples=new float[reader.Length/sizeof(float)];int count=reader.Read(samples,0,samples.Length);
                var tone=JsonSerializer.SerializeToNode(Analyze(samples.AsSpan(0,count).ToArray(),reader.WaveFormat.SampleRate,frequency,false))!;
                detected &= tone["valid"]!.GetValue<bool>();tones.Add(tone);
            }
            row!["tones"]=tones;row["detected"]=detected;
            row["criteria"]="Route discovery only: two known tones, stable 400 ms, SNR >=20 dB, no clipping, DSP carrier shift <8 Hz. No product PASS claim.";
        }
        File.WriteAllText(Path.Combine(directory,"routes-qualified.json"),rows.ToJsonString(new(){WriteIndented=true}));
        Console.WriteLine(rows.ToJsonString(new(){WriteIndented=true}));
    }
    public static async Task Run(string output, ulong address,string? baselinePath=null,bool usbOnly=false)
    {
        Directory.CreateDirectory(output);
        using var ble = new SoncaBleManager(); ble.LogMessage += Console.WriteLine;
        if (!await ble.ConnectAsync(address) || !SoncaBleMixerProfile.TryReadModelId(ble.FirmwareInfoPayload, out ushort model) || model != 1071)
            throw new Exception("Requested ACOUSTIC3 firmware was not verified; no audio emitted.");
        var before = ble.MixerState.ToDictionary(p => p.Key,p => p.Value);
        if(!string.IsNullOrWhiteSpace(baselinePath))
        {
            using var saved=JsonDocument.Parse(File.ReadAllText(baselinePath));
            if(saved.RootElement.GetProperty("address").GetString()!=address.ToString("X12"))throw new Exception("Restore baseline belongs to another board.");
            before=saved.RootElement.GetProperty("parameters").EnumerateObject().ToDictionary(p=>Convert.ToUInt16(p.Name,16),p=>p.Value.GetUInt16());
        }
        using var devices = new MMDeviceEnumerator();
        var outputs = devices.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active)
            .Where(d => d.FriendlyName.Contains("USB Audio Device",StringComparison.OrdinalIgnoreCase)
                || d.FriendlyName.Contains("CS3600 MUSIC",StringComparison.OrdinalIgnoreCase)
                ).ToArray();
        using var input = devices.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active)
            .Single(d => d.FriendlyName.Contains("C360 usb audio",StringComparison.OrdinalIgnoreCase));
        using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
        engine.PlaybackSampleRate=44100; engine.RecordingGain=1; engine.UseExclusivePlayback=false;
        var results = new List<object>();
        async Task Set(ushort reg,ushort value)
        {
            if (!await ble.SetParamAsync(0,reg,value,"ACOUSTIC3 route probe")) throw new Exception("Unconfirmed route control " + reg.ToString("X4"));
        }
        try
        {
            await Set(SoncaBleMixerProfile.EchoEnable,0); await Set(SoncaBleMixerProfile.ReverbEnable,0);
            foreach (var route in new[] { (Name:"MIC IN",Source:(byte)4,Mic:true), (Name:"LINE IN",Source:(byte)4,Mic:false), (Name:"USB A",Source:(byte)1,Mic:false) })
            {
                if(usbOnly && route.Name!="USB A") continue;
                await Set(SoncaBleMixerProfile.InputSource,route.Source);
                await Set(SoncaBleMixerProfile.MicMute,(ushort)(route.Mic?0:1));
                await Set(SoncaBleMixerProfile.MicVolume,route.Mic?before[SoncaBleMixerProfile.MicVolume]:(ushort)0);
                await Set(SoncaBleMixerProfile.MusicMute,(ushort)(route.Mic?1:0));
                foreach (var render in outputs.Where(d => route.Name=="USB A" ? d.FriendlyName.Contains("CS3600 MUSIC") || usbOnly && d.FriendlyName.Contains("C360 usb audio") : d.FriendlyName.Contains("USB Audio Device")))
                {
                    var tones = new List<object>(); bool detected = true;
                    foreach (int frequency in new[] {997,1603})
                    {
                        const int rate=44100; double amplitude=Math.Pow(10,(usbOnly?-18:-30)/20d);
                        var signal=Enumerable.Range(0,usbOnly?rate*2:rate).Select(i => (float)(amplitude*Math.Sin(2*Math.PI*frequency*i/rate))).ToArray();
                        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var samples=await engine.PlayAndRecordAsync(render,input,SignalType.Sweep,frequency,usbOnly?2.5:1.25,cancellationToken:timeout.Token,customSweepSamples:signal);
                        var assessment=JsonSerializer.SerializeToElement(Analyze(samples,engine.RecordingSampleRate,frequency,route.Mic));
                        bool valid=assessment.GetProperty("valid").GetBoolean();
                        detected &= valid;
                        tones.Add(assessment);
                        string suffix=usbOnly?"-"+(render.FriendlyName.Contains("CS3600")?"CS3600":"C360record"):"";
                        using var wav=new WaveFileWriter(Path.Combine(output,route.Name.Replace(" ","-")+suffix+"-"+frequency+".wav"),WaveFormat.CreateIeeeFloatWaveFormat(engine.RecordingSampleRate,1));
                        wav.WriteSamples(samples,0,samples.Length);
                    }
                    var row=new {route=route.Name,source=route.Source,render=render.FriendlyName,renderId=render.ID,capture=input.FriendlyName,captureId=input.ID,detected,tones};
                    results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row,C360Reference.Json));
                }
            }
            File.WriteAllText(Path.Combine(output,"routes.json"),JsonSerializer.Serialize(results,C360Reference.Json));
        }
        finally
        {
            engine.StopAllAudio();
            foreach (ushort reg in new ushort[] {0x6400,0x6447,0x6448,0x6404,0x640A,0x6242})
                if (!await ble.SetParamAsync(0,reg,before[reg],"Restore probe baseline")) throw new Exception("Probe restore failed " + reg.ToString("X4"));
            foreach(var option in Enum.GetValues<SoncaMixerOption>())
                if(SoncaBleMixerProfile.SupportsOption(before[0x6254],option)
                    && !await ble.SetOptionAsync(option,(before[0x6254]&SoncaBleMixerProfile.OptionMask(option))!=0))
                    throw new Exception("Probe option restore failed "+option);
            await ble.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket());
            var diff=before.Where(p => ble.MixerState.GetValueOrDefault(p.Key)!=p.Value).Select(p => p.Key.ToString("X4")).ToArray();
            File.WriteAllText(Path.Combine(output,"restoration.json"),JsonSerializer.Serialize(new {diff},C360Reference.Json));
            foreach(var render in outputs) render.Dispose();
            if(diff.Length>0)throw new Exception("Route probe baseline differs.");
        }
    }
}
