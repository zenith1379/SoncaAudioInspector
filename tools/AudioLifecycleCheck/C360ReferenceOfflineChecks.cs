using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SoncaAudioInspector;

/// <summary>Replay stored results and injected failures; never opens audio, BLE or Windows pairing.</summary>
internal static class C360ReferenceOfflineChecks
{
    public static async Task Run(string captures, string output)
    {
        Directory.CreateDirectory(output);
        static void Check(bool value, string why) { if (!value) throw new Exception(why); }
        static string Rejection(Action action)
        {
            try { action(); } catch (InvalidOperationException ex) { return ex.Message; }
            throw new Exception("Unqualified reference was accepted");
        }
        StandardAcousticResult Read(int run, string stage) => JsonSerializer.Deserialize<StandardAcousticResult>(
            File.ReadAllText(Path.Combine(captures, $"reference-run-{run}", stage + "-result.json")), C360Reference.Json)!;
        var usb = Enumerable.Range(1, 3).Select(run => new C360ElectronicResult(C360ElectronicStage.UsbA, "PASS", "stored capture", Read(run, "UsbA"))).ToArray();
        var definition = C360ModelDefinition.Create();
        var io = definition.testItems.InOut;
        var inventory = io.Devices.Input.Select(p => new C360ModelEndpoint(p.Key, p.Value, false))
            .Concat(io.Devices.Output.Select(p => new C360ModelEndpoint(p.Key, p.Value, true))).ToArray();
        var plan = C360ModelPlan.Create(io, inventory, new("analog", "usb", "capture"));
        var usbTest = plan.Configuration.Tests.Single(t => t.SpeakerStage == "UsbA");
        string unstable = Rejection(() => C360Reference.Create("replay", usb[1..], usbTest));
        Check(unstable.Contains("2/408") && unstable.Contains("15499"), "Stored USB rejection lacks actual outlier count/frequency");
        usbTest.AllowLineOutliers = true; usbTest.AllowedLineOutlierPercent = 100;
        Check(Rejection(() => C360Reference.Create("replay", usb[1..], usbTest)) == unstable,
            "Production outlier allowance weakened reference repeatability");
        usbTest.AllowLineOutliers = false;
        double missingFrequency = usb[0].Measurement!.FrequencyResponse.First(p => usbTest.EvaluationRange.Contains(p.FrequencyHz)).FrequencyHz;
        var missing = usb[0] with { Measurement = usb[0].Measurement! with {
            FrequencyResponse = usb[0].Measurement!.FrequencyResponse.Where(p => p.FrequencyHz != missingFrequency).ToArray() } };
        Check(Rejection(() => C360Reference.Create("replay", [usb[0], missing], usbTest)).StartsWith("Thiếu điểm"),
            "Reference accepted partial frequency overlap");
        Check(Rejection(() => C360Reference.Create("replay", [usb[0], usb[0] with { Status = "FAIL" }], usbTest)).Contains("đạt tiêu chí"),
            "Failed capture became a reference");
        var same = C360Reference.Create("replay", [usb[0], usb[0] with { }], usbTest);
        Check(same.IsUsable && same.Runs == 2, "Stable accepted pair was rejected");

        var stableDirectory = Path.Combine(output, "stable");
        var stableBench = new FixtureBench([usb[0], usb[0] with { }]);
        var reference = new C360ReferenceBench(stableBench, plan, "C360", stableDirectory, "offline", true, 3);
        var setup = plan.SetupFor(usbTest);
        var result = await reference.CaptureAsync(C360ElectronicStage.UsbA, setup, default);
        Check(result.Status == "PASS" && result.Reference?.IsUsable == true && stableBench.Count == 2,
            "Stable pair was not persisted after two captures");
        string path = Directory.GetFiles(stableDirectory, "c360-*.json").Single();
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        result = await reference.CaptureAsync(C360ElectronicStage.UsbA, setup, default);
        Check(result.Summary.StartsWith("ĐÃ CÓ LINE CHUẨN") && stableBench.Count == 2,
            "Matching reference was unnecessarily recaptured");

        var unstableDirectory = Path.Combine(output, "unstable");
        var replayBench = new FixtureBench(usb);
        result = await new C360ReferenceBench(replayBench, plan, "C360", unstableDirectory, "offline", true, 3)
            .CaptureAsync(C360ElectronicStage.UsbA, setup, default);
        Check(result.Status == "INVALID" && result.Summary.Contains("Lượt 2 và 3") && result.Summary.Contains("2/408")
            && result.Reference == null && !Directory.Exists(unstableDirectory), "Unstable USB replay saved a reference or lost the reason");

        var lineMeasurement = Read(1, "LineIn");
        using var engine = new AudioEngine();
        var thd = new TestRunner(engine).BuildSweepDistortionSummary(lineMeasurement, 1000);
        var lineTest = plan.Configuration.Tests.Single(t => t.SpeakerStage == "LineIn");
        string lineStatus = MeasurementCriteria.ThdVerdict([thd], lineTest);
        Check(lineStatus == "FAIL" && Math.Abs(thd.ThdPercent - .79304772604) < 1e-7 && lineTest.Thd1000Hz == .5,
            "Stored LINE IN THD or configured acceptance changed");
        var failedDirectory = Path.Combine(output, "failed");
        var failedBench = new FixtureBench([new(C360ElectronicStage.LineIn, lineStatus, $"THD 1000 Hz {thd.ThdPercent:F3}% > 0.500%", lineMeasurement)]);
        result = await new C360ReferenceBench(failedBench, plan, "C360", failedDirectory, "offline", true, 3)
            .CaptureAsync(C360ElectronicStage.LineIn, plan.SetupFor(lineTest), default);
        Check(result.Status == "FAIL" && result.Summary.Contains("lượt 1/3") && result.Reference == null
            && failedBench.Count == 1 && !Directory.Exists(failedDirectory), "Failed THD was retried/accepted as a reference");
        Check(before == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "Another failed stage changed a saved reference");

        var unavailable = new FixtureBench([]) { PreparationFailure = new BluetoothPlaybackUnavailableException("fixture: Windows pairing failed") };
        var pipeline = new C360ReferenceBench(unavailable, plan, "C360", output, "offline", false, 3);
        var btSetup = plan.SetupFor(plan.Configuration.Tests.Single(t => t.SpeakerStage == "Bluetooth"));
        result = await new C360RepeatedBench(unavailable, new(), _ => pipeline).CaptureAsync(C360ElectronicStage.Bluetooth, btSetup, default);
        Check(result.Status == "INVALID" && result.Attempts.Count == 2 && result.Attempts.All(a => a.Status == "NO_SIGNAL")
            && unavailable.Count == 0, "Missing BT preparation lost voting or opened a capture");
        unavailable.PreparationFailure = new InvalidOperationException("fixture: BLE route failure");
        Check(Rejection(() => pipeline.CaptureAsync(C360ElectronicStage.Bluetooth, btSetup, default).GetAwaiter().GetResult()).Contains("BLE route failure"),
            "Unexpected BLE/control failure was swallowed");
        unavailable.PreparationFailure = null;
        try { await pipeline.CaptureAsync(C360ElectronicStage.Bluetooth, btSetup, new CancellationToken(true)); throw new Exception("Cancelled reference continued"); }
        catch (OperationCanceledException) { Check(unavailable.Count == 0, "Cancelled reference opened a capture"); }

        File.WriteAllText(Path.Combine(output, "replay-summary.json"), JsonSerializer.Serialize(new {
            offline = true, newHardwareMeasurement = false, usbRejection = unstable,
            lineThd1000Hz = thd.ThdPercent, lineLimitPercent = lineTest.Thd1000Hz,
            stablePairSaved = true, cachedReferenceReused = true, failedReferencesNotSaved = true,
            bluetoothNoSignalAttempts = result.Attempts.Count, savedReferenceSha256 = before
        }, C360Reference.Json));
        Console.WriteLine("PASS stored USB/LINE reference replay, detailed rejection, incomplete-grid refusal, unchanged limits, atomic save/reuse, previous reference preservation, Bluetooth NO_SIGNAL voting, fatal control failures and cancellation. Offline only.");
    }

    private sealed class FixtureBench(C360ElectronicResult[] results) : IC360ElectronicBench, IC360AcquisitionPreparation
    {
        public int Count;
        public Exception? PreparationFailure;
        public string DeviceIdentity => "offline-fixture";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => throw new NotSupportedException();
        public void StopAudio() { }
        public Task<C360ElectronicSetup> PrepareAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (PreparationFailure != null) throw PreparationFailure;
            return Task.FromResult(setup);
        }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(results[Count++]); }
    }
}
