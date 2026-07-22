using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SoncaAudioInspector
{
    public static class ServerEngine
    {
        private const string DefaultApiBaseUrl = "https://speaker-inventory-system.vercel.app";
        private const string RegistryPath = @"Software\SoncaAudioInspector\Auth";
        private const string AppSessionValueName = "AppSession";
        private const string RememberedLoginValueName = "RememberedLogin";

        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private static readonly SemaphoreSlim VerifyLock = new(1, 1);
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
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
        public static ProductInfo? CurrentProduct { get; private set; }

        public static bool HasValidApiKey =>
            !string.IsNullOrWhiteSpace(ApiKey) &&
            (!ApiKeyExpiresAtUtc.HasValue || ApiKeyExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1));

        public static bool HasValidRefreshToken => !string.IsNullOrWhiteSpace(RefreshToken);

        public static bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(StaffID);

        public static string CurrentApiBaseUrl =>
            (Environment.GetEnvironmentVariable("SONCA_API_BASE_URL") ?? DefaultApiBaseUrl).TrimEnd('/');

        private static string ApiBaseUrl => CurrentApiBaseUrl;

        public static async Task<bool> VerifyAppAsync()
        {
            try
            {
                await EnsureAppApiKeyAsync(forceBootstrap: false);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        public static async Task<bool> AuthenticateAsync(string account, string password)
        {
            ClearStaffSession(keepError: true);

            if (string.IsNullOrWhiteSpace(account))
            {
                LastError = "Vui lòng nhập tài khoản hoặc email.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                LastError = "Vui lòng nhập mật khẩu.";
                return false;
            }

            try
            {
                LoginRequest requestBody = new(account.Trim(), password);
                bool appRetried = false;

                while (true)
                {
                    await EnsureAppApiKeyAsync(forceBootstrap: false);

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/app/login")
                    {
                        Content = JsonContent(requestBody)
                    };
                    request.Headers.Add("X-App-Api-Key", AppToken);

                    using HttpResponseMessage response = await Client.SendAsync(request);
                    string responseJson = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        ApiError error = ReadApiError(responseJson);
                        if (!appRetried && IsAppKeyError(response.StatusCode, error))
                        {
                            appRetried = true;
                            await ClearStoredAppSessionAsync();
                            await EnsureAppApiKeyAsync(forceBootstrap: true);
                            continue;
                        }

                        LastError = BuildLoginError(response.StatusCode, error);
                        return false;
                    }

                    LoginData data = ReadData<LoginData>(responseJson);
                    StaffID = Require(data.StaffId, "Backend không trả staffId.");
                    ApiKey = Require(data.AccessToken, "Backend không trả accessToken.");
                    RefreshToken = Require(data.RefreshToken, "Backend không trả refreshToken.");

                    UserName = data.Name ?? account.Trim();
                    UserEmail = data.Email ?? (account.Contains('@') ? account.Trim() : null);
                    UserRole = string.IsNullOrWhiteSpace(data.Role) ? "STAFF" : data.Role.ToUpperInvariant();

                    ApiKeyExpiresAtUtc = ReadExpiresFromPayload(responseJson, "expiresIn");
                    RefreshTokenExpiresAtUtc = ReadExpiresFromPayload(responseJson, "refreshExpiresIn");
                    LastError = null;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                ClearStaffSession(keepError: true);
                return false;
            }
        }

        public static async Task<bool> RefreshAccessTokenAsync()
        {
            try
            {
                await RefreshAccessTokenOrThrowAsync(ApiKey);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        public static async Task LogoutAsync()
        {
            if (!string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(RefreshToken))
            {
                try
                {
                    await EnsureAppApiKeyAsync(forceBootstrap: false);
                    using var request = CreateAuthorizedRequest(HttpMethod.Post, "api/auth/logout");
                    request.Content = JsonContent(new { refreshToken = RefreshToken });
                    await Client.SendAsync(request);
                }
                catch
                {
                    // Logout local must always complete even if revoke fails offline.
                }
            }

            ClearStaffSession();
        }

        public static void Logout()
        {
            ClearStaffSession();
        }

        public static RememberedLogin? GetRememberedLogin()
        {
            return ReadProtectedRegistryValue<RememberedLogin>(RememberedLoginValueName);
        }

        public static void SaveRememberedLogin(string account, string password)
        {
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
            {
                return;
            }

            WriteProtectedRegistryValue(
                RememberedLoginValueName,
                new RememberedLogin(account.Trim(), password, DateTimeOffset.UtcNow));
        }

        public static void ClearRememberedLogin()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            key?.DeleteValue(RememberedLoginValueName, throwOnMissingValue: false);
        }

        public static async Task<IReadOnlyList<ProductInfo>> GetProductsAsync(
            int page = 1,
            int pageSize = 10,
            string? keyword = null)
        {
            if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "page phải >= 1.");
            if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize phải trong khoảng 1-100.");

            string endpoint = $"api/products?page={page.ToString(CultureInfo.InvariantCulture)}&pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                endpoint += $"&keyword={Uri.EscapeDataString(keyword.Trim())}";
            }

            JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Get, endpoint);
            return ProductInfo.FromApiData(data);
        }

        public static async Task<ProductInfo?> GetProductBySerialAsync(string serialNumber)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                LastError = "Serial Number không được để trống.";
                return null;
            }

            try
            {
                IReadOnlyList<ProductInfo> products = await GetProductsAsync(1, 100, serialNumber.Trim());
                ProductInfo? product = products.FirstOrDefault(p =>
                    EqualsIgnoreCase(p.SerialNumber, serialNumber) ||
                    EqualsIgnoreCase(p.ProductCode, serialNumber) ||
                    EqualsIgnoreCase(p.Id, serialNumber));

                product ??= products.FirstOrDefault();
                CurrentProduct = product;
                LastError = product is null ? "Không tìm thấy thông tin sản phẩm từ server." : null;
                return product;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<ProductInfo?> CheckProductStatusAsync(string serialNumber, string model)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                LastError = "Serial Number không được để trống.";
                return null;
            }

            try
            {
                string endpoint = $"api/app/products/status?serialNumber={Uri.EscapeDataString(serialNumber.Trim())}&model={Uri.EscapeDataString(model?.Trim() ?? "")}";
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Get, endpoint);
                
                ProductInfo? product = ProductInfo.FromApiData(data).FirstOrDefault();
                CurrentProduct = product;
                LastError = null;
                return product;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<ProductInfo?> AddProductAsync(string barcode, string serialNumber, string speakerModel)
        {
            if (string.IsNullOrWhiteSpace(barcode) || string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(speakerModel))
            {
                LastError = "Vui lòng nhập đầy đủ thông tin barcode, serial number và model.";
                return null;
            }

            try
            {
                var body = new { barcode = barcode.Trim(), serialNumber = serialNumber.Trim(), speakerModel = speakerModel.Trim() };
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Post, "api/app/products", body);
                
                ProductInfo? product = ProductInfo.FromApiData(data).FirstOrDefault();
                CurrentProduct = product;
                LastError = null;
                return product;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<VisualQaUploadResult> UploadVisualQaImageAsync(
            ProductInfo product,
            byte[] imageBytes,
            string status,
            string note,
            string fileName)
        {
            if (product is null || string.IsNullOrWhiteSpace(product.Id))
            {
                throw new InvalidOperationException("Chưa có sản phẩm hợp lệ để ghi log QA.");
            }

            if (imageBytes is null || imageBytes.Length == 0)
            {
                throw new InvalidOperationException("Ảnh ngoại quan rỗng, không thể upload.");
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(product.Id), "productId");
            form.Add(new StringContent(string.IsNullOrWhiteSpace(status) ? "PENDING" : status.Trim().ToUpperInvariant()), "status");
            form.Add(new StringContent(note ?? ""), "note");

            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(imageContent, "file", string.IsNullOrWhiteSpace(fileName) ? "visual-ai-capture.jpg" : fileName);

            using HttpResponseMessage response = await SendAuthorizedAsync(HttpMethod.Post, "api/app/qa-visual", form);
            string responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                ApiError error = ReadApiError(responseJson);
                WriteVisualQaUploadLog(product.Id, status, response.StatusCode, null, null, null, error.Code, error.Message);
                throw new ApiException(response.StatusCode, error.Code, ToApiMessage(response.StatusCode, error));
            }

            VisualQaUploadResult result = ReadData<VisualQaUploadResult>(responseJson);
            WriteVisualQaUploadLog(product.Id, status, response.StatusCode, result.StorageProvider, result.Key, result.ImageUrl, null, null);
            return result;
        }

        public static async Task<bool> UploadAudioQaResultAsync(ProductInfo product, bool passed, IEnumerable<TestStep>? steps = null)
        {
            if (product is null || string.IsNullOrWhiteSpace(product.Id))
            {
                LastError = "Chưa có sản phẩm để lưu kết quả QA âm thanh.";
                return false;
            }

            try
            {
                var stepList = steps?.Select((s, idx) => new
                {
                    stepIndex = idx + 1,
                    stepName = s.Name,
                    status = MapStepStatus(s.Status),
                    details = s.Details
                }).ToList();

                var body = new
                {
                    productId = product.Id,
                    status = passed ? "PASS" : "FAIL",
                    note = passed ? "Audio auto test passed" : "Audio auto test failed",
                    steps = stepList
                };

                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Post, "api/app/qa-audio", body);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        private static string MapStepStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "PENDING";
            string s = status.Trim().ToUpperInvariant();
            if (s is "PASS" or "PASSED") return "PASS";
            if (s is "FAIL" or "FAILED") return "FAIL";
            return "PENDING";
        }

        public static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new InvalidOperationException("Chưa đăng nhập hoặc phiên đăng nhập đã hết hạn.");
            }

            var request = new HttpRequestMessage(method, $"{ApiBaseUrl}/{relativePath.TrimStart('/')}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

            if (!string.IsNullOrWhiteSpace(AppToken))
            {
                request.Headers.Add("X-App-Api-Key", AppToken);
            }

            return request;
        }

        public static async Task<HttpResponseMessage> SendAuthorizedAsync(
            HttpMethod method,
            string relativePath,
            HttpContent? content = null)
        {
            if (!HasValidApiKey && HasValidRefreshToken)
            {
                await RefreshAccessTokenOrThrowAsync(ApiKey);
            }

            HttpRequestMessage request = CreateAuthorizedRequest(method, relativePath);
            request.Content = content;
            return await Client.SendAsync(request);
        }

        public static void ClearSession(bool keepError = false)
        {
            ClearStaffSession(keepError);
            AppToken = null;
            AppTokenExpiresAtUtc = null;

            if (!keepError)
            {
                LastError = null;
            }
        }

        private static async Task<JsonElement> SendAuthorizedForDataAsync(
            HttpMethod method,
            string relativePath,
            object? body = null)
        {
            bool appRetried = false;
            bool authRetried = false;

            while (true)
            {
                await EnsureAppApiKeyAsync(forceBootstrap: false);
                string? tokenUsed = ApiKey;

                if (!HasValidApiKey && HasValidRefreshToken)
                {
                    await RefreshAccessTokenOrThrowAsync(tokenUsed);
                    tokenUsed = ApiKey;
                }

                using HttpRequestMessage request = CreateAuthorizedRequest(method, relativePath);
                if (body is not null)
                {
                    request.Content = JsonContent(body);
                }

                using HttpResponseMessage response = await Client.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument document = JsonDocument.Parse(responseJson);
                    JsonElement payload = GetPayload(document.RootElement);
                    return payload.Clone();
                }

                ApiError error = ReadApiError(responseJson);

                if (response.StatusCode == HttpStatusCode.Unauthorized
                    && error.Code == "ACCESS_TOKEN_EXPIRED"
                    && !authRetried)
                {
                    authRetried = true;
                    await RefreshAccessTokenOrThrowAsync(tokenUsed);
                    continue;
                }

                if (IsAppKeyError(response.StatusCode, error) && !appRetried)
                {
                    appRetried = true;
                    await ClearStoredAppSessionAsync();
                    await EnsureAppApiKeyAsync(forceBootstrap: true);
                    continue;
                }

                throw new ApiException(response.StatusCode, error.Code, ToApiMessage(response.StatusCode, error));
            }
        }

        private static async Task EnsureAppApiKeyAsync(bool forceBootstrap)
        {
            if (!forceBootstrap && HasValidAppSessionInMemory())
            {
                return;
            }

            await VerifyLock.WaitAsync();
            try
            {
                if (!forceBootstrap && HasValidAppSessionInMemory())
                {
                    return;
                }

                if (!forceBootstrap)
                {
                    AppSessionData? stored = ReadProtectedRegistryValue<AppSessionData>(AppSessionValueName);
                    if (stored is not null && !string.IsNullOrWhiteSpace(stored.AppApiKey))
                    {
                        AppToken = stored.AppApiKey;
                        AppTokenExpiresAtUtc = stored.ExpiresAtUtc;
                        return;
                    }
                }

                BootstrapCredentials credentials = ReadVerifyFile();

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/app/verify")
                {
                    Content = JsonContent(new VerifyAppRequest(credentials.Email, credentials.Password))
                };

                using HttpResponseMessage response = await Client.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    ApiError error = ReadApiError(responseJson);
                    throw new ApiException(response.StatusCode, error.Code, ToApiMessage(response.StatusCode, error));
                }

                VerifyAppData data = ReadData<VerifyAppData>(responseJson);
                string appApiKey = Require(data.AppApiKey, "Backend không trả appApiKey.");
                DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(data.ExpiresIn);

                AppToken = appApiKey;
                AppTokenExpiresAtUtc = expiresAtUtc;
                WriteProtectedRegistryValue(AppSessionValueName, new AppSessionData(appApiKey, expiresAtUtc));
                SecureDelete(credentials.SourcePath);
            }
            finally
            {
                VerifyLock.Release();
            }
        }

        private static async Task RefreshAccessTokenOrThrowAsync(string? tokenThatFailed)
        {
            await RefreshLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(tokenThatFailed)
                    && !string.Equals(ApiKey, tokenThatFailed, StringComparison.Ordinal))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(RefreshToken))
                {
                    throw new InvalidOperationException("Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.");
                }

                await EnsureAppApiKeyAsync(forceBootstrap: false);

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/auth/refresh")
                {
                    Content = JsonContent(new RefreshRequest(RefreshToken))
                };
                request.Headers.Add("X-App-Api-Key", AppToken);

                using HttpResponseMessage response = await Client.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    ApiError error = ReadApiError(responseJson);
                    if (error.Code is "REFRESH_TOKEN_EXPIRED" or "REFRESH_TOKEN_REVOKED" or "REFRESH_TOKEN_REUSED")
                    {
                        ClearStaffSession(keepError: true);
                    }

                    throw new ApiException(response.StatusCode, error.Code, ToApiMessage(response.StatusCode, error));
                }

                RefreshData data = ReadData<RefreshData>(responseJson);
                ApiKey = Require(data.AccessToken, "Backend không trả accessToken mới.");
                RefreshToken = string.IsNullOrWhiteSpace(data.RefreshToken) ? RefreshToken : data.RefreshToken;
                ApiKeyExpiresAtUtc = ReadExpiresFromPayload(responseJson, "expiresIn");
            }
            finally
            {
                RefreshLock.Release();
            }
        }

        private static bool HasValidAppSessionInMemory()
        {
            return !string.IsNullOrWhiteSpace(AppToken);
        }

        private static void ClearStaffSession(bool keepError = false)
        {
            StaffID = null;
            ApiKey = null;
            RefreshToken = null;
            UserRole = null;
            UserName = null;
            UserEmail = null;
            ApiKeyExpiresAtUtc = null;
            RefreshTokenExpiresAtUtc = null;
            CurrentProduct = null;

            if (!keepError)
            {
                LastError = null;
            }
        }

        private static async Task ClearStoredAppSessionAsync()
        {
            AppToken = null;
            AppTokenExpiresAtUtc = null;
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            key?.DeleteValue(AppSessionValueName, throwOnMissingValue: false);
            await Task.CompletedTask;
        }

        private static HttpContent JsonContent<T>(T value)
        {
            return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
        }

        private static T ReadData<T>(string responseJson)
        {
            using JsonDocument document = JsonDocument.Parse(responseJson);
            JsonElement payload = GetPayload(document.RootElement);
            return JsonSerializer.Deserialize<T>(payload.GetRawText(), JsonOptions)
                ?? throw new InvalidOperationException("Backend trả dữ liệu không hợp lệ.");
        }

        private static JsonElement GetPayload(JsonElement root)
        {
            return TryGetProperty(root, "data", out JsonElement data) ? data : root;
        }

        private static ApiError ReadApiError(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return new ApiError("", "Server không trả nội dung lỗi.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement root = document.RootElement;

                if (TryGetProperty(root, "error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return new ApiError("", error.GetString() ?? "API error");
                    }

                    if (error.ValueKind == JsonValueKind.Object)
                    {
                        string code = GetString(error, "code") ?? "";
                        string errorMessage = GetString(error, "message") ?? error.GetRawText();
                        return new ApiError(code, errorMessage);
                    }
                }

                if (TryGetProperty(root, "message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return new ApiError("", message.GetString() ?? "API error");
                }
            }
            catch (JsonException)
            {
            }

            return new ApiError("", "Server trả lỗi không đúng JSON.");
        }

        private static string ToUserMessage(Exception ex)
        {
            return ex switch
            {
                ApiException apiEx => apiEx.Message,
                HttpRequestException => "Không thể kết nối server. Vui lòng kiểm tra mạng hoặc backend.",
                TaskCanceledException => "Kết nối server quá thời gian chờ. Vui lòng thử lại.",
                FileNotFoundException fileEx => $"Không tìm thấy verify.txt: {fileEx.FileName}",
                JsonException => "Server hoặc verify.txt trả JSON không hợp lệ.",
                _ => ex.Message
            };
        }

        private static string BuildLoginError(HttpStatusCode statusCode, string responseJson)
        {
            ApiError error = ReadApiError(responseJson);
            return BuildLoginError(statusCode, error);
        }

        private static string BuildLoginError(HttpStatusCode statusCode, ApiError error)
        {
            if (!string.IsNullOrWhiteSpace(error.Message))
            {
                return error.Code.Length > 0 ? $"{error.Code}: {error.Message}" : error.Message;
            }

            return statusCode switch
            {
                HttpStatusCode.Unauthorized => "Tài khoản hoặc mật khẩu không chính xác.",
                HttpStatusCode.Forbidden => "Tài khoản không có quyền truy cập hoặc bị khóa.",
                HttpStatusCode.RequestTimeout => "Server phản hồi quá chậm. Vui lòng thử lại.",
                _ => $"Đăng nhập thất bại: HTTP {(int)statusCode}"
            };
        }

        private static bool IsAppKeyError(HttpStatusCode statusCode, ApiError error)
        {
            string code = error.Code.Trim();
            if (code.Equals("APP_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                || code.Equals("APP_KEY_EXPIRED", StringComparison.OrdinalIgnoreCase)
                || code.Equals("INVALID_API_KEY", StringComparison.OrdinalIgnoreCase)
                || code.Equals("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase)
                || code.Equals("API_KEY_EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string message = error.Message.Trim();
            return message.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("api key invalid", StringComparison.OrdinalIgnoreCase)
                || message.Contains("expired api key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("api key expired", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid app api key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("expired app api key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid or expired app api key", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToApiMessage(HttpStatusCode statusCode, ApiError error)
        {
            if (!string.IsNullOrWhiteSpace(error.Message))
            {
                return !string.IsNullOrWhiteSpace(error.Code)
                    ? $"{error.Code}: {error.Message}"
                    : error.Message;
            }

            return $"API lỗi HTTP {(int)statusCode}";
        }

        private static string Require(string? value, string message)
        {
            return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
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
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static DateTimeOffset? ReadExpiresFromPayload(string responseJson, string propertyName)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement payload = GetPayload(document.RootElement);
                string? expiresIn = GetString(payload, propertyName);
                return double.TryParse(expiresIn, NumberStyles.Number, CultureInfo.InvariantCulture, out double seconds)
                    ? DateTimeOffset.UtcNow.AddSeconds(seconds)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static bool EqualsIgnoreCase(string? left, string? right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }

        private static BootstrapCredentials ReadVerifyFile()
        {
            string path = GetVerifyFilePath();
            if (!File.Exists(path))
            {
#if DEBUG
                // Fallback to default seeded credentials in Debug mode to allow seamless local development
                return new BootstrapCredentials("adminapp@speaker.local", "Adminapp@123A", path);
#else
                throw new FileNotFoundException("Không tìm thấy verify.txt.", path);
#endif
            }

            string text = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                VerifyFileJson? json = JsonSerializer.Deserialize<VerifyFileJson>(text, JsonOptions);
                return new BootstrapCredentials(
                    Require(json?.Email, "verify.txt thiếu email."),
                    Require(json?.Password, "verify.txt thiếu password."),
                    path);
            }

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string current = line.Trim();
                if (current.Length == 0 || current.StartsWith("#", StringComparison.Ordinal)) continue;

                int separator = current.IndexOf('=');
                if (separator <= 0) continue;

                values[current[..separator].Trim()] = current[(separator + 1)..].Trim();
            }

            values.TryGetValue("email", out string? email);
            values.TryGetValue("password", out string? password);
            return new BootstrapCredentials(
                Require(email, "verify.txt thiếu email."),
                Require(password, "verify.txt thiếu password."),
                path);
        }

        private static string GetVerifyFilePath()
        {
            string? configuredPath = Environment.GetEnvironmentVariable("SONCA_VERIFY_FILE");
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            List<string> candidates = new();
            AddCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "verify.txt"));
            AddCandidate(candidates, Path.Combine(Environment.CurrentDirectory, "verify.txt"));

            foreach (string root in FindProjectRoots(AppContext.BaseDirectory, Environment.CurrentDirectory))
            {
                AddCandidate(candidates, Path.Combine(root, "verify.txt"));
                AddCandidate(candidates, Path.Combine(root, "bin", "Debug", "net9.0-windows", "verify.txt"));
                AddCandidate(candidates, Path.Combine(root, "bin", "x64", "Debug", "net9.0-windows", "verify.txt"));
            }

            return candidates.FirstOrDefault(File.Exists)
                ?? Path.Combine(AppContext.BaseDirectory, "verify.txt");
        }

        private static IEnumerable<string> FindProjectRoots(params string[] startPaths)
        {
            HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
            foreach (string startPath in startPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                DirectoryInfo? directory = new DirectoryInfo(startPath);
                if (File.Exists(startPath))
                {
                    directory = directory.Parent;
                }

                while (directory is not null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "SoncaAudioInspector.csproj"))
                        && roots.Add(directory.FullName))
                    {
                        yield return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }
        }

        private static void AddCandidate(List<string> candidates, string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(fullPath);
            }
        }

        private static void SecureDelete(string path)
        {
            if (!File.Exists(path)) return;

            try
            {
                long length = new FileInfo(path).Length;
                if (length > 0)
                {
                    using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                    byte[] buffer = new byte[Math.Min(81920, (int)Math.Min(length, int.MaxValue))];
                    RandomNumberGenerator.Fill(buffer);

                    long remaining = length;
                    while (remaining > 0)
                    {
                        int count = (int)Math.Min(buffer.Length, remaining);
                        stream.Write(buffer, 0, count);
                        remaining -= count;
                    }

                    stream.Flush(flushToDisk: true);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static T? ReadProtectedRegistryValue<T>(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            string? protectedBase64 = key?.GetValue(name) as string;
            if (string.IsNullOrWhiteSpace(protectedBase64))
            {
                return default;
            }

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
                byte[] jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                try
                {
                    return JsonSerializer.Deserialize<T>(jsonBytes, JsonOptions);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(jsonBytes);
                }
            }
            catch
            {
                return default;
            }
        }

        private static void WriteProtectedRegistryValue<T>(string name, T value)
        {
            using RegistryKey key = OpenOrCreateRestrictedRegistryKey();
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            try
            {
                byte[] protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);
                key.SetValue(name, Convert.ToBase64String(protectedBytes), RegistryValueKind.String);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(jsonBytes);
            }
        }

        public static void WriteVisualQaClientLog(string message)
        {
            WriteVisualQaLogLine($"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} | server={CurrentApiBaseUrl} | event={message}");
        }

        private static void WriteVisualQaUploadLog(
            string? productId,
            string? status,
            HttpStatusCode httpStatus,
            string? storageProvider,
            string? key,
            string? imageUrl,
            string? errorCode,
            string? errorMessage)
        {
            try
            {
                string line = string.Join(" | ", new[]
                {
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                    $"server={CurrentApiBaseUrl}",
                    $"productId={productId ?? ""}",
                    $"status={(status ?? "").ToUpperInvariant()}",
                    $"http={(int)httpStatus}",
                    $"storage={storageProvider ?? ""}",
                    $"key={key ?? ""}",
                    $"imageUrl={imageUrl ?? ""}",
                    $"errorCode={errorCode ?? ""}",
                    $"error={errorMessage ?? ""}"
                });
                WriteVisualQaLogLine(line);
            }
            catch
            {
                // Upload logging must never break the QA flow.
            }
        }

        private static void WriteVisualQaLogLine(string line)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SoncaAudioInspector");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "visual-ai-upload.log");
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break app behavior.
            }
        }

        private static RegistryKey OpenOrCreateRestrictedRegistryKey()
        {
            RegistrySecurity security = CreateCurrentUserOnlySecurity();
            RegistryKey key = Registry.CurrentUser.CreateSubKey(
                RegistryPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryOptions.None,
                security);
            key.SetAccessControl(security);
            return key;
        }

        private static RegistrySecurity CreateCurrentUserOnlySecurity()
        {
            var security = new RegistrySecurity();
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Không xác định được Windows user hiện tại.");

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new RegistryAccessRule(
                currentUser,
                RegistryRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        private sealed record BootstrapCredentials(string Email, string Password, string SourcePath);
        private sealed record AppSessionData(string AppApiKey, DateTimeOffset ExpiresAtUtc);
        private sealed record VerifyAppRequest(string Email, string Password);
        private sealed record VerifyAppData(string AppApiKey, int ExpiresIn);
        private sealed record LoginRequest(string Email, string Password);
        private sealed record RefreshRequest(string RefreshToken);
        private sealed record RefreshData(string AccessToken, string? RefreshToken);
        private sealed record ApiError(string Code, string Message);

        public sealed record RememberedLogin(string Account, string Password, DateTimeOffset SavedAtUtc);

        public sealed record VisualQaUploadResult(
            string? LogId,
            string? ProductId,
            string? StaffId,
            string? CheckType,
            string? Status,
            string? Note,
            string? ImageUrl,
            string? Key,
            string? StorageProvider,
            DateTimeOffset? CreatedAt);

        private sealed class VerifyFileJson
        {
            public string? Email { get; set; }
            public string? Password { get; set; }
        }

        private sealed class LoginData
        {
            public string? AccessToken { get; set; }
            public string? RefreshToken { get; set; }
            public string? StaffId { get; set; }
            public string? Role { get; set; }
            public string? Name { get; set; }
            public string? Username { get; set; }
            public string? Email { get; set; }
        }

        public sealed class ApiException : Exception
        {
            public ApiException(HttpStatusCode statusCode, string code, string message)
                : base(message)
            {
                StatusCode = statusCode;
                Code = code;
            }

            public HttpStatusCode StatusCode { get; }
            public string Code { get; }
        }
    }

    public sealed class ProductInfo
    {
        public string? Id { get; set; }
        public string? ProductCode { get; set; }
        public string? SerialNumber { get; set; }
        public string? Model { get; set; }
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? QaStatus { get; set; }
        public string? QcStatus { get; set; }
        public JsonElement Raw { get; set; }

        public string DisplayName =>
            FirstNonEmpty(Model, Name, ProductCode, SerialNumber, Id) ?? "Unknown product";

        public static IReadOnlyList<ProductInfo> FromApiData(JsonElement data)
        {
            JsonElement items = data;
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(data, "items", out JsonElement itemArray)) items = itemArray;
                else if (TryGetProperty(data, "data", out JsonElement dataArray)) items = dataArray;
                else if (TryGetProperty(data, "products", out JsonElement productArray)) items = productArray;
                else if (TryGetProperty(data, "results", out JsonElement resultArray)) items = resultArray;
            }

            if (items.ValueKind == JsonValueKind.Array)
            {
                return items.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object)
                    .Select(FromJson)
                    .ToList();
            }

            if (items.ValueKind == JsonValueKind.Object)
            {
                return new[] { FromJson(items) };
            }

            return Array.Empty<ProductInfo>();
        }

        private static ProductInfo FromJson(JsonElement item)
        {
            return new ProductInfo
            {
                Id = GetString(item, "id") ?? GetString(item, "_id") ?? GetString(item, "productId"),
                ProductCode = GetString(item, "productCode") ?? GetString(item, "code") ?? GetString(item, "sku"),
                SerialNumber = GetString(item, "serialNumber") ?? GetString(item, "serial") ?? GetString(item, "sn"),
                Model = GetString(item, "model") ?? GetString(item, "modelName") ?? GetString(item, "speakerModel"),
                Name = GetString(item, "name") ?? GetString(item, "productName"),
                Status = GetString(item, "status"),
                QaStatus = GetString(item, "qaStatus"),
                QcStatus = GetString(item, "qcStatus"),
                Raw = item.Clone()
            };
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }
    }
}
