using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Channel.NyxIdRelay;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayApiKeyOwnershipVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsPersonalApiKeyWhenCurrentUserMatchesScope()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys/key-1", """{"id":"key-1","credential_source":{"type":"personal"}}""");
        handler.Enqueue("/api/v1/users/me", """{"id":"scope-1"}""");
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token-1", "scope-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v1/api-keys/key-1",
            "/api/v1/users/me");
    }

    [Fact]
    public async Task VerifyAsync_RejectsPersonalApiKeyWhenReturnedUserIdDiffers()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys/key-1", """{"id":"key-1","user_id":"scope-other","credential_source":{"type":"personal"}}""");
        handler.Enqueue("/api/v1/users/me", """{"id":"scope-1"}""");
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token-1", "scope-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("api_key_owner_scope_mismatch key_user_id_mismatch");
    }

    [Fact]
    public async Task VerifyAsync_RejectsOrgApiKeyWhenCallerIsNotAdmin()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys/key-1", """{"id":"key-1","credential_source":{"type":"org","org_id":"scope-org","role":"member"}}""");
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token-1", "scope-org", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("api_key_owner_scope_unresolved org_role=member");
        handler.Requests.Select(request => request.Path).Should().Equal("/api/v1/api-keys/key-1");
    }

    [Fact]
    public async Task VerifyAsync_RejectsOrgApiKeyWhenOwnerScopeDiffers()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys/key-1", """{"id":"key-1","credential_source":{"type":"org","org_id":"scope-org","role":"admin"}}""");
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token-1", "scope-other", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("api_key_owner_scope_mismatch");
        handler.Requests.Select(request => request.Path).Should().Equal("/api/v1/api-keys/key-1");
    }

    [Fact]
    public async Task VerifyAsync_RejectsNyxIdErrorEnvelope()
    {
        var handler = new RecordingHandler();
        handler.Enqueue("/api/v1/api-keys/key-1", """{"error":true,"status":404,"body":"not found"}""");
        var verifier = CreateVerifier(handler);

        var result = await verifier.VerifyAsync("token-1", "scope-1", "key-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Contain("api_key_lookup_failed nyx_status=404");
        handler.Requests.Select(request => request.Path).Should().Equal("/api/v1/api-keys/key-1");
    }

    private static NyxRelayApiKeyOwnershipVerifier CreateVerifier(HttpMessageHandler handler) =>
        new(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler)));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(string Path, string Body)> _responses = new();

        public List<(HttpMethod Method, string Path, string? Authorization)> Requests { get; } = [];

        public void Enqueue(string path, string body) => _responses.Enqueue((path, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("No more queued responses.");

            var (expectedPath, responseBody) = _responses.Dequeue();
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.AbsolutePath.Should().Be(expectedPath);
            Requests.Add((request.Method, expectedPath, request.Headers.Authorization?.ToString()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
