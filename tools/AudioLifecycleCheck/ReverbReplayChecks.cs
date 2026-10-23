using System.IO;
using System.Reflection;
using System.Text.Json;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class ReverbReplayChecks
{
    public static void Run(string directory, string output)
    {
        Directory.CreateDirectory(output);
        int rate = 0;
        float[] Read(string name)
        {
            using var reader = new WaveFileReader(Path.Combine(directory, name)); rate = reader.WaveFormat.SampleRate;
            var values = new List<float>(); float[]? frame; while ((frame = reader.ReadNextSampleFrame()) != null) values.Add(frame[0]); return values.ToArray();
        }
        var dry = Read("reverb-dry.wav"); int dryRate = rate; var wet = Read("reverb-wet.wav");
        double preRoll = 0; string metadata = Path.Combine(directory,"reverb-attempt.json");
        ReverbProbeBand[]? probeBands = null;
        if (File.Exists(metadata)) using (var saved = JsonDocument.Parse(File.ReadAllText(metadata)))
        {
            if (saved.RootElement.TryGetProperty("PreRollSeconds",out var value)) preRoll=value.GetDouble();
            if (saved.RootElement.TryGetProperty("ProbeBands",out var bands) && bands.GetArrayLength()>0)
                probeBands = bands.Deserialize<ReverbProbeBand[]>();
        }
        if (rate != dryRate) throw new Exception("Rate mismatch");
        object Stats(float[] samples) => new { samples = samples.Length, seconds = samples.Length/(double)rate,
            peak = samples.Select(Math.Abs).DefaultIfEmpty().Max(), clipped = samples.Count(v=>Math.Abs(v)>=.999f),
            clippedMs = samples.Select((v,i)=>(v,i)).Where(p=>Math.Abs(p.v)>=.999f).Take(12).Select(p=>1000*p.i/(double)rate).ToArray(),
            envelopeDbFs = Enumerable.Range(0,samples.Length/(rate/10)).Select(b=>10*Math.Log10(samples.Skip(b*(rate/10)).Take(rate/10).Select(v=>(double)v*v).Average()+1e-30)).ToArray(),
            rmsDbFs = 10*Math.Log10(samples.Select(v=>(double)v*v).DefaultIfEmpty().Average()+1e-30) };
        var stops = new List<object>();
        foreach (var band in new[] { (500.0,2000.0), (2000.0,8000.0) })
        {
            var type = typeof(ReverbDecayDiagnostics);
            var filter = type.GetMethod("Filter", BindingFlags.NonPublic|BindingFlags.Static)!;
            var stop = type.GetMethod("Stop", BindingFlags.NonPublic|BindingFlags.Static);
            if (stop == null) continue;
            foreach (var pair in new[] { ("dry",dry), ("wet",wet) })
            {
                var filtered = (double[])filter.Invoke(null,new object[] {pair.Item2,rate,band.Item1,band.Item2})!;
                double start = probeBands?.Single(b=>b.LowHz==band.Item1).StartSeconds ?? preRoll;
                stops.Add(new { capture=pair.Item1, band, stopMs=1000*(int)stop.Invoke(null,stop.GetParameters().Length==3 ? new object[]{filtered,rate,start} : new object[]{filtered,rate})!/(double)rate });
            }
        }
        var result = ReverbDecayDiagnostics.Analyze(dry,wet,rate,preRoll,probeBands);
        File.WriteAllText(Path.Combine(output,"replay.json"),JsonSerializer.Serialize(new {directory,rate,dry=Stats(dry),wet=Stats(wet),stops,result},C360Reference.Json));
        Console.WriteLine(JsonSerializer.Serialize(new {directory,dry=Stats(dry),wet=Stats(wet),stops,result},C360Reference.Json));
    }
}
