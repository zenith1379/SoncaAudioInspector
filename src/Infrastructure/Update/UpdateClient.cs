using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace SoncaAudioInspector.Update;

public sealed record UpdatePlan(UpdateManifest Manifest, IReadOnlyList<UpdateFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class UpdateClient : IDisposable
{
    public const string DefaultFeed = "https://inventory.acnos.store/updates/audio-inspector/";
    private readonly HttpClient _http;
    private readonly Uri _feed;
    private readonly string _publicKey;
    public UpdateClient(Uri? feed = null, HttpMessageHandler? handler = null, string publicKey = UpdateSignature.PublicKey)
    {
        string address = (feed?.AbsoluteUri ?? Environment.GetEnvironmentVariable("SONCA_UPDATE_FEED") ?? DefaultFeed).TrimEnd('/') + "/";
        _feed = new Uri(address, UriKind.Absolute);
        if (_feed.Scheme is not ("https" or "http") || _feed.UserInfo.Length > 0 || _feed.Query.Length > 0 || _feed.Fragment.Length > 0)
            throw new InvalidDataException("Địa chỉ feed không hợp lệ.");
        _publicKey = publicKey;
        _http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
    }
    public async Task<UpdateManifest?> CheckAsync(Version current, CancellationToken token = default)
    {
        byte[] bytes = await _http.GetByteArrayAsync(new Uri(_feed, "latest.json"), token).ConfigureAwait(false);
        string sig = await _http.GetStringAsync(new Uri(_feed, "latest.json.sig"), token).ConfigureAwait(false);
        UpdateSignature.Verify(bytes, sig, _publicKey);
        var manifest = UpdateManifest.Read(bytes);
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "unsupported";
        return manifest.Validate(architecture) > AppVersion.Normalize(current) ? manifest : null;
    }
    public static UpdatePlan CalculateChanges(UpdateManifest manifest, string appDirectory)
    {
        manifest.Validate();
        return new(manifest, manifest.Files.Where(f => !UpdateSignature.Matches(UpdatePaths.Resolve(appDirectory, f.Path), f)).ToArray());
    }
    public static TimeSpan DefaultFileTimeout(long size) => TimeSpan.FromSeconds(Math.Max(120, size / (32d * 1024)));
    public async Task<string> DownloadAsync(UpdatePlan plan, string appDirectory, IProgress<string>? progress = null, CancellationToken token = default,
        TimeSpan? idleTimeout = null, Func<long, TimeSpan>? fileTimeout = null)
    {
        plan.Manifest.Validate();
        TimeSpan idleWait = idleTimeout ?? TimeSpan.FromSeconds(30);
        UpdateInstaller.CheckWritable(appDirectory);
        string stage = UpdateInstaller.StageDirectory(appDirectory, plan.Manifest.Version);
        try
        {
            UpdateInstaller.DeleteStage(stage);
            Directory.CreateDirectory(stage);
            int count = 0;
            foreach (var file in plan.Files)
            {
                if (!plan.Manifest.Files.Contains(file)) throw new InvalidDataException("File không có trong manifest.");
                string target = UpdatePaths.Resolve(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress?.Report($"Đang tải cập nhật {++count}/{plan.Files.Count}: {file.Path}");
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                lifetime.CancelAfter((fileTimeout ?? DefaultFileTimeout)(file.Size));
                // HttpClient still limits the wait for headers to 15 seconds; the body has separate budgets.
                using var response = await _http.GetAsync(new Uri(_feed, "files/" + file.Sha256), HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    idle.CancelAfter(idleWait);
                    await using (var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buffer = new byte[81920];
                        long size = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) != 0)
                        {
                            idle.CancelAfter(idleWait); // Progress renews only the silence timer, never the total limit.
                            size = checked(size + read);
                            if (size > file.Size) throw new InvalidDataException("File tải về vượt kích thước: " + file.Path);
                            await output.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                        }
                    }
                }
                if (!UpdateSignature.Matches(target, file)) throw new InvalidDataException("File tải về sai size/SHA-256: " + file.Path);
                lifetime.Token.ThrowIfCancellationRequested();
            }
            return stage;
        }
        catch { UpdateInstaller.DeleteStage(stage); throw; }
    }
    public void Dispose() => _http.Dispose();
}
