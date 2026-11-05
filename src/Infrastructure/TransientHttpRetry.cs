using System.Net;
using System.Net.Http;

namespace SoncaAudioInspector;

public static class TransientHttpRetry
{
    public static bool IsTransient(HttpStatusCode status) => (int)status is 408 or 429 or 500 or 502 or 503 or 504;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> createRequest,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan retryDelay = TimeSpan.FromSeconds(attempt);
            try
            {
                using HttpRequestMessage request = createRequest();
                HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt >= 3) return response;
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                // Respect the server's retry window. A long rate limit is
                // returned to the UI instead of retrying sooner than allowed.
                if (retryAfter > TimeSpan.FromSeconds(30)) return response;
                if (retryAfter.HasValue) retryDelay = TimeSpan.FromSeconds(Math.Max(1, retryAfter.Value.TotalSeconds));
                progress?.Report($"Server đang bận (HTTP {(int)response.StatusCode}). Đang chờ và thử lại {attempt + 1}/3...");
                response.Dispose();
            }
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException)
            {
                progress?.Report($"Đang chờ kết nối server, tự thử lại {attempt + 1}/3...");
            }
            await delay(retryDelay, cancellationToken);
        }
    }
}
