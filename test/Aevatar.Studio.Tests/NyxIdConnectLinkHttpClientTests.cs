using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Infrastructure.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdConnectLinkHttpClientTests
{
    [Fact]
    public async Task CreateAsync_ShouldOmitCallbackAndRedactHostedUrlFromFormatting()
    {
        const string hostedUrl = "https://nyx.example/connect?token=nyx_clk_secret";
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
              {
                "id": "link-alpha",
                "connect_url": "{{hostedUrl}}",
                "expires_at": "2026-08-16T14:15:16.123Z"
              }
              """));
        var client = CreateClient(handler);

        var result = await client.CreateAsync(
            "caller-bearer",
            new NyxIdConnectLinkCreateRequest(
                "api-lark",
                Label: "Delivery Lark",
                RequestedBy: "workflow-delivery",
                ExpiresInSeconds: 900));

        result.ConnectLinkId.Should().Be("link-alpha");
        result.ConnectUrl.Should().Be(hostedUrl);
        result.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-08-16T14:15:16.123Z"));
        result.ToString().Should().Contain("[REDACTED]");
        result.ToString().Should().NotContain("nyx_clk_secret");

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.PathAndQuery.Should().Be("/api/v1/connect-links");
        request.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "caller-bearer"));
        using var payload = JsonDocument.Parse(request.Body);
        payload.RootElement.GetProperty("service_slug").GetString().Should().Be("api-lark");
        payload.RootElement.GetProperty("label").GetString().Should().Be("Delivery Lark");
        payload.RootElement.GetProperty("requested_by").GetString().Should().Be("workflow-delivery");
        payload.RootElement.TryGetProperty("callback_url", out _).Should().BeFalse();
        payload.RootElement.GetProperty("expires_in").GetInt64().Should().Be(900);
    }

    [Fact]
    public async Task GetAsync_WhenCompleted_ShouldMapConnectedServiceIdToUserServiceId()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
              {
                "id": "link-alpha",
                "status": "completed",
                "service_name": "Lark",
                "service_slug": "api-lark",
                "expires_at": "2026-08-16T14:15:16.123Z",
                "completed_at": "2026-08-16T14:10:00.000Z",
                "connected_service": {
                  "id": "us-alpha",
                  "slug": "api-lark"
                }
              }
              """));
        var client = CreateClient(handler);

        var result = await client.GetAsync("caller-bearer", "link-alpha");

        result.Status.Should().Be(NyxIdConnectLinkStatus.Completed);
        result.ServiceSlug.Should().Be("api-lark");
        result.UserServiceId.Should().Be("us-alpha");
        result.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-08-16T14:10:00.000Z"));
        handler.Requests.Should().ContainSingle().Which.PathAndQuery
            .Should().Be("/api/v1/connect-links/link-alpha");
    }

    [Fact]
    public async Task GetAsync_WhenPending_ShouldNotManufactureConnectionReference()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
              {
                "id": "link-pending",
                "status": "pending",
                "service_name": "Lark",
                "service_slug": "api-lark",
                "expires_at": "2026-08-16T14:15:16.123Z"
              }
              """));
        var client = CreateClient(handler);

        var result = await client.GetAsync("caller-bearer", "link-pending");

        result.Status.Should().Be(NyxIdConnectLinkStatus.Pending);
        result.UserServiceId.Should().BeNull();
        result.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenCompletedReferenceIsMissing_ShouldFailClosed()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
              {
                "id": "link-alpha",
                "status": "completed",
                "service_name": "Lark",
                "service_slug": "api-lark",
                "expires_at": "2026-08-16T14:15:16.123Z",
                "completed_at": "2026-08-16T14:10:00.000Z"
              }
              """));
        var client = CreateClient(handler);

        var act = () => client.GetAsync("caller-bearer", "link-alpha");

        var exception = await act.Should().ThrowAsync<NyxIdConnectLinkException>();
        exception.Which.Kind.Should().Be(NyxIdConnectLinkFailureKind.ResponseInvalid);
        exception.Which.Message.Should().Contain("connected_service.id");
    }

    [Fact]
    public async Task CreateAsync_WhenUpstreamFails_ShouldReturnTypedFailureWithoutResponseBody()
    {
        const string sensitiveBody = "{\"access_token\":\"response-secret\"}";
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.ServiceUnavailable, sensitiveBody));
        var client = CreateClient(handler);

        var act = () => client.CreateAsync(
            "caller-bearer",
            new NyxIdConnectLinkCreateRequest("api-lark"));

        var exception = await act.Should().ThrowAsync<NyxIdConnectLinkException>();
        exception.Which.Kind.Should().Be(NyxIdConnectLinkFailureKind.Unavailable);
        exception.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        exception.Which.Message.Should().NotContain("response-secret");
        exception.Which.Message.Should().NotContain("access_token");
    }

    [Theory]
    [InlineData("https://aevatar.example/delivery#/customer/delivery-alpha")]
    [InlineData("https://user@aevatar.example/delivery")]
    public async Task CreateAsync_WhenCallbackCannotBeAcceptedByNyxId_ShouldFailBeforeRequest(
        string callbackUrl)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Request was not expected."));
        var client = CreateClient(handler);

        var act = () => client.CreateAsync(
            "caller-bearer",
            new NyxIdConnectLinkCreateRequest(
                "api-lark",
                CallbackUrl: new Uri(callbackUrl)));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*without user info or a fragment*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioInfrastructure_ShouldRegisterConnectLinkPort()
    {
        var services = new ServiceCollection();

        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdConnectLinkPort) &&
            descriptor.ImplementationType == typeof(NyxIdConnectLinkHttpClient) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static NyxIdConnectLinkHttpClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyxid.example",
            })
            .Build();
        return new NyxIdConnectLinkHttpClient(
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<NyxIdConnectLinkHttpClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string PathAndQuery,
        AuthenticationHeaderValue? Authorization,
        string Body);
}
