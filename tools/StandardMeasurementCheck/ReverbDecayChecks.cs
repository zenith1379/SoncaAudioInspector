using SoncaAudioInspector;

internal static class ReverbDecayChecks
{
    public static void Run()
    {
        foreach (int rate in new[] { 44100, 48000 })
        foreach (double latency in new[] { 0.0, .19 })
        {
            var signal = ReverbDecayDiagnostics.Generate(rate, -12);
            if (signal.Max(Math.Abs) > Math.Pow(10, -12.0 / 20)) throw new Exception("Peak exceeded");
            float[] Capture(int seed, bool reverb, bool constant = false, bool tone = false)
            {
                var random = new Random(seed); var result = new float[rate * 4]; int start = (int)(latency * rate);
                for (int i = 0; i < result.Length; i++) result[i] = (float)((random.NextDouble() * 2 - 1) * 1e-5);
                for (int i = 0; i < signal.Length; i++) result[start + i] += signal[i];
                if (reverb) for (int i = start + signal.Length; i < result.Length; i++)
                {
                    double t = (i - start - signal.Length) / (double)rate;
                    double value = tone ? Math.Sin(2 * Math.PI * 2000 * t) : random.NextDouble() * 2 - 1;
                    result[i] += (float)(.03 * value * (constant ? t < 1.5 ? 1 : 0 : Math.Exp(-Math.Log(1000) * t / 1.2)));
                }
                return result;
            }
            var dry = Capture(1, false); var wet = Capture(2, true);
            var result = ReverbDecayDiagnostics.Analyze(dry, wet, rate);
            if (!result.IsValid || !result.Detected) throw new Exception("Diffuse wet decay missed: " + System.Text.Json.JsonSerializer.Serialize(result));
            if (ReverbDecayDiagnostics.Analyze(dry, Capture(3, false), rate) is not { IsValid: true, Detected: false }) throw new Exception("Dry control falsely called reverb");
            if (ReverbDecayDiagnostics.Analyze(dry, Capture(4, true, constant: true), rate).Detected) throw new Exception("Stationary background called reverb");
            if (ReverbDecayDiagnostics.Analyze(dry, Capture(5, true, tone: true), rate).Detected) throw new Exception("Single ring called diffuse reverb");
            if (ReverbDecayDiagnostics.Analyze(new float[rate * 4], new float[rate * 4], rate).IsValid) throw new Exception("Silence allowed absence diagnosis");
            if (ReverbDecayDiagnostics.Analyze(dry, Enumerable.Repeat(1f, rate * 4).ToArray(), rate).IsValid) throw new Exception("Clipping allowed diagnosis");
            if (rate == 44100 && latency == 0)
            {
                var clipped = (float[])wet.Clone(); clipped[100] = 1;
                int calls = 0; var levels = new List<double>();
                var recovered = ReverbDecayDiagnostics.VerifyAsync(-12, (attempt, level, _) =>
                { calls++; levels.Add(level); return Task.FromResult(ReverbDecayDiagnostics.Analyze(dry, attempt == 1 ? clipped : wet, rate)); }).GetAwaiter().GetResult();
                if (!recovered.Detected || calls != 2 || recovered.Attempts != 2 || !levels.SequenceEqual(new[] {-12.0,-15.0})
                    || recovered.PreviousAttempts.Single().FailureCode != "CLIPPING") throw new Exception("Clipped probe retry did not preserve evidence/lower both-waveform level");
                calls = 0;
                var absent = ReverbDecayDiagnostics.VerifyAsync(-12, (_, _, _) => { calls++; return Task.FromResult(ReverbDecayDiagnostics.Analyze(dry, Capture(8,false),rate)); }).GetAwaiter().GetResult();
                if (!absent.IsValid || absent.Detected || calls != 1) throw new Exception("Valid negative control retried or turned positive");
                calls = 0;
                var unknown = ReverbDecayDiagnostics.VerifyAsync(-60, (_, level, _) =>
                { calls++; if (level != -60) throw new Exception("Retry exceeded configured output floor"); return Task.FromResult(ReverbDecayDiagnostics.Analyze(dry,clipped,rate)); }).GetAwaiter().GetResult();
                if (unknown.IsValid || unknown.Detected || calls != 2) throw new Exception("Repeated clipping accepted or retry count unbounded");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); calls = 0;
                bool stopped = false;
                try { ReverbDecayDiagnostics.VerifyAsync(-12, (_, _, _) => { calls++; return Task.FromResult(result); }, cancelled.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { stopped = true; }
                if (!stopped || calls != 0) throw new Exception("Cancelled probe emitted another attempt");
                var partial = new ImpulseEffectResult(true,true,false,200,new[] {new EchoTap(200,-10.0),new EchoTap(400,-16.0)},-10,-20,0,double.NaN,double.NaN,-30,3,false,"INCONCLUSIVE",Array.Empty<string>()) {ReverbVerification=unknown};
                if (partial.CanClassify(new()) || !partial.Describe().Contains("chạm mức thu") || !partial.Describe().Contains("2 cặp")) throw new Exception("Unknown reverb lost quality cause or was classified as missing");
                int pad = (int)(rate*.35);
                float[] Pad(float[] source) { var values = new float[source.Length+pad]; Array.Copy(source,0,values,pad,source.Length); values[rate/50]=1; return values; }
                var paddedDry = Pad(dry); var paddedWet = Pad(wet);
                var settled = ReverbDecayDiagnostics.Analyze(paddedDry,paddedWet,rate,.35);
                if (!settled.IsValid || !settled.Detected || settled.StartupClippedSamplesIgnored!=2) throw new Exception("Known silent pre-roll transient invalidated a clean measured burst/tail");
                paddedWet[pad+rate/10]=1;
                if (ReverbDecayDiagnostics.Analyze(paddedDry,paddedWet,rate,.35).FailureCode!="CLIPPING") throw new Exception("Clipping during actual excitation was hidden by startup settling");
                var generated = ReverbDecayDiagnostics.Generate(rate,-12,.35);
                if (generated.Take(pad).Any(v=>v!=0) || generated.Skip(pad).Take(rate/100).Max(Math.Abs)>.01) throw new Exception("Silent pre-roll or gradual excitation start missing");
                var noisyDry = Capture(9,false); var noisyWet=Capture(10,false); var extraNoise=new Random(324);
                for(int i=0;i<noisyDry.Length;i++){noisyDry[i]+=(float)((extraNoise.NextDouble()*2-1)*.01);noisyWet[i]+=(float)((extraNoise.NextDouble()*2-1)*.01);}
                var uncertainDry = ReverbDecayDiagnostics.Analyze(noisyDry,noisyWet,rate);
                if (uncertainDry.IsValid || uncertainDry.Detected || !uncertainDry.Summary.Contains("35 dB")) throw new Exception("Low-SNR dry control falsely diagnosed absence or explained a missing positive tail");
            }
        }
        ReverbBandProbeChecks.Run();
        Console.WriteLine("PASS interrupted-noise decay: wet/dry, two rates, latency, stationary background, narrow ringing, silence, clipping, peak level, clipped-probe recovery at -3 dB, valid-negative no retry, bounded failure/floor, cancellation and specific unknown reason. Synthetic only.");
    }
}
