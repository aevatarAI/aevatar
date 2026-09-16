using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdModelDiscoveryHttpClientTests
{
    [Fact]
    public void Parser_ShouldReturnStableDistinctModelsAndOptionalDefault()
    {
        const string json = """
            {
              "object": "list",
              "data": [
                { "id": "gpt-5.5", "object": "model" },
                { "id": "gpt-5.4-mini" },
                { "id": "gpt-5.5" }
              ],
              "default_model": "gpt-5.5"
            }
            """;

        var result = NyxIdModelDiscoveryParser.Parse(json);

        result.ModelIds.Should().Equal("gpt-5.4-mini", "gpt-5.5");
        result.DefaultModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public void Parser_WithValidEmptyData_ShouldReturnEmptySuggestions()
    {
        var result = NyxIdModelDiscoveryParser.Parse("{\"data\":[]}");

        result.ModelIds.Should().BeEmpty();
        result.DefaultModelId.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[{\"id\":\" model-a\"}]}")]
    [InlineData("{\"data\":[{\"id\":\"model-*\"}]}")]
    [InlineData("{\"data\":[{\"id\":\"model-a\"}],\"default_model\":\"model-b\"}")]
    [InlineData("{\"data\":[],\"data\":[]}")]
    public void Parser_WithInvalidContract_ShouldFailClosed(string json)
    {
        var act = () => NyxIdModelDiscoveryParser.Parse(json);

        act.Should().Throw<NyxIdModelDiscoveryException>()
            .Which.Kind.Should().Be(NyxIdModelDiscoveryFailureKind.ResponseInvalid);
    }

    [Fact]
    public void Parser_WithTooManyEntries_ShouldFailClosedWithoutTruncating()
    {
        var entries = Enumerable.Range(0, LLMSelectionPolicy.MaxModelsPerCatalog + 1)
            .Select(static index => $"{{\"id\":\"model-{index}\"}}");
        var json = $"{{\"data\":[{string.Join(',', entries)}]}}";

        var act = () => NyxIdModelDiscoveryParser.Parse(json);

        act.Should().Throw<NyxIdModelDiscoveryException>()
            .Which.Kind.Should().Be(NyxIdModelDiscoveryFailureKind.ResponseTooLarge);
    }

    [Fact]
    public async Task ReadMethods_ShouldUseExactNyxIdProxyRoutesAndBearerToken()
    {
        const string scopePath = "/api/v1/proxy/s/chrono-llm/models?_nyxid_via=us%20alpha%2B1";
        const string platformPath = "/api/v1/proxy/catalog-alpha/models";
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            [scopePath] = new(HttpStatusCode.OK, "{\"data\":[{\"id\":\"gpt-5.5\"}]}"),
            [platformPath] = new(HttpStatusCode.OK, "{\"data\":[{\"id\":\"gpt-5.4\"}]}"),
        });
        var client = CreateClient(handler);

        var scope = await client.GetScopeModelsAsync(
            "scope-token",
            "chrono-llm",
            "us alpha+1",
            CancellationToken.None);
        var platform = await client.GetPlatformModelsAsync(
            "scope-token",
            "catalog-alpha",
            CancellationToken.None);

        scope.ModelIds.Should().Equal("gpt-5.5");
        platform.ModelIds.Should().Equal("gpt-5.4");
        handler.Requests.Select(static request => request.PathAndQuery)
            .Should().Equal(scopePath, platformPath);
        handler.Requests.Should().OnlyContain(static request =>
            request.Method == HttpMethod.Get &&
            request.Authorization != null &&
            request.Authorization.Scheme == "Bearer" &&
            request.Authorization.Parameter == "scope-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, NyxIdModelDiscoveryFailureKind.UpstreamRejected)]
    [InlineData(HttpStatusCode.Forbidden, NyxIdModelDiscoveryFailureKind.UpstreamRejected)]
    [InlineData(HttpStatusCode.NotFound, NyxIdModelDiscoveryFailureKind.EndpointNotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, NyxIdModelDiscoveryFailureKind.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, NyxIdModelDiscoveryFailureKind.Unavailable)]
    public async Task ReadMethod_WhenProxyRejectsRequest_ShouldPreserveTypedFailureWithoutBody(
        HttpStatusCode statusCode,
        NyxIdModelDiscoveryFailureKind expectedKind)
    {
        const string sensitiveBody = "{\"access_token\":\"response-secret\"}";
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            ["/api/v1/proxy/catalog-alpha/models"] = new(statusCode, sensitiveBody),
        });
        var client = CreateClient(handler);

        var act = () => client.GetPlatformModelsAsync(
            "scope-token",
            "catalog-alpha",
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdModelDiscoveryException>();
        exception.Which.Kind.Should().Be(expectedKind);
        exception.Which.StatusCode.Should().Be(statusCode);
        exception.Which.Message.Should().NotContain("response-secret");
        exception.Which.Message.Should().NotContain("access_token");
    }

    [Fact]
    public async Task ReadMethod_WhenSuccessfulBodyExceedsLimit_ShouldFailClosed()
    {
        var handler = new RecordingHandler(new Dictionary<string, StubResponse>(StringComparer.Ordinal)
        {
            ["/api/v1/proxy/catalog-alpha/models"] = new(
                HttpStatusCode.OK,
                new string('x', NyxIdModelDiscoveryHttpClient.MaxResponseBodyBytes + 1)),
        });
        var client = CreateClient(handler);

        var act = () => client.GetPlatformModelsAsync(
            "scope-token",
            "catalog-alpha",
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdModelDiscoveryException>();
        exception.Which.Kind.Should().Be(NyxIdModelDiscoveryFailureKind.ResponseTooLarge);
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void AddStudioHostingCore_ShouldRegisterDiscoveryPortWithBoundedDefaults()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioHostingCore(configuration);

        services.Should().ContainSingle(static descriptor =>
            descriptor.ServiceType == typeof(INyxIdModelDiscoveryPort) &&
            descriptor.ImplementationType == typeof(NyxIdModelDiscoveryHttpClient) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        NyxIdModelDiscoveryHttpClient.MaxResponseBodyBytes.Should().Be(4 * 1024 * 1024);
        NyxIdModelDiscoveryHttpClient.SourceTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    private static NyxIdModelDiscoveryHttpClient CreateClient(RecordingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyxid.example",
            })
            .Build();
        return new NyxIdModelDiscoveryHttpClient(
            new StubHttpClientFactory(handler),
            configuration,
            NullLogger<NyxIdModelDiscoveryHttpClient>.Instance);
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
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            Requests.Add(new RecordedRequest(
                request.Method,
                pathAndQuery,
                request.Headers.Authorization));

            var response = responses.TryGetValue(pathAndQuery, out var configured)
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
        string PathAndQuery,
        AuthenticationHeaderValue? Authorization);

    private sealed record StubResponse(HttpStatusCode StatusCode, string Body);
}
