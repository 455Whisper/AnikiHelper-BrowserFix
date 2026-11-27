using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnikiHelper.Services.Spotify
{
    internal sealed class SpotifyApiResult
    {
        public int Status;
        public JToken Body;

        public bool Ok
        {
            get { return Status >= 200 && Status < 300; }
        }
    }

    /// <summary>
    /// Minimal Spotify Web API client: Authorization Code + PKCE sign-in through a
    /// loopback redirect (http://127.0.0.1:PORT/callback) and token refresh.
    /// The Client ID is read from client_id.txt; tokens are stored with DPAPI.
    /// </summary>
    internal sealed class SpotifyApiClient : IDisposable
    {
        public const int RedirectPort = 43821;
        public const string RedirectUri = "http://127.0.0.1:43821/callback";

        private const string ApiBase = "https://api.spotify.com/v1";
        private const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
        private const string TokenUrl = "https://accounts.spotify.com/api/token";
        private const string Scopes =
            "user-read-playback-state user-modify-playback-state user-read-currently-playing " +
            "user-read-recently-played playlist-read-private playlist-read-collaborative " +
            "user-library-read user-library-modify user-read-private";

        private readonly ILogger logger;
        private readonly string dataDir;
        private readonly HttpClient http;
        private readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);

        private string clientId;
        private string accessToken;
        private string refreshToken;
        private DateTime accessTokenExpiresUtc = DateTime.MinValue;

        public SpotifyApiClient(ILogger logger, string dataDir)
        {
            this.logger = logger;
            this.dataDir = dataDir;

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
            }

            http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            Reload();
        }

        public string DataDirectory
        {
            get { return dataDir; }
        }

        public string ClientIdFilePath
        {
            get { return Path.Combine(dataDir, "client_id.txt"); }
        }

        private string TokenFilePath
        {
            get { return Path.Combine(dataDir, "tokens.bin"); }
        }

        public bool HasClientId
        {
            get { return !string.IsNullOrWhiteSpace(clientId); }
        }

        public bool IsSignedIn
        {
            get { return HasClientId && !string.IsNullOrEmpty(refreshToken); }
        }

        public void Reload()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                if (!File.Exists(ClientIdFilePath))
                {
                    File.WriteAllText(ClientIdFilePath, string.Empty);
                }

                clientId = (File.ReadAllText(ClientIdFilePath) ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not read client_id.txt.");
                clientId = null;
            }

            LoadTokens();
        }

        public void SignOut()
        {
            accessToken = null;
            refreshToken = null;
            accessTokenExpiresUtc = DateTime.MinValue;
            try
            {
                if (File.Exists(TokenFilePath))
                {
                    File.Delete(TokenFilePath);
                }
            }
            catch
            {
            }
        }

        private void LoadTokens()
        {
            accessToken = null;
            refreshToken = null;
            accessTokenExpiresUtc = DateTime.MinValue;

            try
            {
                if (!File.Exists(TokenFilePath))
                {
                    return;
                }

                var bytes = ProtectedData.Unprotect(
                    File.ReadAllBytes(TokenFilePath),
                    null,
                    DataProtectionScope.CurrentUser);
                var json = JObject.Parse(Encoding.UTF8.GetString(bytes));
                if ((string)json["client_id"] != clientId)
                {
                    return;
                }

                refreshToken = (string)json["refresh_token"];
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not read saved Spotify sign-in.");
            }
        }

        private void SaveTokens()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                var json = new JObject
                {
                    ["client_id"] = clientId,
                    ["refresh_token"] = refreshToken
                };
                var bytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(json.ToString()),
                    null,
                    DataProtectionScope.CurrentUser);
                File.WriteAllBytes(TokenFilePath, bytes);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not save Spotify sign-in.");
            }
        }

        // ---------------- Sign-in (PKCE + loopback) ----------------

        public async Task<bool> SignInAsync(Action<string> openUrl, CancellationToken cancellation)
        {
            if (!HasClientId)
            {
                return false;
            }

            var verifier = RandomUrlSafe(64);
            string challenge;
            using (var sha = SHA256.Create())
            {
                challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }

            var state = RandomUrlSafe(16);
            var url = AuthorizeUrl +
                      "?response_type=code" +
                      "&client_id=" + Uri.EscapeDataString(clientId) +
                      "&scope=" + Uri.EscapeDataString(Scopes) +
                      "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                      "&state=" + state +
                      "&code_challenge_method=S256" +
                      "&code_challenge=" + challenge;

            var listener = new TcpListener(IPAddress.Loopback, RedirectPort);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Could not listen on the sign-in port.");
                return false;
            }

            try
            {
                openUrl?.Invoke(url);

                var deadline = DateTime.UtcNow.AddMinutes(5);
                Task<TcpClient> acceptTask = null;
                while (DateTime.UtcNow < deadline && !cancellation.IsCancellationRequested)
                {
                    if (acceptTask == null)
                    {
                        acceptTask = listener.AcceptTcpClientAsync();
                    }

                    var finished = await Task.WhenAny(acceptTask, Task.Delay(1000, cancellation)).ConfigureAwait(true);
                    if (finished != acceptTask)
                    {
                        continue;
                    }

                    var accepted = acceptTask.Result;
                    acceptTask = null;
                    using (var client = accepted)
                    using (var stream = client.GetStream())
                    {
                        var requestLine = await ReadRequestLineAsync(stream).ConfigureAwait(true);
                        var query = ParseQuery(requestLine);

                        string code;
                        string returnedState;
                        string error;
                        query.TryGetValue("code", out code);
                        query.TryGetValue("state", out returnedState);
                        query.TryGetValue("error", out error);

                        var success = !string.IsNullOrEmpty(code) && returnedState == state;
                        await WriteHtmlAsync(
                            stream,
                            success
                                ? "Spotify is connected to Aniki. You can return to Playnite."
                                : "Spotify sign-in did not complete" + (string.IsNullOrEmpty(error) ? "." : ": " + error))
                            .ConfigureAwait(true);

                        if (requestLine.IndexOf("/callback", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        if (!success)
                        {
                            return false;
                        }

                        return await ExchangeCodeAsync(code, verifier).ConfigureAwait(true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Sign-in failed.");
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            return false;
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var builder = new StringBuilder();
            stream.ReadTimeout = 5000;
            while (builder.Length < 65536)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (builder.ToString().Contains("\r\n"))
                {
                    break;
                }
            }

            var text = builder.ToString();
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            return end >= 0 ? text.Substring(0, end) : text;
        }

        private static Dictionary<string, string> ParseQuery(string requestLine)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = (requestLine ?? string.Empty).Split(' ');
            if (parts.Length < 2)
            {
                return result;
            }

            var target = parts[1];
            var q = target.IndexOf('?');
            if (q < 0)
            {
                return result;
            }

            foreach (var pair in target.Substring(q + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                result[Uri.UnescapeDataString(pair.Substring(0, eq))] =
                    Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }

            return result;
        }

        private static async Task WriteHtmlAsync(NetworkStream stream, string message)
        {
            var html =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Aniki Spotify</title></head>" +
                "<body style=\"background:#121212;color:#fff;font-family:Segoe UI,sans-serif;display:flex;" +
                "align-items:center;justify-content:center;height:100vh;margin:0;font-size:28px\">" +
                "<div>" + WebUtilityEncode(message) + "</div></body></html>";
            var body = Encoding.UTF8.GetBytes(html);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
                body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string WebUtilityEncode(string text)
        {
            return WebUtility.HtmlEncode(text ?? string.Empty);
        }

        private async Task<bool> ExchangeCodeAsync(string code, string verifier)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier
            };

            return await RequestTokenAsync(form).ConfigureAwait(true);
        }

        private async Task<bool> RequestTokenAsync(Dictionary<string, string> form)
        {
            try
            {
                using (var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form)).ConfigureAwait(true))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger?.Warn("[AnikiHelper][Spotify] Token request failed: " + (int)response.StatusCode + " " + text);
                        if ((int)response.StatusCode == 400 && text.Contains("invalid_grant"))
                        {
                            SignOut();
                        }

                        return false;
                    }

                    var json = JObject.Parse(text);
                    accessToken = (string)json["access_token"];
                    var expires = (int?)json["expires_in"] ?? 3600;
                    accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expires - 60));
                    var newRefresh = (string)json["refresh_token"];
                    if (!string.IsNullOrEmpty(newRefresh))
                    {
                        refreshToken = newRefresh;
                        SaveTokens();
                    }

                    return !string.IsNullOrEmpty(accessToken);
                }
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, "[AnikiHelper][Spotify] Token request error.");
                return false;
            }
        }

        private async Task<bool> EnsureAccessTokenAsync(bool force)
        {
            if (!IsSignedIn)
            {
                return false;
            }

            if (!force && !string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < accessTokenExpiresUtc)
            {
                return true;
            }

            await tokenLock.WaitAsync().ConfigureAwait(true);
            try
            {
                if (!force && !string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < accessTokenExpiresUtc)
                {
                    return true;
                }

                return await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = clientId
                }).ConfigureAwait(true);
            }
            finally
            {
                tokenLock.Release();
            }
        }

        // ---------------- API calls ----------------

        // Rate limiting: after a 429, every call waits out Spotify's Retry-After (doubling on
        // repeats up to 15 min, never shorter than Spotify asks) instead of hammering the API, which only extends the block.
        private DateTime blockedUntilUtc = DateTime.MinValue;
        private double backoffSeconds;

        public bool IsRateLimited
        {
            get { return DateTime.UtcNow < blockedUntilUtc; }
        }

        public async Task<SpotifyApiResult> SendAsync(HttpMethod method, string path, JToken body = null)
        {
            var result = new SpotifyApiResult();
            if (DateTime.UtcNow < blockedUntilUtc)
            {
                result.Status = 429;
                return result;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!await EnsureAccessTokenAsync(attempt > 0).ConfigureAwait(true))
                {
                    result.Status = 401;
                    return result;
                }

                try
                {
                    using (var request = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ApiBase + path))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                        if (body != null)
                        {
                            request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                        }
                        else if (method == HttpMethod.Put || method == HttpMethod.Post)
                        {
                            request.Content = new StringContent(string.Empty);
                        }

                        using (var response = await http.SendAsync(request).ConfigureAwait(true))
                        {
                            result.Status = (int)response.StatusCode;
                            if (result.Status == 401 && attempt == 0)
                            {
                                continue;
                            }

                            if (result.Status == 429)
                            {
                                var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0;
                                backoffSeconds = Math.Max(retry, Math.Min(900, backoffSeconds > 0 ? backoffSeconds * 2 : 30));
                                blockedUntilUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
                                logger?.Warn("[AnikiHelper][Spotify] Rate limited (" + method + " " + path + "), pausing all requests for " + (int)backoffSeconds + " s (Retry-After " + (int)retry + " s).");
                                return result;
                            }

                            if (result.Ok)
                            {
                                backoffSeconds = 0;
                            }

                            var text = response.Content == null
                                ? null
                                : await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                try { result.Body = JToken.Parse(text); } catch { }
                            }

                            if (!result.Ok && result.Status != 404)
                            {
                                logger?.Warn("[AnikiHelper][Spotify] " + method + " " + path + " -> " + result.Status + " " + text);
                            }

                            return result;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, "[AnikiHelper][Spotify] Request failed: " + method + " " + path);
                    result.Status = 0;
                    return result;
                }
            }

            return result;
        }

        private static string RandomUrlSafe(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var builder = new StringBuilder(length);
            foreach (var b in bytes)
            {
                builder.Append(chars[b % chars.Length]);
            }

            return builder.ToString();
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public void Dispose()
        {
            try { http.Dispose(); } catch { }
        }
    }
}
