using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

/// <summary>
/// M6 fail-closed default: a connected-service <c>ConnectedServiceProxyTool</c> built from a
/// write operation (any HTTP method other than the safe GET/HEAD/OPTIONS) must resolve
/// <see cref="IAgentTool.IsDestructive"/> to <c>true</c> unless the <c>x-aevatar-tool</c> marker
/// EXPLICITLY sets <c>destructive: false</c>. Read operations stay non-destructive. Combined with
/// <c>ToolApprovalMiddleware</c> (which routes any destructive Auto-mode tool through approval),
/// this flips the historical fail-open default so unmarked writes now require approval.
/// </summary>
public class ConnectedServiceProxyToolDestructiveDefaultTests
{
    private const string ServiceSlug = "api-shop";
    private const string ServiceId = "svc-1";

    [Fact]
    public async Task WriteOperation_WithoutMarkerOverride_IsDestructiveByDefault()
    {
        var post = await ResolveToolAsync(
            operationId: "create_order",
            method: "post",
            path: "/orders",
            marker: "true");

        post.IsDestructive.Should().BeTrue(
            "an unmarked POST is a write and the fail-closed default treats writes as destructive");
        post.IsReadOnly.Should().BeFalse("a POST is not read-only without an explicit marker");
    }

    [Theory]
    [InlineData("put", "/orders/{orderId}")]
    [InlineData("patch", "/orders/{orderId}")]
    [InlineData("delete", "/orders/{orderId}")]
    public async Task MutatingMethods_WithoutMarkerOverride_AreDestructiveByDefault(string method, string path)
    {
        var tool = await ResolveToolAsync(
            operationId: $"mutate_{method}",
            method: method,
            path: path,
            marker: "true");

        tool.IsDestructive.Should().BeTrue($"{method.ToUpperInvariant()} is a write and defaults to destructive");
    }

    [Fact]
    public async Task GetOperation_IsNotDestructive()
    {
        var get = await ResolveToolAsync(
            operationId: "get_order",
            method: "get",
            path: "/orders/{orderId}",
            marker: "true");

        get.IsDestructive.Should().BeFalse("GET is a safe method and is never destructive by default");
        get.IsReadOnly.Should().BeTrue("GET defaults to read-only");
    }

    [Fact]
    public async Task ExplicitDestructiveFalseMarker_OnWrite_OptsOut()
    {
        var post = await ResolveToolAsync(
            operationId: "safe_write",
            method: "post",
            path: "/orders/preview",
            marker: """{ "enabled": true, "destructive": false }""");

        post.IsDestructive.Should().BeFalse(
            "an explicit destructive:false marker opts a write back out of the fail-closed default");
    }

    [Fact]
    public async Task ExplicitDestructiveTrueMarker_OnWrite_StaysDestructive()
    {
        var post = await ResolveToolAsync(
            operationId: "explicit_write",
            method: "post",
            path: "/orders/wipe",
            marker: """{ "enabled": true, "destructive": true }""");

        post.IsDestructive.Should().BeTrue("an explicit destructive:true marker keeps a write destructive");
    }

    [Fact]
    public async Task SafeMethod_IsNonDestructive_RegardlessOfMarker()
    {
        // Per the fail-closed spec, GET/HEAD/OPTIONS are always treated as non-destructive;
        // the destructive default only escalates writes. A marker on a safe method does not
        // make it destructive.
        var get = await ResolveToolAsync(
            operationId: "marked_get",
            method: "get",
            path: "/orders/report",
            marker: """{ "enabled": true, "destructive": true }""");

        get.IsDestructive.Should().BeFalse("a safe method stays non-destructive; only writes escalate");
    }

    private static async Task<IAgentTool> ResolveToolAsync(
        string operationId,
        string method,
        string path,
        string marker)
    {
        var spec = BuildSpec(operationId, method, path, marker);
        var handler = new SpecHandler(spec);
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.test" }, new HttpClient(handler));
        var source = new NyxIdConnectedServiceToolSource(options, client);

        using var _scope = PushContext("user-token");
        var tools = await source.DiscoverToolsAsync();
        return tools.Single();
    }

    private static string BuildSpec(string operationId, string method, string path, string marker) => $$"""
        {
          "openapi": "3.0.0",
          "info": { "title": "Shop" },
          "paths": {
            "{{path}}": {
              "{{method}}": {
                "operationId": "{{operationId}}",
                "summary": "Op",
                "x-aevatar-tool": {{marker}},
                "parameters": [
                  { "name": "orderId", "in": "path", "required": false, "schema": { "type": "string" } }
                ]
              }
            }
          }
        }
        """;

    private static AgentToolContextScope PushContext(string userToken) =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials(userToken, null, null),
            AgentToolCallerContext.Empty,
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)));

    private sealed class SpecHandler(string spec) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path == "/api/v1/proxy/services")
                return Task.FromResult(Json($$"""[{ "slug": "{{ServiceSlug}}", "id": "{{ServiceId}}" }]"""));

            if (path.StartsWith("/api/v1/proxy/services/", StringComparison.Ordinal) &&
                path.EndsWith("/openapi.json", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(spec));
            }

            return Task.FromResult(Json($$"""{ "error": "unexpected", "path": "{{path}}" }"""));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
