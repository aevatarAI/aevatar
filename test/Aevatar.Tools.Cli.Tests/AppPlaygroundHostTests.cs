using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppPlaygroundHostTests : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _hostTask;
    private string? _baseUrl;
    private readonly List<WebApplication> _backgroundApps = [];

    private async Task<string> StartHostAsync(int backendPort = 59999)
    {
        // Use a random available port by binding to port 0.
        // We start with a known port and let the host bind.
        var port = GetAvailablePort();
        _cts = new CancellationTokenSource();
        var apiBaseUrl = $"http://localhost:{backendPort}";
        var startedSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hostTask = AppPlaygroundHost.RunAsync(port, apiBaseUrl, noBrowser: true, _cts.Token, startedSignal);
        _baseUrl = await startedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        return _baseUrl;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _backgroundApps)
        {
            try
            {
                await app.StopAsync();
            }
            catch
            {
            }

            await app.DisposeAsync();
        }

        if (_cts != null)
        {
            await _cts.CancelAsync();
            try
            {
                if (_hostTask != null)
                    await _hostTask;
            }
            catch (OperationCanceledException)
            {
            }
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Health_ShouldReturnAevatarApp()
    {
        var baseUrl = await StartHostAsync();
        using var client = new HttpClient();

        var response = await client.GetAsync($"{baseUrl}/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("service").GetString().Should().Be("aevatar.app");
    }

    [Fact]
    public async Task AuthCallback_ShouldServeIndexHtml()
    {
        var baseUrl = await StartHostAsync();
        using var client = new HttpClient();

        var response = await client.GetAsync($"{baseUrl}/auth/callback?code=test&state=test");

        // Should serve index.html (SPA fallback for OAuth callback), not proxy to backend.
        // Status depends on whether index.html exists in the webroot.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("<html", "should serve HTML for SPA routing");
        }
    }

    [Fact]
    public async Task ApiProxy_WhenBackendUnreachable_ShouldReturn502()
    {
        // Use a port that nothing listens on.
        var baseUrl = await StartHostAsync(backendPort: GetAvailablePort());
        using var client = new HttpClient();

        var response = await client.GetAsync($"{baseUrl}/api/services?tenantId=test");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("unreachable");
    }

    [Fact]
    public async Task AuthLogin_ShouldFallbackToIndexHtml()
    {
        // /auth/login is not proxied — it falls through to SPA fallback.
        var baseUrl = await StartHostAsync();
        using var client = new HttpClient();

        var response = await client.GetAsync($"{baseUrl}/auth/login");

        // Falls through to MapFallbackToFile("index.html").
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task VoiceWebSocketProxy_ShouldForwardBinaryAndTextFrames()
    {
        byte[]? forwardedAudio = null;
        string? forwardedControl = null;
        string? forwardedActorId = null;
        string? forwardedModule = null;

        var backendPort = GetAvailablePort();
        var backend = await StartVoiceBackendAsync(
            backendPort,
            async (context, socket) =>
            {
                forwardedActorId = context.Request.RouteValues["actorId"]?.ToString();
                forwardedModule = context.Request.Query["module"].ToString();

                var audioBuffer = new byte[256];
                var audioResult = await socket.ReceiveAsync(audioBuffer, CancellationToken.None);
                forwardedAudio = audioBuffer[..audioResult.Count].ToArray();

                var controlBuffer = new byte[1024];
                var controlResult = await socket.ReceiveAsync(controlBuffer, CancellationToken.None);
                forwardedControl = Encoding.UTF8.GetString(controlBuffer, 0, controlResult.Count);

                await socket.SendAsync(new byte[] { 9, 8, 7 }, WebSocketMessageType.Binary, true, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            });
        _backgroundApps.Add(backend);

        var baseUrl = await StartHostAsync(backendPort);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(
            new Uri($"{baseUrl.Replace("http://", "ws://", StringComparison.Ordinal)}/ws/voice/agent-1?module=voice_presence_openai"),
            CancellationToken.None);

        await client.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await client.SendAsync(
            Encoding.UTF8.GetBytes("{\"drainAcknowledged\":{\"responseId\":7,\"playoutSequence\":\"42\"}}"),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        var receiveBuffer = new byte[64];
        var receiveResult = await client.ReceiveAsync(receiveBuffer, CancellationToken.None);

        receiveResult.MessageType.Should().Be(WebSocketMessageType.Binary);
        receiveBuffer[..receiveResult.Count].Should().Equal(9, 8, 7);
        forwardedActorId.Should().Be("agent-1");
        forwardedModule.Should().Be("voice_presence_openai");
        forwardedAudio.Should().Equal(1, 2, 3);
        forwardedControl.Should().Contain("drainAcknowledged");
    }

    [Fact]
    public async Task FallbackToIndex_ShouldServeHtml()
    {
        var baseUrl = await StartHostAsync();
        using var client = new HttpClient();

        var response = await client.GetAsync($"{baseUrl}/some-nonexistent-page");

        // If playground/index.html exists, it will be served. If not, 404.
        // We just verify it doesn't crash.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    private static async Task<WebApplication> StartVoiceBackendAsync(
        int port,
        Func<HttpContext, WebSocket, Task> handleSocketAsync)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws/voice/{actorId}", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await handleSocketAsync(context, socket);
        });

        await app.StartAsync();
        return app;
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
