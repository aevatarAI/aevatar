using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdDurableGrantsToolTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldUseExactReadRouteAndProjectOnlySafeReceiptFields()
    {
        var handler = new RecordingHandler(ValidResponse("key/alpha"));
        using var client = CreateClient(handler);
        var tool = new NyxIdDurableGrantsTool(client);
        using var _ = PushCredentials(new AgentToolCredentials(
            "proxy-delegation",
            null,
            null,
            AgentToolNyxIdCredentialKind.ProxyDelegation,
            "source-readable-bearer"));

        var result = await tool.ExecuteAsync(
            """{"key_id":"key/alpha","include_revoked":true}""");

        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastPathAndQuery.Should()
            .Be("/api/v1/api-keys/key%2Falpha/durable-grants?include_revoked=true");
        handler.LastBearerToken.Should().Be("source-readable-bearer");
        result.Should().Contain("grant-alpha").And.Contain("service-alpha");
        result.Should().NotMatchRegex("(?i)(constraints|client_audit_binding|must-not-pass|secret)");
        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("returned").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        tool.IsReadOnly.Should().BeTrue();
        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        tool.Capabilities.Should().Contain(AgentToolCapabilities.RequiresHumanSession);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"key_id\":1}")]
    [InlineData("{\"key_id\":\"key-alpha\",\"include_revoked\":\"true\"}")]
    [InlineData("{\"key_id\":\"key-alpha\",\"secret\":\"must-not-pass\"}")]
    public async Task ExecuteAsync_ShouldRejectInvalidOrUndeclaredArgumentsBeforeHttp(string argumentsJson)
    {
        var handler = new RecordingHandler(ValidResponse());
        using var client = CreateClient(handler);
        var tool = new NyxIdDurableGrantsTool(client);
        using var _ = PushCredentials(new AgentToolCredentials(
            "source-readable-bearer",
            null,
            null,
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer));

        var result = await tool.ExecuteAsync(argumentsJson);

        result.Should().Contain("error");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRequireSourceReadableUserBearer()
    {
        var handler = new RecordingHandler(ValidResponse());
        using var client = CreateClient(handler);
        var tool = new NyxIdDurableGrantsTool(client);
        using var _ = PushCredentials(new AgentToolCredentials(
            "proxy-delegation",
            null,
            null,
            AgentToolNyxIdCredentialKind.ProxyDelegation));

        var result = await tool.ExecuteAsync("""{"key_id":"key-alpha"}""");

        result.Should().Contain("No source-readable NyxID user bearer");
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task ExecuteAsync_ShouldFailClosedForInvalidGrantEvidence(string response)
    {
        var handler = new RecordingHandler(response);
        using var client = CreateClient(handler);
        var tool = new NyxIdDurableGrantsTool(client);
        using var _ = PushCredentials(new AgentToolCredentials(
            "source-readable-bearer",
            null,
            null,
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer));

        var result = await tool.ExecuteAsync("""{"key_id":"key-alpha"}""");

        result.Should().Be("{\"error\":\"invalid_nyxid_response\"}");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStripUntrustedProviderErrorDetails()
    {
        var handler = new RecordingHandler("""
            {"error":true,"status":403,"retry_after_seconds":5,
             "body":"{\"secret\":\"must-not-pass\"}","message":"must-not-pass"}
            """);
        using var client = CreateClient(handler);
        var tool = new NyxIdDurableGrantsTool(client);
        using var _ = PushCredentials(new AgentToolCredentials(
            "source-readable-bearer",
            null,
            null,
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer));

        var result = await tool.ExecuteAsync("""{"key_id":"key-alpha"}""");

        result.Should().Be("{\"error\":true,\"status\":403,\"retry_after_seconds\":5}");
        result.Should().NotContain("must-not-pass");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBoundLargeGrantCollectionsAndReportTruncation()
    {
        var grants = Enumerable.Range(0, 25)
            .Select(index => ValidGrant(grantId: $"grant-{index:D2}"));
        var handler = new RecordingHandler("{\"grants\":[" + string.Join(',', grants) + "]}");
        using var client = CreateClient(handler);
        var tool = new NyxIdDurableGrantsTool(client);
        using var _ = PushCredentials(new AgentToolCredentials(
            "source-readable-bearer",
            null,
            null,
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer));

        var result = await tool.ExecuteAsync("""{"key_id":"key-alpha"}""");

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(32 * 1024);
        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("total").GetInt32().Should().Be(25);
        document.RootElement.GetProperty("returned").GetInt32().Should().Be(20);
        document.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("grants").GetArrayLength().Should().Be(20);
    }

    public static TheoryData<string> InvalidResponses => new()
    {
        "not-json",
        "{}",
        ValidResponse("key-other"),
        "{\"grants\":[" + ValidGrant() + "," + ValidGrant() + "]}",
    };

    private static string ValidResponse(string apiKeyId = "key-alpha") =>
        "{\"grants\":[" + ValidGrant(apiKeyId) + "]}";

    private static string ValidGrant(
        string apiKeyId = "key-alpha",
        string grantId = "grant-alpha") => """
        {
          "id":"GRANT_ID",
          "api_key_id":"API_KEY_ID",
          "user_service_id":"service-alpha",
          "endpoint_id":"endpoint-alpha",
          "method":"POST",
          "normalized_path_template":"/messages",
          "contract_digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "constraints":{"body":{"fields":{"/text":{"required":true,"type":"exact","value":"must-not-pass"}}}},
          "valid_from":"2026-08-10T00:00:00.0000000+00:00",
          "expires_at":"2026-08-11T00:00:00.0000000+00:00",
          "total_limit":10,
          "total_used":2,
          "window":{"duration_seconds":3600,"max_operations":4},
          "window_used":1,
          "replay_policy":"non_replayable",
          "client_audit_binding":{"call_site":"secret"},
          "revoked_at":null,
          "state_version":3,
          "reauthorized_from":null,
          "created_at":"2026-08-10T00:00:00.0000000+00:00",
          "secret":"must-not-pass"
        }
        """
        .Replace("API_KEY_ID", apiKeyId, StringComparison.Ordinal)
        .Replace("GRANT_ID", grantId, StringComparison.Ordinal);

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient(handler));

    private static IDisposable PushCredentials(AgentToolCredentials credentials)
    {
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with { Credentials = credentials };
        return new ContextReset(previous);
    }

    private sealed class ContextReset(AgentToolExecutionContext? previous) : IDisposable
    {
        public void Dispose() => AgentToolRequestContext.Current = previous;
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPathAndQuery { get; private set; }
        public string? LastBearerToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastPathAndQuery = request.RequestUri?.PathAndQuery;
            LastBearerToken = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }
}
