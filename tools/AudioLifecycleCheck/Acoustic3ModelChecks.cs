using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoncaAudioInspector;

internal static class Acoustic3ModelChecks
{
    private static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    public static void Logic()
    {
        var model=Acoustic3ModelDefinition.Create("Analog","Record","USB",true,true,false);
        var io=model.testItems.InOut;
        C360ModelDefinition.Validate(io);
        Require(model.model=="ACOUSTIC 3" && C360ModelDefinition.Matches(io) && io.FirmwareModelId==1071,"Common model identity/profile missing");
        Require(io.Tests.Where(t=>t.IsEnabled).Select(t=>t.SpeakerStage).SequenceEqual(new[]{"MicEffects","MicFrequencyShift","MicDry","LineIn"}),"Unverified route enabled or MIC stages missing");
        Require(io.Tests.All(t=>t.MaximumRuns==3 && t.RequiredPasses==2 && (t.SweepLengthSamples==65536 || t.SpeakerStage=="MicFrequencyShift")),"Run policy/sweep default mismatch");
        string original=JsonSerializer.Serialize(io);
        var config=new CheckingConfig{models=new(){model,C360ModelDefinition.Create()}};
        C360ModelDefinition.EnsureSeed(config);
        Require(JsonSerializer.Serialize(model.testItems.InOut)==original,"C360 seed migrated/added stages to the common model");
        byte[] info=System.Text.Encoding.ASCII.GetBytes("SCA_MAIN48_3x100A.ACOUSTIC3\0").Concat(new byte[]{45,4,47}).ToArray();
        Acoustic3ModelDefinition.RequireFirmware(io,info);
        try{Acoustic3ModelDefinition.RequireFirmware(io,info[..^1].Concat(new byte[]{51}).ToArray());throw new Exception("Wrong board accepted");}
        catch(InvalidOperationException){}
        var controls=SoncaMixerControls.ForFirmware(info);
        using var recorded=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","Acoustic3-controls.json")));
        var repeat=SoncaMixerControls.ForFirmware(Convert.FromHexString(recorded.RootElement.GetProperty("infoPayloadHex").GetString()!)).Single(c=>c.Register==0x6406);
        Require(repeat.CommandFor(54)==55 && repeat.MatchesReadback(55,54) && !repeat.MatchesReadback(55,52),"Recorded Repeat quantization was ignored or confirmation tolerance widened");
        Require(controls.Count==20 && controls.Select(c=>c.Register).Distinct().Count()==20,"Additional controls incomplete/duplicated");
        foreach(var control in controls)
        {
            foreach(double value in new[]{control.Minimum,0,control.Maximum}.Where(v=>v>=control.Minimum && v<=control.Maximum))
                Require(control.Accepts(control.Encode(value)) && Math.Abs(control.Decode(control.Encode(value))-value)<1e-9,"Control conversion changed value: "+control.Name);
            Require(!control.Accepts(65535),"Control accepted absent-register sentinel");
        }
        Require(controls.Single(c=>c.Register==0x3041).Decode(553)==5.3
            && controls.Single(c=>c.Register==0x3C40).Decode(532)==3.2
            && SoncaMixerControls.ForFirmware(Array.Empty<byte>()).Count==0,"Tone mapping used legacy guessed addresses/scale");
        byte[] recordedInfo=Convert.FromHexString(recorded.RootElement.GetProperty("infoPayloadHex").GetString()!);
        var settings=new Dictionary<ushort,ushort>{[0x6406]=54,[0x3040]=500,[0x6447]=0};
        string initial=SoncaMixerControls.Fingerprint(settings,recordedInfo,44100);
        settings[0x6406]=55;Require(SoncaMixerControls.Fingerprint(settings,recordedInfo,44100)!=initial,"Repeat change kept an old LINE standard identity");
        settings[0x6406]=54;settings[0x6447]=1;Require(SoncaMixerControls.Fingerprint(settings,recordedInfo,44100)==initial,"Automatic stage mute invalidated the baseline reference");
        Console.WriteLine("PASS ACOUSTIC 3 factory, measured-route activation, 3/2 policy, 64K sweeps, firmware identity guard, C360 seed isolation and 20 mapped controls.");
    }
    public static void Prepare(string configPath,string output,string probes,string? usbProbes,bool apply,string? volumeBaseline=null)
    {
        Directory.CreateDirectory(output);
        var probe=JsonNode.Parse(File.ReadAllText(probes))!.AsArray();
        if(!string.IsNullOrWhiteSpace(usbProbes) && File.Exists(usbProbes))
            foreach(var row in JsonNode.Parse(File.ReadAllText(usbProbes))!.AsArray())probe.Add(row!.DeepClone());
        JsonNode? Ready(string route)=>probe.FirstOrDefault(p=>p!["route"]!.GetValue<string>()==route && p["detected"]!.GetValue<bool>());
        var mic=Ready("MIC IN");var line=Ready("LINE IN");var usb=Ready("USB A");
        var analog=mic??line??throw new Exception("No qualified analog path; model not saved.");
        var config=JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        var models=config["models"]!.AsArray();
        var existing=models.SingleOrDefault(m=>string.Equals(m!["model"]!.GetValue<string>(),Acoustic3ModelDefinition.Name,StringComparison.OrdinalIgnoreCase));
        var candidate=Acoustic3ModelDefinition.Create(analog["render"]!.GetValue<string>(),analog["capture"]!.GetValue<string>(),
            usb?["render"]!.GetValue<string>()??"Speakers (CS3600 MUSIC)",mic!=null,line!=null,usb!=null);
        if(line!=null)candidate.testItems.InOut.Devices.Input["LINE IN"]=line["render"]!.GetValue<string>();
        if(!string.IsNullOrWhiteSpace(volumeBaseline))
        {
            using var saved=JsonDocument.Parse(File.ReadAllText(volumeBaseline));
            var values=saved.RootElement.GetProperty("parameters");
            candidate.testItems.InOut.TestVolume=new(){BleMasterPercent=SoncaBleMixerProfile.VolumeFromRaw(values.GetProperty("6102").GetUInt16()),
                BleMusicPercent=SoncaBleMixerProfile.VolumeFromRaw(values.GetProperty("6103").GetUInt16()),
                BleMicPercent=SoncaBleMixerProfile.VolumeFromRaw(values.GetProperty("6400").GetUInt16()),WindowsPlaybackPercent=75,WindowsRecordingPercent=100};
        }
        var target=existing?.DeepClone()??JsonSerializer.SerializeToNode(candidate)!;
        target["testItems"]!["InOut"]=JsonSerializer.SerializeToNode(candidate.testItems.InOut,ModelConfigEditorWindow.JsonOptions);
        target["AudioConfigUpdatedUtc"]=DateTimeOffset.UtcNow;
        var parsed=ModelConfigEditorWindow.ParseModel(Acoustic3ModelDefinition.Name,target.ToJsonString());
        File.WriteAllText(Path.Combine(output,"model.json"),JsonSerializer.Serialize(parsed,ModelConfigEditorWindow.JsonOptions));
        if(existing!=null)models[models.IndexOf(existing)]=target;else models.Add(target);
        File.WriteAllText(Path.Combine(output,"checking-config.json"),config.ToJsonString(new(){WriteIndented=true}));
        if(apply)
        {
            File.Copy(configPath,Path.Combine(output,"checking-config-before.json"),false);
            AtomicFile.WriteAllText(configPath,config.ToJsonString(new(){WriteIndented=true}));
        }
        Console.WriteLine($"{(apply?"SAVED":"PREPARED")} ACOUSTIC 3; MIC={mic!=null}; LINE={line!=null}; USB={usb!=null}; BT disabled until connected; kept assembly/other model fields.");
    }
}
