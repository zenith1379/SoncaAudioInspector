using System.Text.Json;
using SoncaAudioInspector;

internal static class C360AcousticChecks
{
    public static void Run()
    {
        void Check(bool value, string why) { if (!value) throw new Exception(why); }
        void Reject(Action action, string why) { try { action(); } catch { return; } throw new Exception(why); }
        var model = C360ModelDefinition.Create(); var io = model.testItems.InOut;
        var acoustic = io.Tests.Single(t => t.SpeakerStage == "AcousticMic");
        Check(acoustic.id == "IO006" && !acoustic.IsEnabled && acoustic.AcousticSetupPending, "Acoustic stage must start as a disabled draft");
        acoustic.Enable = true;
        Reject(() => C360ModelDefinition.Validate(io), "Provisional microphone setup was allowed to run");
        acoustic.AcousticSetupPending = false;
        var bytes = System.Text.Encoding.UTF8.GetBytes("20 2\n1000 0\n20000 -3\n");
        acoustic.MicrophoneCalibration = new("real-mic.txt", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), Convert.ToBase64String(bytes));
        var copy = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(io))!;
        var attachment = copy.Tests.Single(t => t.id == acoustic.id).MicrophoneCalibration!;
        Check(attachment.Resolve().GetGainCorrectionDb(20000) == -3, "Model round trip lost calibration data");
        Reject(() => (attachment with { Sha256 = new string('0', 64) }).Resolve(), "Damaged attachment accepted");
        var endpoints = new[] { new C360ModelEndpoint("mic-out", io.Devices.Input["MIC IN"], false),
            new C360ModelEndpoint("line", io.Devices.Input["LINE IN"], false), new C360ModelEndpoint("usb", "CS3600 MUSIC", false),
            new C360ModelEndpoint("line-out", io.Devices.Output["LINE OUT"], true), new C360ModelEndpoint("physical-mic", io.Devices.Output[acoustic.RecordingIn], true) };
        var plan = C360ModelPlan.Create(io, endpoints, new("default", "default-usb", "default-capture"));
        Check(plan.SetupFor(acoustic).LineOutCaptureId == "physical-mic"
            && plan.SetupFor(io.Tests.Single(t => t.SpeakerStage == "LineIn")).LineOutCaptureId == "line-out", "Acoustic capture replaced the electronic LINE OUT capture");
        string key = C360Reference.ConfigurationKey("C360", plan, C360ElectronicStage.AcousticMic);
        acoustic.MicrophoneCalibration = null;
        Check(C360Reference.ConfigurationKey("C360", C360ModelPlan.Create(io, endpoints, plan.Setup), C360ElectronicStage.AcousticMic) != key, "Changing calibration reused an incompatible reference");
        io.Devices.Output[acoustic.RecordingIn] = io.Devices.Output["LINE OUT"];
        Reject(() => C360ModelDefinition.Validate(io), "LINE OUT accepted as the real measurement microphone");
        Console.WriteLine("PASS acoustic draft gate; calibration JSON round trip/tamper rejection; independent physical-mic capture; reference invalidation. No hardware opened.");
    }
}
