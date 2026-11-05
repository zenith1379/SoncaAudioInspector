using System.Globalization;
using System.Text.RegularExpressions;

namespace SoncaAudioInspector;

public sealed record BluetoothPlaybackEndpoint(string Id, string Name, ulong? Address, bool Stereo,
    Guid? ContainerId = null);

/// <summary>The requested speaker has no usable Windows audio connection; BLE routing errors remain fatal.</summary>
public sealed class BluetoothPlaybackUnavailableException(string message) : InvalidOperationException(message);

public static class BluetoothPlaybackMatcher
{
    public static bool IsPhysicalContainer(Guid? id) => id.HasValue && id != Guid.Empty
        && id != new Guid("00000000-0000-0000-ffff-ffffffffffff");
    public static BluetoothPlaybackEndpoint? Select(ulong controller, IEnumerable<BluetoothPlaybackEndpoint> endpoints,
        string? preferredName = null, Guid? containerId = null)
    {
        // Hardware identity is mandatory. A name is only a tie-breaker between
        // two endpoints of the same physical speaker, never an identity fallback.
        var matching = endpoints.Where(e => e.Stereo && (e.Address is ulong address
            ? SoncaBleDiscovery.IsSameSpeaker(controller, address)
            : IsPhysicalContainer(containerId) && e.ContainerId == containerId))
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (matching.Length <= 1) return matching.SingleOrDefault();
        var named = matching.Where(e => string.Equals(e.Name, preferredName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 1) return named[0];
        throw new InvalidOperationException("Cùng loa có nhiều ngõ Bluetooth stereo: " + string.Join("; ", matching.Select(e => e.Name))
            + ". Chọn tên ngõ cụ thể trong cấu hình bài BT.");
    }

    public static ulong? AddressFromInstanceId(string instanceId)
    {
        if (!instanceId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)
            && !instanceId.StartsWith("BTHHF", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(instanceId, @"(?:DEV_|BLUETOOTHDEVICE_|&)([0-9A-F]{12})(?:_|\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
            && address is > 0 and <= 0xFFFFFFFFFFFFUL ? address : null;
    }
}

/// <summary>One route preparation: inspect first, pair at most once, wait for the actual Windows render endpoint.</summary>
public static class BluetoothPlaybackConnection
{
    public static async Task<BluetoothPlaybackEndpoint> EnsureAsync(ulong controller, string? preferredName, bool autoPair,
        Func<IReadOnlyList<BluetoothPlaybackEndpoint>> enumerate, Func<bool, CancellationToken, Task<Guid?>> prepareConnection,
        CancellationToken token, Action<string>? progress = null, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        if (!SoncaBleDiscovery.IsSameSpeaker(controller, controller)) throw new ArgumentException("Chưa có địa chỉ BLE hợp lệ của loa đang đo.");
        token.ThrowIfCancellationRequested();
        var endpoint = BluetoothPlaybackMatcher.Select(controller, enumerate(), preferredName);
        if (endpoint != null) return endpoint;
        if (autoPair)
            progress?.Invoke("Đang ghép Bluetooth Windows với đúng loa BLE đang đo… Nếu Windows yêu cầu, xác nhận ghép đôi.");
        // Even with pairing disabled, reading an already-associated container
        // can identify an offloaded audio driver without changing Windows state.
        Guid? container = await prepareConnection(autoPair, token);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            endpoint = BluetoothPlaybackMatcher.Select(controller, enumerate(), preferredName, container);
            if (endpoint != null) return endpoint;
            var remaining = (timeout ?? TimeSpan.FromSeconds(15)) - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new BluetoothPlaybackUnavailableException($"Chưa có ngõ phát Bluetooth stereo của loa BLE {controller:X12}. "
                    + "Bật chế độ BT trên loa, đặt loa ở trạng thái cho phép ghép đôi và kiểm tra kết nối âm thanh trong Bluetooth Windows. Chưa phát tín hiệu đo.");
            var pause = interval ?? TimeSpan.FromMilliseconds(200);
            await Task.Delay(pause < remaining ? pause : remaining, token);
        }
    }
}
