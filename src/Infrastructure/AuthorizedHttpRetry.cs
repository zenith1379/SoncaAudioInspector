using System.Net;
using System.Net.Http;

namespace SoncaAudioInspector;

public static class AuthorizedHttpRetry
{
    // Only an explicit authentication rejection can be replayed here. A timeout or
    // 5xx POST may already have been committed by the server and must not be replayed.
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client,
        Func<HttpRequestMessage> createRequest, Func<HttpResponseMessage, Task<bool>> recover)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            HttpResponseMessage response = await client.SendAsync(request);
            if (attempt >= 2 || response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                return response;
            try
            {
                if (!await recover(response)) return response;
            }
            catch { response.Dispose(); throw; }
            response.Dispose();
        }
    }
}
