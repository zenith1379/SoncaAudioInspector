using System.IO;
using System.Text;
using System.Text.Json;

namespace SoncaAudioInspector;

public static class AtomicFile
{
    private static readonly object WriteLock = new();

    public static void WriteAllText(string path, string text, bool keepBackup = true)
    {
        lock (WriteLock)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, keepBackup ? path + ".bak" : null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null)
    {
        Exception? failure = null;
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), options)
                ?? throw new JsonException("File cấu hình rỗng."); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { failure = ex; }
        }
        if (failure != null) throw new IOException("Không đọc được cấu hình hoặc bản dự phòng.", failure);
        return default;
    }

    public static bool WriteIfUnchanged(string path, string expectedText, string replacement)
    {
        lock (WriteLock)
        {
            if (!File.Exists(path) || File.ReadAllText(path) != expectedText) return false;
            WriteAllText(path, replacement);
            return true;
        }
    }
}
