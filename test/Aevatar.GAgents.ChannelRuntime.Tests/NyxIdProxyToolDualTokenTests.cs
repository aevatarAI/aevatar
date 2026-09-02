using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Unit tests for NyxIdProxyTool exact dual-token routing.
/// </summary>
public class NyxIdProxyToolDualTokenTests
{
    [Fact]
    public async Task ProxyExecution_UsesLiveNyxIdDiscoveryForEachRouteDecision()
    {
        var handler = new RecordingNyxIdHandler();
        using var http = new HttpClient(handler);
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, http);
        var tool = new NyxIdProxyTool(client);

        using var _ = AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("user-token", "org-token", null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

        var first = await tool.ExecuteAsync(
            """{"service_id":"us-org-alpha","slug":"org-service","path":"/ping"}""");
        var second = await tool.ExecuteAsync(
            """{"service_id":"us-org-alpha","slug":"org-service","path":"/ping"}""");

        first.Should().Be("""{"ok":true,"token":"org-token"}""");
        second.Should().Be("""{"ok":true,"token":"org-token"}""");
        handler.ServiceDiscoveryRequests.Should().Be(4, "routing must read live NyxID service discovery instead of keeping slug facts in a tool-instance cache");
        handler.ProxyRequests.Should().Be(2);
    }

    private sealed class RecordingNyxIdHandler : HttpMessageHandler
    {
        public int ServiceDiscoveryRequests { get; private set; }
        public int ProxyRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/v1/keys")
            {
                ServiceDiscoveryRequests++;
                var body = token == "user-token"
                    ? """[{"id":"us-user-alpha","slug":"user-service"}]"""
                    : """[{"id":"us-org-alpha","slug":"org-service"}]""";
                return Task.FromResult(Json(body));
            }

            if (path == "/api/v1/proxy/s/org-service/ping")
            {
                ProxyRequests++;
                return Task.FromResult(Json($$"""{"ok":true,"token":"{{token}}"}"""));
            }

            return Task.FromResult(Json($$"""{"error":"unexpected_request","path":"{{path}}"}"""));
        }

        private static HttpResponseMessage Json(string body) => new()
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
