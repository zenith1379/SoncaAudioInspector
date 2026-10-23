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
    public static partial class ServerEngine
    {
        private const string DefaultApiBaseUrl = "https://inventory.acnos.store";
        private const string RegistryPath = @"Software\SoncaAudioInspector\Auth";
        private const string AppSessionValueName = "AppSession";
        private const string RememberedLoginValueName = "RememberedLogin";
        private const string DeviceIdValueName = "DeviceId";

        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            // DNS can change during a VPS cutover. Recycle pooled connections
            // so a long-running desktop app does not keep talking to the old
            // Vercel origin indefinitely.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        private static readonly SemaphoreSlim VerifyLock = new(1, 1);
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);
        private static readonly SemaphoreSlim AuthenticationLock = new(1, 1);

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
        public static string? SessionId { get; private set; }
        public static string? LastError { get; private set; }
        public static ProductInfo? CurrentProduct { get; set; }
        private static System.Timers.Timer? _heartbeatTimer;

        static ServerEngine()
        {
            try
            {
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("SoncaAudioInspector/1.0 (Windows NT 10.0; Win64; x64)");
            }
            catch
            {
            }
        }

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

        public static async Task<bool> VerifyAppAsync(IProgress<string>? progress = null)
        {
            try
            {
                await EnsureAppApiKeyAsync(forceBootstrap: false, progress);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        public static async Task<bool> AuthenticateAsync(string account, string password, IProgress<string>? progress = null)
        {
            await AuthenticationLock.WaitAsync();
            try { return await AuthenticateCoreAsync(account, password, progress); }
            finally { AuthenticationLock.Release(); }
        }

        private static async Task<bool> AuthenticateCoreAsync(string account, string password, IProgress<string>? progress)
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
                LoginRequest requestBody = new(
                    account.Trim(),
                    password,
                    GetOrCreateDeviceId(),
                    GetStableDeviceName());
                bool appRetried = false;

                while (true)
                {
                    progress?.Report("Đang chờ xác thực ứng dụng với server...");
                    await EnsureAppApiKeyAsync(forceBootstrap: false, progress);

                    progress?.Report("Đang chờ server xác thực tài khoản...");
                    using HttpResponseMessage response = await TransientHttpRetry.SendAsync(Client, () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/app/login")
                        {
                            Content = JsonContent(requestBody)
                        };
                        request.Headers.Add("X-App-Api-Key", AppToken);
                        return request;
                    }, progress);
                    string responseJson = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        ApiError error = ReadApiError(responseJson);
                        if (!appRetried && IsAppKeyError(response.StatusCode, error))
                        {
                            appRetried = true;
                            await ClearStoredAppSessionAsync();
                            await EnsureAppApiKeyAsync(forceBootstrap: true, progress);
                            continue;
                        }

                        LastError = BuildLoginError(response.StatusCode, error);
                        return false;
                    }

                    LoginData data = ReadData<LoginData>(responseJson);
                    StaffID = Require(data.StaffId, "Backend không trả staffId.");
                    ApiKey = Require(data.AccessToken, "Backend không trả accessToken.");
                    RefreshToken = Require(data.RefreshToken, "Backend không trả refreshToken.");

                    UserName = new[] { data.FullName, data.Name, data.Username }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? account.Trim();
                    UserEmail = data.Email ?? (account.Contains('@') ? account.Trim() : null);
                    UserRole = string.IsNullOrWhiteSpace(data.Role) ? "STAFF" : data.Role.ToUpperInvariant();
                    SessionId = data.SessionId;

                    ApiKeyExpiresAtUtc = ReadExpiresFromPayload(responseJson, "expiresIn");
                    RefreshTokenExpiresAtUtc = ReadExpiresFromPayload(responseJson, "refreshExpiresIn");
                    LastError = null;
                    StartHeartbeat();
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

        private static void StartHeartbeat()
        {
            StopHeartbeat();
            if (string.IsNullOrWhiteSpace(SessionId) || string.IsNullOrWhiteSpace(AppToken)) return;

            _heartbeatTimer = new System.Timers.Timer(60000); // 1 minute
            _heartbeatTimer.Elapsed += OnHeartbeatTimerElapsed;
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
        }

        private static void StopHeartbeat()
        {
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
        }

        private static async void OnHeartbeatTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SessionId) || string.IsNullOrWhiteSpace(AppToken))
                {
                    StopHeartbeat();
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/app/heartbeat")
                {
                    Content = JsonContent(new { sessionId = SessionId })
                };
                request.Headers.Add("X-App-Api-Key", AppToken);
                using var response = await Client.SendAsync(request);
            }
            catch
            {
                // Ignore network errors on heartbeat
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

        public static async Task LogoutAsync(
            CancellationToken cancellationToken = default,
            bool clearRememberedLogin = false)
        {
            if (!string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(RefreshToken))
            {
                try
                {
                    using var request = CreateAuthorizedRequest(HttpMethod.Post, "api/auth/logout");
                    request.Content = JsonContent(new { refreshToken = RefreshToken, sessionId = SessionId });
                    using var response = await Client.SendAsync(request, cancellationToken);
                }
                catch
                {
                    // Logout local must always complete even if revoke fails offline.
                }
            }

            if (clearRememberedLogin)
            {
                ClearRememberedLogin();
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
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
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

        public static async Task<ProductInfo?> GetProductBySerialAsync(string serialNumber, string? model = null)
        {
            if (string.IsNullOrWhiteSpace(serialNumber))
            {
                LastError = "Serial Number không được để trống.";
                return null;
            }

            try
            {
                IReadOnlyList<ProductInfo> products = await GetProductsAsync(1, 100, serialNumber.Trim());
                List<ProductInfo> matches = products.Where(p =>
                        (EqualsIgnoreCase(p.SerialNumber, serialNumber) ||
                         EqualsIgnoreCase(p.ProductCode, serialNumber) ||
                         EqualsIgnoreCase(p.Id, serialNumber))
                        && (string.IsNullOrWhiteSpace(model) || EqualsIgnoreCase(p.Model, model)))
                    .ToList();
                ProductInfo? product = matches.Count == 1 ? matches[0] : null;

                CurrentProduct = product;
                LastError = matches.Count > 1
                    ? "Serial tồn tại ở nhiều model. Vui lòng chọn đúng model."
                    : product is null ? "Không tìm thấy thông tin sản phẩm từ server." : null;
                return product;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<ProductInfo?> GetProductByQrCodeAsync(string qrCode)
        {
            if (string.IsNullOrWhiteSpace(qrCode))
            {
                LastError = "Barcode không được để trống.";
                return null;
            }

            try
            {
                string endpoint = $"api/app/products/scan?qrCode={Uri.EscapeDataString(qrCode.Trim())}";
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Get, endpoint);
                ProductInfo? product = ProductInfo.FromApiData(data).FirstOrDefault();
                CurrentProduct = product;
                LastError = product is null ? "Không tìm thấy barcode trên server." : null;
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
                LastError = product is null ? "Không tìm thấy thông tin sản phẩm từ server." : null;
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

        public static async Task<ProductResolveResult?> ResolveProductAsync(string barcode, string serialNumber, string speakerModel)
        {
            if (string.IsNullOrWhiteSpace(barcode) || string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(speakerModel))
            {
                LastError = "Vui lòng nhập đầy đủ thông tin ID, serial number và model.";
                return null;
            }

            try
            {
                var body = new
                {
                    barcode = barcode.Trim(),
                    serialNumber = serialNumber.Trim(),
                    speakerModel = speakerModel.Trim(),
                    resolve = true
                };
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Post, "api/app/products", body);
                bool isResolveResponse = TryGetProperty(data, "product", out JsonElement productData);
                if (!isResolveResponse) productData = data;

                ProductInfo? product = ProductInfo.FromApiData(productData).FirstOrDefault();
                if (product is null)
                {
                    throw new InvalidOperationException("Server không trả thông tin sản phẩm hợp lệ.");
                }

                // Older servers ignore the extra `resolve` field and return the
                // newly-created product directly. Treat that legacy response as
                // Created=true so item synchronization can continue normally.
                bool created = !isResolveResponse
                    || (TryGetProperty(data, "created", out JsonElement createdData)
                        && createdData.ValueKind == JsonValueKind.True);
                BomDefinitionInfo? bom = TryGetProperty(data, "bom", out JsonElement bomData)
                    && bomData.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<BomDefinitionInfo>(bomData.GetRawText(), JsonOptions)
                    : null;
                CurrentProduct = product;
                LastError = null;
                return new ProductResolveResult(product, created, bom);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict
                && (ex.Message.Contains("đã tồn tại", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
            {
                // Older servers answer 409 when the ID already exists instead
                // of returning the existing product in the resolve response.
                ProductInfo? existing = await GetProductByQrCodeAsync(barcode);
                if (existing is not null)
                {
                    LastError = null;
                    return new ProductResolveResult(existing, false, null);
                }

                LastError = ToUserMessage(ex);
                return null;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<bool> RollbackNewProductAsync(ProductInfo product, IEnumerable<string> itemCodes)
        {
            if (product is null || string.IsNullOrWhiteSpace(product.Id))
            {
                LastError = "Chưa có sản phẩm mới để hủy.";
                return false;
            }

            try
            {
                var body = new
                {
                    productId = product.Id,
                    itemCodes = itemCodes
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };
                await SendAuthorizedForDataAsync(HttpMethod.Delete, "api/app/products", body);
                if (CurrentProduct?.Id == product.Id) CurrentProduct = null;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        public static async Task<ModelScanLayoutInfo?> GetModelScanLayoutAsync(string speakerModel)
        {
            if (string.IsNullOrWhiteSpace(speakerModel)) return null;
            try
            {
                string endpoint = $"api/app/model-layout?speakerModel={Uri.EscapeDataString(speakerModel.Trim())}";
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Get, endpoint);
                if (data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
                LastError = null;
                return JsonSerializer.Deserialize<ModelScanLayoutInfo>(data.GetRawText(), JsonOptions);
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<bool> SaveModelScanLayoutAsync(
            string speakerModel,
            IReadOnlyList<ItemSlotConfig> items,
            bool locked)
        {
            if (string.IsNullOrWhiteSpace(speakerModel) || items.Count == 0) return false;
            try
            {
                var body = new
                {
                    speakerModel = speakerModel.Trim(),
                    locked,
                    items = items.Select(item => new
                    {
                        slot = item.slot,
                        name = item.name,
                        layoutX = item.layoutX ?? 0,
                        layoutY = item.layoutY ?? 0,
                        layoutXRatio = item.layoutXRatio,
                        layoutYRatio = item.layoutYRatio,
                        scale = item.scale,
                        rotation = item.rotation,
                    }).ToList(),
                };
                await SendAuthorizedForDataAsync(HttpMethod.Put, "api/app/model-layout", body);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        public static async Task<BomImportResult?> ImportBomAsync(
            IReadOnlyList<BomImportRow> bomRows,
            IReadOnlyList<ItemImportRow> itemRows)
        {
            if (bomRows is null || bomRows.Count == 0 || itemRows is null || itemRows.Count == 0)
            {
                LastError = "Cần đủ dữ liệu từ cả file BOM_MODELS và file ITEMS.";
                return null;
            }

            try
            {
                var body = new
                {
                    bomRows,
                    itemRows,
                    driveOwnerEmail = BomCsvParser.DriveOwnerEmail
                };
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Post, "api/app/bom", body);
                BomImportResult? result = JsonSerializer.Deserialize<BomImportResult>(data.GetRawText(), JsonOptions);
                LastError = result is null ? "Server không trả kết quả import BOM hợp lệ." : null;
                return result;
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                LastError = "Server production chưa có API import BOM (/api/app/bom). "
                    + "Cần deploy backend và migration BOM trước khi import.";
                return null;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<BomDefinitionInfo?> GetBomDefinitionAsync(string speakerModel)
        {
            if (string.IsNullOrWhiteSpace(speakerModel)) return null;
            try
            {
                string endpoint = "api/app/bom?speakerModel=" + Uri.EscapeDataString(speakerModel.Trim());
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Get, endpoint);
                BomDefinitionInfo? result = JsonSerializer.Deserialize<BomDefinitionInfo>(data.GetRawText(), JsonOptions);
                LastError = result is null ? "Không tìm thấy BOM của model." : null;
                return result;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<ProductInfo?> LinkProductItemAsync(
            ProductInfo product,
            string itemCode,
            string itemName,
            int slotIndex)
        {
            if (product is null || string.IsNullOrWhiteSpace(product.Id))
            {
                LastError = "Chưa có sản phẩm hợp lệ để gắn item.";
                return null;
            }
            if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(itemName))
            {
                LastError = "Mã và tên item không được để trống.";
                return null;
            }
            if (slotIndex < 1)
            {
                LastError = "Vị trí item không hợp lệ.";
                return null;
            }

            try
            {
                var body = new
                {
                    itemCode = itemCode.Trim(),
                    name = itemName.Trim(),
                    slotIndex
                };
                string endpoint = $"api/app/products/{Uri.EscapeDataString(product.Id)}/items";
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Post, endpoint, body);
                ProductInfo? updated = ProductInfo.FromApiData(data).FirstOrDefault();
                CurrentProduct = updated ?? product;
                LastError = null;
                return updated ?? product;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<ProductInfo?> LinkProductItemsAsync(
            ProductInfo product,
            IEnumerable<ProductItemLinkInput> items)
        {
            if (product is null || string.IsNullOrWhiteSpace(product.Id))
            {
                LastError = "Chưa có sản phẩm hợp lệ để gắn item.";
                return null;
            }

            var itemList = items?.Select(item => new
            {
                itemCode = item.ItemCode.Trim(),
                name = item.Name.Trim(),
                slotIndex = item.SlotIndex
            }).ToList();
            if (itemList is null || itemList.Count == 0
                || itemList.Any(item => string.IsNullOrWhiteSpace(item.itemCode)
                    || string.IsNullOrWhiteSpace(item.name)
                    || item.slotIndex < 1))
            {
                LastError = "Danh sách item không hợp lệ.";
                return null;
            }

            try
            {
                var body = new { items = itemList };
                string endpoint = $"api/app/products/{Uri.EscapeDataString(product.Id)}/items/batch";
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Post, endpoint, body);
                ProductInfo? updated = ProductInfo.FromApiData(data).FirstOrDefault();
                CurrentProduct = updated ?? product;
                LastError = null;
                return updated ?? product;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return null;
            }
        }

        public static async Task<ProductInfo?> UpdateProductItemStatusAsync(
            string productId,
            string itemCode,
            string status)
        {
            if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(itemCode))
            {
                LastError = "Product ID và Item Code không được để trống.";
                return null;
            }

            try
            {
                var body = new { status = status };
                string endpoint = $"api/app/products/{Uri.EscapeDataString(productId)}/items/{Uri.EscapeDataString(itemCode)}";
                JsonElement data = await SendAuthorizedForDataAsync(HttpMethod.Put, endpoint, body);
                ProductInfo? updated = ProductInfo.FromApiData(data).FirstOrDefault();
                CurrentProduct = updated ?? CurrentProduct;
                LastError = null;
                return updated;
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

            using var form = CreateMultipartFormDataContent();
            form.Add(new StringContent(product.Id), "productId");
            form.Add(new StringContent(string.IsNullOrWhiteSpace(status) ? "PENDING" : status.Trim().ToUpperInvariant()), "status");
            form.Add(new StringContent(note ?? ""), "note");
            if (!string.IsNullOrWhiteSpace(UserName))
            {
                form.Add(new StringContent(UserName), "staffName");
            }

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

        public sealed record AudioQaUploadReceipt(int HttpStatus, string LogId, string[] ImageUrls, int ExpectedImageCount);
        public static AudioQaUploadReceipt? LastAudioQaUploadReceipt { get; private set; }

        public static async Task<bool> UploadAudioQaResultAsync(
            ProductInfo product,
            bool passed,
            IEnumerable<AudioQaStepResult>? steps = null,
            IEnumerable<string>? graphImagePaths = null,
            bool deviceReady = false,
            string? uploadSessionId = null)
        {
            LastAudioQaUploadReceipt = null;
            if (product is null || string.IsNullOrWhiteSpace(product.Id))
            {
                LastError = "Chưa có sản phẩm để lưu kết quả QA âm thanh.";
                return false;
            }

            if (!passed && !deviceReady)
            {
                LastError = "Không upload FAIL lên server vì chưa xác nhận đủ thiết bị audio theo cấu hình.";
                return false;
            }

            string? pendingPath = null;
            try
            {
                var stepSnapshot = steps?.ToArray() ?? Array.Empty<AudioQaStepResult>();
                var graphSnapshot = graphImagePaths?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();
                if (graphSnapshot.Length == 0 || graphSnapshot.Any(path => !File.Exists(path)))
                    throw new IOException("Chưa có đủ ảnh QA để gửi; kết quả chưa được xác nhận upload.");
                pendingPath = LocalQaStorage.SavePending(new PendingAudioQaUpload(CurrentApiBaseUrl, StaffID ?? "",
                    product.Id, passed, stepSnapshot, graphSnapshot.Select(Path.GetFileName).Select(name => name!).ToArray(),
                    deviceReady, uploadSessionId ?? "", DateTimeOffset.UtcNow), graphSnapshot);
                var stepList = stepSnapshot.Select((s, idx) => new
                {
                    stepIndex = idx + 1,
                    stepName = s.Name,
                    status = MapStepStatus(s.Status),
                    details = s.Details
                }).ToList();

                var images = new List<object>();
                foreach (string imagePath in graphSnapshot)
                {
                    if (!File.Exists(imagePath)) continue;
                    byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
                    string fileName = Path.GetFileName(imagePath);
                    images.Add(new
                    {
                        fileName = string.IsNullOrWhiteSpace(fileName) ? "audio-qa-response.png" : fileName,
                        contentType = "image/png",
                        base64 = Convert.ToBase64String(imageBytes)
                    });
                }

                var body = new
                {
                    productId = product.Id,
                    status = passed ? "PASS" : "FAIL",
                    note = passed ? "Audio auto test passed" : "Audio auto test failed",
                    deviceReady,
                    uploadSessionId = string.IsNullOrWhiteSpace(uploadSessionId) ? null : uploadSessionId.Trim(),
                    staffName = UserName,
                    steps = stepList,
                    images
                };
                using HttpResponseMessage response = await SendAuthorizedAsync(
                    HttpMethod.Post,
                    "api/app/qa-audio",
                    JsonContent(body));
                string responseJson = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    ApiError error = ReadApiError(responseJson);
                    LastError = ToApiMessage(response.StatusCode, error);
                    if (pendingPath != null && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        LocalQaStorage.MarkRetrySafe(pendingPath);
                    return false;
                }

                using var saved = JsonDocument.Parse(responseJson);
                var log = saved.RootElement.GetProperty("data");
                string logId = log.TryGetProperty("logId", out var id) ? id.GetString() ?? "" : "";
                string[] urls = log.TryGetProperty("imageUrl", out var imageUrl) ? (imageUrl.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
                if (string.IsNullOrWhiteSpace(logId) || urls.Length != images.Count)
                {
                    LastError = $"Server đã nhận yêu cầu nhưng chưa xác nhận đủ ảnh QA ({urls.Length}/{images.Count}). Giữ bản lưu tại máy; kiểm tra log server trước khi gửi lại.";
                    return false;
                }
                LastAudioQaUploadReceipt = new((int)response.StatusCode, logId, urls, images.Count);
                if (pendingPath != null)
                {
                    try { LocalQaStorage.MarkUploaded(LocalQaStorage.Root, pendingPath); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Ghi xác nhận upload QA: " + ex.Message); }
                }
                _ = Task.Run(() =>
                {
                    try { LocalQaStorage.PruneUploadedGraphs(LocalQaStorage.Root); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Dọn ảnh QA đã gửi: " + ex.Message); }
                });
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ToUserMessage(ex);
                return false;
            }
        }

        public sealed record PendingAudioQaItem(string Path, PendingAudioQaUpload Result);

        public static IReadOnlyList<PendingAudioQaItem> GetPendingAudioQaUploads()
        {
            var items = new List<PendingAudioQaItem>();
            if (string.IsNullOrWhiteSpace(StaffID)) return items;
            foreach (string path in LocalQaStorage.FindPending(LocalQaStorage.Root))
            {
                try
                {
                    var pending = LocalQaStorage.ReadPending(LocalQaStorage.Root, path);
                    if (pending != null && pending.StaffId == StaffID
                        && string.Equals(pending.Server.TrimEnd('/'), CurrentApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                        items.Add(new PendingAudioQaItem(path, pending));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Đọc kết quả QA chờ gửi: " + ex.Message); }
            }
            return items.OrderBy(item => item.Result.CreatedUtc).ToArray();
        }

        public static async Task<bool> RetryPendingAudioQaAsync(string path, bool allowUncertainRetry)
        {
            var pending = LocalQaStorage.ReadPending(LocalQaStorage.Root, path);
            if (pending == null || pending.StaffId != StaffID
                || !string.Equals(pending.Server.TrimEnd('/'), CurrentApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                LastError = "Kết quả chờ gửi không thuộc tài khoản/server hiện tại hoặc đã được gửi.";
                return false;
            }
            if (!pending.SafeToRetry && !allowUncertainRetry)
            {
                LastError = "Cần kiểm tra lịch sử QA trước khi gửi lại kết quả chưa rõ trạng thái trên server.";
                return false;
            }
            string folder = Path.GetDirectoryName(path)!;
            string[] graphs = pending.GraphFiles.Select(name => Path.Combine(folder, name)).ToArray();
            if (graphs.Any(graph => !File.Exists(graph)))
            {
                LastError = "Thiếu ảnh đo đã lưu; không gửi lại kết quả QA thiếu dữ liệu.";
                return false;
            }
            return await UploadAudioQaResultAsync(new ProductInfo { Id = pending.ProductId }, pending.Passed,
                pending.Steps, graphs, pending.DeviceReady, pending.UploadSessionId);
        }

        public static async Task<bool?> GetAudioQaUploadPreferenceAsync()
        {
            try
            {
                using HttpResponseMessage response = await SendAuthorizedAsync(HttpMethod.Get, "api/app/preferences");
                if (!response.IsSuccessStatusCode) return null;
                string responseJson = await response.Content.ReadAsStringAsync();
                using JsonDocument document = JsonDocument.Parse(responseJson);
                JsonElement payload = GetPayload(document.RootElement);
                if (payload.TryGetProperty("audioQaUploadEnabled", out JsonElement prop))
                {
                    return prop.GetBoolean();
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<bool> SaveAudioQaUploadPreferenceAsync(bool enabled)
        {
            try
            {
                var content = JsonContent(new { audioQaUploadEnabled = enabled });
                using HttpResponseMessage response = await SendAuthorizedAsync(HttpMethod.Patch, "api/app/preferences", content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
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

        private static MultipartFormDataContent CreateMultipartFormDataContent()
        {
            string boundary = $"----SoncaAudioInspector{Guid.NewGuid():N}";
            var form = new MultipartFormDataContent(boundary);
            NameValueHeaderValue? boundaryParameter = form.Headers.ContentType?.Parameters
                .FirstOrDefault(parameter =>
                    string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase));
            if (boundaryParameter is not null)
            {
                // Some Fetch/FormData parsers reject a quoted boundary even though
                // RFC 2046 permits it. Emit the token form for broad compatibility.
                boundaryParameter.Value = boundary;
            }
            return form;
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
            using (content)
            {
                await EnsureAppApiKeyAsync(forceBootstrap: false);
                if (!HasValidApiKey && HasValidRefreshToken)
                    await RefreshAccessTokenOrThrowAsync(ApiKey);
                byte[]? bytes = content == null ? null : await content.ReadAsByteArrayAsync();
                var headers = content?.Headers.ToArray();
                bool appRetried = false, authRetried = false;
                string? tokenUsed = null;
                return await AuthorizedHttpRetry.SendAsync(Client, () =>
                {
                    tokenUsed = ApiKey;
                    HttpRequestMessage request = CreateAuthorizedRequest(method, relativePath);
                    if (bytes != null)
                    {
                        request.Content = new ByteArrayContent(bytes);
                        foreach (var header in headers!) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    return request;
                }, async response =>
                {
                    ApiError error = ReadApiError(await response.Content.ReadAsStringAsync());
                    if (!appRetried && IsAppKeyError(response.StatusCode, error))
                    {
                        appRetried = true;
                        await ClearStoredAppSessionAsync();
                        await EnsureAppApiKeyAsync(forceBootstrap: true);
                        return true;
                    }
                    if (!authRetried && response.StatusCode == HttpStatusCode.Unauthorized && HasValidRefreshToken)
                    {
                        authRetried = true;
                        await RefreshAccessTokenOrThrowAsync(tokenUsed);
                        return true;
                    }
                    return false;
                });
            }
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
            using HttpResponseMessage response = await SendAuthorizedAsync(method, relativePath,
                body is null ? null : JsonContent(body));
            string responseJson = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(responseJson);
                return GetPayload(document.RootElement).Clone();
            }
            ApiError error = ReadApiError(responseJson);
            throw new ApiException(response.StatusCode, error.Code, ToApiMessage(response.StatusCode, error));
        }

        private static async Task EnsureAppApiKeyAsync(bool forceBootstrap, IProgress<string>? progress = null)
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
                    if (stored is not null && IsAppSessionUsable(stored.AppApiKey, stored.ExpiresAtUtc))
                    {
                        AppToken = stored.AppApiKey;
                        AppTokenExpiresAtUtc = stored.ExpiresAtUtc;
                        return;
                    }
                }

                BootstrapCredentials credentials = ReadVerifyFile();

                using HttpResponseMessage response = await TransientHttpRetry.SendAsync(Client,
                    () => new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/api/app/verify")
                    {
                        Content = JsonContent(new VerifyAppRequest(credentials.Email, credentials.Password))
                    }, progress);
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
                    if (response.StatusCode == HttpStatusCode.Unauthorized
                        || error.Code is "REFRESH_TOKEN_INVALID" or "REFRESH_TOKEN_EXPIRED" or "REFRESH_TOKEN_REVOKED" or "REFRESH_TOKEN_REUSED")
                    {
                        ClearStaffSession(keepError: true);
                    }

                    // If server returned a temporary 500/502/503/504 error during refresh,
                    // but the existing ApiKey is still unexpired, keep using it rather than aborting.
                    if ((int)response.StatusCode >= 500 && ApiKeyExpiresAtUtc.HasValue && ApiKeyExpiresAtUtc.Value > DateTimeOffset.UtcNow)
                    {
                        return;
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
            return IsAppSessionUsable(AppToken, AppTokenExpiresAtUtc);
        }

        internal static bool IsAppSessionUsable(string? token, DateTimeOffset? expiresAtUtc) =>
            !string.IsNullOrWhiteSpace(token) && expiresAtUtc.HasValue
            && expiresAtUtc.Value > DateTimeOffset.UtcNow.AddSeconds(30);

        public static void ClearStaffSession(bool keepError = false)
        {
            StopHeartbeat();
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
                // Reverse proxies and framework error pages may return HTML or
                // plain text instead of the API's JSON envelope. Keep a short,
                // sanitized preview so the operator can distinguish a 502/404
                // proxy response from an expired API session.
                string preview = System.Text.RegularExpressions.Regex.Replace(responseJson, "<[^>]+>", " ");
                preview = System.Net.WebUtility.HtmlDecode(preview)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
                if (preview.Length > 180) preview = preview[..180] + "…";
                return new ApiError("NON_JSON_RESPONSE",
                    string.IsNullOrWhiteSpace(preview)
                        ? "Server trả phản hồi không phải JSON."
                        : $"Server trả phản hồi không phải JSON: {preview}");
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
            if (!string.IsNullOrWhiteSpace(error.Message) && error.Message != "Server không trả nội dung lỗi.")
            {
                return error.Code.Length > 0 ? $"{error.Code}: {error.Message}" : error.Message;
            }

            return statusCode switch
            {
                HttpStatusCode.Unauthorized => "Tài khoản hoặc mật khẩu không chính xác.",
                HttpStatusCode.Forbidden => "Tài khoản không có quyền truy cập hoặc bị khóa.",
                HttpStatusCode.RequestTimeout => "Server phản hồi quá chậm. Vui lòng thử lại.",
                HttpStatusCode.TooManyRequests => "Server đang giới hạn lượt đăng nhập. Vui lòng chờ trước khi thử lại.",
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
            if (error.Message == "Server không trả nội dung lỗi.")
                return $"Server trả HTTP {(int)statusCode} nhưng không có nội dung lỗi.";
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

        public static string GetExpectedVerifyFilePath() => GetVerifyFilePath();

        public static string GetVerifyFilePath()
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
                AddCandidate(candidates, Path.Combine(root, "bin", "Debug", "net9.0-windows10.0.19041.0", "verify.txt"));
                AddCandidate(candidates, Path.Combine(root, "bin", "x64", "Debug", "net9.0-windows", "verify.txt"));
                AddCandidate(candidates, Path.Combine(root, "bin", "x64", "Debug", "net9.0-windows10.0.19041.0", "verify.txt"));
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

        private static string GetOrCreateDeviceId()
        {
            string? stored = ReadProtectedRegistryValue<string>(DeviceIdValueName);
            if (Guid.TryParse(stored, out Guid existing))
            {
                return existing.ToString("D").ToLowerInvariant();
            }

            string created = Guid.NewGuid().ToString("D").ToLowerInvariant();
            WriteProtectedRegistryValue(DeviceIdValueName, created);
            return created;
        }

        private static string GetStableDeviceName()
        {
            string name = Environment.MachineName?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? "Windows PC" : name[..Math.Min(name.Length, 160)];
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
                LocalQaStorage.AppendRotatingLog(logPath, line);
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
        private sealed record LoginRequest(string Email, string Password, string DeviceId, string DeviceName);
        private sealed record RefreshRequest(string RefreshToken);
        private sealed record RefreshData(string AccessToken, string? RefreshToken);
        private sealed record ApiError(string Code, string Message);

        public sealed record RememberedLogin(string Account, string Password, DateTimeOffset SavedAtUtc);

        public sealed record AudioQaStepResult(string Name, string Status, string Details);

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
            public string? FullName { get; set; }
            public string? Username { get; set; }
            public string? Email { get; set; }
            public string? SessionId { get; set; }
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
        public List<ProductItemInfo> Items { get; set; } = new();
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
                Items = GetItems(item),
                Raw = item.Clone()
            };
        }

        private static List<ProductItemInfo> GetItems(JsonElement product)
        {
            if (!TryGetProperty(product, "items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return new List<ProductItemInfo>();
            }

            return items.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Object)
                .Select(value => new ProductItemInfo
                {
                    Id = GetString(value, "itemId") ?? GetString(value, "id"),
                    Code = GetString(value, "itemCode") ?? GetString(value, "code"),
                    Name = GetString(value, "name"),
                    ImageLink = GetString(value, "imageUrl"),
                    DriveOwnerEmail = GetString(value, "driveOwnerEmail"),
                    SlotIndex = GetInt(value, "slotIndex")
                })
                .ToList();
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

        private static int? GetInt(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out JsonElement value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
            return null;
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

    public sealed class ModelScanLayoutInfo
    {
        [JsonPropertyName("speakerModel")]
        public string SpeakerModel { get; set; } = "";

        [JsonPropertyName("assemblyItems")]
        public List<ItemSlotConfig> Items { get; set; } = new();

        [JsonPropertyName("locked")]
        public bool Locked { get; set; }
    }

    public sealed class ProductItemInfo
    {
        public string? Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? ImageLink { get; set; }
        public string? DriveOwnerEmail { get; set; }
        public int? SlotIndex { get; set; }
    }

    public sealed record ProductItemLinkInput(string ItemCode, string Name, int SlotIndex);
    public sealed record ProductResolveResult(ProductInfo Product, bool Created, BomDefinitionInfo? Bom);
}
