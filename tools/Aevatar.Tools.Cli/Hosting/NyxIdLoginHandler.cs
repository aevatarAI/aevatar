using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace Aevatar.Tools.Cli.Hosting;

/// <summary>
/// Handles NyxID CLI login flows: browser-based (default) and password-based (headless).
/// Mirrors the pattern from NyxID's own CLI (<c>login_cli.rs</c>).
/// </summary>
internal static class NyxIdLoginHandler
{
    private const int CallbackTimeoutSeconds = 120;
    private const string CliUserAgent = "aevatar-cli/1.0";

    // ─── Browser login ───

    public static async Task<bool> BrowserLoginAsync(CancellationToken ct)
    {
        var authority = NyxIdTokenStore.ResolveAuthority();

        // Fetch frontend URL from NyxID server
        string frontendUrl;
        try
        {
            frontendUrl = await FetchFrontendUrlAsync(authority, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to reach NyxID server at {authority}: {ex.Message}");
            return false;
        }

        // Bind local callback server on random port
        var listener = new HttpListener();
        var port = GetAvailablePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var state = GenerateState();
        var authUrl = BuildCliAuthUrl(frontendUrl, port, state);

        Console.Error.WriteLine("Opening browser to log in...");
        Console.Error.WriteLine();
        Console.Error.WriteLine("If the browser does not open, visit:");
        Console.Error.WriteLine($"  {authUrl}");
        Console.Error.WriteLine();

        BrowserLauncher.Open(authUrl);

        // Wait for callback
        try
        {
            var token = await WaitForCallbackAsync(listener, state, ct);
            if (token is null)
            {
                Console.Error.WriteLine("Login failed: no token received.");
                return false;
            }

            // Fetch user info
            var (email, name) = await FetchUserInfoAsync(authority, token, ct);
            NyxIdTokenStore.SaveToken(token, email, name);

            Console.Error.WriteLine($"Logged in successfully{(email is not null ? $" as {email}" : "")}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Login cancelled.");
            return false;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($"Login timed out after {CallbackTimeoutSeconds}s. Please try again.");
            return false;
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    // ─── Password login ───

    public static async Task<bool> PasswordLoginAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Error.Write("Email: ");
            email = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                Console.Error.WriteLine("Email is required.");
                return false;
            }
        }

        Console.Error.Write("Password: ");
        var password = ReadPassword();
        Console.Error.WriteLine();
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Password is required.");
            return false;
        }

        var authority = NyxIdTokenStore.ResolveAuthority();
        var loginUrl = $"{authority}/api/v1/auth/login";

        using var client = CreateHttpClient();
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(loginUrl, new
            {
                email,
                password,
                client = "cli",
            }, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to connect to NyxID server: {ex.Message}");
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Forbidden && body.Contains("mfa_required"))
            {
                Console.Error.WriteLine("MFA is required. Please use browser login (without --password).");
                return false;
            }

            Console.Error.WriteLine($"Login failed (HTTP {(int)response.StatusCode}): {body}");
            return false;
        }

        var result = await response.Content.ReadFromJsonAsync<LoginResponse>(ct);
        if (result?.AccessToken is null)
        {
            Console.Error.WriteLine("Login failed: no access token in response.");
            return false;
        }

        var (userName, userDisplayName) = await FetchUserInfoAsync(authority, result.AccessToken, ct);
        NyxIdTokenStore.SaveToken(result.AccessToken, userName ?? email, userDisplayName);

        Console.Error.WriteLine($"Logged in as {email}");
        return true;
    }

    // ─── Callback server ───

    private static async Task<string?> WaitForCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(CallbackTimeoutSeconds));

        while (!cts.Token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }

            var request = ctx.Request;
            var response = ctx.Response;

            if (request.Url?.AbsolutePath != "/callback")
            {
                response.StatusCode = 404;
                response.Close();
                continue;
            }

            var query = HttpUtility.ParseQueryString(request.Url.Query);
            var state = query["state"];
            var accessToken = query["access_token"];

            if (state != expectedState || string.IsNullOrEmpty(accessToken))
            {
                response.StatusCode = 400;
                response.Close();
                continue;
            }

            // Send success response
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            var html = CallbackSuccessHtml();
            var buffer = System.Text.Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, ct);
            response.Close();

            return accessToken;
        }

        return null;
    }

    // ─── Helpers ───

    private static async Task<string> FetchFrontendUrlAsync(string authority, CancellationToken ct)
    {
        using var client = CreateHttpClient();
        var configUrl = $"{authority}/api/v1/public/config";
        var response = await client.GetFromJsonAsync<PublicConfig>(configUrl, ct);
        return response?.FrontendUrl?.TrimEnd('/') ?? authority;
    }

    private static async Task<(string? Email, string? Name)> FetchUserInfoAsync(
        string authority, string accessToken, CancellationToken ct)
    {
        try
        {
            using var client = CreateHttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var userInfo = await client.GetFromJsonAsync<UserInfoResponse>(
                $"{authority}/oauth/userinfo", ct);
            return (userInfo?.Email, userInfo?.Name);
        }
        catch
        {
            return (null, null);
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexStringLower(bytes);
    }

    private static string BuildCliAuthUrl(string frontendUrl, int port, string state)
    {
        return $"{frontendUrl}/cli-auth?port={port}&state={Uri.EscapeDataString(state)}&client_ua={Uri.EscapeDataString(CliUserAgent)}";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CliUserAgent);
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                continue;
            }
            if (key.KeyChar >= ' ')
                password.Append(key.KeyChar);
        }
        return password.ToString();
    }

    private static string CallbackSuccessHtml() =>
        """
        <!doctype html>
        <html>
        <head><title>Aevatar CLI</title></head>
        <body style="display:flex;align-items:center;justify-content:center;min-height:100vh;font-family:system-ui;background:#0f172a;color:#e2e8f0">
        <div style="text-align:center">
        <h2>Login successful</h2>
        <p style="color:#94a3b8">You can close this tab and return to your terminal.</p>
        </div>
        </body>
        </html>
        """;

    // ─── DTOs ───

    private sealed class PublicConfig
    {
        [JsonPropertyName("frontend_url")]
        public string? FrontendUrl { get; set; }
    }

    private sealed class LoginResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }

    private sealed class UserInfoResponse
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("sub")]
        public string? Sub { get; set; }
    }
}
