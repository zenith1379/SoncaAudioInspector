using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoncaAudioInspector
{
    public class ServerEngine
    {
        private const string DefaultApiBaseUrl = "https://speaker-inventory-system.vercel.app";
        private const string AppApiKeyConstant = "acnos_app_4YpA9cVn7xS2mKq8LrT6wHz3BdE5uJfP";

        private static readonly HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public static string? StaffID { get; private set; }
        public static string? ApiKey { get; private set; }
        public static string? RefreshToken { get; private set; }
        public static string? AppToken { get; private set; }
        public static string? UserRole { get; private set; }
        public static string? UserName { get; private set; }
        public static string? UserEmail { get; private set; }
        public static DateTimeOffset? ApiKeyExpiresAtUtc { get; private set; }
        public static DateTimeOffset? RefreshTokenExpiresAtUtc { get; private set; }
        public static DateTimeOffset? AppTokenExpiresAtUtc { get; private set; }
        public static string? LastError { get; private set; }

        public static bool HasValidApiKey =>
            !string.IsNullOrWhiteSpace(ApiKey) &&
            (!ApiKeyExpiresAtUtc.HasValue || ApiKeyExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1));

        public static bool HasValidRefreshToken =>
            !string.IsNullOrWhiteSpace(RefreshToken) &&
            (!RefreshTokenExpiresAtUtc.HasValue || RefreshTokenExpiresAtUtc > DateTimeOffset.UtcNow);

        public static bool IsAuthenticated =>
            (HasValidApiKey || HasValidRefreshToken) &&
            !string.IsNullOrWhiteSpace(StaffID) &&
            (UserRole == "ADMIN" || UserRole == "STAFF");

        private static string ApiBaseUrl =>
            (Environment.GetEnvironmentVariable("SONCA_API_BASE_URL") ?? DefaultApiBaseUrl)
            .TrimEnd('/');

        /// <summary>
        /// Step 1: Verify the application to get a temporary AppToken.
        /// </summary>
        public static async Task<bool> VerifyAppAsync()
        {
            if (AppToken != null && AppTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(30))
                return true;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/app/verify");
                request.Headers.Add("X-App-Key", AppApiKeyConstant);

                using HttpResponseMessage response = await client.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LastError = ReadApiError(responseJson) ?? "Không thể xác thực ứng dụng (App Verify Failed).";
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement payload = GetPayload(document.RootElement);

                AppToken = GetString(payload, "appToken");
                AppTokenExpiresAtUtc = ReadExpiresIn(payload);

                return !string.IsNullOrEmpty(AppToken);
            }
            catch (Exception ex)
            {
                LastError = $"Lỗi xác thực App: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Step 2: Authenticate user with AppToken and credentials.
        /// </summary>
        public static async Task<bool> AuthenticateAsync(string account, string password)
        {
            ClearSession();

            // Always try to verify app first
            if (!await VerifyAppAsync()) return false;

            try
            {
                var requestBody = new { email = account, password };

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/auth/login")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-App-Token", AppToken);

                using HttpResponseMessage response = await client.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LastError = ReadApiError(responseJson) ?? (response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "Tài khoản hoặc mật khẩu không chính xác.",
                        HttpStatusCode.Forbidden => "Tài khoản không có quyền truy cập hoặc bị giới hạn thiết bị.",
                        _ => $"Lỗi đăng nhập: {(int)response.StatusCode}"
                    });
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement payload = GetPayload(document.RootElement);

                // User Info
                StaffID = GetString(payload, "staffId");
                UserRole = GetString(payload, "role")?.ToUpperInvariant();
                UserName = GetString(payload, "name") ?? GetString(payload, "username");
                UserEmail = GetString(payload, "email");

                // Access Token (SIS Key)
                ApiKey = GetString(payload, "accessToken");
                ApiKeyExpiresAtUtc = ReadExpiresIn(payload, "expiresIn");

                // Refresh Token
                RefreshToken = GetString(payload, "refreshToken");
                RefreshTokenExpiresAtUtc = ReadExpiresIn(payload, "refreshExpiresIn");

                if (!IsAuthenticated)
                {
                    LastError = "Tài khoản không đủ quyền hạn hoặc thiếu token.";
                    ClearSession(keepError: true);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Lỗi hệ thống: {ex.Message}";
                ClearSession(keepError: true);
                return false;
            }
        }

        /// <summary>
        /// Step 3: Refresh Access Token using Refresh Token.
        /// </summary>
        public static async Task<bool> RefreshAccessTokenAsync()
        {
            if (string.IsNullOrEmpty(RefreshToken)) return false;

            try
            {
                var requestBody = new { refreshToken = RefreshToken };
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/auth/refresh")
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
                };

                using HttpResponseMessage response = await client.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    ClearSession();
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement payload = GetPayload(document.RootElement);

                ApiKey = GetString(payload, "accessToken");
                ApiKeyExpiresAtUtc = ReadExpiresIn(payload, "expiresIn");

                return !string.IsNullOrEmpty(ApiKey);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Step 4: Logout and revoke tokens on server.
        /// </summary>
        public static async Task LogoutAsync()
        {
            if (!string.IsNullOrEmpty(ApiKey))
            {
                try
                {
                    var requestBody = new { refreshToken = RefreshToken };
                    using var request = CreateAuthorizedRequest(HttpMethod.Post, "api/auth/logout");
                    request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                    await client.SendAsync(request);
                }
                catch { /* Ignore logout errors */ }
            }
            ClearSession();
        }

        public static void Logout()
        {
            _ = LogoutAsync(); // Fire and forget
        }

        public static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new InvalidOperationException("Chưa đăng nhập.");
            }

            var request = new HttpRequestMessage(method, $"{ApiBaseUrl}/{relativePath.TrimStart('/')}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            return request;
        }

        public static async Task<HttpResponseMessage> SendAuthorizedAsync(
            HttpMethod method,
            string relativePath,
            HttpContent? content = null)
        {
            // Auto-refresh if expired
            if (!HasValidApiKey && HasValidRefreshToken)
            {
                await RefreshAccessTokenAsync();
            }

            using HttpRequestMessage request = CreateAuthorizedRequest(method, relativePath);
            request.Content = content;
            return await client.SendAsync(request);
        }
        public static void ClearSession(bool keepError = false)
        {
            StaffID = null;
            ApiKey = null;
            RefreshToken = null;
            AppToken = null;
            UserRole = null;
            UserName = null;
            UserEmail = null;
            ApiKeyExpiresAtUtc = null;
            RefreshTokenExpiresAtUtc = null;
            AppTokenExpiresAtUtc = null;

            if (!keepError)
            {
                LastError = null;
            }
        }

        // ── JSON helpers ─────────────────────────────────────────────

        private static JsonElement GetPayload(JsonElement root)
        {
            return TryGetProperty(root, "data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
                ? data
                : root;
        }

        private static string? ReadApiError(string responseJson)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement root = document.RootElement;

                if (TryGetProperty(root, "message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (TryGetProperty(root, "error", out JsonElement error))
                {
                    return error.ValueKind == JsonValueKind.String
                        ? error.GetString()
                        : error.GetRawText();
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        private static DateTimeOffset? ReadDateTimeOffset(JsonElement payload, string propertyName)
        {
            string? value = GetString(payload, propertyName);
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;
        }

        private static DateTimeOffset? ReadExpiresIn(JsonElement payload, string propertyName = "expiresIn")
        {
            string? expiresIn = GetString(payload, propertyName);
            return double.TryParse(expiresIn, NumberStyles.Number, CultureInfo.InvariantCulture, out double seconds)
                ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                : null;
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
