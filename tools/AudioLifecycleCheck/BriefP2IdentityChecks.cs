using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using SoncaAudioInspector;

internal static class BriefP2IdentityChecks
{
    public static int Run(string configPath, string baselineAssembly, string output)
    {
        var config = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!;
        C360ModelDefinition.EnsureSeed(config);
        Assembly? baseline = null;
        if (!string.IsNullOrEmpty(baselineAssembly))
        {
            string path = Path.GetFullPath(baselineAssembly), folder = Path.GetDirectoryName(path)!;
            var context = new AssemblyLoadContext("P1-identity", isCollectible: true);
            context.Resolving += (_, name) => File.Exists(Path.Combine(folder, name.Name + ".dll"))
                ? context.LoadFromAssemblyPath(Path.Combine(folder, name.Name + ".dll")) : null;
            baseline = context.LoadFromAssemblyPath(path);
        }
        var lines = new List<string>(); int count = 0;
        void Equal(string expected, string actual, string why)
        {
            if (expected != actual) throw new Exception(why + ": " + expected + " != " + actual);
            count++; lines.Add(why + " = " + actual);
        }
        foreach (var model in config.models)
        {
            var io = model.testItems.InOut;
            var original = JsonSerializer.Serialize(io);
            var acoustic = io.Tests.Select(t => Fingerprint(typeof(AudioRouting).Assembly, io, t)).ToArray();
            var setup = new C360ElectronicSetup("mock-out", "mock-usb", "mock-in") { BluetoothPlaybackId = "mock-bt" };
            var plan = new C360ModelPlan(io, setup, []);
            var stages = io.Tests.Where(t => t.IsEnabled && Enum.TryParse<C360ElectronicStage>(t.SpeakerStage, out _)).ToArray();
            var keys = stages.ToDictionary(t => t.id, t => C360Reference.ConfigurationKey(model.model, plan, Enum.Parse<C360ElectronicStage>(t.SpeakerStage!), "offline-mixer", setup));
            if (baseline != null)
            {
                object oldIo = JsonSerializer.Deserialize(original, baseline.GetType("SoncaAudioInspector.InOutConfig")!)!;
                var oldTests = ((System.Collections.IEnumerable)oldIo.GetType().GetProperty("Tests")!.GetValue(oldIo)!).Cast<object>().ToArray();
                for (int i = 0; i < io.Tests.Count; i++)
                {
                    object oldTest = oldTests[i];
                    if (io.Tests[i].ReferenceTestId is string reference && reference != io.Tests[i].id)
                    {
                        Equal(Fingerprint(baseline, oldIo, oldTest, true), Fingerprint(typeof(AudioRouting).Assembly, io, io.Tests[i], true), model.model + "/" + io.Tests[i].id + " acoustic legacy display-id fallback");
                        oldTest = JsonSerializer.Deserialize(JsonSerializer.Serialize(oldTest, oldTest.GetType()), oldTest.GetType())!;
                        oldTest.GetType().GetProperty("id")!.SetValue(oldTest, reference);
                    }
                    Equal(Fingerprint(baseline, oldIo, oldTest), acoustic[i], model.model + "/" + io.Tests[i].id + " acoustic primary reference identity");
                }
                Type oldStage = baseline.GetType("SoncaAudioInspector.C360ElectronicStage")!;
                object oldSetup = JsonSerializer.Deserialize(JsonSerializer.Serialize(setup), baseline.GetType("SoncaAudioInspector.C360ElectronicSetup")!)!;
                object oldPlan = Activator.CreateInstance(baseline.GetType("SoncaAudioInspector.C360ModelPlan")!, oldIo, oldSetup, Array.CreateInstance(oldStage, 0))!;
                foreach (var test in stages)
                {
                    string key = (string)baseline.GetType("SoncaAudioInspector.C360Reference")!.GetMethod("ConfigurationKey")!
                        .Invoke(null, [model.model, oldPlan, Enum.Parse(oldStage, test.SpeakerStage!), "offline-mixer", oldSetup])!;
                    Equal(key, keys[test.id], model.model + "/" + test.id + " C360 P1/P2 absent");
                }
            }
            foreach (bool enabled in new[] { false, true })
            {
                foreach (var test in io.Tests)
                {
                    test.DipWindowsEnabled = enabled;
                    test.DipWindows = [new() { MinHz = enabled ? 100 : 56, MaxHz = enabled ? 120 : 75, Mode = enabled ? "Exclude" : null, Note = "identity only" }];
                }
                for (int i = 0; i < io.Tests.Count; i++) Equal(acoustic[i], Fingerprint(typeof(AudioRouting).Assembly, io, io.Tests[i]), model.model + "/" + io.Tests[i].id + " acoustic windows " + enabled);
                foreach (var test in stages) Equal(keys[test.id], C360Reference.ConfigurationKey(model.model, plan, Enum.Parse<C360ElectronicStage>(test.SpeakerStage!), "offline-mixer", setup), model.model + "/" + test.id + " C360 windows " + enabled);
            }
            if (stages.Length == 0) lines.Add(model.model + ": C360 key N/A (no SpeakerStage); all acoustic fingerprints verified.");
        }
        File.WriteAllLines(Path.Combine(output, "identity-checks.txt"), lines.Prepend($"PASS {count} identity assertions across {config.models.Count} models; P1 binary comparison: {baseline != null}."));
        if (config.models.Count != 3) throw new Exception("Expected the three checked-in models");
        return count;
    }

    private static string Fingerprint(Assembly assembly, object io, object test, bool useCurrentDisplayId = false)
    {
        Type routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        object routing = RuntimeHelpers.GetUninitializedObject(routingType);
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        routingType.GetField("_appliedTestConfig", hidden)!.SetValue(routing, test);
        routingType.GetField("_activeInOutConfig", hidden)!.SetValue(routing, io);
        var fingerprint = routingType.GetMethod("AppliedModelTestFingerprint", hidden)!;
        return (string)fingerprint.Invoke(routing, fingerprint.GetParameters().Length == 0 ? null : new object[] { useCurrentDisplayId })!;
    }
}
