using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SoncaAudioInspector;

internal static class LiveQaUploadChecks
{
    public static async Task Run(string sessionFolder, string output, bool resume = false)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "local-data"));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionFolder, "summary.json")));
        var stages = summary.RootElement.GetProperty("results").EnumerateArray().ToArray();
        if (stages.Any(s => s.GetProperty("Status").GetString() is not ("PASS" or "FAIL"))) throw new Exception("Refusing QA upload: incomplete/invalid measurement.");
        string[] images = Directory.GetFiles(sessionFolder, "*.png").Order().ToArray();
        if (images.Length != stages.Length) throw new Exception("A measured stage is missing its graph.");
        // Exercise the desktop pending/complete lifecycle under this helper's own artifact root.
        string uploadFolder = Path.Combine(AppContext.BaseDirectory, "fail data", Path.GetFileName(sessionFolder) + "_" + DateTime.Now.ToString("HHmmssfff"));
        if (!resume)
        {
            Directory.CreateDirectory(uploadFolder);
            foreach (string image in images) File.Copy(image, Path.Combine(uploadFolder, Path.GetFileName(image)), false);
        }
        File.WriteAllText(Path.Combine(output, "source-measurement-session.txt"), sessionFolder);
        if (!resume) images = Directory.GetFiles(uploadFolder, "*.png").Order().ToArray();
        var remembered = ServerEngine.GetRememberedLogin() ?? throw new Exception("No saved login; no server mutation attempted.");
        string baseUrl = ServerEngine.CurrentApiBaseUrl;
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Add("Origin", baseUrl);
        using var login = await client.PostAsJsonAsync("api/auth/login", new { email = remembered.Account, password = remembered.Password,
            deviceId = Guid.NewGuid().ToString(), deviceName = "Sonca C360 measured QA image verification" });
        if (!login.IsSuccessStatusCode) throw new Exception("Saved-account login HTTP " + (int)login.StatusCode);
        using var credentials = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = credentials.RootElement.GetProperty("data");
        string access = data.GetProperty("accessToken").GetString()!, refresh = data.GetProperty("refreshToken").GetString()!;
        string payload = data.GetProperty("sessionToken").GetString()!.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        string sessionId = claims.RootElement.GetProperty("sessionId").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        try
        {
            typeof(ServerEngine).GetProperty("ApiKey")!.SetValue(null, access);
            typeof(ServerEngine).GetProperty("ApiKeyExpiresAtUtc")!.SetValue(null, DateTimeOffset.UtcNow.AddMinutes(10));
            typeof(ServerEngine).GetProperty("StaffID")!.SetValue(null, claims.RootElement.GetProperty("staffId").GetString());
            string serial = "C360_DIAG_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            ProductInfo product;
            ServerEngine.AudioQaUploadReceipt receipt;
            var steps = stages.Select(s => new ServerEngine.AudioQaStepResult(s.GetProperty("StageName").GetString()!, s.GetProperty("Status").GetString()!,
                "Kiểm tra tích hợp từ bản thu GUI Auto Test thật; mic âm học IO005 đang lưu tạm, chưa đo. " + s.GetProperty("Summary").GetString())).ToArray();
            if (resume)
            {
                using var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "upload-receipt.json")));
                product = new ProductInfo { Id = stored.RootElement.GetProperty("Id").GetString() };
                receipt = JsonSerializer.Deserialize<ServerEngine.AudioQaUploadReceipt>(stored.RootElement.GetProperty("receipt"))!;
            }
            else
            {
                var resolved = await ServerEngine.ResolveProductAsync(serial, serial, "C360");
                product = resolved?.Product ?? throw new Exception(ServerEngine.LastError ?? "Server did not resolve diagnostic product.");
                if (!await ServerEngine.UploadAudioQaResultAsync(product, stages.All(s => s.GetProperty("Status").GetString() == "PASS"), steps, images, true, Path.GetFileName(sessionFolder)))
                    throw new Exception(ServerEngine.LastError ?? "QA upload failed.");
                receipt = ServerEngine.LastAudioQaUploadReceipt ?? throw new Exception("Missing upload receipt");
                File.WriteAllText(Path.Combine(output, "upload-receipt.json"), JsonSerializer.Serialize(new { product.Id, serial, receipt }, new JsonSerializerOptions { WriteIndented = true }));
                if (!File.Exists(Path.Combine(uploadFolder, ".uploaded-audio-qa.json")) || File.Exists(Path.Combine(uploadFolder, ".pending-audio-qa.json")))
                    throw new Exception("Upload receipt did not complete the local pending lifecycle.");
            }
            using var readback = await client.GetAsync("api/qa-qc?productId=" + Uri.EscapeDataString(product.Id!));
            readback.EnsureSuccessStatusCode();
            string readbackJson = await readback.Content.ReadAsStringAsync();
            File.WriteAllText(Path.Combine(output, "independent-log-readback.json"), readbackJson);
            if (!readbackJson.Contains(receipt.LogId) || receipt.ImageUrls.Any(url => !readbackJson.Contains(url))) throw new Exception("Independent QA log readback is incomplete.");
            var verified = new List<object>();
            for (int i = 0; i < receipt.ImageUrls.Length; i++)
            {
                // The web QA-log gallery serves private images by log ID/index.
                // Raw /uploads URLs deliberately return 404 at nginx.
                var uri = new Uri(new Uri(baseUrl), "/api/uploads/product-image/" + Uri.EscapeDataString(receipt.LogId + "-" + i));
                using var download = await client.GetAsync(uri); download.EnsureSuccessStatusCode();
                byte[] remote = await download.Content.ReadAsByteArrayAsync();
                string hash = Convert.ToHexString(SHA256.HashData(remote));
                string localHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(images[i])));
                if (hash != localHash) throw new Exception("Stored graph differs from the local capture: " + Path.GetFileName(images[i]));
                File.WriteAllBytes(Path.Combine(output, "server-" + Path.GetFileName(images[i])), remote);
                verified.Add(new { local = Path.GetFileName(images[i]), url = receipt.ImageUrls[i], sha256 = hash, bytes = remote.Length });
            }
            File.WriteAllText(Path.Combine(output, "verified-images.json"), JsonSerializer.Serialize(verified, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS live QA HTTP {receipt.HttpStatus}: {steps.Length} measured stages, {verified.Count} images; independent log readback and downloaded SHA256 match.");
        }
        finally
        {
            using var logout = await client.PostAsJsonAsync("api/auth/logout", new { refreshToken = refresh, sessionId });
            Console.WriteLine("Verification session logout HTTP " + (int)logout.StatusCode);
        }
    }
}
