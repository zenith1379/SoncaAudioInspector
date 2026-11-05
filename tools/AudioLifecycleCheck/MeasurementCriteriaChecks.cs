using System.Reflection;
using System.Text.Json;
using SoncaAudioInspector;

internal static class MeasurementCriteriaChecks
{
    public static void Run(string fixture)
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid criteria accepted"); }
        var old = JsonSerializer.Deserialize<TestConfig>("{\"THD Limit\":0.7,\"MidThdLimit\":0.4}")!;
        Check(old.FrequencyRange.SequenceEqual(new[] { 50d, 18000d }) && old.Thd1000Hz == .4, "Legacy defaults lost");
        var test = JsonSerializer.Deserialize<TestConfig>("{\"freq_range\":[100,12000],\"THD_1000Hz\":0.2,\"THD Limit\":3,\"MidThdLimit\":5}")!;
        test.ValidateMeasurementCriteria();
        Check(test.Thd1000Hz == .2 && test.EvaluationRange.Contains(100) && !test.EvaluationRange.Contains(50), "Explicit criteria not applied");
        Check(JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(test))!.Thd1000Hz == .2, "Criteria round trip lost");
        foreach (double[] range in new[] { new[] { 50d }, new[] { 12000d, 100d }, new[] { 0d, 18000d }, new[] { 50d, 22000d } })
            Reject(() => new TestConfig { FrequencyRange = range }.ValidateMeasurementCriteria());
        Reject(() => new TestConfig { Thd1000Hz = double.NaN }.ValidateMeasurementCriteria());
        Check(!old.AllowLineOutliers && old.EffectiveLineOutlierPercent == 0,"LINE outlier allowance must default off");
        Check(new LineOutlierAssessment(10,100,10).Passed && !new LineOutlierAssessment(11,100,10).Passed
            && !new LineOutlierAssessment(1,7,14.28).Passed && new LineOutlierAssessment(1,7,14.3).Passed,
            "Outlier quota boundary/denominator was rounded or compared as amplitude percent");
        Check(!new LineOutlierAssessment(0,0,100).Passed && !new LineOutlierAssessment(1,100,100,true).Passed,
            "No data or critical failure was hidden by quota");
        Check(new LineOutlierAssessment(3276,65536,5).Passed && !new LineOutlierAssessment(3277,65536,5).Passed
            && new LineOutlierAssessment(49,997,5).Passed && !new LineOutlierAssessment(50,997,5).Passed,
            "Point quota used a fixed denominator instead of the actual evaluated frequency-point count");
        Reject(()=>new TestConfig{AllowLineOutliers=true,AllowedLineOutlierPercent=101}.ValidateMeasurementCriteria());
        var sweepTest=new TestConfig{SweepLengthSamples=65536};
        Check(sweepTest.ResolveSweepSeconds(44100)*44100==65536 && sweepTest.ResolveSweepSeconds(48000)*48000==65536,
            "64K became seconds or 64 kHz instead of a fixed sample count");
        Check(new TestConfig{LogSweepDurationSeconds=7.2}.ResolveSweepSeconds(48000)==7.2,"Legacy custom sweep duration was lost");
        Reject(()=>new TestConfig{SweepLengthSamples=12345}.ValidateMeasurementCriteria());

        using var engine = new AudioEngine(); var runner = new TestRunner(engine);
        bool GenericQuota(int outside,int total,bool critical=false)=>(bool)typeof(TestRunner)
            .GetMethod("IsResponsePassed",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(runner,new object[]{false,outside,total,critical})!;
        Check(!GenericQuota(1,100),"Generic default silently permitted 10% outliers");
        runner.AllowedFrequencyOutlierPercent=10;
        Check(GenericQuota(10,100)&&!GenericQuota(11,100)&&!GenericQuota(1,100,true),"Generic quota/critical gate differs from model rule");
        var measurement = JsonSerializer.Deserialize<StandardAcousticResult>(System.IO.File.ReadAllText(fixture), C360Reference.Json)!;
        var points = new[] { 80d, 1000d, 4000d }.Select(f => runner.BuildSweepDistortionSummary(measurement, f)).ToArray();
        var good = points[1] with { ThdPercent = .2, IsValid = true };
        var high = points[0] with { ThdPercent = 30, IsValid = true };
        var invalid = points[2] with { ThdPercent = double.NaN, IsValid = false };
        Check(MeasurementCriteria.ThdVerdict(new[] { high, good, invalid }, .5) == "PASS", "80/4000 Hz changed 1k verdict");
        Check(MeasurementCriteria.ThdVerdict(new[] { good with { ThdPercent = .6 }, high, invalid }, .5) == "FAIL", "1k over threshold passed");
        Check(MeasurementCriteria.ThdVerdict(new[] { good with { IsValid = false }, high }, .5) == "INVALID", "Invalid 1k was hidden");
        var optional = new TestConfig { Thd1000Hz=.5, BassThdLimit=1, TrebleThdLimit=.5 };
        Check(!optional.EvaluateThd80 && !optional.EvaluateThd4000 && MeasurementCriteria.ThdVerdict(new[]{high,good,invalid},optional)=="PASS","Optional THD flags enabled by default");
        optional.EvaluateThd80=true;
        Check(MeasurementCriteria.ThdVerdict(new[]{high,good,invalid},optional)=="FAIL","THD_80 true did not gate verdict");
        optional.EvaluateThd80=false; optional.EvaluateThd4000=true;
        Check(MeasurementCriteria.ThdVerdict(new[]{high,good,invalid},optional)=="INVALID","THD_4000 true hid invalid measurement");
        Check(JsonSerializer.Deserialize<TestConfig>("{\"THD_80\":\"True\",\"THD_4000\":true}") is {EvaluateThd80:true,EvaluateThd4000:true},"Boolean flags JSON round trip failed");
        var volume = new ModelTestVolume { BleMasterPercent=100,BleMusicPercent=50,BleMicPercent=50,WindowsPlaybackPercent=75,WindowsRecordingPercent=50 };
        var liveState = new Dictionary<ushort,ushort> { [SoncaBleMixerProfile.MasterVolume]=8160,[SoncaBleMixerProfile.MusicVolume]=4064,[SoncaBleMixerProfile.MicVolume]=4064 };
        Check(volume.Mismatches(liveState).Length==0,"Quantized physical volume did not match model");
        liveState[SoncaBleMixerProfile.MicVolume]=0;
        Check(volume.Mismatches(liveState).Length==1 && volume.Mismatches(liveState,false).Length==0,"MIC isolation confused music route volume guard");
        liveState.Remove(SoncaBleMixerProfile.MasterVolume);
        Check(volume.Mismatches(liveState).Length==2,"Missing live readback was assumed correct");
        Reject(()=>new ModelTestVolume{BleMasterPercent=double.NaN}.Validate());

        var curve = new Dictionary<double,double> { [20]=-20, [50]=-20, [100]=-20, [1000]=-20, [12000]=-20, [18000]=-20, [20000]=-20 };
        var reference = new C360Reference("test", DateTimeOffset.UtcNow, curve, null, null, 2);
        var response = measurement.FrequencyResponse.First();
        var changed = measurement with { FrequencyResponse = curve.Select(p => response with { FrequencyHz=p.Key, LevelDb=p.Value-measurement.ExcitationRmsDbFs+(test.EvaluationRange.Contains(p.Key)?0:15) }).ToArray() };
        var current = new C360ElectronicResult(C360ElectronicStage.LineIn,"PASS","fixture",changed);
        Check(reference.Compare(current,test,3).Status == "PASS", "Out-of-range response changed verdict");
        test.FrequencyRange = [20,20000]; Check(reference.Compare(current,test,3).Status == "FAIL", "Changed range not applied to reference comparison");
        var denseCurve=Enumerable.Range(1,100).ToDictionary(i=>i*100d,_=>-20d);
        var denseReference=new C360Reference("quota",DateTimeOffset.UtcNow,denseCurve,null,null,2);
        StandardAcousticResult Capture(int outside)=>measurement with{FrequencyResponse=denseCurve.Select((p,i)=>response with{
            FrequencyHz=p.Key,LevelDb=p.Value-measurement.ExcitationRmsDbFs+(i<outside?3.1:0)}).ToArray()};
        var quota=new TestConfig{FrequencyRange=[100,10000]};
        C360ElectronicResult Compare(int outside)=>denseReference.Compare(new(C360ElectronicStage.LineIn,"PASS","fixture",Capture(outside)),quota,3);
        Check(Compare(1).Status=="FAIL","Disabled quota let a point outside ±3 dB pass");
        quota.AllowLineOutliers=true;quota.AllowedLineOutlierPercent=10;
        Check(Compare(10).Status=="PASS"&&Compare(11).Status=="FAIL"&&Compare(10).Summary.Contains("10/100"),"C360 quota or count details are wrong");
        quota.AllowedLineOutlierPercent=100;
        var missingCapture=Capture(1);missingCapture=missingCapture with{FrequencyResponse=missingCapture.FrequencyResponse.Skip(1).ToArray()};
        Check(denseReference.Compare(new(C360ElectronicStage.LineIn,"PASS","fixture",missingCapture),quota,3).Status=="INVALID",
            "Quota treated a missing response point as an allowed deviation");
        quota.CriticalZones=new(){new(){MinHz=100,MaxHz=200}};
        Check(Compare(1).Status=="FAIL","Quota hid a critical response failure");

        runner.EvaluationMinFrequencyHz=100; runner.EvaluationMaxFrequencyHz=12000;
        runner.StandardCurve=curve;
        var missing=(double[])typeof(TestRunner).GetMethod("FindMissingFrequencyLimits",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(runner,null)!;
        Check(missing.Length==0,"Missing golden points outside range block evaluation");
        var centers = new[] {20d,50d,1000d,18000d,20000d};
        var full=(Dictionary<double,double>)typeof(TestRunner).GetMethod("BuildSweepComparisonResponse",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(runner,new object[]{measurement.FrequencyResponse,centers})!;
        Check(full.ContainsKey(20) && full.ContainsKey(20000),"Comparison criteria truncated full-band plot data");
        Console.WriteLine("PASS criteria defaults/JSON/validation; 1 kHz THD plus optional THD_80/THD_4000 flags; enabled invalid points cannot pass; quantized/missing BLE volume readback and MIC isolation; out-of-range deviations ignored; full 20–20000 Hz data retained. Fixture replay, no playback.");
    }
}
