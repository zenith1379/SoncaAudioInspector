using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using SoncaAudioInspector;

internal static class ReverbBandProbeChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("Invalid band probe accepted");
    }
    public static void Run()
    {
        foreach (int rate in new[] { 44100, 48000 })
        {
            var previous = new ReverbDecayResult(false, false, "Low SNR", new[] {
                new ReverbDecayBand(500,2000,11,0,8,0,0,0,false) { PeakLevelDbFs=-30 },
                new ReverbDecayBand(2000,8000,25,8,18,500,3,.68,false) { PeakLevelDbFs=-12 }
            }) { FailureCode="LOW_TAIL_QUALITY" };
            var bands = ReverbDecayDiagnostics.PlanBandRetry(previous,-12,rate,.35,3) ?? throw new Exception("Valid band plan rejected");
            Check(bands.All(b => b.LevelDbFs is >= -60 and <= -6 && b.PredictedPeakLevelDbFs <= -6 + 1e-9), "Band retry exceeded source/capture headroom");
            Check(bands[0].SourceBandGainDb > bands[1].SourceBandGainDb, "Weak band did not receive available headroom");
            var closeToClip = previous with { Bands=previous.Bands.Select(b=>b with {PeakLevelDbFs=-2}).ToArray() };
            Check(ReverbDecayDiagnostics.PlanBandRetry(closeToClip,-12,rate,.35,3) is { } quietPlan && quietPlan.All(b=>b.SourceBandGainDb <= 1e-9 && b.PredictedPeakLevelDbFs<=-6+1e-8), "Near-clipping capture received extra band power");
            var noPeak = previous with { Bands=previous.Bands.Select(b=>b with {PeakLevelDbFs=null}).ToArray() };
            Check(ReverbDecayDiagnostics.PlanBandRetry(noPeak,-12,rate,.35,3) is { } conservative && conservative.All(b=>b.SourceBandGainDb <= 1e-9), "Missing peak evidence allowed an increase");
            Check(ReverbDecayDiagnostics.PlanBandRetry(closeToClip,-60,rate,.35,3)==null, "Source floor allowed an unsafe concentration gain");
            var source = ReverbDecayDiagnostics.GenerateBandSequence(rate,bands);
            Check(source.SequenceEqual(ReverbDecayDiagnostics.GenerateBandSequence(rate,bands)), "Dry/wet excitation is not identical");
            Check(source.Take((int)(rate*.35)).All(v=>v==0), "Band pre-roll emitted audio");
            foreach (var band in bands)
            {
                var segment = source.Skip((int)Math.Round(rate*band.StartSeconds)).Take(rate).ToArray();
                Check(segment.Max(Math.Abs) <= Math.Pow(10,band.LevelDbFs/20)+1e-7, "Band excitation exceeded its peak ceiling");
                var spectrum = segment.Select(v=>new Complex(v,0)).ToArray(); Fourier.Forward(spectrum,FourierOptions.Matlab);
                double inBand=0, otherBand=0;
                for(int i=0;i<spectrum.Length/2;i++)
                {
                    double hz=i*rate/(double)spectrum.Length, power=spectrum[i].Magnitude*spectrum[i].Magnitude;
                    if(hz>=band.LowHz && hz<band.HighHz) inBand+=power;
                    if(band.LowHz==500 ? hz>=2000 && hz<8000 : hz>=500 && hz<2000) otherBand+=power;
                }
                Check(10*Math.Log10(inBand/Math.Max(1e-30,otherBand))>=8, "Excitation bands are not sufficiently isolated");
            }
            int gapStart=(int)Math.Round(rate*(bands[0].StartSeconds+1));
            int gapEnd=(int)Math.Round(rate*bands[1].StartSeconds);
            Check(source.Skip(gapStart).Take(gapEnd-gapStart).All(v=>v==0), "Inter-band decay gap emitted audio");
            foreach(double latency in new[]{0.0,.19})
            {
                float[] dry=Capture(source,rate,latency,false,1), wet=Capture(source,rate,latency,true,2);
                var result=ReverbDecayDiagnostics.Analyze(dry,wet,rate,.35,bands);
                Check(result.IsValid && result.Detected, "Two-band diffuse convolution missed: "+System.Text.Json.JsonSerializer.Serialize(result));
                var absent=ReverbDecayDiagnostics.Analyze(dry,Capture(source,rate,latency,false,3),rate,.35,bands);
                Check(absent.IsValid && !absent.Detected, "Two-band dry convolution called reverb");
                if(latency==0)
                {
                    var ring=ReverbDecayDiagnostics.Analyze(dry,Capture(source,rate,latency,true,5,narrow:true),rate,.35,bands);
                    Check(!ring.Detected,"A narrow decaying ring was called diffuse reverb by band protocol");
                    var constant=Capture(source,rate,latency,false,6); var stationary=new Random(100);
                    for(int i=(int)(rate*(bands[0].StartSeconds+1));i<constant.Length;i++) constant[i]+=(float)((stationary.NextDouble()*2-1)*.01);
                    Check(!ReverbDecayDiagnostics.Analyze(dry,constant,rate,.35,bands).Detected,"Stationary background was called reverb by band protocol");
                }
                var clipped=(float[])wet.Clone(); clipped[(int)(rate*(bands[1].StartSeconds+.5))]=1;
                Check(ReverbDecayDiagnostics.Analyze(dry,clipped,rate,.35,bands).FailureCode=="CLIPPING", "Second-band clipping was hidden");
                Check(!ReverbDecayDiagnostics.Analyze(dry,wet.Take(rate*4).ToArray(),rate,.35,bands).IsValid, "Truncated second-band capture accepted");
                var noisy=(float[])wet.Clone();var random=new Random(7);
                for(int i=0;i<noisy.Length;i++) noisy[i]+=(float)((random.NextDouble()*2-1)*.15);
                Check(!ReverbDecayDiagnostics.Analyze(dry,noisy,rate,.35,bands).IsValid, "Noisy band sequence converted to a qualified diagnosis");
            }
            Reject(()=>ReverbDecayDiagnostics.GenerateBandSequence(rate,new[]{bands[0],bands[1] with {StartSeconds=1}}));
            Reject(()=>ReverbDecayDiagnostics.GenerateBandSequence(rate,new[]{bands[0],bands[1] with {LevelDbFs=double.NaN}}));
            Reject(()=>ReverbDecayDiagnostics.PlanBandRetry(previous,-12,rate,.35,double.PositiveInfinity));
            Reject(()=>ReverbDecayDiagnostics.PlanBandRetry(previous with {Bands=Array.Empty<ReverbDecayBand>()},-12,rate,.35,3));
        }
        Console.WriteLine("PASS band-isolated probe: independent diffuse convolution/dry controls, two rates/latencies, waveform peak and spectral isolation, headroom including concentration, missing peak conservative, deterministic pair, silent gap, clipping, truncation, noise and invalid plan rejection. Synthetic only; no device opened.");
    }
    private static float[] Capture(float[] source,int rate,double latency,bool reverb,int seed,bool narrow=false)
    {
        int offset=(int)Math.Round(rate*latency), size=1;
        var random=new Random(666); var impulse=new double[rate*2]; impulse[0]=1;
        if(reverb) for(int i=rate/50;i<impulse.Length;i++)
            impulse[i]=(narrow ? Math.Sin(2*Math.PI*2000*i/rate) : random.NextDouble()*2-1)*.5/Math.Sqrt(rate)*Math.Exp(-Math.Log(1000)*i/rate/1.2);
        while(size<source.Length+impulse.Length) size*=2;
        var x=new Complex[size]; var h=new Complex[size];
        for(int i=0;i<source.Length;i++)x[i]=source[i];
        for(int i=0;i<impulse.Length;i++)h[i]=impulse[i];
        Fourier.Forward(x,FourierOptions.Matlab);Fourier.Forward(h,FourierOptions.Matlab);
        for(int i=0;i<size;i++)x[i]*=h[i]; Fourier.Inverse(x,FourierOptions.Matlab);
        var capture=new float[source.Length+rate*3+offset]; random=new Random(seed);
        for(int i=0;i<capture.Length;i++) capture[i]=(float)((random.NextDouble()*2-1)*1e-6+(i>=offset && i-offset<size ? x[i-offset].Real : 0));
        return capture;
    }
}
