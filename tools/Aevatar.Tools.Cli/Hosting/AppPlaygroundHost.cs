using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;

namespace Aevatar.Tools.Cli.Hosting;

internal static class AppPlaygroundHost
{
    private static volatile string _currentApiBaseUrl = "http://127.0.0.1:5080";
    private static volatile string _localApiBaseUrl = "http://127.0.0.1:5080";

    public static async Task RunAsync(
        int port,
        string apiBaseUrl,
        bool noBrowser,
        CancellationToken cancellationToken,
        TaskCompletionSource<string>? startedSignal = null)
    {
        _localApiBaseUrl = apiBaseUrl.TrimEnd('/');
        _currentApiBaseUrl = _localApiBaseUrl;
        var baseDir = AppContext.BaseDirectory;
        var webRootCandidates = new[]
        {
            Path.Combine(baseDir, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDir, "../../../../tools/Aevatar.Tools.Cli/wwwroot")),
            Path.GetFullPath(Path.Combine(baseDir, "../../../../tools/Aevatar.Tools.Cli/Frontend/dist")),
            Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Cli", "wwwroot"),
            Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Cli", "Frontend", "dist"),
        };

        var webRootPath = webRootCandidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "index.html")))
            ?? webRootCandidates[0];

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            WebRootPath = webRootPath,
            ContentRootPath = baseDir,
        });

        var url = $"http://localhost:{port}";
        builder.WebHost.UseUrls(url);
        builder.Services.AddHttpClient("api-proxy");

        var app = builder.Build();

        PrintBanner(url, apiBaseUrl, webRootPath);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            startedSignal?.TrySetResult(url);
            if (!noBrowser)
                OpenBrowser(url);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
                context.Context.Response.Headers.Expires = "0";
            },
        });

        app.MapGet("/api/health", () => Results.Json(new { ok = true, service = "aevatar.app" }));

        // Allow frontend to read/update the proxy target URL at runtime.
        app.MapGet("/api/_proxy/runtime-url", () => Results.Json(new { runtimeBaseUrl = _currentApiBaseUrl }));
        app.MapPut("/api/_proxy/runtime-url", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<RuntimeUrlUpdate>(ctx.RequestAborted);
            var url = body?.RuntimeBaseUrl?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(url))
                return Results.BadRequest(new { error = "runtimeBaseUrl is required" });
            _currentApiBaseUrl = url;
            return Results.Json(new { runtimeBaseUrl = _currentApiBaseUrl });
        });

        // OAuth callback: serve index.html so frontend JS handles the code exchange.
        app.MapGet("/auth/callback", async (HttpContext ctx) =>
        {
            var indexPath = Path.Combine(webRootPath, "index.html");
            if (File.Exists(indexPath))
            {
                ctx.Response.ContentType = "text/html";
                ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                ctx.Response.Headers.Pragma = "no-cache";
                ctx.Response.Headers.Expires = "0";
                await ctx.Response.SendFileAsync(indexPath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        });

        // Reverse proxy: forward /api/* to the backend API.
        app.Map("/api/{**rest}", ProxyToBackend);

        async Task ProxyToBackend(HttpContext ctx, IHttpClientFactory factory)
        {
            var client = factory.CreateClient("api-proxy");
            var path = ctx.Request.Path + ctx.Request.QueryString;
            var targetBase = ResolveTargetBaseUrl(ctx.Request.Path).TrimEnd('/');
            var targetUri = new Uri($"{targetBase}{path}");

            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(ctx.Request.Method),
                RequestUri = targetUri,
            };

            foreach (var header in ctx.Request.Headers)
            {
                if (header.Key.StartsWith("Host", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;

                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            if (ctx.Request.ContentLength > 0 || ctx.Request.ContentType != null)
            {
                requestMessage.Content = new StreamContent(ctx.Request.Body);
                if (ctx.Request.ContentType != null)
                    requestMessage.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctx.Request.ContentType);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            }
            catch (HttpRequestException)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsJsonAsync(new { error = "Backend API is unreachable", target = targetBase });
                return;
            }

            ctx.Response.StatusCode = (int)response.StatusCode;
            var isSse = false;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (header.Key.StartsWith("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;

                ctx.Response.Headers[header.Key] = header.Value.ToArray();

                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                    header.Value.Any(v => v.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)))
                    isSse = true;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
            if (isSse)
            {
                // SSE: disable buffering and flush each chunk so the browser
                // receives events in real time instead of all at once.
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                var buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            else
            {
                await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
        }

        app.MapFallbackToFile("index.html");

        try
        {
            await app.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startedSignal?.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            startedSignal?.TrySetException(ex);
            throw;
        }
    }

    private static string ResolveTargetBaseUrl(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return ShouldUseLocalBackend(value) ? _localApiBaseUrl : _currentApiBaseUrl;
    }

    private static bool ShouldUseLocalBackend(string path)
    {
        if (path.StartsWith("/api/user-config", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.StartsWith("/api/settings/runtime/test", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith("/api/scopes/", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Contains("/nyxid-chat/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/streaming-proxy/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/chat-history", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintBanner(string url, string apiBaseUrl, string webRootPath)
    {
        Console.WriteLine();
        Console.WriteLine("  aevatar app");
        Console.WriteLine($"  Web UI:   {url}");
        Console.WriteLine($"  API:      {apiBaseUrl}");
        Console.WriteLine($"  WebRoot:  {webRootPath}");
        Console.WriteLine("  Press Ctrl+C to stop");
        Console.WriteLine();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start("xdg-open", url);
        }
        catch { }
    }

    private sealed record RuntimeUrlUpdate(string? RuntimeBaseUrl);
}
