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
        if (string.IsNullOrEmpty(path)) return "APP " + AppVersion.Current.ToString(3);
        using var file = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(file));
        return $"APP {AppVersion.Display} Â· {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm} · {hash[..10]}";
    });
    public static string Current => CurrentBuild.Value;
    public static string Firmware(SoncaBleReply reply)
    {
        if(reply.Command!=SoncaBleProtocol.CmdInfo || reply.Status!=0)return "";
        string text=Encoding.ASCII.GetString(reply.Payload);
        string Printable(int start)
        {int end=start;while(end<text.Length && text[end]>=' ' && text[end]<='~')end++;return text[start..end].Trim();}
        string model=Printable(0);
        int version=text.IndexOf("VER ",StringComparison.Ordinal);
        if(version>=0 && model.Contains("VER ",StringComparison.Ordinal))model=model[..model.IndexOf("VER ",StringComparison.Ordinal)].Trim();
        string details=version>=0?Printable(version):"";
        return string.Join(" · ",new[]{model,details}.Where(s=>s.Length>0));
    }
}
