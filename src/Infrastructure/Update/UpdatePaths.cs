using System.IO;

namespace SoncaAudioInspector.Update;

// The publisher and client compile this same policy. Windows rules also apply on other hosts.
public static class UpdatePaths
{
    private static readonly string[] DataDirectories = ["save standards", "calibrations", "standards", "fail data", "update-staging"];
    public static bool IsExcluded(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        string name = parts[^1];
        return parts.Any(p => DataDirectories.Contains(p, StringComparer.OrdinalIgnoreCase))
            || name.StartsWith("checking_config", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".old-", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Chay_SoncaAudioInspector.bat", StringComparison.OrdinalIgnoreCase);
    }

    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains("..", StringComparison.Ordinal)
            || path.StartsWith('/') || IsExcluded(path)) throw new InvalidDataException("Đường dẫn cập nhật bị từ chối: " + path);
        foreach (string part in path.Split('/'))
        {
            string device = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part == "." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && "123456789¹²³".Contains(device[3]))
                throw new InvalidDataException("Đường dẫn cập nhật không hợp lệ: " + path);
        }
    }

    public static string Resolve(string root, string path)
    {
        Validate(path);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đường dẫn ra ngoài thư mục ứng dụng.");
        RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if (Path.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Không cập nhật qua liên kết/junction: " + p);
    }

    // Never traverse reparse points or protected station directories, including during cleanup.
    public static IEnumerable<string> ApplicationFiles(string root)
    {
        RejectLinks(root);
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Không duyệt liên kết: " + path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (DataDirectories.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) continue;
                foreach (string file in ApplicationFiles(path)) yield return file;
            }
            else yield return path;
        }
    }
}
