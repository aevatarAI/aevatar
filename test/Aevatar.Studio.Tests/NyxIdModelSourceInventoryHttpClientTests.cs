using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdModelSourceInventoryHttpClientTests
{
    [Fact]
    public async Task ReadMethods_ShouldCallOnlyTypedInventoryEndpointsWithBearerToken()
    {
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            [NyxIdModelSourceInventoryHttpClient.PlatformCatalogServicesPath] = new(
                HttpStatusCode.OK,
                """{"services":[{"id":"cat-alpha","name":"Alpha","slug":"alpha","service_type":"http","visibility":"public","auth_method":"none","service_category":"internal","requires_user_credential":false,"is_active":true}]}"""),
            [NyxIdModelSourceInventoryHttpClient.ScopeKeysPath] = new(
                HttpStatusCode.OK,
                """{"keys":[{"id":"us-alpha","catalog_service_id":"cat-alpha","slug":"alpha-user","label":"Alpha User","catalog_service_name":"Alpha","is_active":true,"service_type":"http","status":"active","credential_missing":false,"connection_status":null,"node_id":null,"node_status":null,"credential_source":{"type":"personal"}}]}"""),
        });
        var client = CreateClient(handler);

        var platform = await client.GetPlatformCatalogServicesAsync("scope-token", CancellationToken.None);
        var scope = await client.GetScopeModelSourcesAsync("scope-token", CancellationToken.None);

        platform.Services.Should().ContainSingle()
            .Which.CatalogServiceId.Should().Be("cat-alpha");
        scope.Services.Should().ContainSingle()
            .Which.UserServiceId.Should().Be("us-alpha");
        handler.Requests.Select(static request => request.Path).Should().Equal(
            NyxIdModelSourceInventoryHttpClient.PlatformCatalogServicesPath,
            NyxIdModelSourceInventoryHttpClient.ScopeKeysPath);
        handler.Requests.Should().OnlyContain(static request => request.Method == HttpMethod.Get);
        handler.Requests.Should().OnlyContain(static request =>
            request.Authorization != null &&
            request.Authorization.Scheme == "Bearer" &&
            request.Authorization.Parameter == "scope-token");
        handler.Requests.Should().ContainSingle(static request =>
            request.Path == NyxIdModelSourceInventoryHttpClient.ScopeKeysPath);
        handler.Requests.Should().NotContain(static request =>
            request.Path.Contains("/user-services", StringComparison.Ordinal) ||
            request.Path.Contains("/models", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadMethod_WhenNyxIdRejectsRequest_ShouldNotExposeResponseBody()
    {
        const string sensitiveBody = "{\"access_token\":\"response-secret\"}";
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            [NyxIdModelSourceInventoryHttpClient.PlatformCatalogServicesPath] = new(
                HttpStatusCode.Unauthorized,
                sensitiveBody),
        });
        var client = CreateClient(handler);

        var act = () => client.GetPlatformCatalogServicesAsync("scope-token", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdModelSourceInventoryException>();
        exception.Which.Kind.Should().Be(
            NyxIdModelSourceInventoryFailureKind.AuthenticationRejected);
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        exception.Which.Message.Should().NotContain("response-secret");
        exception.Which.Message.Should().NotContain("access_token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, NyxIdModelSourceInventoryFailureKind.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, NyxIdModelSourceInventoryFailureKind.Unavailable)]
    public async Task ReadMethod_WhenNyxIdReturnsFailure_ShouldPreserveTypedFailure(
        HttpStatusCode statusCode,
        NyxIdModelSourceInventoryFailureKind expectedKind)
    {
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            [NyxIdModelSourceInventoryHttpClient.PlatformCatalogServicesPath] = new(
                statusCode,
                "{\"detail\":\"upstream-sensitive-detail\"}"),
        });
        var client = CreateClient(handler);

        var act = () => client.GetPlatformCatalogServicesAsync("scope-token", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdModelSourceInventoryException>();
        exception.Which.Kind.Should().Be(expectedKind);
        exception.Which.StatusCode.Should().Be(statusCode);
        exception.Which.Message.Should().NotContain("upstream-sensitive-detail");
    }

    [Fact]
    public async Task ReadMethod_WhenSuccessfulBodyExceedsLimit_ShouldFailClosed()
    {
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            [NyxIdModelSourceInventoryHttpClient.PlatformCatalogServicesPath] = new(
                HttpStatusCode.OK,
                new string('x', NyxIdModelSourceInventoryHttpClient.MaxResponseBodyBytes + 1)),
        });
        var client = CreateClient(handler);

        var act = () => client.GetPlatformCatalogServicesAsync("scope-token", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdModelSourceInventoryException>();
        exception.Which.Kind.Should().Be(NyxIdModelSourceInventoryFailureKind.Unavailable);
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void InventorySourceLimits_ShouldRemainBounded()
    {
        NyxIdModelSourceInventoryHttpClient.MaxResponseBodyBytes.Should().Be(4 * 1024 * 1024);
        NyxIdModelSourceInventoryHttpClient.SourceTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ReadMethod_WithoutPublicApiBaseUrl_ShouldFailBeforeSendingRequest()
    {
        var handler = new RecordingHandler(
            new Dictionary<string, StubResponse>(StringComparer.Ordinal));
        var configuration = new ConfigurationBuilder().Build();
        var client = new NyxIdModelSourceInventoryHttpClient(
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<NyxIdModelSourceInventoryHttpClient>.Instance);

        var act = () => client.GetPlatformCatalogServicesAsync("scope-token", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*base URL is not configured*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioHostingCore_ShouldRegisterInventoryPort()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioHostingCore(configuration);

        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdModelSourceInventoryPort) &&
            descriptor.ImplementationType == typeof(NyxIdModelSourceInventoryHttpClient) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static NyxIdModelSourceInventoryHttpClient CreateClient(RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyxid.example",
            })
            .Build();
        return new NyxIdModelSourceInventoryHttpClient(
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<NyxIdModelSourceInventoryHttpClient>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(IReadOnlyDictionary<string, StubResponse> responses)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add(new RecordedRequest(
                request.Method,
                path,
                request.Headers.Authorization));

            var response = responses.TryGetValue(path, out var configured)
                ? configured
                : new StubResponse(HttpStatusCode.NotFound, "{}");
            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        AuthenticationHeaderValue? Authorization);

    private sealed record StubResponse(HttpStatusCode StatusCode, string Body);
}
