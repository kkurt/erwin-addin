using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EliteSoft.Erwin.Admin.Models;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// HTTP client for erwin Mart Server API
    /// </summary>
    public sealed class MartApiClient : IMartApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClientHandler _handler;
        private readonly CookieContainer _cookieContainer;
        private readonly JsonSerializerOptions _jsonOptions;

        private string _baseUrl;
        private string _bearerToken;
        private string _xsrfToken;
        private string _rsaPublicKey;  // RSA public key (authKey) from appdata, used to encrypt state
        private bool _disposed;

        public bool IsAuthenticated => !string.IsNullOrEmpty(_bearerToken);
        public string BearerToken => _bearerToken;

        public event EventHandler<AuthenticationStateChangedEventArgs> AuthenticationStateChanged;
        public event EventHandler<LogEventArgs> LogMessage;

        public MartApiClient()
        {
            _cookieContainer = new CookieContainer();
            _handler = new HttpClientHandler
            {
                CookieContainer = _cookieContainer,
                UseCookies = true, // Let handler manage cookies automatically (including HttpOnly)
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
        }

        public async Task<AuthenticationResult> AuthenticateAsync(
            MartConnectionInfo connectionInfo,
            CancellationToken cancellationToken = default)
        {
            try
            {
                ClearSession();
                _baseUrl = connectionInfo.BaseUrl;

                // Try the auth/login endpoint first (as per curl analysis)
                var authUrl = $"{_baseUrl}/MartServerCloud/service/auth/login";

                // Step 1: GET to obtain XSRF token
                Log(LogLevel.Info, $"Step 1: GET for XSRF token");
                var initUrl = $"{_baseUrl}/MartServerCloud/jwt/authenticate/login";
                var getResult = await WebRequestGetAsync(initUrl);
                Log(LogLevel.Debug, $"Init GET Response: {getResult.StatusCode}");

                if (string.IsNullOrEmpty(_xsrfToken))
                {
                    Log(LogLevel.Warning, "No XSRF from init, trying alternative...");
                    // Try getting XSRF from main page
                    await WebRequestGetAsync(_baseUrl);
                }

                // Step 2: POST login credentials to auth/login endpoint
                Log(LogLevel.Info, $"Step 2: POST to {authUrl}");
                var authData = new { username = connectionInfo.Username, password = connectionInfo.Password };
                var postResult = await WebRequestPostAsync(authUrl, authData);
                Log(LogLevel.Debug, $"POST Response: {postResult.StatusCode}");
                Log(LogLevel.Debug, $"Login response: {postResult.Content}");

                if (postResult.StatusCode != 200)
                {
                    // Fallback to jwt/authenticate/login endpoint
                    Log(LogLevel.Info, "Trying fallback jwt/authenticate/login endpoint...");
                    authUrl = $"{_baseUrl}/MartServerCloud/jwt/authenticate/login";
                    postResult = await WebRequestPostAsync(authUrl, authData);
                    Log(LogLevel.Debug, $"Fallback POST Response: {postResult.StatusCode}");

                    if (postResult.StatusCode != 200)
                    {
                        return AuthenticationResult.Failed($"Authentication failed: HTTP {postResult.StatusCode}");
                    }
                }

                Log(LogLevel.Debug, $"Login response content: {postResult.Content.Substring(0, Math.Min(500, postResult.Content.Length))}");
                _bearerToken = ExtractToken(postResult.Content);

                if (string.IsNullOrEmpty(_bearerToken))
                {
                    return AuthenticationResult.Failed("No token received from server");
                }

                // Add JWT token as _access_token cookie (server expects this for session)
                AddSessionCookies(connectionInfo.Username);

                // Step 3: Initialize session by calling user/info
                Log(LogLevel.Info, "Step 3: GET user/info");
                var userInfo = await GetUserInfoAsync(cancellationToken);

                // Step 4: Get user modules
                Log(LogLevel.Info, "Step 4: GET user/getModules");
                await GetUserModulesAsync(cancellationToken);

                // Step 5: Call mart/appdata to complete session setup
                Log(LogLevel.Info, "Step 5: GET mart/appdata");
                await GetMartAppDataAsync(cancellationToken);

                LogCookies();

                OnAuthenticationStateChanged(true, userInfo?.Username);
                return AuthenticationResult.Successful(_bearerToken, userInfo);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Authentication error: {ex.Message}", ex);
                return AuthenticationResult.Failed(ex.Message);
            }
        }

        public async Task<CatalogResult> GetCatalogChildrenAsync(
            string parentId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Use /MartServer/ endpoint (not /MartServerCloud/) - this is the working endpoint
                var url = $"{_baseUrl}/MartServer/service/catalog/getCatalogChildren";

                // Build FormData payload (NOT JSON) - as per Mart Portal browser analysis
                // The JavaScript uses: new FormData() with .append() calls
                // id: "0" for root, or parent entry id
                // state: must be encrypted with RSA public key from appdata
                // getHiddenVersions: "" (empty string in browser)
                // connector: false

                // State handling:
                // - For root (id=0), state should be EMPTY (tested: empty state works!)
                // - For children, state comes from parent's response
                var entryId = string.IsNullOrEmpty(parentId) ? "0" : parentId;

                // For initial call (id=0), use empty state
                // State value only needed for subsequent calls with parent's state
                var stateValue = "";  // Empty for root call - this works!
                Log(LogLevel.Debug, $"State value: '{stateValue}'");

                Log(LogLevel.Debug, $"Catalog POST: {url}");
                Log(LogLevel.Debug, $"Payload: entryid={entryId}");
                Log(LogLevel.Debug, $"Cookies being sent: {GetCookieHeaderValue()}");

                // Use FormData POST (application/x-www-form-urlencoded)
                var result = await WebRequestPostFormDataAsync(url, entryId, stateValue);
                Log(LogLevel.Debug, $"Catalog Response: {result.StatusCode}");

                if (result.StatusCode != 200)
                {
                    Log(LogLevel.Error, $"Catalog failed: {result.Content}");
                    return CatalogResult.Failed($"HTTP {result.StatusCode}: {result.Content}");
                }

                var content = result.Content;
                Log(LogLevel.Debug, $"Raw catalog response: {content.Substring(0, Math.Min(500, content.Length))}");
                content = StripResponsePrefix(content);

                var catalogResponse = JsonSerializer.Deserialize<CatalogChildrenResponse>(content, _jsonOptions);
                var entries = catalogResponse?.GetChildCatalogs ?? new List<CatalogEntry>();
                Log(LogLevel.Info, $"Catalog returned {entries.Count} entries");
                return CatalogResult.Successful(entries);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Catalog error: {ex.Message}", ex);
                return CatalogResult.Failed(ex.Message);
            }
        }

        public async Task<UserInfo> GetUserInfoAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"{_baseUrl}/MartServerCloud/service/user/info";
                var request = CreateRequest(HttpMethod.Get, url);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                ProcessSetCookieHeaders(response);

                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<UserInfo>>(content, _jsonOptions);
                return apiResponse?.Data;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Get user info failed: {ex.Message}");
                return null;
            }
        }

        public void ClearSession()
        {
            _bearerToken = null;
            _xsrfToken = null;
            _baseUrl = null;
            _rsaPublicKey = null;

            // Note: CookieContainer doesn't have a clear method in .NET 4.8
            // Cookies will be expired naturally when making new requests

            OnAuthenticationStateChanged(false, null);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _httpClient.Dispose();
            _handler.Dispose();
        }

        #region Private Methods

        private HttpRequestMessage CreateRequest(HttpMethod method, string url, object payload = null)
        {
            var request = new HttpRequestMessage(method, url);

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrEmpty(_bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
            }

            if (!string.IsNullOrEmpty(_xsrfToken))
            {
                request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", _xsrfToken);
            }

            request.Headers.TryAddWithoutValidation("X-APP-TYPE", "0");

            // Cookies are now automatically managed by HttpClientHandler
            // No need to manually add Cookie header

            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload, _jsonOptions);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return request;
        }

        private void ProcessSetCookieHeaders(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;

            foreach (var setCookie in cookies)
            {
                Log(LogLevel.Debug, $"Set-Cookie (full): {setCookie}");

                // Check if this looks like a truncated cookie (just attributes, no name=value)
                if (setCookie.StartsWith("HttpOnly") || setCookie.StartsWith("Secure") ||
                    setCookie.StartsWith("Path") || setCookie.StartsWith("SameSite"))
                {
                    Log(LogLevel.Warning, "Cookie appears truncated - HttpOnly cookies may not be captured by HttpClient");
                    continue;
                }

                ParseAndAddCookie(setCookie);
            }

            // Also check raw headers
            if (response.Headers.Contains("Set-Cookie"))
            {
                var cookieList = new List<string>(response.Headers.GetValues("Set-Cookie"));
                Log(LogLevel.Debug, $"Raw Set-Cookie count: {cookieList.Count}");
            }
        }

        private void ParseAndAddCookie(string setCookieHeader)
        {
            try
            {
                var parts = setCookieHeader.Split(';');
                if (parts.Length == 0) return;

                var nameValue = parts[0].Trim();
                var eqIndex = nameValue.IndexOf('=');
                if (eqIndex <= 0) return;

                var cookieName = nameValue.Substring(0, eqIndex).Trim();
                var cookieValue = nameValue.Substring(eqIndex + 1).Trim();

                // Skip attribute names that might appear as cookie names
                var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "HttpOnly", "Secure", "Path", "Domain", "Expires", "Max-Age", "SameSite"
                };

                if (string.IsNullOrEmpty(cookieName) || skipNames.Contains(cookieName)) return;

                // Store XSRF token for header and update NEKOT cookie
                if (cookieName.Equals("XSRF-TOKEN", StringComparison.OrdinalIgnoreCase))
                {
                    _xsrfToken = cookieValue;

                    // Also update NEKOT-FRSX-REGGAWS cookie (Mart Portal requirement)
                    var uri2 = new Uri(_baseUrl);
                    var nekotCookie = new Cookie("NEKOT-FRSX-REGGAWS", cookieValue, "/", uri2.Host);
                    _cookieContainer.Add(uri2, nekotCookie);
                    Log(LogLevel.Debug, $"  Updated NEKOT-FRSX-REGGAWS cookie");
                }

                // Add cookie with root path
                var uri = new Uri(_baseUrl);
                var cookie = new Cookie(cookieName, cookieValue, "/", uri.Host);
                _cookieContainer.Add(uri, cookie);

                Log(LogLevel.Debug, $"  Added cookie: {cookieName}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Cookie parse error: {ex.Message}");
            }
        }

        private string GetCookieHeaderValue()
        {
            if (string.IsNullOrEmpty(_baseUrl)) return string.Empty;

            var cookies = new List<string>();
            foreach (Cookie c in _cookieContainer.GetCookies(new Uri(_baseUrl)))
            {
                cookies.Add($"{c.Name}={c.Value}");
            }
            return string.Join("; ", cookies);
        }

        private static string ExtractToken(string responseContent)
        {
            // JWT token returned directly
            if (responseContent.StartsWith("eyJ"))
            {
                return responseContent.Trim().Trim('"');
            }

            // Token in JSON response
            try
            {
                using (var doc = JsonDocument.Parse(responseContent))
                {
                    if (doc.RootElement.TryGetProperty("id_token", out var tokenElement))
                    {
                        return tokenElement.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("access_token", out tokenElement))
                    {
                        return tokenElement.GetString();
                    }
                }
            }
            catch { }

            return null;
        }

        private static string StripResponsePrefix(string content)
        {
            // Some responses have "N|" prefix where N is a digit
            if (content.Length > 2 && char.IsDigit(content[0]) && content[1] == '|')
            {
                return content.Substring(2);
            }
            return content;
        }

        private async Task GetUserModulesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var url = $"{_baseUrl}/MartServerCloud/service/user/getModules";
                var request = CreateRequest(HttpMethod.Get, url);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                ProcessSetCookieHeaders(response);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    content = StripResponsePrefix(content);
                    Log(LogLevel.Debug, $"Modules response: {content.Substring(0, Math.Min(200, content.Length))}");
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Get modules failed: {ex.Message}");
            }
        }

        private async Task GetMartAppDataAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Use /MartServer/ path (not /MartServerCloud/) - this endpoint works correctly
                var url = $"{_baseUrl}/MartServer/service/mart/appdata";
                var request = CreateRequest(HttpMethod.Get, url);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                ProcessSetCookieHeaders(response);

                var content = await response.Content.ReadAsStringAsync();
                Log(LogLevel.Debug, $"AppData status: {(int)response.StatusCode}, content length: {content?.Length ?? 0}");

                if (response.IsSuccessStatusCode)
                {
                    content = StripResponsePrefix(content);
                    Log(LogLevel.Debug, $"AppData response: {content.Substring(0, Math.Min(500, content.Length))}");

                    // Extract authKey (RSA public key) from appdata response
                    try
                    {
                        using (var doc = JsonDocument.Parse(content))
                        {
                            // authKey is inside the data object
                            if (doc.RootElement.TryGetProperty("data", out var dataElement))
                            {
                                if (dataElement.TryGetProperty("authKey", out var authKeyElement))
                                {
                                    _rsaPublicKey = authKeyElement.GetString();
                                    Log(LogLevel.Debug, $"Got RSA public key (authKey): {_rsaPublicKey?.Substring(0, Math.Min(80, _rsaPublicKey?.Length ?? 0))}...");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Get app data failed: {ex.Message}");
            }
        }

        private void LogCookies()
        {
            if (string.IsNullOrEmpty(_baseUrl)) return;

            var uri = new Uri(_baseUrl);
            var cookies = _cookieContainer.GetCookies(uri);
            Log(LogLevel.Debug, $"=== Cookie Container for {uri.Host}: {cookies.Count} cookies ===");
            foreach (Cookie c in cookies)
            {
                Log(LogLevel.Debug, $"  {c.Name}={c.Value.Substring(0, Math.Min(20, c.Value.Length))}... (Path={c.Path}, HttpOnly={c.HttpOnly})");
            }
        }

        /// <summary>
        /// Encrypts the state value using RSA public key (for catalog API calls)
        /// </summary>
        private string EncryptStateWithRsa(string plainText)
        {
            if (string.IsNullOrEmpty(_rsaPublicKey))
            {
                Log(LogLevel.Warning, "No RSA public key available for encryption");
                return plainText;
            }

            try
            {
                // The authKey from appdata is a base64-encoded X.509 SubjectPublicKeyInfo DER format
                var keyBytes = Convert.FromBase64String(_rsaPublicKey);

                // Parse the X.509 SubjectPublicKeyInfo structure to extract RSA parameters
                var rsaParams = ParseX509PublicKey(keyBytes);

                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.ImportParameters(rsaParams);

                    // Encrypt the plain text using PKCS#1 v1.5 padding (most common for JS interop)
                    var plainBytes = Encoding.UTF8.GetBytes(plainText);
                    var encryptedBytes = rsa.Encrypt(plainBytes, false); // false = PKCS#1 padding

                    // Return as base64
                    return Convert.ToBase64String(encryptedBytes);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"RSA encryption failed: {ex.Message}");
                return plainText;
            }
        }

        /// <summary>
        /// Parses an X.509 SubjectPublicKeyInfo DER-encoded public key
        /// </summary>
        private RSAParameters ParseX509PublicKey(byte[] x509Key)
        {
            // X.509 SubjectPublicKeyInfo structure:
            // SEQUENCE {
            //   SEQUENCE {
            //     OBJECT IDENTIFIER (rsaEncryption 1.2.840.113549.1.1.1)
            //     NULL
            //   }
            //   BIT STRING {
            //     SEQUENCE {
            //       INTEGER (modulus)
            //       INTEGER (exponent)
            //     }
            //   }
            // }

            using (var ms = new MemoryStream(x509Key))
            using (var reader = new BinaryReader(ms))
            {
                // Read outer SEQUENCE
                if (reader.ReadByte() != 0x30)
                    throw new FormatException("Invalid X.509 key format: expected SEQUENCE");

                ReadDerLength(reader);

                // Read AlgorithmIdentifier SEQUENCE
                if (reader.ReadByte() != 0x30)
                    throw new FormatException("Invalid X.509 key format: expected AlgorithmIdentifier SEQUENCE");

                var algIdLen = ReadDerLength(reader);
                reader.ReadBytes(algIdLen); // Skip algorithm identifier (OID + NULL)

                // Read BIT STRING containing the public key
                if (reader.ReadByte() != 0x03)
                    throw new FormatException("Invalid X.509 key format: expected BIT STRING");

                ReadDerLength(reader);
                reader.ReadByte(); // Skip unused bits indicator (should be 0)

                // Now we have the PKCS#1 RSAPublicKey structure
                // SEQUENCE { INTEGER modulus, INTEGER exponent }
                if (reader.ReadByte() != 0x30)
                    throw new FormatException("Invalid RSA key format: expected SEQUENCE");

                ReadDerLength(reader);

                // Read modulus
                var modulus = ReadDerInteger(reader);

                // Read exponent
                var exponent = ReadDerInteger(reader);

                return new RSAParameters
                {
                    Modulus = modulus,
                    Exponent = exponent
                };
            }
        }

        private int ReadDerLength(BinaryReader reader)
        {
            var firstByte = reader.ReadByte();
            if (firstByte < 0x80)
                return firstByte;

            var numBytes = firstByte & 0x7F;
            int length = 0;
            for (int i = 0; i < numBytes; i++)
            {
                length = (length << 8) | reader.ReadByte();
            }
            return length;
        }

        private byte[] ReadDerInteger(BinaryReader reader)
        {
            if (reader.ReadByte() != 0x02)
                throw new FormatException("Expected INTEGER tag");

            var length = ReadDerLength(reader);
            var bytes = reader.ReadBytes(length);

            // Remove leading zero if present (used for positive sign in DER)
            if (bytes.Length > 1 && bytes[0] == 0)
            {
                var trimmed = new byte[bytes.Length - 1];
                Array.Copy(bytes, 1, trimmed, 0, trimmed.Length);
                return trimmed;
            }

            return bytes;
        }

        private void AddSessionCookies(string username)
        {
            if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_bearerToken)) return;

            var uri = new Uri(_baseUrl);

            // Add _access_token cookie with JWT token
            var accessTokenCookie = new Cookie("_access_token", _bearerToken, "/", uri.Host)
            {
                HttpOnly = true
            };
            _cookieContainer.Add(uri, accessTokenCookie);
            Log(LogLevel.Debug, $"Added _access_token cookie (JWT token)");

            // Add username cookie
            if (!string.IsNullOrEmpty(username))
            {
                var usernameCookie = new Cookie("username", username, "/", uri.Host);
                _cookieContainer.Add(uri, usernameCookie);
                Log(LogLevel.Debug, $"Added username cookie: {username}");
            }

            // Generate a session ID
            var sessionId = Guid.NewGuid().ToString();
            var sidCookie = new Cookie("sid", sessionId, "/", uri.Host)
            {
                HttpOnly = true
            };
            _cookieContainer.Add(uri, sidCookie);
            Log(LogLevel.Debug, $"Added sid cookie");

            // Add jsid (Java Session ID) - generate or extract from JWT
            var jsidCookie = new Cookie("jsid", sessionId, "/", uri.Host)
            {
                HttpOnly = true
            };
            _cookieContainer.Add(uri, jsidCookie);
            Log(LogLevel.Debug, $"Added jsid cookie");

            // Add NEKOT-FRSX-REGGAWS cookie (required by Mart Portal - reverse of XSRF-TOKEN)
            if (!string.IsNullOrEmpty(_xsrfToken))
            {
                var nekotCookie = new Cookie("NEKOT-FRSX-REGGAWS", _xsrfToken, "/", uri.Host);
                _cookieContainer.Add(uri, nekotCookie);
                Log(LogLevel.Debug, $"Added NEKOT-FRSX-REGGAWS cookie");
            }
        }

        #region WebRequest Methods for Raw Cookie Access

        private async Task<WebRequestResult> WebRequestGetAsync(string url)
        {
            return await Task.Run(() =>
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/json";
                request.CookieContainer = _cookieContainer;
                request.AllowAutoRedirect = true;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                return ExecuteWebRequest(request);
            });
        }

        private async Task<WebRequestResult> WebRequestPostAsync(string url, object payload)
        {
            return await Task.Run(() =>
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Accept = "application/json, text/plain, */*";
                request.ContentType = "application/json";
                request.CookieContainer = _cookieContainer;
                request.AllowAutoRedirect = true;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                // Add Authorization header with Bearer token
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {_bearerToken}");
                }

                // Add XSRF token header
                if (!string.IsNullOrEmpty(_xsrfToken))
                {
                    request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);
                }
                request.Headers.Add("X-APP-TYPE", "0");

                // Write body
                var json = JsonSerializer.Serialize(payload, _jsonOptions);
                var data = Encoding.UTF8.GetBytes(json);
                request.ContentLength = data.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                return ExecuteWebRequest(request);
            });
        }

        private async Task<WebRequestResult> WebRequestPostFormDataAsync(string url, string entryId, string encryptedState)
        {
            return await Task.Run(() =>
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Accept = "application/json, text/plain, */*";
                request.ContentType = "application/x-www-form-urlencoded";
                request.CookieContainer = _cookieContainer;
                request.AllowAutoRedirect = true;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                // Add Authorization header with Bearer token
                if (!string.IsNullOrEmpty(_bearerToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {_bearerToken}");
                }

                // Add XSRF token header
                if (!string.IsNullOrEmpty(_xsrfToken))
                {
                    request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);
                }
                request.Headers.Add("X-APP-TYPE", "0");

                // Build FormData body (tested working configuration)
                // id: entry id (0 for root)
                // state: empty for initial call
                // getHiddenVersions: "N" to exclude hidden versions
                // connector: false
                var formData = new StringBuilder();
                formData.Append($"id={Uri.EscapeDataString(entryId)}");
                formData.Append($"&state={Uri.EscapeDataString(encryptedState)}");
                formData.Append($"&getHiddenVersions=N");
                formData.Append($"&connector=false");

                var data = Encoding.UTF8.GetBytes(formData.ToString());
                request.ContentLength = data.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                return ExecuteWebRequest(request);
            });
        }

        private WebRequestResult ExecuteWebRequest(HttpWebRequest request)
        {
            HttpWebResponse response = null;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    Log(LogLevel.Error, $"WebRequest failed: {ex.Message}");
                    return new WebRequestResult { StatusCode = 0, Content = ex.Message };
                }
            }

            using (response)
            {
                // Process raw Set-Cookie headers - this gives us access to HttpOnly cookies
                ProcessRawSetCookieHeaders(response);

                // Read response content
                string content;
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    content = reader.ReadToEnd();
                }

                return new WebRequestResult
                {
                    StatusCode = (int)response.StatusCode,
                    Content = content
                };
            }
        }

        private void ProcessRawSetCookieHeaders(HttpWebResponse response)
        {
            // Get all Set-Cookie headers as raw strings
            var setCookieHeaders = response.Headers.GetValues("Set-Cookie");
            if (setCookieHeaders == null) return;

            Log(LogLevel.Debug, $"=== Raw Set-Cookie headers ({setCookieHeaders.Length}) ===");

            foreach (var header in setCookieHeaders)
            {
                Log(LogLevel.Debug, $"  Raw: {header}");
                ParseRawCookie(header);
            }

            // Also log what CookieContainer captured automatically
            var uri = new Uri(_baseUrl);
            var autoCookies = _cookieContainer.GetCookies(uri);
            Log(LogLevel.Debug, $"CookieContainer now has {autoCookies.Count} cookies");
        }

        private void ParseRawCookie(string setCookieHeader)
        {
            if (string.IsNullOrEmpty(setCookieHeader)) return;

            try
            {
                // Parse: name=value; Path=/; HttpOnly; Secure
                var parts = setCookieHeader.Split(';');
                if (parts.Length == 0) return;

                var firstPart = parts[0].Trim();
                var eqIndex = firstPart.IndexOf('=');
                if (eqIndex <= 0) return;

                var cookieName = firstPart.Substring(0, eqIndex).Trim();
                var cookieValue = firstPart.Substring(eqIndex + 1).Trim();

                // Skip if it's an attribute name (malformed cookie)
                var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "HttpOnly", "Secure", "Path", "Domain", "Expires", "Max-Age", "SameSite"
                };

                if (string.IsNullOrEmpty(cookieName) || skipNames.Contains(cookieName))
                {
                    Log(LogLevel.Warning, $"Skipping malformed cookie: {firstPart}");
                    return;
                }

                // Extract path from attributes
                string path = "/";
                bool httpOnly = false;
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (trimmed.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                    {
                        path = trimmed.Substring(5);
                    }
                    else if (trimmed.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        httpOnly = true;
                    }
                }

                // Store XSRF token for header
                if (cookieName.Equals("XSRF-TOKEN", StringComparison.OrdinalIgnoreCase))
                {
                    _xsrfToken = cookieValue;
                    Log(LogLevel.Debug, $"  Captured XSRF-TOKEN");
                }

                // Add to cookie container
                var uri = new Uri(_baseUrl);
                var cookie = new Cookie(cookieName, cookieValue, path, uri.Host)
                {
                    HttpOnly = httpOnly
                };

                // Check if cookie already exists
                var existingCookies = _cookieContainer.GetCookies(uri);
                bool exists = false;
                foreach (Cookie c in existingCookies)
                {
                    if (c.Name == cookieName)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    _cookieContainer.Add(uri, cookie);
                    Log(LogLevel.Debug, $"  Added cookie: {cookieName} (HttpOnly={httpOnly})");
                }
                else
                {
                    Log(LogLevel.Debug, $"  Cookie already exists: {cookieName}");
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Failed to parse cookie: {ex.Message}");
            }
        }

        #endregion

        private void Log(LogLevel level, string message, Exception ex = null)
        {
            LogMessage?.Invoke(this, new LogEventArgs(level, message, ex));
        }

        private void OnAuthenticationStateChanged(bool isAuthenticated, string username)
        {
            AuthenticationStateChanged?.Invoke(this, new AuthenticationStateChangedEventArgs(isAuthenticated, username));
        }

        #endregion
    }

    /// <summary>
    /// Result from WebRequest operations
    /// </summary>
    internal class WebRequestResult
    {
        public int StatusCode { get; set; }
        public string Content { get; set; }
    }
}
