using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;

internal static class ResponseDbLabelsChecks
{
    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        Assembly plotting = Assembly.Load("ScottPlot");
        var reports = new List<object>();
        double Render(string name, int height, double bottom, double top)
        {
            dynamic plot = Activator.CreateInstance(plotting.GetType("ScottPlot.Plot")!)!;
            dynamic generator = Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ResponseDbTickGenerator")!)!;
            plot.Axes.Left.TickGenerator = generator;
            plot.Axes.SetLimits(Math.Log10(20), Math.Log10(20000), bottom, top);
            plot.Axes.Left.Label.Text = "Mức thu RMS (dBFS)";
            plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
            // The same response, limits and 1 dB grid are kept while changing available text space.
            double[] frequency = Enumerable.Range(0,401).Select(i => 20*Math.Pow(1000,i/400d)).ToArray();
            double[] x = frequency.Select(Math.Log10).ToArray();
            double[] y = frequency.Select(f => -6-14*Math.Pow(f/20000,3.4)).ToArray();
            dynamic curve = plot.Add.Scatter(x,y); curve.MarkerSize=0;
            plot.SavePng(Path.Combine(output,name+".png"),1200,height);
            var ticks = ((IEnumerable)generator.Ticks).Cast<object>().Select(t =>
                (Position:(double)t.GetType().GetField("Position")!.GetValue(t)!, Label:(string)t.GetType().GetField("Label")!.GetValue(t)!,
                 Major:(bool)t.GetType().GetField("IsMajor")!.GetValue(t)!))
                .Where(t=>t.Major && t.Position>=bottom && t.Position<=top).OrderBy(t=>t.Position).ToArray();
            if(ticks.Length<2 || ticks.Zip(ticks.Skip(1)).Any(p=>Math.Abs(p.Second.Position-p.First.Position-1)>.001))
                throw new Exception("dB grid positions/major grid lines changed while thinning labels: " + JsonSerializer.Serialize(ticks.Select(t => new {t.Position,t.Label,t.Major})));
            var limits=plot.Axes.GetLimits();
            if(Math.Abs((double)limits.Bottom-bottom)>.001 || Math.Abs((double)limits.Top-top)>.001)
                throw new Exception("Label spacing moved the graph viewport");
            double interval=generator.LabelInterval;
            reports.Add(new {name,height,bottom,top,gridInterval=1,labelInterval=interval,gridTicks=ticks.Length,labels=ticks.Count(t=>t.Label.Length>0)});
            return interval;
        }
        double shortWide=Render("wide-short",360,-80,0);
        double tallWide=Render("wide-tall",900,-80,0);
        double flat=Render("flat",600,-24,-4);
        if(shortWide<=1 || tallWide>=shortWide || flat!=1) throw new Exception("Label density did not adapt to height and dB span");
        File.WriteAllText(Path.Combine(output,"labels.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS adaptive dB text: wide compact chart thins labels, taller chart restores more labels, flat chart retains 1 dB labels; 1 dB grid and axis limits unchanged.");
    }
}
