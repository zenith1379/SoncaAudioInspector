using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SoncaAudioInspector;

/// <summary>Names from the vendor catalogue, keyed by the two model bytes in SCA=INFO.</summary>
public sealed class SoncaFirmwareModels
{
    public const string SourceUrl = "http://data.soncamedia.com/index.php/apps/files/files/320?dir=/firmware/smartbox&editing=false&openfile=true";
    public const string DownloadUrl = "http://data.soncamedia.com/firmware/smartbox/model_profile.txt";
    private readonly IReadOnlyDictionary<ushort, string[]> _names;
    private static readonly Lazy<SoncaFirmwareModels> Bundled = new(() =>
    {
        using var stream = typeof(SoncaFirmwareModels).Assembly.GetManifestResourceStream("SoncaAudioInspector.FirmwareModels")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });
    public static SoncaFirmwareModels Current { get; private set; } = Load();
    public int EntryCount => _names.Values.Sum(names => names.Length);
    public IReadOnlyList<string> Find(ushort modelId) => _names.GetValueOrDefault(modelId) ?? Array.Empty<string>();
    private SoncaFirmwareModels(IReadOnlyDictionary<ushort, string[]> names) => _names = names;

    public static SoncaFirmwareModels Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Bảng model vượt quá 2 MiB.");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("model", out var models) || models.ValueKind != JsonValueKind.Array
            || models.GetArrayLength() is < 1 or > 4096)
            throw new InvalidDataException("Cần file model_profile.txt có danh sách model.");
        var entries = new List<(ushort Id, string Name)>();
        foreach (var model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("model_id", out var id) || !id.TryGetUInt16(out ushort number)
                || !model.TryGetProperty("model_name", out var label) || label.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Bảng model có mã hoặc tên không hợp lệ.");
            string name = label.GetString()!.Trim();
            if (name.Length is < 1 or > 200 || name.Any(char.IsControl)) throw new InvalidDataException("Tên model không hợp lệ.");
            entries.Add((number, name));
        }
        // The vendor table has duplicate IDs. Preserve all names, never pick an arbitrary one.
        return new(entries.GroupBy(e => e.Id).ToDictionary(g => g.Key,
            g => g.Select(e => e.Name).Distinct(StringComparer.Ordinal).ToArray()));
    }

    public static void Import(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Bảng model vượt quá 2 MiB.");
        Save(Parse(File.ReadAllText(path)));
    }

    public static async Task RefreshAsync(CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Bảng model vượt quá 2 MiB.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        int count;
        while ((count = await source.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Bảng model vượt quá 2 MiB.");
            buffer.Write(chunk, 0, count);
        }
        Save(Parse(Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF')));
    }

    private static void Save(SoncaFirmwareModels catalogue)
    {
        var reduced = new { model = catalogue._names.SelectMany(pair => pair.Value.Select(name => new { model_id = pair.Key, model_name = name })) };
        AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(reduced));
        Current = catalogue;
    }

    private static string CachePath
    {
        get
        {
            string? directory = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector");
            return Path.Combine(directory, "firmware-models.json");
        }
    }
    private static SoncaFirmwareModels Load()
    {
        try { if (File.Exists(CachePath) && new FileInfo(CachePath).Length <= 2 * 1024 * 1024) return Parse(File.ReadAllText(CachePath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { System.Diagnostics.Debug.WriteLine("Using bundled model names: " + ex.Message); }
        return Bundled.Value;
    }
}
