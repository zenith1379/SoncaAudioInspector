using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SoncaAudioInspector;

public static class SoncaBuildIdentity
{
    private static readonly Lazy<string> CurrentBuild = new(() =>
    {
        string path = typeof(SoncaBuildIdentity).Assembly.Location;
        if (string.IsNullOrEmpty(path)) return "APP " + Assembly.GetExecutingAssembly().GetName().Version;
        using var file = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(file));
        return $"APP {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm} · {hash[..10]}";
    });
    public static string Current => CurrentBuild.Value;
    public static string Firmware(SoncaBleReply reply)
    {
        if (!SoncaBleMixerProfile.SupportsInfo(reply)) return "Firmware chưa xác định";
        string text = Encoding.ASCII.GetString(reply.Payload);
        int end = text.IndexOf('\0'); string model = end >= 0 ? text[..end] : "C360";
        int version = text.IndexOf("VER ", StringComparison.Ordinal);
        string details = version >= 0 ? text[version..].Trim('\0', '\r', '\n', ' ') : "chưa có chuỗi phiên bản";
        return model + " · " + details;
    }
}
