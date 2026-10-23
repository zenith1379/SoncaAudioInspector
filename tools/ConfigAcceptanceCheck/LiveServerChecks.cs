using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoncaAudioInspector;

internal static class LiveServerChecks
{
    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        const string baseUrl = "https://inventory.acnos.store";
        using var first = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        using var anonymous = await first.GetAsync("api/app/audio-config");
        if (anonymous.StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Unauthenticated config read was not rejected.");
        var remembered = ServerEngine.GetRememberedLogin();
        if (remembered == null) throw new Exception("No saved login is available for the live check.");
        first.DefaultRequestHeaders.Add("Origin", baseUrl);
        using var login = await first.PostAsJsonAsync("api/auth/login", new { email = remembered.Account, password = remembered.Password,
            deviceId = Guid.NewGuid().ToString(), deviceName = "Sonca config synchronization verification" });
        if (!login.IsSuccessStatusCode) throw new Exception("Normal saved-account login failed with HTTP " + (int)login.StatusCode);
        using var credentials = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = credentials.RootElement.GetProperty("data");
        string access = data.GetProperty("accessToken").GetString()!, refresh = data.GetProperty("refreshToken").GetString()!;
        string payload = data.GetProperty("sessionToken").GetString()!.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var session = JsonDocument.Parse(Convert.FromBase64String(payload));
        string sessionId = session.RootElement.GetProperty("sessionId").GetString()!;
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        try
        {
            string modelName = "__SONCA_CONFIG_SYNC_CHECK_" + Guid.NewGuid().ToString("N");
            var model = new ModelConfig { model = modelName, testItems = new TestItems { InOut = new InOutConfig
            {
                Devices = new DevicesConfig { Input = new() { ["OUT"] = "Test playback" }, Output = new() { ["MIC"] = "Test microphone" } },
                Tests = new() { new TestConfig { id = "IO001", name = "Đồng bộ cấu hình", PlaybackOut = "OUT", RecordingIn = "MIC", TestEffect = true, TestSoundLevel = true } }
            } } };
            var json = JsonSerializer.SerializeToElement(model, ModelConfigEditorWindow.JsonOptions);
            using var put = await first.PutAsJsonAsync("api/app/audio-config", json);
            if (!put.IsSuccessStatusCode) throw new Exception("Config PUT failed with HTTP " + (int)put.StatusCode);
            File.WriteAllText(Path.Combine(output, "server-test-model.json"), JsonSerializer.Serialize(new { model = modelName }));
            using var second = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = first.BaseAddress, Timeout = first.Timeout };
            second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var read = await second.GetAsync("api/app/audio-config?speakerModel=" + Uri.EscapeDataString(modelName));
            read.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var row = doc.RootElement.GetProperty("data")[0];
            if (!JsonNode.DeepEquals(JsonNode.Parse(json.GetRawText()), JsonNode.Parse(row.GetProperty("config").GetRawText())))
                throw new Exception("Second client readback differs from published model.");
            var received = ModelConfigEditorWindow.ParseModel(modelName, row.GetProperty("config").GetRawText());
            if (!received.testItems.InOut.Tests[0].TestEffect || row.GetProperty("updatedAt").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddMinutes(-5))
                throw new Exception("Shared flags or version timestamp were lost.");
            // Use the same authenticated client methods as the editor; credentials stay in this test process.
            typeof(ServerEngine).GetProperty("ApiKey")!.SetValue(null, access);
            typeof(ServerEngine).GetProperty("ApiKeyExpiresAtUtc")!.SetValue(null, DateTimeOffset.UtcNow.AddMinutes(10));
            var published = await ServerEngine.PublishAudioModelConfigAsync(model);
            var downloaded = await ServerEngine.GetAudioModelConfigsAsync();
            if (!published.AudioConfigUpdatedUtc.HasValue || !downloaded.Any(m => m.model == modelName && m.testItems.InOut.Tests[0].TestEffect))
                throw new Exception("Desktop publish/download methods lost the model, version or effect flag.");
            using var invalid = await first.PutAsJsonAsync("api/app/audio-config", new { model = modelName, testItems = new { } });
            if (invalid.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Invalid configuration was accepted.");
            File.WriteAllText(Path.Combine(output, "live-server-checks.txt"), "PASS: anonymous 401, ordinary saved-account login, authenticated PUT, independent-client GET exact JSON/flags/version, invalid PUT 400. Temporary model is recorded for cleanup. No user model was published or changed.\n");
            Console.WriteLine("PASS live server: authenticated PUT and independent-client readback match; invalid input rejected.");
        }
        finally
        {
            using var logout = await first.PostAsJsonAsync("api/auth/logout", new { refreshToken = refresh, sessionId });
            Console.WriteLine("Verification session logout HTTP " + (int)logout.StatusCode);
        }
    }
}
