namespace SoncaAudioInspector;

public sealed record MeasurementRateEndpoint(string Id, string Name, string Role, int DefaultRate, int StreamRate);
public sealed record MeasurementSampleRates(int AppRate, MeasurementRateEndpoint Playback, MeasurementRateEndpoint Recording)
{
    public IEnumerable<string> MismatchKeys()
    {
        if (AppRate != 44100) yield return "app:" + AppRate;
        foreach (var e in new[] { Playback, Recording })
            if (e.DefaultRate != 44100 || e.StreamRate != 44100)
                yield return $"{e.Role}:{e.Id}:{e.DefaultRate}:{e.StreamRate}";
    }
    public string Message => "Sample rate đang khác chuẩn 44,1 kHz.\n\n"
        + $"App phát: {AppRate} Hz\n"
        + $"PHÁT: {Playback.Name}\nDefault Format: {Playback.DefaultRate} Hz · luồng phát: {Playback.StreamRate} Hz\n"
        + $"THU: {Recording.Name}\nDefault Format: {Recording.DefaultRate} Hz · luồng thu thực: {Recording.StreamRate} Hz\n\n"
        + "Tiếp tục có thể sử dụng chuyển đổi sample rate; kết quả sẽ ghi đúng rate thu thực tế.\nBạn có muốn tiếp tục?";
}

public sealed class MeasurementSampleRateConsent
{
    private readonly HashSet<string> _accepted = new();
    public void Reset() => _accepted.Clear();
    public async Task<bool> ConfirmAsync(MeasurementSampleRates rates, Func<MeasurementSampleRates, Task<bool>> ask, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (rates.AppRate <= 0 || new[] { rates.Playback, rates.Recording }.Any(e => e.DefaultRate <= 0 || e.StreamRate <= 0))
            throw new InvalidOperationException("Chưa đọc được sample rate của ngõ đo; kiểm tra thiết bị trước khi phát.");
        var keys = rates.MismatchKeys().ToArray();
        if (keys.All(_accepted.Contains)) return true;
        bool accepted = await ask(rates);
        token.ThrowIfCancellationRequested();
        if (accepted) foreach (string key in keys) _accepted.Add(key);
        return accepted;
    }
}
