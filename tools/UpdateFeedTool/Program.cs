using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoncaAudioInspector.Update;

try
{
    if (args.Length == 0 || args[0] is not ("build" or "verify")) throw new ArgumentException("Dùng build hoặc verify cùng các tham số --name value.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !args[i].StartsWith("--") || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Tham số không hợp lệ.");
    string Required(string key) => options.TryGetValue("--" + key, out var value) ? value : throw new ArgumentException("Thiếu --" + key);
    if (args[0] == "verify")
    {
        string feed = Path.GetFullPath(Required("feed"));
        string publicKey = File.ReadAllText(Required("public-key"));
        var manifest = ReadVerified(feed, "latest.json", publicKey);
        foreach (var file in manifest.Files)
        {
            string blob = Path.Combine(feed, "files", file.Sha256);
            UpdatePaths.RejectLinks(blob);
            if (!UpdateSignature.Matches(blob, file)) throw new InvalidDataException("Thiếu hoặc sai blob: " + file.Path);
        }
        string archived = "versions/" + manifest.Version + ".json";
        if (!File.ReadAllBytes(Path.Combine(feed, archived)).SequenceEqual(File.ReadAllBytes(Path.Combine(feed, "latest.json"))))
            throw new InvalidDataException("Manifest lưu trữ khác latest.json.");
        ReadVerified(feed, archived, publicKey);
        Console.WriteLine($"PASS verify {manifest.Version}: {manifest.Files.Count} file, {manifest.Files.Sum(f => f.Size)} byte; chữ ký P-256/P1363 và mọi SHA-256 hợp lệ.");
        return;
    }
    string portable = Path.GetFullPath(Required("portable"));
    string output = Path.GetFullPath(Required("out"));
    string version = Required("version");
    UpdateManifest.ParseVersion(version);
    DateTimeOffset released = options.TryGetValue("--released", out var releasedText)
        ? DateTimeOffset.Parse(releasedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime() : DateTimeOffset.UtcNow;
    string[] features = options.TryGetValue("--features-file", out var featuresFile)
        ? File.ReadAllLines(featuresFile, Encoding.UTF8).Select(f => f.Trim()).Where(f => f.Length > 0).ToArray() : [];
    if (Nested(output, portable) || Nested(portable, output)) throw new ArgumentException("Feed và portable phải ở các thư mục riêng.");
    UpdatePaths.RejectLinks(output);
    string privateKey = File.ReadAllText(Required("key"));
    using var key = ECDsa.Create(); key.ImportFromPem(privateKey);
    string publicPem = key.ExportSubjectPublicKeyInfoPem();
    string? previous = options.TryGetValue("--previous", out var prev) ? Path.GetFullPath(prev) : null;
    if (previous != null) ReadVerified(previous, "latest.json", publicPem);
    string archive = Path.Combine(output, "versions", version + ".json");
    foreach (string path in new[] { archive, archive + ".sig", Path.Combine(output, "latest.json"), Path.Combine(output, "latest.json.sig"), Path.Combine(output, "packages", version) })
        UpdatePaths.RejectLinks(path);
    if (File.Exists(archive)) throw new IOException("Phiên bản đã tồn tại; dùng số phiên bản cao hơn.");
    Directory.CreateDirectory(Path.Combine(output, "files"));
    Directory.CreateDirectory(Path.Combine(output, "versions"));
    Directory.CreateDirectory(Path.Combine(output, "packages", version));
    if (previous != null) FeedHistoryPage.CopyHistory(previous, output, publicPem);
    string versionInfo = UpdatePaths.Resolve(portable, "version-info.json");
    File.WriteAllBytes(versionInfo, JsonSerializer.SerializeToUtf8Bytes(new InstalledVersionInfo(version, released, features), UpdateManifest.JsonOptions));
    var files = new List<UpdateFile>(); int reused = 0;
    foreach (string source in UpdatePaths.ApplicationFiles(portable).Order(StringComparer.Ordinal))
    {
        string relative = Path.GetRelativePath(portable, source).Replace('\\', '/');
        if (UpdatePaths.IsExcluded(relative)) continue;
        UpdatePaths.Validate(relative);
        var entry = new UpdateFile(relative, new FileInfo(source).Length, UpdateSignature.Hash(source));
        string blob = Path.Combine(output, "files", entry.Sha256);
        UpdatePaths.RejectLinks(blob);
        string? old = previous == null ? null : Path.Combine(previous, "files", entry.Sha256);
        if (old != null) UpdatePaths.RejectLinks(old);
        bool reuse = old != null && UpdateSignature.Matches(old, entry);
        if (!File.Exists(blob)) File.Copy(reuse ? old! : source, blob);
        if (!UpdateSignature.Matches(blob, entry)) throw new InvalidDataException("Blob không khớp; portable thay đổi trong khi build: " + relative);
        if (reuse) reused++;
        files.Add(entry);
    }
    var built = new UpdateManifest(1, "SoncaAudioInspector", version, "win-x64", released, Required("notes"), files, features);
    built.Validate();
    string zipPath = Path.Combine(output, "packages", version, "SoncaAudioInspector-win-x64.zip");
    UpdatePaths.RejectLinks(zipPath);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        foreach (var entry in files) zip.CreateEntryFromFile(Path.Combine(output, "files", entry.Sha256), entry.Path, CompressionLevel.Optimal);
    // Publish archive first; latest pointers are written only after every immutable blob/package exists.
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(built, UpdateManifest.JsonOptions);
    string signature = UpdateSignature.Sign(bytes, privateKey);
    File.WriteAllBytes(archive, bytes);
    File.WriteAllText(archive + ".sig", signature, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(output, "latest.json.sig"), signature, new UTF8Encoding(false));
    File.WriteAllBytes(Path.Combine(output, "latest.json"), bytes);
    FeedHistoryPage.Write(output, version, publicPem);
    Console.WriteLine($"PASS build {version}: {files.Count} file, {files.Sum(f => f.Size)} byte, {reused} file dùng lại từ feed cũ. MKL được giữ nếu có trong portable.");
}
catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); Environment.ExitCode = 1; }

static bool Nested(string value, string root) => value.Equals(root, StringComparison.OrdinalIgnoreCase)
    || value.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
static UpdateManifest ReadVerified(string feed, string relative, string key)
{
    string path = Path.Combine(feed, relative); UpdatePaths.RejectLinks(path); UpdatePaths.RejectLinks(path + ".sig");
    byte[] bytes = File.ReadAllBytes(path);
    UpdateSignature.Verify(bytes, File.ReadAllText(path + ".sig"), key);
    var manifest = UpdateManifest.Read(bytes); manifest.Validate(); return manifest;
}
