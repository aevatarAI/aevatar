using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdDelegationRefreshClientTests
{
    [Fact]
    public async Task RefreshAsync_ShouldSendBearerWithoutBody_AndParseTypedResponse()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"delegation-2","token_type":"Bearer","expires_in":300,"scope":"openid service:read"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var client = CreateClient(handler);

        var result = await client.RefreshDelegationAsync(" delegation-1 ", CancellationToken.None);

        result.Should().BeEquivalentTo(new NyxIdDelegationRefreshResult(
            true,
            "delegation-2",
            "Bearer",
            300,
            "openid service:read"));
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/delegation/refresh");
        handler.LastRequest.Headers.Authorization!.ToString().Should().Be("Bearer delegation-1");
        handler.LastRequestBody.Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_ShouldFailClosedForMissingTokenProviderErrorOrMalformedSuccess()
    {
        var missing = await CreateClient("{}").RefreshDelegationAsync(" ", CancellationToken.None);
        missing.Should().BeEquivalentTo(new NyxIdDelegationRefreshResult(
            false,
            Detail: "missing_delegation_token"));

        var denied = await CreateClient(
                """{"error":"client_not_found","error_code":1002,"message":"do-not-expose-this-provider-message"}""",
                HttpStatusCode.Forbidden)
            .RefreshDelegationAsync("delegation-1", CancellationToken.None);
        denied.Succeeded.Should().BeFalse();
        denied.Detail.Should().Be("nyx_status=403 provider_error=client_not_found");
        denied.Detail.Should().NotContain("do-not-expose");
        denied.HttpStatus.Should().Be(403);
        denied.ProviderErrorCode.Should().Be("client_not_found");

        var malformed = await CreateClient(
                """{"access_token":"delegation-2","token_type":"bearer","expires_in":0,"scope":"openid"}""")
            .RefreshDelegationAsync("delegation-1", CancellationToken.None);
        malformed.Should().BeEquivalentTo(new NyxIdDelegationRefreshResult(
            false,
            Detail: "invalid_delegation_refresh_response"));
    }

    [Fact]
    public async Task RefreshAsync_ShouldRejectOversizedResponseWithoutBufferingItAsProviderDetail()
    {
        var oversized = new string('x', NyxIdApiClient.DelegationRefreshMaxResponseBytes + 1);

        var result = await CreateClient(oversized)
            .RefreshDelegationAsync("delegation-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Detail.Should().Be("delegation_refresh_response_too_large");
        result.Detail.Should().NotContain(oversized);
    }

    private static NyxIdApiClient CreateClient(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        CreateClient(new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        }, statusCode));

    private static NyxIdApiClient CreateClient(CaptureHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CaptureHandler(HttpResponseMessage response)
            : this(response, response.StatusCode)
        {
        }

        public CaptureHandler(HttpResponseMessage response, HttpStatusCode statusCode)
        {
            _response = response;
            _response.StatusCode = statusCode;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
