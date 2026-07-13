using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledAgentApiKeyIssuerTests
{
    [Fact]
    public async Task RevokeAsync_WithSuccessfulDelete_Completes()
    {
        var handler = new RoutingJsonHandler("""{"ok":true}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeAsync("session-token", " key-1 ", CancellationToken.None);

        result.Completed.Should().BeTrue();
        result.HttpStatus.Should().Be(0);
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.None);
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/api-keys/key-1");
    }

    [Fact]
    public async Task RevokeAsync_WithNotFound_CompletesIdempotently()
    {
        var handler = new RoutingJsonHandler("""{"error":true,"status":404,"body":"already deleted"}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeAsync("session-token", "key-404", CancellationToken.None);

        result.Completed.Should().BeTrue();
        result.HttpStatus.Should().Be(404);
        result.Error.Should().BeEmpty();
        result.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.None);
    }

    [Theory]
    [InlineData(401, "bearer expired", UserAgentApiKeyRevocationFailureKind.Unauthorized)]
    [InlineData(403, "owner mismatch", UserAgentApiKeyRevocationFailureKind.Unauthorized)]
    [InlineData(429, "rate limited", UserAgentApiKeyRevocationFailureKind.Transient)]
    [InlineData(503, "upstream unavailable", UserAgentApiKeyRevocationFailureKind.Transient)]
    [InlineData(400, "bad request", UserAgentApiKeyRevocationFailureKind.ProviderError)]
    public async Task RevokeAsync_WithErrorEnvelope_RecordsPendingFailure(
        int status,
        string detail,
        UserAgentApiKeyRevocationFailureKind failureKind)
    {
        var handler = new RoutingJsonHandler($$"""{"error":true,"status":{{status}},"body":"{{detail}}"}""");
        var issuer = CreateIssuer(handler);

        var result = await issuer.RevokeAsync("session-token", "key-fail", CancellationToken.None);

        result.Completed.Should().BeFalse();
        result.HttpStatus.Should().Be(status);
        result.Error.Should().Be(detail);
        result.FailureKind.Should().Be(failureKind);
    }

    [Fact]
    public async Task RevokeAsync_WithMissingTokenOrKey_ReturnsPendingWithoutHttpCall()
    {
        var handler = new RoutingJsonHandler("""{"ok":true}""");
        var issuer = CreateIssuer(handler);

        var missingToken = await issuer.RevokeAsync("", "key-1", CancellationToken.None);
        var missingKey = await issuer.RevokeAsync("session-token", " ", CancellationToken.None);

        missingToken.Completed.Should().BeFalse();
        missingToken.Error.Should().Be("missing_access_token");
        missingToken.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.Unauthorized);
        missingKey.Completed.Should().BeFalse();
        missingKey.Error.Should().Be("missing_api_key_id");
        missingKey.FailureKind.Should().Be(UserAgentApiKeyRevocationFailureKind.ProviderError);
        handler.Requests.Should().BeEmpty();
    }

    private static ScheduledAgentApiKeyIssuer CreateIssuer(RoutingJsonHandler handler)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return new ScheduledAgentApiKeyIssuer(
            new TestNyxIdApiClientFactory(client),
            new ScheduledAgentCreatorOptions());
    }

    private sealed class TestNyxIdApiClientFactory : INyxIdApiClientFactory
    {
        private readonly NyxIdApiClient _client;

        public TestNyxIdApiClientFactory(NyxIdApiClient client)
        {
            _client = client;
        }

        public NyxIdApiClient CreateClient() => _client;
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public RoutingJsonHandler(string json)
        {
            _json = json;
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
