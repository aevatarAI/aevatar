using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

public class NyxIdApiKeysToolBindTests
{
    [Fact]
    public async Task Bind_WhenServicesResponseIsInvalid_ReturnsTypedFailureWithoutBinding()
    {
        var (tool, handler) = CreateTool(_ => "{");
        using var _ = PushToken("user-token");

        var result = await tool.ExecuteAsync(
            """{"action":"bind","id":"key-1","service_slug":"mail"}""");

        ReadError(result).Should().Be("invalid_services_response");
        handler.Paths.Should().Equal("/api/v1/keys");
    }

    [Fact]
    public async Task Bind_WhenSpecifiedCredentialResponseIsInvalid_DoesNotFallBackToDefaultCredential()
    {
        var (tool, handler) = CreateTool(path => path switch
        {
            "/api/v1/keys" =>
                """{"keys":[{"slug":"mail","id":"service-1","api_key_id":"default-key"}]}""",
            "/api/v1/api-keys/external" => "{",
            _ => """{"ok":true}""",
        });
        using var _ = PushToken("user-token");

        var result = await tool.ExecuteAsync(
            """{"action":"bind","id":"key-1","service_slug":"mail","credential_label":"work"}""");

        ReadError(result).Should().Be("invalid_external_keys_response");
        handler.Paths.Should().Equal("/api/v1/keys", "/api/v1/api-keys/external");
    }

    private static string? ReadError(string result)
    {
        using var document = JsonDocument.Parse(result);
        return document.RootElement.GetProperty("error").GetString();
    }

    private static (NyxIdApiKeysTool Tool, RespondingHandler Handler) CreateTool(
        Func<string, string> responseBody)
    {
        var handler = new RespondingHandler(responseBody);
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return (new NyxIdApiKeysTool(client), handler);
    }

    private static IDisposable PushToken(string token)
    {
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(token, null, null),
        };
        return new ContextReset(previous);
    }

    private sealed class RespondingHandler(Func<string, string> responseBody) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody(path)),
            });
        }
    }

    private sealed class ContextReset(AgentToolExecutionContext? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.Current = previous;
    }
}
