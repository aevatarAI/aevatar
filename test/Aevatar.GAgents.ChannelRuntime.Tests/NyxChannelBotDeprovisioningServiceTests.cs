using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Service-level tests for <see cref="NyxChannelBotDeprovisioningService"/>. These drive a real
/// <see cref="NyxIdApiClient"/> over a canned <see cref="HttpMessageHandler"/> so the successful
/// <c>204 No Content</c> and "404 = already gone = success" classifications are exercised against
/// the actual client contract (non-2xx is returned as <c>{"error":true,"status":...}</c>, not thrown).
/// </summary>
public sealed class NyxChannelBotDeprovisioningServiceTests
{
    private const string BaseUrl = "https://nyx.example.com";

    private const string RoutePath = "/api/v1/channel-conversations/route-1";
    private const string ChannelBotPath = "/api/v1/channel-bots/bot-1";
    private const string ApiKeyPath = "/api/v1/api-keys/key-1";

    [Fact]
    public async Task DeprovisionAsync_DeletesAllThree_InReverseOrder_AndSucceeds()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", "route-1", "bot-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();

        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath, ApiKeyPath);
    }

    [Fact]
    public async Task DeprovisionAsync_204NoContentOnEveryResource_IsTreatedAsSuccess()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.NoContent, string.Empty);
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.NoContent, string.Empty);
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.NoContent, string.Empty);

        var result = await CreateService(handler).DeprovisionAsync(
            "token", "route-1", "bot-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath, ApiKeyPath);
    }

    [Fact]
    public async Task DeprovisionAsync_404OnEveryResource_IsTreatedAsSuccess()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.NotFound, """{"detail":"gone"}""");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.NotFound, """{"detail":"gone"}""");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.NotFound, """{"detail":"gone"}""");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", "route-1", "bot-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task DeprovisionAsync_HardChannelBotFailure_FailsAndSkipsApiKey()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.InternalServerError, """{"detail":"boom"}""");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", "route-1", "bot-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ChannelBotRemoved.Should().BeFalse();
        // api-key delete must be skipped while the channel-bot is still alive.
        handler.DeletedPaths.Should().Equal(RoutePath, ChannelBotPath);
    }

    [Fact]
    public async Task DeprovisionAsync_ResidualRouteAndApiKeyFailures_StillSucceed_WithWarnings()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.InternalServerError, """{"detail":"route-boom"}""");
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.InternalServerError, """{"detail":"key-boom"}""");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", "route-1", "bot-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Contains("conversation_route_delete_failed") && w.Contains("route-1"));
        result.Warnings.Should().Contain(w => w.Contains("relay_api_key_delete_failed") && w.Contains("key-1"));
    }

    [Fact]
    public async Task DeprovisionAsync_SkipsBlankIds()
    {
        var handler = new StatusRoutingHandler();
        // Only the channel-bot has an id; the route + api-key are blank and must not be called.
        handler.Set(HttpMethod.Delete, ChannelBotPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", conversationRouteId: "  ", channelBotId: "bot-1", apiKeyId: null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(ChannelBotPath);
    }

    [Fact]
    public async Task DeprovisionAsync_NoChannelBotId_SucceedsWithoutCallingChannelBotDelete()
    {
        var handler = new StatusRoutingHandler();
        handler.Set(HttpMethod.Delete, RoutePath, HttpStatusCode.OK, "{}");
        handler.Set(HttpMethod.Delete, ApiKeyPath, HttpStatusCode.OK, "{}");

        var result = await CreateService(handler).DeprovisionAsync(
            "token", "route-1", channelBotId: null, apiKeyId: "key-1", CancellationToken.None);

        // No channel-bot to delete → vacuously removed; route + api-key still cleaned best-effort.
        result.Succeeded.Should().BeTrue();
        result.ChannelBotRemoved.Should().BeTrue();
        result.Warnings.Should().BeEmpty();
        handler.DeletedPaths.Should().Equal(RoutePath, ApiKeyPath);
    }

    private static NyxChannelBotDeprovisioningService CreateService(StatusRoutingHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = BaseUrl },
            new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) });
        return new NyxChannelBotDeprovisioningService(nyxClient, NullLogger<NyxChannelBotDeprovisioningService>.Instance);
    }

    private sealed class StatusRoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DeletedPaths { get; } = [];

        public void Set(HttpMethod method, string path, HttpStatusCode status, string body)
        {
            _responses[$"{method.Method}:{path}"] = (status, body);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (request.Method == HttpMethod.Delete)
                DeletedPaths.Add(path);

            if (!_responses.TryGetValue($"{request.Method.Method}:{path}", out var canned))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"detail":"unrouted"}""", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(canned.Status)
            {
                Content = new StringContent(canned.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
