using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

/// <summary>Qualify the two existing BT captures without re-pairing after Auto Test cleanup.</summary>
internal static class C360RecordedReferenceChecks
{
    public static void Run(string session, string identity, string standards, string output)
    {
        static void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), C360Reference.Json)!;
        var configuration = Read<InOutConfig>(Path.Combine(session,"model.json"));
        var setup = Read<C360ElectronicSetup>(Path.Combine(session,"acquisition.json"));
        using var info = JsonDocument.Parse(File.ReadAllText(identity));
        byte[] firmware = Convert.FromHexString(info.RootElement.GetProperty("infoPayloadHex").GetString()!);
        Require(SoncaBleMixerProfile.TryReadModelId(firmware,out ushort modelId) && modelId==1075,"Recorded INFO is not C360");
        using var first = JsonDocument.Parse(File.ReadLines(Path.Combine(session,"control-readbacks.jsonl")).First());
        var state = first.RootElement.GetProperty("state").EnumerateObject().ToDictionary(
            p=>Convert.ToUInt16(p.Name,16),p=>p.Value.GetUInt16());
        var inventory = new List<C360ModelEndpoint>();
        foreach (var pair in configuration.Devices.Input)
        {
            string id = pair.Key switch { "MIC IN"=>setup.AnalogPlaybackId,"LINE IN"=>setup.LinePlaybackId??setup.AnalogPlaybackId,
                "USB A"=>setup.UsbPlaybackId,_=>"" };
            if(id.Length>0)inventory.Add(new(id,pair.Value,false));
        }
        inventory.Add(new(setup.LineOutCaptureId,configuration.Devices.Output["LINE OUT"],true));
        var plan = C360ModelPlan.Create(configuration,inventory,setup);
        using var summary=JsonDocument.Parse(File.ReadAllText(Path.Combine(session,"summary.json")));
        string model=summary.RootElement.GetProperty("model").GetString()!;
        string mixer=SoncaMixerControls.Fingerprint(state,firmware,0)+";CAPTURE_RATE:"+setup.SampleRate;
        var lineSetup=Read<C360ElectronicSetup>(Path.Combine(session,"attempt-1","LineIn-acquisition.json"));
        string lineKey=C360Reference.ConfigurationKey(model,plan,C360ElectronicStage.LineIn,mixer,lineSetup);
        var lineReference=Read<C360Reference>(Path.Combine(standards,"c360-"+lineKey+".json"));
        Require(lineReference.Key==lineKey && lineReference.IsUsable,"Recorded baseline/configuration does not match the qualified LINE reference");
        var bt=summary.RootElement.GetProperty("results").EnumerateArray().Single(r=>r.GetProperty("StageName").GetString()==C360ElectronicResult.Name(C360ElectronicStage.Bluetooth));
        var attempts=bt.GetProperty("Attempts").EnumerateArray().Where(a=>a.GetProperty("Status").GetString()=="PASS").Take(2).ToArray();
        Require(attempts.Length==2,"Two accepted recorded BT captures are required");
        var test=plan.Configuration.Tests.Single(t=>t.SpeakerStage=="Bluetooth");
        var captures=new List<C360ElectronicResult>(); string? key=null;
        using var engine=new AudioEngine(); var runner=new TestRunner(engine);
        foreach(var attempt in attempts)
        {
            string folder=Path.Combine(session,"attempt-"+attempt.GetProperty("Run").GetInt32());
            var resolved=Read<C360ElectronicSetup>(Path.Combine(folder,"Bluetooth-acquisition.json"));
            string currentKey=C360Reference.ConfigurationKey(model,plan,C360ElectronicStage.Bluetooth,mixer,resolved);
            Require(key==null || key==currentKey,"Recorded BT route changed between captures"); key=currentKey;
            var measured=Read<StandardAcousticResult>(Path.Combine(folder,"Bluetooth-result.json"));
            Require(measured.Validity!="INVALID" && !measured.IsClipped && !MeasurementSignalStatus.MissingStimulus(measured),"Recorded BT capture is invalid");
            string status=MeasurementCriteria.ThdVerdict(new[]{80d,1000d,4000d}.Select(f=>runner.BuildSweepDistortionSummary(measured,f)).ToArray(),test);
            Require(status=="PASS","Recorded BT THD no longer meets the configured criteria");
            captures.Add(new(C360ElectronicStage.Bluetooth,status,attempt.GetProperty("Summary").GetString()!,measured));
        }
        var reference=C360Reference.Create(key!,captures,test);
        Require(reference.IsUsable,"Recorded BT pair is not a usable reference");
        Directory.CreateDirectory(output);
        AtomicFile.WriteAllText(Path.Combine(output,"c360-"+key+".json"),JsonSerializer.Serialize(reference,C360Reference.Json));
        File.WriteAllText(Path.Combine(output,"qualification.json"),JsonSerializer.Serialize(new { session,key,lineKey,reference.Runs,
            sameConfigurationAndBaseline=true,passesRecomputed=true,repeatabilityLimitDb=3,newPlaybackOrPairing=false },C360Reference.Json));
        Console.WriteLine("PASS recorded BT pair qualified with unchanged THD/repeatability gates and verified LINE baseline identity. No new playback or pairing.");
    }
}
