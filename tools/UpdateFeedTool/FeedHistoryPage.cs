using System.Globalization;
using System.Net;
using System.Text;
using SoncaAudioInspector;
using SoncaAudioInspector.Update;

internal static class FeedHistoryPage
{
    private static IEnumerable<(string Path, UpdateManifest Manifest)> Versions(string feed, string publicKey)
    {
        string directory = Path.Combine(feed, "versions"); UpdatePaths.RejectLinks(directory);
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            UpdatePaths.RejectLinks(path); UpdatePaths.RejectLinks(path + ".sig");
            byte[] bytes = File.ReadAllBytes(path); UpdateSignature.Verify(bytes, File.ReadAllText(path + ".sig"), publicKey);
            var manifest = UpdateManifest.Read(bytes); manifest.Validate();
            if (Path.GetFileNameWithoutExtension(path) != manifest.Version) throw new InvalidDataException("Tên archive khác phiên bản.");
            yield return (path, manifest);
        }
    }
    public static void CopyHistory(string previous, string output, string key)
    {
        foreach (var (source, manifest) in Versions(previous, key))
        {
            foreach (var file in manifest.Files)
            {
                string blob = Path.Combine(previous, "files", file.Sha256); UpdatePaths.RejectLinks(blob);
                if (!UpdateSignature.Matches(blob, file)) throw new InvalidDataException("Previous feed thiếu/sai blob.");
                CopyImmutable(blob, Path.Combine(output, "files", file.Sha256));
            }
            CopyImmutable(Package(previous, manifest.Version), Package(output, manifest.Version));
            CopyImmutable(source, Path.Combine(output, "versions", Path.GetFileName(source)));
            CopyImmutable(source + ".sig", Path.Combine(output, "versions", Path.GetFileName(source) + ".sig"));
        }
    }
    private static string Package(string feed, string version) => Path.Combine(feed, "packages", version, "SoncaAudioInspector-win-x64.zip");
    private static void CopyImmutable(string source, string target)
    {
        UpdatePaths.RejectLinks(source); UpdatePaths.RejectLinks(target);
        if (File.Exists(target))
        {
            if (UpdateSignature.Hash(source) != UpdateSignature.Hash(target)) throw new IOException("Không ghi đè nội dung bất biến: " + target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
    }
    public static void Write(string feed, string latestVersion, string key)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("""
<!doctype html>
<html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SONCA Audio Inspector — Phiên bản</title><style>
*{box-sizing:border-box}body{margin:0;background:#101114;color:#e4e4e7;font:16px/1.65 system-ui,sans-serif}main{max-width:860px;margin:auto;padding:28px 18px}h1{font-size:clamp(1.6rem,5vw,2.4rem)}article{margin:22px 0;padding:24px;border:1px solid #34343b;border-radius:14px;background:#1b1d22}.latest{border:2px solid #34d399}h2{margin:0;color:#6ee7b7}.badge{display:inline-block;background:#064e3b;color:#a7f3d0;padding:3px 12px;border-radius:20px}a{color:#7dd3fc;overflow-wrap:anywhere}.hash{font:12px/1.7 monospace;overflow-wrap:anywhere}.notes{white-space:pre-wrap}time,.muted{color:#a1a1aa}ul{padding-left:22px}footer{padding:20px 0}@media(max-width:480px){article{padding:18px}}
</style></head><body><main><h1>SONCA Audio Inspector — Phiên bản</h1>
<p>Máy đã cài v1.0 trở lên tự nhận cập nhật khi mở app</p>
""");
        foreach (var (_, m) in Versions(feed, key).OrderByDescending(v => UpdateManifest.ParseVersion(v.Manifest.Version)))
        {
            string package = Package(feed, m.Version); UpdatePaths.RejectLinks(package);
            long size = new FileInfo(package).Length;
            bool latest = m.Version == latestVersion;
            html.Append("<article class=\"").Append(latest ? "latest" : "release").Append("\" data-version=\"").Append(E(m.Version)).Append("\"><h2>")
                .Append(E(AppVersion.Format(UpdateManifest.ParseVersion(m.Version)))).Append("</h2>");
            if (latest) html.Append("<p class=\"badge\">Phiên bản mới nhất</p>");
            html.Append("<time>").Append(E(m.ReleasedUtc.UtcDateTime.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture))).Append("</time>");
            html.Append("<p class=\"notes\">").Append(E(m.Notes)).Append("</p><ul>");
            foreach (string feature in m.Features ?? []) html.Append("<li>").Append(E(feature)).Append("</li>");
            html.Append("</ul><p><a href=\"").Append(E("packages/" + m.Version + "/SoncaAudioInspector-win-x64.zip"))
                .Append("\">Tải gói đầy đủ win-x64</a> · ").Append((size / 1048576d).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" MB (").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" byte)</p><p class=\"hash\">SHA-256: ")
                .Append(E(UpdateSignature.Hash(package))).Append("</p></article>\n");
        }
        html.Append("<footer class=\"muted\">SONCA Audio Inspector · Lịch sử phát hành</footer></main></body></html>\n");
        string target = Path.Combine(feed, "index.html"); UpdatePaths.RejectLinks(target);
        File.WriteAllText(target, html.ToString(), new UTF8Encoding(false));
    }
}
