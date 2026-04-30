using System.Net;
using System.Net.Http.Json;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// HTTP-level behaviour tests for <see cref="NyxIdDynamicClientRegistrationClient"/>:
/// happy-path payload, missing client_id, non-success responses (PR #521 review).
/// Uses an in-process <see cref="HttpMessageHandler"/> stub instead of a live
/// NyxID test deployment so the suite is fast and deterministic.
/// </summary>
public sealed class NyxIdDynamicClientRegistrationClientTests
{
    [Fact]
    public async Task RegisterPublicClient_ReturnsClientIdAndIssuedAt_OnSuccess()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, new
        {
            client_id = "client-issued",
            client_id_issued_at = 1700000123L,
        });
        var registrar = new NyxIdDynamicClientRegistrationClient(
            new HttpClient(handler), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance);

        var result = await registrar.RegisterPublicClientAsync(
            "https://nyxid.test", "aevatar", "https://aevatar.test/api/oauth/nyxid-callback");

        result.ClientId.Should().Be("client-issued");
        result.IssuedAt.ToUnixTimeSeconds().Should().Be(1700000123);
        handler.Last.Should().NotBeNull();
        handler.Last!.RequestUri!.AbsoluteUri.Should().Be("https://nyxid.test/oauth/register");
    }

    [Fact]
    public async Task RegisterPublicClient_FallsBackIssuedAt_WhenServerOmitsTimestamp()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, new { client_id = "client-no-ts" });
        var registrar = new NyxIdDynamicClientRegistrationClient(
            new HttpClient(handler), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance);

        var result = await registrar.RegisterPublicClientAsync(
            "https://nyxid.test", "aevatar", "https://aevatar.test/cb");

        result.ClientId.Should().Be("client-no-ts");
        // Should populate IssuedAt with "now" — assert it's recent, not exact.
        (DateTimeOffset.UtcNow - result.IssuedAt).Duration().Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RegisterPublicClient_Throws_OnMissingClientId()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, new { client_name = "aevatar" });
        var registrar = new NyxIdDynamicClientRegistrationClient(
            new HttpClient(handler), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance);

        Func<Task> act = () => registrar.RegisterPublicClientAsync(
            "https://nyxid.test", "aevatar", "https://aevatar.test/cb");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not include a client_id*");
    }

    [Fact]
    public async Task RegisterPublicClient_Throws_OnNonSuccessResponse()
    {
        var handler = StubHandler.Json(HttpStatusCode.BadGateway, new { error = "upstream_unavailable" });
        var registrar = new NyxIdDynamicClientRegistrationClient(
            new HttpClient(handler), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance);

        Func<Task> act = () => registrar.RegisterPublicClientAsync(
            "https://nyxid.test", "aevatar", "https://aevatar.test/cb");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task RegisterPublicClient_TrimsTrailingSlashOnAuthority()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, new { client_id = "ok" });
        var registrar = new NyxIdDynamicClientRegistrationClient(
            new HttpClient(handler), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance);

        await registrar.RegisterPublicClientAsync(
            "https://nyxid.test/", "aevatar", "https://aevatar.test/cb");

        handler.Last!.RequestUri!.AbsoluteUri.Should().Be("https://nyxid.test/oauth/register");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object? _payload;
        public HttpRequestMessage? Last { get; private set; }

        private StubHandler(HttpStatusCode status, object? payload)
        {
            _status = status;
            _payload = payload;
        }

        public static StubHandler Json(HttpStatusCode status, object payload) => new(status, payload);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            // Buffer the request content so the test can inspect the body later
            // without racing against the request lifetime.
            if (request.Content is not null)
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            var response = new HttpResponseMessage(_status);
            if (_payload is not null)
                response.Content = JsonContent.Create(_payload);
            return response;
        }
    }
}
