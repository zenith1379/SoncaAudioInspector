using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class C360VolumeQualificationChecks
{
    public static async Task Run(string output, string setupPath, string configPath, double? confirmationRecording = null)
    {
        Directory.CreateDirectory(output);
        var setup = JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(setupPath))!;
        var config = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!;
        C360ModelDefinition.EnsureSeed(config);
        var model = config.models.Single(m => C360ModelDefinition.Matches(m.testItems.InOut));
        var io = model.testItems.InOut;
        using var manager = new SoncaBleManager();
        if (!await manager.EnsureConnectedAsync(setup.BleControllerAddress, userInitiated: true)) throw new Exception("BLE unavailable; no volume recommendation saved");
        using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
        using var devices = new MMDeviceEnumerator();
        C360ModelEndpoint[] Endpoints() => devices.EnumerateAudioEndPoints(DataFlow.All,DeviceState.Active).Select(d => { using(d) return new C360ModelEndpoint(d.ID,d.FriendlyName,d.DataFlow==DataFlow.Capture); }).ToArray();
        var control = new C360AudioBench(engine,manager,output);
        var baseline = await control.ReadAsync(default);
        var volume = new ModelTestVolume { BleMasterPercent = SoncaBleMixerProfile.VolumeFromRaw(baseline[SoncaBleMixerProfile.MasterVolume]),
            BleMusicPercent = SoncaBleMixerProfile.VolumeFromRaw(baseline[SoncaBleMixerProfile.MusicVolume]), BleMicPercent = SoncaBleMixerProfile.VolumeFromRaw(baseline[SoncaBleMixerProfile.MicVolume]) };
        io.TestVolume = volume;
        if (confirmationRecording.HasValue) io.Tests.Single(t=>t.SpeakerStage=="UsbA").PlaybackLevelDbfs=-6;
        var rows = new List<Sample>();
        async Task<Sample> Measure(string stage, double playback, double recording, string tag)
        {
            volume.WindowsPlaybackPercent = playback; volume.WindowsRecordingPercent = recording;
            foreach (var test in io.Tests) test.Enable = test.SpeakerStage == stage;
            var plan = C360ModelPlan.Create(io,Endpoints(),setup);
            var bench = new C360AudioBench(engine,manager,Path.Combine(output,tag));
            var result = (await new C360ElectronicSuite().RunAsync(plan.Bind(bench),plan.Setup,stages:plan.Stages)).Single();
            var m = result.Measurement;
            var thd = m == null ? null : new TestRunner(engine).BuildSweepDistortionSummary(m,1000);
            var curve = m?.FrequencyResponse.Where(p => p.FrequencyHz >= 50 && p.FrequencyHz <= 18000).ToDictionary(p=>p.FrequencyHz,p=>p.LevelDb+m.ExcitationRmsDbFs) ?? new();
            var sample = new Sample(stage,playback,recording,tag,result.Status,thd?.IsValid==true && m?.IsClipped==false,
                thd?.ThdPercent ?? double.NaN,m?.SignalLevelDbFs ?? double.NaN,m?.EstimatedSnrDb ?? double.NaN,m?.PeakSample ?? double.NaN,curve);
            rows.Add(sample); File.WriteAllText(Path.Combine(output,"samples.json"),JsonSerializer.Serialize(rows,C360Reference.Json));
            Console.WriteLine($"{tag}: {stage} Windows={playback}/{recording}, THD1k={sample.Thd:F4}%, RMS={sample.Rms:F1}dBFS, SNR={sample.Snr:F1}dB, valid={sample.Valid}");
            return sample;
        }
        try
        {
            foreach(var pair in confirmationRecording.HasValue ? Array.Empty<(double,double)>() : new[]{(100d,50d),(75d,50d),(50d,50d),(100d,100d)})
            { await Measure("MicDry",pair.Item1,pair.Item2,$"candidate-{pair.Item1}-{pair.Item2}-mic"); await Measure("LineIn",pair.Item1,pair.Item2,$"candidate-{pair.Item1}-{pair.Item2}-line"); }
            var candidates = rows.GroupBy(r=>(r.Playback,r.Recording)).Where(g=>g.All(r=>r.Valid && r.Peak < .85 && r.Rms >= -40 && r.Snr >= 24))
                .Select(g=>new { Pair=g.Key,Score=g.Max(r=>r.Thd) }).OrderBy(p=>p.Score).ToArray();
            if(candidates.Length==0 && !confirmationRecording.HasValue) throw new Exception("No common Windows level has valid, unclipped MIC/LINE captures with SNR >=24 dB; keep configuration unchanged.");
            var best=confirmationRecording.HasValue ? (Playback:75d,Recording:confirmationRecording.Value) : candidates[0].Pair;
            foreach(var stage in new[]{"MicDry","LineIn","UsbA"}) for(int run=1;run<=3;run++) await Measure(stage,best.Playback,best.Recording,$"confirm-{stage}-{run}");
            var confirmations=rows.Where(r=>r.Tag.StartsWith("confirm-")).GroupBy(r=>r.Stage).ToArray();
            var consistency=confirmations.Select(g=>
            {
                var samples=g.ToArray();
                var pairs=(from i in Enumerable.Range(0,samples.Length) from j in Enumerable.Range(i+1,samples.Length-i-1)
                    let left=samples[i] let right=samples[j]
                    select new {First=i+1,Second=j+1,Valid=left.Valid && right.Valid && Math.Max(left.Peak,right.Peak)<.85 && Math.Min(left.Snr,right.Snr)>=24,
                        ThdSpread=Math.Abs(left.Thd-right.Thd),ResponseSpread=left.Curve.Keys.Max(f=>Math.Abs(left.Curve[f]-right.Curve.GetValueOrDefault(f)))})
                    .OrderBy(p=>p.ResponseSpread).ToArray();
                var pair=pairs.FirstOrDefault(p=>p.Valid && p.ThdSpread<=.1 && p.ResponseSpread<=1) ?? pairs[0];
                return new {Stage=g.Key,pair.Valid,pair.First,pair.Second,pair.ThdSpread,pair.ResponseSpread};
            }).ToArray();
            bool stable=consistency.All(g=>g.Valid && g.ThdSpread<=.1 && g.ResponseSpread<=1);
            File.WriteAllText(Path.Combine(output,"recommendation.json"),JsonSerializer.Serialize(new {verified=stable,TestVolume=volume,UsbLevelDbFs=io.Tests.Single(t=>t.SpeakerStage=="UsbA").PlaybackLevelDbfs,consistency},C360Reference.Json));
            Console.WriteLine("Common volume verified="+stable);
        }
        finally
        {
            var after=await control.ReadAsync(default); var changed=baseline.Where(p=>after.GetValueOrDefault(p.Key)!=p.Value).Select(p=>p.Key.ToString("X4")).ToArray();
            File.WriteAllText(Path.Combine(output,"restoration.json"),JsonSerializer.Serialize(new {registers=baseline.Count,changed}));
            if(changed.Length>0) throw new Exception("DSP restoration mismatch");
        }
    }
    private sealed record Sample(string Stage,double Playback,double Recording,string Tag,string Status,bool Valid,double Thd,double Rms,double Snr,double Peak,Dictionary<double,double> Curve);

    public static void SaveReference(string captures,string setupPath,string configPath,string standards)
    {
        var samples=JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(Path.Combine(captures,"samples.json")),C360Reference.Json)!;
        var checking=JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!;
        var model=checking.models.Single(m=>C360ModelDefinition.Matches(m.testItems.InOut));
        using var recommendation=JsonDocument.Parse(File.ReadAllText(Path.Combine(captures,"recommendation.json")));
        var volume=recommendation.RootElement.GetProperty("TestVolume").Deserialize<ModelTestVolume>()!;
        volume.BleMasterPercent=Math.Round(volume.BleMasterPercent!.Value); volume.BleMusicPercent=Math.Round(volume.BleMusicPercent!.Value); volume.BleMicPercent=Math.Round(volume.BleMicPercent!.Value);
        double usbLevel=recommendation.RootElement.GetProperty("UsbLevelDbFs").GetDouble();
        model.testItems.InOut.TestVolume=volume;
        model.testItems.InOut.Tests.Single(t=>t.SpeakerStage=="UsbA").PlaybackLevelDbfs=usbLevel;
        var setup=JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(setupPath))!;
        using var devices=new MMDeviceEnumerator(); var endpoints=new List<C360ModelEndpoint>();
        foreach(var d in devices.EnumerateAudioEndPoints(DataFlow.All,DeviceState.Active)) using(d) endpoints.Add(new(d.ID,d.FriendlyName,d.DataFlow==DataFlow.Capture));
        var plan=C360ModelPlan.Create(model.testItems.InOut,endpoints,setup);
        using var snapshot=JsonDocument.Parse(File.ReadLines(Path.Combine(captures,"control-readbacks.jsonl")).First());
        var baseline=snapshot.RootElement.GetProperty("state").EnumerateObject().ToDictionary(p=>Convert.ToUInt16(p.Name,16),p=>p.Value.GetUInt16());
        volume.RequireMatch(baseline);
        string mixerKey=string.Join(";",new ushort[]{0x6102,0x6103,0x6400,0x6402,0x6403,0x6467,0x6254}.Select(reg=>$"{reg:X4}:{baseline.GetValueOrDefault(reg)}"));
        var prepared=new List<(string File,C360Reference Reference,object Evidence)>();
        using var analysisEngine=new AudioEngine(); var reader=new TestRunner(analysisEngine);
        foreach(var group in samples.Where(r=>r.Tag.StartsWith("confirm-")).GroupBy(r=>r.Stage))
        {
            var stage=Enum.Parse<C360ElectronicStage>(group.Key);
            var test=plan.Configuration.Tests.Single(t=>C360ModelDefinition.Stage(t)==stage);
            var rows=group.ToArray();
            var pair=(from i in Enumerable.Range(0,rows.Length) from j in Enumerable.Range(i+1,rows.Length-i-1)
                let a=rows[i] let b=rows[j]
                where a.Valid && b.Valid && a.Thd<=test.Thd1000Hz && b.Thd<=test.Thd1000Hz && a.Peak<.85 && b.Peak<.85 && Math.Abs(a.Thd-b.Thd)<=.1
                select new {A=a,B=b,Spread=a.Curve.Keys.Max(f=>Math.Abs(a.Curve[f]-b.Curve.GetValueOrDefault(f)))})
                .OrderBy(p=>p.Spread).FirstOrDefault(p=>p.Spread<=3) ?? throw new Exception(group.Key+": no pair meets existing 3 dB app reference criteria");
            C360ElectronicResult Load(Sample sample)
            {
                var m=JsonSerializer.Deserialize<StandardAcousticResult>(File.ReadAllText(Path.Combine(captures,sample.Tag,stage+"-result.json")),C360Reference.Json)!;
                if(m.IsClipped || m.Validity=="INVALID") throw new Exception("Invalid capture cannot become reference");
                if(MeasurementCriteria.ThdVerdict(new[]{80d,1000d,4000d}.Select(f=>reader.BuildSweepDistortionSummary(m,f)),test)!="PASS")
                    throw new Exception("Capture does not meet enabled THD criteria; reference not saved");
                return new(stage,"PASS",sample.Tag,m);
            }
            var first=Load(pair.A); var second=Load(pair.B);
            var resolved=JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(Path.Combine(captures,pair.A.Tag,stage+"-acquisition.json")))!;
            string key=C360Reference.ConfigurationKey(model.model,plan,stage,mixerKey+";CAPTURE_RATE:"+first.Measurement!.SampleRate,resolved);
            var reference=C360Reference.Create(key,new[]{first,second},test);
            if(reference.Compare(first,test,3).Status!="PASS" || reference.Compare(second,test,3).Status!="PASS") throw new Exception("Reference readback comparison failed");
            prepared.Add((Path.Combine(standards,"c360-"+key+".json"),reference,new {Stage=group.Key,First=pair.A.Tag,Second=pair.B.Tag,pair.Spread,FirstThd=pair.A.Thd,SecondThd=pair.B.Thd}));
        }
        if(prepared.Count!=3) throw new Exception("MIC dry, LINE and USB A references required");
        Directory.CreateDirectory(standards);
        File.Copy(configPath,Path.Combine(captures,"checking_config-before.json"),overwrite:false);
        foreach(var item in prepared)
        {
            if(File.Exists(item.File)) File.Copy(item.File,Path.Combine(captures,Path.GetFileName(item.File)+".before"),overwrite:false);
            AtomicFile.WriteAllText(item.File,JsonSerializer.Serialize(item.Reference,C360Reference.Json));
            if(JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(item.File),C360Reference.Json)?.IsUsable!=true) throw new Exception("Reference persistence failed");
        }
        // Change only the requested model nodes; keep all other models, routes and calibration JSON intact.
        var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!;
        var modelNode=json["models"]!.AsArray().Single(n=>n!["model"]!.GetValue<string>()==model.model)!;
        modelNode["testItems"]!["InOut"]!["TestVolume"]=JsonSerializer.SerializeToNode(volume);
        modelNode["testItems"]!["InOut"]!["Tests"]!.AsArray().Single(n=>n!["SpeakerStage"]?.GetValue<string>()=="UsbA")!["Playback Level dBFS"]=usbLevel;
        modelNode["AudioConfigUpdatedUtc"]=DateTimeOffset.UtcNow;
        AtomicFile.WriteAllText(configPath,json.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(captures,"applied-reference.json"),JsonSerializer.Serialize(new {verifiedByAppReference=true,appRepeatabilityToleranceDb=3,TestVolume=volume,UsbLevelDbFs=usbLevel,references=prepared.Select(p=>new {p.File,p.Evidence})},C360Reference.Json));
        Console.WriteLine("PASS saved model volume and three references using closest qualified pairs, existing app 3 dB repeatability and persisted readback. No new playback.");
    }
}
