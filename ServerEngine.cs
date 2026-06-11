using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoncaAudioInspector
{
    public class ServerEngine
    {
        // Global variables stored for later use as requested
        public static string? StaffID { get; set; }
        public static string? ProductID { get; set; }
        public static string? TokenApp { get; set; }

        private static readonly HttpClient client = new HttpClient();

        /// <summary>
        /// Requests a temporary token from the server using hardcoded admin credentials.
        /// </summary>
        public static async Task<bool> RequestTokenAsync()
        {
            TokenApp = null;

            // Mock check: for local testing, if offline/no API, we can fall back to mock token
            // In case real connection fails, we log it, but here is the request implementation:
            try
            {
                string requestUrl = "https://api.sonca.vn/token"; // Placeholder API URL

                // Fixed hardcoded admin user and pass
                var requestBody = new
                {
                    user = "admin",
                    pass = "admin"
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                client.Timeout = TimeSpan.FromSeconds(5);
                HttpResponseMessage response = await client.PostAsync(requestUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    string responseJson = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(responseJson))
                    {
                        JsonElement root = doc.RootElement;
                        if (root.TryGetProperty("token", out JsonElement tokenElement) && tokenElement.ValueKind != JsonValueKind.Null)
                        {
                            string? tokenVal = tokenElement.GetString();
                            if (!string.IsNullOrEmpty(tokenVal))
                            {
                                TokenApp = tokenVal;
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ServerEngine Exception] Error during RequestToken: {ex}");
            }

            // Fallback mock token for standard offline developer environment testing:
            // Remove/modify this fallback if you strictly want it to fail without a real server connection.
            #if DEBUG
            TokenApp = "MOCK-DEV-TOKEN-12345";
            return true;
            #else
            return false;
            #endif
        }

        /// <summary>
        /// Authenticates the user with the server using the provided username and password.
        /// </summary>
        public static async Task<bool> AuthenticateAsync(string username, string password)
        {
            // Reset cached values
            StaffID = null;

            // Mock behavior for testing when no server is connected or for default developer login:
            if (username.Equals("admin", StringComparison.OrdinalIgnoreCase) && password == "admin")
            {
                // Pre-fill mock data matching the requested validation pattern
                StaffID = "STAFF-001";
                return true;
            }

            try
            {
                string requestUrl = "https://api.sonca.vn/login"; // Placeholder API URL

                var requestBody = new
                {
                    username = username,
                    password = password
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                
                // Construct request manually to add Authorization headers dynamically
                using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                {
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    
                    // Attach Bearer token obtained from RequestTokenAsync
                    if (!string.IsNullOrEmpty(TokenApp))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenApp);
                    }

                    client.Timeout = TimeSpan.FromSeconds(5);
                    HttpResponseMessage response = await client.SendAsync(request);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        string responseJson = await response.Content.ReadAsStringAsync();
                        
                        // Parse response using JsonDocument to dynamically extract staffID
                        using (JsonDocument doc = JsonDocument.Parse(responseJson))
                        {
                            JsonElement root = doc.RootElement;
                            
                            if (root.TryGetProperty("staffID", out JsonElement staffIdElement) && staffIdElement.ValueKind != JsonValueKind.Null)
                            {
                                string? staffIdVal = staffIdElement.GetString();
                                if (!string.IsNullOrEmpty(staffIdVal))
                                {
                                    StaffID = staffIdVal;                                
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ServerEngine Exception] Error during authentication: {ex}");
            }

            return false;
        }
    }
}
