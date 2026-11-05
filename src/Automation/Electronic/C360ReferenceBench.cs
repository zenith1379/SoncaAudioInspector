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
        try { return await CaptureCoreAsync(stage, setup, token); }
        catch (BluetoothPlaybackUnavailableException ex) when (stage == C360ElectronicStage.Bluetooth)
        {
            token.ThrowIfCancellationRequested();
            return new(stage, "NO_SIGNAL", (findReference ? "Chưa lưu LINE CHUẨN: " : "") + ex.Message);
        }
    }
    private async Task<C360ElectronicResult> CaptureCoreAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        var test = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage);
        if (bench is IC360AcquisitionPreparation prepare)
        {
            setup = await prepare.PrepareAsync(stage, setup, token);
            if (stage == C360ElectronicStage.Bluetooth && string.IsNullOrWhiteSpace(setup.BluetoothPlaybackId))
                return new(stage, "NO_SIGNAL", "Đã chọn nguồn BT nhưng chưa thấy ngõ Bluetooth Windows của đúng loa đang điều khiển. Kết nối âm thanh BT rồi đo lại.");
            if (stage == C360ElectronicStage.UsbA && string.IsNullOrWhiteSpace(setup.UsbPlaybackId))
                return new(stage, "NO_SIGNAL", $"Đã xác nhận BLE ở USB A nhưng Windows chưa có ngõ phát {setup.UsbPlaybackName}. Kiểm tra kết nối USB A rồi đo lại.");
        }
        string key = C360Reference.ConfigurationKey(model, plan, stage, mixerKey, setup);
        string path = Path.Combine(referenceDirectory, "c360-" + key + ".json");
        if (!findReference)
        {
            var current = bench is IC360AcquisitionPreparation prepared
                ? await prepared.CapturePreparedAsync(stage, setup, token) : await bench.CaptureAsync(stage, setup, token);
            if (File.Exists(path))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(path), C360Reference.Json);
                    if (saved?.Key == key && saved.IsUsable) return saved.Compare(current, test, tolerance) with { Reference = saved };
                }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            bool requiresReference = stage is C360ElectronicStage.LineIn or C360ElectronicStage.UsbA or C360ElectronicStage.MicDry
                or C360ElectronicStage.Bluetooth or C360ElectronicStage.AcousticMic;
            return current with { Status = requiresReference && current.Status == "PASS" ? "INVALID" : current.Status,
                Summary = current.Summary + (stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift
                    ? " Chưa có LINE CHUẨN khớp cấu hình hiện tại. Chấm theo kỳ vọng của bài đo."
                    : current.Status == "PASS" ? " Chưa có LINE CHUẨN khớp cấu hình hiện tại. Chưa có LINE CHUẨN — không kết luận. Chạy Tìm LINE CHUẨN cho bài này rồi đo lại."
                    : current.Status == "FAIL" ? " Chưa có LINE CHUẨN khớp cấu hình; đáp tuyến chưa được so."
                    : "") };
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
            if (current.Status != "PASS") return current with { Summary = $"Chưa lưu LINE CHUẨN (lượt {run}/3): " + current.Summary };
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
                catch (InvalidOperationException ex) { reason = $"Lượt {previous + 1} và {run}: " + ex.Message; }
            }
        }
        return captures.Last() with { Status = "INVALID", Summary = "CHUẨN CHƯA ỔN ĐỊNH: đã đo 3 lượt đạt tiêu chí riêng, chưa có hai lượt lặp ổn định. " + reason };
    }
}
