using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector;

/// <summary>Reference and production share the same routed suite, frozen plan and acquisition backend.</summary>
public sealed class C360ReferenceBench(IC360ElectronicBench bench, C360ModelPlan plan, string model,
    string referenceDirectory, string mixerKey, bool findReference, double tolerance,
    Func<int, IC360ElectronicBench>? runBench = null) : IC360ElectronicBench
{
    public string DeviceIdentity => bench.DeviceIdentity;
    public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => bench.ReadAsync(token);
    public Task WriteAsync(ushort register, ushort value, CancellationToken token) => bench.WriteAsync(register, value, token);
    public void StopAudio() => bench.StopAudio();
    public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        var test = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage);
        if (bench is IC360AcquisitionPreparation prepare)
        {
            setup = await prepare.PrepareAsync(stage, setup, token);
            if (stage == C360ElectronicStage.UsbA && string.IsNullOrWhiteSpace(setup.UsbPlaybackId))
                return new(stage, "NO_SIGNAL", $"Đã xác nhận BLE ở USB A nhưng Windows chưa có ngõ phát {setup.UsbPlaybackName}. Kiểm tra kết nối USB A rồi đo lại.");
        }
        string key = C360Reference.ConfigurationKey(model, plan, stage, mixerKey, setup);
        string path = Path.Combine(referenceDirectory, "c360-" + key + ".json");
        if (!findReference)
        {
            var current = await bench.CaptureAsync(stage, setup, token);
            if (File.Exists(path))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(path), C360Reference.Json);
                    if (saved?.Key == key && saved.IsUsable) return saved.Compare(current, test, tolerance) with { Reference = saved };
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            return current with { Summary = current.Summary + " Chưa có LINE CHUẨN khớp cấu hình hiện tại." };
        }
        // Reuse is keyed by the complete measurement configuration, not just a route label.
        if (File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(path), C360Reference.Json);
                if (saved?.Key == key && saved.IsUsable)
                    return new C360ElectronicResult(stage, "PASS", "ĐÃ CÓ LINE CHUẨN khớp cấu hình; bỏ qua đo lại.") { Reference = saved, FrequencyShift = saved.FrequencyShift };
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        var captures = new List<C360ElectronicResult>(); string reason = "";
        for (int run = 1; run <= 3; run++)
        {
            token.ThrowIfCancellationRequested();
            var current = await (runBench?.Invoke(run) ?? bench).CaptureAsync(stage, setup, token);
            if (current.Status is "NO_SIGNAL" or "INVALID") return current;
            if (current.Status != "PASS") return current with { Summary = "Chưa lưu LINE CHUẨN: " + current.Summary };
            captures.Add(current);
            for (int previous = 0; previous < captures.Count - 1; previous++)
            {
                try
                {
                    var reference = C360Reference.Create(key, new[] { captures[previous], current }, test);
                    token.ThrowIfCancellationRequested(); Directory.CreateDirectory(referenceDirectory);
                    AtomicFile.WriteAllText(path, JsonSerializer.Serialize(reference, C360Reference.Json));
                    var readback = JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(path), C360Reference.Json);
                    if (readback?.Key != key || readback.Runs < 2) throw new IOException("Không đọc lại được mẫu chuẩn vừa lưu.");
                    return current with { Summary = $"ĐÃ LƯU LINE CHUẨN: trung bình lượt {previous + 1} và {run}. " + current.Summary, Reference = reference };
                }
                catch (InvalidOperationException ex) { reason = ex.Message; }
            }
        }
        return captures.Last() with { Status = "INVALID", Summary = "CHUẨN CHƯA ỔN ĐỊNH: " + reason };
    }
}
