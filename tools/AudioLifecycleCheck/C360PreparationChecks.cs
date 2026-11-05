using System.Reflection;
using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

internal static class C360PreparationChecks
{
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        var definition = C360ModelDefinition.Create(); var io = definition.testItems.InOut;
        var endpoints = io.Devices.Input.Select(p => new C360ModelEndpoint(p.Key, p.Value, false))
            .Concat(io.Devices.Output.Select(p => new C360ModelEndpoint(p.Key, p.Value, true))).ToArray();
        var plan = C360ModelPlan.Create(io, endpoints, new("a", "u", "c"));
        var bench = new PreparedBench();
        var pipeline = new C360ReferenceBench(bench, plan, "C360", output, "mock", false, 3);
        var setup = plan.SetupFor(io.Tests.Single(t => t.SpeakerStage == "UsbA"));
        await pipeline.CaptureAsync(C360ElectronicStage.UsbA, setup, default);
        await pipeline.CaptureAsync(C360ElectronicStage.UsbA, setup, default);
        Check(bench.Preparations == 2 && bench.PreparedCaptures == 2 && bench.NormalCaptures == 0,
            "Reference/production prepared a route twice per capture or reused it across attempts");

        var proofType = typeof(C360AudioBench).Assembly.GetType("SoncaAudioInspector.C360PlaybackPreparation")!;
        var proof = Activator.CreateInstance(proofType)!;
        void Remember() => proofType.GetMethod("Remember")!.Invoke(proof,new object[]{C360ElectronicStage.UsbA,setup,7});
        bool Consume(C360ElectronicStage stage, C360ElectronicSetup value, int generation, bool connected)
            => (bool)proofType.GetMethod("TryConsume")!.Invoke(proof,new object[]{stage,value,generation,connected})!;
        Remember(); Check(Consume(C360ElectronicStage.UsbA,setup,7,true),"Fresh preparation rejected");
        Check(!Consume(C360ElectronicStage.UsbA,setup,7,true),"Preparation was used for a second capture");
        Remember(); Check(!Consume(C360ElectronicStage.LineIn,setup,7,true),"Preparation crossed stage");
        Remember(); Check(!Consume(C360ElectronicStage.UsbA,setup with {UsbPlaybackId="another-output"},7,true),"Preparation crossed endpoint/setup");
        Remember(); Check(!Consume(C360ElectronicStage.UsbA,setup,8,true),"Preparation crossed BLE reconnection");
        Remember(); Check(!Consume(C360ElectronicStage.UsbA,setup,7,false),"Disconnected preparation accepted");
        File.WriteAllText(Path.Combine(output,"preparation-counts.json"),JsonSerializer.Serialize(new {
            captures=2, previousPreparationsPerCapture=2, preparationsPerCapture=bench.Preparations/2d,
            bench.Preparations, bench.PreparedCaptures, synthetic=true, noRadioOrAudioOpened=true },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS route preparation once per capture; no reuse across attempts, changed endpoint, stage, BLE generation or disconnect. Mock reference pipeline; no hardware opened.");
    }
    private sealed class PreparedBench : IC360ElectronicBench, IC360AcquisitionPreparation
    {
        public int Preparations, NormalCaptures, PreparedCaptures;
        private C360ElectronicSetup? _prepared;
        public string DeviceIdentity => "fixture";
        public Task<IReadOnlyDictionary<ushort,ushort>> ReadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task WriteAsync(ushort register,ushort value,CancellationToken token) => throw new NotSupportedException();
        public void StopAudio() { }
        public Task<C360ElectronicSetup> PrepareAsync(C360ElectronicStage stage,C360ElectronicSetup setup,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Preparations++; return Task.FromResult(_prepared=setup with{UsbPlaybackId="resolved-usb"}); }
        public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage,C360ElectronicSetup setup,CancellationToken token)
        { NormalCaptures++; return await CapturePreparedAsync(stage,await PrepareAsync(stage,setup,token),token); }
        public Task<C360ElectronicResult> CapturePreparedAsync(C360ElectronicStage stage,C360ElectronicSetup setup,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!ReferenceEquals(_prepared,setup)) throw new Exception("Capture was not prepared");
            _prepared=null; PreparedCaptures++; return Task.FromResult(new C360ElectronicResult(stage,"PASS","fixture"));
        }
    }
}
