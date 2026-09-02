using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

/// <summary>
/// The agent-facing nyxid_api_keys tool forwards <c>callback_url</c> into the NyxID API-key payload,
/// which can then be bound to a channel route — a second path (besides the Lark/Telegram provisioning
/// services) that would ship the relay user token to a cleartext callback. Pins that create/update
/// reject a cleartext public callback before any NyxID call, while https/loopback pass through.
/// </summary>
public class NyxIdApiKeysToolCallbackUrlGuardTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}"""),
            });
        }
    }

    private static (NyxIdApiKeysTool Tool, RecordingHandler Handler) CreateTool()
    {
        var handler = new RecordingHandler();
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

    private sealed class ContextReset(AgentToolExecutionContext? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.Current = previous;
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task RejectsCleartextPublicCallbackUrl_WithoutCallingNyx(string action)
    {
        var (tool, handler) = CreateTool();
        using var _ = PushToken("user-token");

        var result = await tool.ExecuteAsync(
            $$"""{"action":"{{action}}","id":"key-1","name":"k","callback_url":"http://aevatar.example.com"}""");

        result.Should().Contain("error").And.Contain("callback_url");
        handler.Requests.Should().BeEmpty("a cleartext public callback_url must be rejected before any NyxID call");
    }

    [Theory]
    [InlineData("https://relay.example.com/cb")]
    [InlineData("http://localhost/cb")]
    [InlineData("http://127.0.0.1/cb")]
    public async Task AcceptsSecureOrLoopbackCallbackUrl_AndCallsNyx(string callbackUrl)
    {
        var (tool, handler) = CreateTool();
        using var _ = PushToken("user-token");

        await tool.ExecuteAsync(
            $$"""{"action":"create","name":"k","callback_url":"{{callbackUrl}}"}""");

        handler.Requests.Should().NotBeEmpty("a secure/loopback callback_url must be allowed through to NyxID");
    }

    [Fact]
    public async Task CreateWithoutCallbackUrl_IsUnaffected()
    {
        var (tool, handler) = CreateTool();
        using var _ = PushToken("user-token");

        await tool.ExecuteAsync("""{"action":"create","name":"k"}""");

        handler.Requests.Should().NotBeEmpty("omitting callback_url leaves the create path unchanged");
    }
}
