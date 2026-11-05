using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoncaAudioInspector;

string configPath = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var config = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configPath))!;
string? modelName=args.FirstOrDefault(a=>a.StartsWith("--model="))?.Split('=',2)[1];
if(modelName==null) C360ModelDefinition.EnsureSeed(config);
var candidate = modelName==null ? config.models.First(m => m.testItems?.InOut?.AutomationProfile==C360ModelDefinition.Profile)
    : config.models.Single(m=>string.Equals(m.model,modelName,StringComparison.OrdinalIgnoreCase));
candidate.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow;
candidate = ModelConfigEditorWindow.ParseModel(candidate.model, JsonSerializer.Serialize(candidate, ModelConfigEditorWindow.JsonOptions));
var payload = JsonSerializer.SerializeToElement(candidate, ModelConfigEditorWindow.JsonOptions);
File.WriteAllText(Path.Combine(output, "model-to-sync.json"), payload.GetRawText());
if (!args.Contains("--publish")) { Console.WriteLine("Prepared " + candidate.model + ": " + candidate.testItems.InOut.Tests.Count + " tests."); return; }
const string baseUrl = "https://inventory.acnos.store";
using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
client.DefaultRequestHeaders.Add("Origin", baseUrl);
var remembered = ServerEngine.GetRememberedLogin() ?? throw new InvalidOperationException("No remembered account available for configuration sync.");
using var login = await client.PostAsJsonAsync("api/auth/login", new { email = remembered.Account, password = remembered.Password,
    deviceId = Guid.NewGuid().ToString(), deviceName = "Sonca audio model synchronization" });
if (!login.IsSuccessStatusCode) throw new InvalidOperationException("Saved-account login HTTP " + (int)login.StatusCode);
using var credentials = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
var auth = credentials.RootElement.GetProperty("data");
string access = auth.GetProperty("accessToken").GetString()!, refresh = auth.GetProperty("refreshToken").GetString()!;
string encoded = auth.GetProperty("sessionToken").GetString()!.Split('.')[1].Replace('-', '+').Replace('_', '/');
encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
using var session = JsonDocument.Parse(Convert.FromBase64String(encoded));
string sessionId = session.RootElement.GetProperty("sessionId").GetString()!;
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
try
{
    string endpoint = "api/app/audio-config?speakerModel=" + Uri.EscapeDataString(candidate.model);
    using (var before = await client.GetAsync(endpoint))
    {
        before.EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(output, "server-before.json"), await before.Content.ReadAsStringAsync());
    }
    typeof(ServerEngine).GetProperty("ApiKey")!.SetValue(null, access);
    typeof(ServerEngine).GetProperty("ApiKeyExpiresAtUtc")!.SetValue(null, DateTimeOffset.UtcNow.AddMinutes(10));
    var published = await ServerEngine.PublishAudioModelConfigAsync(candidate);
    using var independent = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = client.BaseAddress, Timeout = client.Timeout };
    independent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
    using var read = await independent.GetAsync(endpoint); read.EnsureSuccessStatusCode();
    using var readback = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
    var rows = readback.RootElement.GetProperty("data");
    if (rows.GetArrayLength() != 1 || !JsonNode.DeepEquals(JsonNode.Parse(payload.GetRawText()), JsonNode.Parse(rows[0].GetProperty("config").GetRawText())))
        throw new InvalidOperationException("Independent server readback differs from published configuration.");
    File.WriteAllText(Path.Combine(output, "server-readback.json"), readback.RootElement.GetRawText());
    File.WriteAllText(Path.Combine(output, "model-synced.json"), JsonSerializer.Serialize(published, ModelConfigEditorWindow.JsonOptions));
    File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new { model = published.model,
        stages = published.testItems.InOut.Tests.Select(t => t.SpeakerStage), published.AudioConfigUpdatedUtc,
        exactReadback = true, independentClient = true, publishedUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS published " + published.model + "; independent GET matched exact JSON; " + published.testItems.InOut.Tests.Count + " tests.");
}
finally
{
    using var logout = await client.PostAsJsonAsync("api/auth/logout", new { refreshToken = refresh, sessionId });
    Console.WriteLine("Synchronization session logout HTTP " + (int)logout.StatusCode);
}
