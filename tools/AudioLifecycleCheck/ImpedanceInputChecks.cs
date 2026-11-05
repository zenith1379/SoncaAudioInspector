using System.IO;
using System.Reflection;
using System.Text.Json;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class ImpedanceInputChecks
{
    // Metadata/context check only: never starts capture or plays a stimulus.
    public static void Run(string output)
    {
        using var devices=new MMDeviceEnumerator();
        using var playback=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        var method=typeof(AudioRouting).GetMethod("ImpedanceContext",BindingFlags.Static|BindingFlags.NonPublic)!;
        var rows=new List<object>();
        foreach(var input in devices.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active)) using(input)
        {
            using var client=input.AudioClient;
            int channels=client.MixFormat.Channels, rate=client.MixFormat.SampleRate;
            string? error=null; bool accepted=false;
            try { accepted=method.Invoke(null,new object[]{playback.ID,input.ID,false,new ImpedanceSettings()}) is string; }
            catch(TargetInvocationException ex) { error=ex.InnerException?.Message ?? ex.Message; }
            rows.Add(new {name=input.FriendlyName,id=input.ID,channels,rate,contextAccepted=accepted,error,
                independentAnalogChannelsVerified=false,calibrationPerformed=false});
        }
        var result=new {utc=DateTimeOffset.UtcNow,playback=playback.FriendlyName,readOnly=true,playbackStarted=false,captureStarted=false,inputs=rows};
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"input-format.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
