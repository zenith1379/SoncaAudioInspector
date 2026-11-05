using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoncaAudioInspector;

internal static class ModelSyncChecks
{
    public static async Task C360(string configPath,string output)
    {
        Directory.CreateDirectory(output);
        string source=File.ReadAllText(configPath);
        var config=JsonSerializer.Deserialize<CheckingConfig>(source)!;
        C360ModelDefinition.EnsureSeed(config);
        var model=config.models.Single(m=>C360ModelDefinition.Matches(m.testItems.InOut));
        model=ModelConfigEditorWindow.ParseModel(model.model,JsonSerializer.Serialize(model));
        foreach(var t in model.testItems.InOut.Tests)t.ValidateRunPolicy();
        if(model.testItems.InOut.TestVolume==null)throw new Exception("C360 volume is missing; model not published");
        var remembered=ServerEngine.GetRememberedLogin() ?? throw new Exception("No remembered account available for authorized model synchronization");
        string baseUrl=ServerEngine.CurrentApiBaseUrl.TrimEnd('/');
        using var client=new HttpClient(new HttpClientHandler{UseCookies=false}){BaseAddress=new Uri(baseUrl+"/"),Timeout=TimeSpan.FromSeconds(30)};
        client.DefaultRequestHeaders.Add("Origin",baseUrl);
        using var login=await client.PostAsJsonAsync("api/auth/login",new{email=remembered.Account,password=remembered.Password,deviceId=Guid.NewGuid().ToString(),deviceName="Sonca model configuration synchronization"});
        if(!login.IsSuccessStatusCode)throw new Exception("Model sync login HTTP "+(int)login.StatusCode);
        using var credentials=JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data=credentials.RootElement.GetProperty("data");
        string access=data.GetProperty("accessToken").GetString()!,refresh=data.GetProperty("refreshToken").GetString()!;
        string payload=data.GetProperty("sessionToken").GetString()!.Split('.')[1].Replace('-','+').Replace('_','/');payload=payload.PadRight((payload.Length+3)/4*4,'=');
        using var session=JsonDocument.Parse(Convert.FromBase64String(payload));string sessionId=session.RootElement.GetProperty("sessionId").GetString()!;
        try
        {
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",access);
            using var beforeResponse=await client.GetAsync("api/app/audio-config");beforeResponse.EnsureSuccessStatusCode();
            using var beforeReceipt=JsonDocument.Parse(await beforeResponse.Content.ReadAsStringAsync());
            var beforeRows=beforeReceipt.RootElement.GetProperty("data").EnumerateArray().ToArray();
            int previousCount=beforeRows.Count(r=>r.GetProperty("config").GetProperty("model").GetString()==model.model);
            if(previousCount!=1)throw new Exception("Expected exactly one existing C360 model before overwrite verification; no model written");
            typeof(ServerEngine).GetProperty("ApiKey")!.SetValue(null,access);
            typeof(ServerEngine).GetProperty("ApiKeyExpiresAtUtc")!.SetValue(null,DateTimeOffset.UtcNow.AddMinutes(10));
            model.AudioConfigUpdatedUtc=DateTimeOffset.UtcNow;
            var saved=await ServerEngine.PublishAudioModelConfigAsync(model);
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",access);
            using var response=await client.GetAsync("api/app/audio-config?speakerModel="+Uri.EscapeDataString(model.model));response.EnsureSuccessStatusCode();
            using var receipt=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var remote=receipt.RootElement.GetProperty("data")[0].GetProperty("config");
            if(!JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(model,ModelConfigEditorWindow.JsonOptions)),JsonNode.Parse(remote.GetRawText())))throw new Exception("Independent model readback differs");
            using var afterResponse=await client.GetAsync("api/app/audio-config");afterResponse.EnsureSuccessStatusCode();
            using var afterReceipt=JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync());
            var afterRows=afterReceipt.RootElement.GetProperty("data").EnumerateArray().ToArray();
            int currentCount=afterRows.Count(r=>r.GetProperty("config").GetProperty("model").GetString()==model.model);
            if(currentCount!=1 || beforeRows.Length!=afterRows.Length)throw new Exception("Overwrite changed model count");
            foreach(var row in beforeRows.Where(r=>r.GetProperty("config").GetProperty("model").GetString()!=model.model))
            {
                string otherName=row.GetProperty("config").GetProperty("model").GetString()!;
                var other=afterRows.Single(r=>r.GetProperty("config").GetProperty("model").GetString()==otherName);
                if(!JsonNode.DeepEquals(JsonNode.Parse(row.GetRawText()),JsonNode.Parse(other.GetRawText())))throw new Exception("Other server model changed during overwrite check: "+otherName);
            }
            var original=JsonNode.Parse(source)!;
            var originalModel=original["models"]!.AsArray().Single(n=>n!["model"]!.GetValue<string>()==model.model)!;
            var latest=JsonNode.Parse(File.ReadAllText(configPath))!;
            var list=latest["models"]!.AsArray();var current=list.Single(n=>n!["model"]!.GetValue<string>()==model.model)!;
            if(!JsonNode.DeepEquals(originalModel,current))throw new Exception("Model changed locally during synchronization; server saved, local newer edit preserved");
            File.WriteAllText(Path.Combine(output,"checking_config-before.json"),File.ReadAllText(configPath));
            int index=list.IndexOf(current);list[index]=JsonSerializer.SerializeToNode(saved,ModelConfigEditorWindow.JsonOptions);
            AtomicFile.WriteAllText(configPath,latest.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(output,"published-model.json"),JsonSerializer.Serialize(saved,ModelConfigEditorWindow.JsonOptions));
            File.WriteAllText(Path.Combine(output,"receipt.json"),JsonSerializer.Serialize(new{model=model.model,status=(int)response.StatusCode,independentReadbackMatches=true,serverUpdatedUtc=saved.AudioConfigUpdatedUtc,
                volume=saved.testItems.InOut.TestVolume,BluetoothAutoRemove=saved.testItems.InOut.BluetoothAutoRemove,tests=saved.testItems.InOut.Tests.Select(t=>new{t.id,t.name,t.SpeakerStage,t.IsEnabled,t.MaximumRuns,t.RequiredPasses}),otherLocalModelsPreserved=true,
                overwriteVerified=true,matchingModelsBefore=previousCount,matchingModelsAfter=currentCount,totalServerModelsBefore=beforeRows.Length,totalServerModelsAfter=afterRows.Length,otherServerModelsUnchanged=true},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PASS existing C360 model overwritten and independently read back; exactly one row before/after, total model count unchanged, other server models unchanged; local timestamp synchronized");
        }
        finally
        {
            using var logout=await client.PostAsJsonAsync("api/auth/logout",new{refreshToken=refresh,sessionId});
            Console.WriteLine("Model sync session logout HTTP "+(int)logout.StatusCode);
        }
    }
}
