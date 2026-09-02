using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Web.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class WebFetchToolExecutionTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldReturnBoundaryJson_WhenFetchRedirectsAcrossHosts()
    {
        var handler = new RecordingFetchHandler(_ => Redirect("https://8.8.4.4/final"));
        using var http = new HttpClient(handler);
        var tool = CreateTool(http);

        var result = await tool.ExecuteAsync("""{"url":"http://8.8.8.8/start"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("redirect");
        root.GetProperty("original_url").GetString().Should().Be("https://8.8.8.8/start");
        root.GetProperty("redirect_url").GetString().Should().Be("https://8.8.4.4/final");
        root.GetProperty("message").GetString().Should().Be(
            "The URL redirected to a different host. Fetch the redirect_url to get the content.");

        handler.RequestUrls.Should().ContainSingle("https://8.8.8.8/start");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTruncatedBoundaryJson_WhenFetchedContentExceedsToolLimit()
    {
        const int maxToolContentChars = 50_000;
        var oversizedContent = new string('x', maxToolContentChars + 7);
        var handler = new RecordingFetchHandler(_ => Ok(oversizedContent));
        using var http = new HttpClient(handler);
        var tool = CreateTool(http);

        var result = await tool.ExecuteAsync("""{"url":"http://8.8.8.8/large"}""");

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("url").GetString().Should().Be("https://8.8.8.8/large");
        root.GetProperty("status_code").GetInt32().Should().Be(200);
        root.GetProperty("content_type").GetString().Should().Be("text/plain");
        root.GetProperty("content").GetString().Should().Be(new string('x', maxToolContentChars));
        root.GetProperty("truncated").GetBoolean().Should().BeTrue();
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-success", tool.Name, "{}", result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);

        handler.RequestUrls.Should().ContainSingle("https://8.8.8.8/large");
    }

    [Fact]
    public async Task ExecuteAsync_Http503_ShouldReturnTypedFailureReceiptWithoutRawBody()
    {
        var handler = new RecordingFetchHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("provider-secret"),
        });
        using var http = new HttpClient(handler);
        var tool = CreateTool(http);

        var result = await tool.ExecuteAsync("""{"url":"https://8.8.8.8/failure"}""");

        AssertFailureReceipt(tool, result, "WEB_FETCH_HTTP_503");
        result.Should().NotContain("provider-secret");
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "WEB_FETCH_DNS_FAILURE")]
    [InlineData(HttpRequestError.SecureConnectionError, "WEB_FETCH_TLS_FAILURE")]
    public async Task ExecuteAsync_TransportFailure_ShouldReturnTypedFailureReceipt(
        HttpRequestError requestError,
        string expectedCode)
    {
        var handler = new ThrowingFetchHandler(new HttpRequestException(requestError, "provider-secret"));
        using var http = new HttpClient(handler);
        var tool = CreateTool(http);

        var result = await tool.ExecuteAsync("""{"url":"https://8.8.8.8/failure"}""");

        AssertFailureReceipt(tool, result, expectedCode);
        result.Should().NotContain("provider-secret");
    }

    [Fact]
    public void CreateResultReceipt_HostResolutionErrorJson_ShouldReturnDnsFailureReceipt()
    {
        using var http = new HttpClient(new RecordingFetchHandler(_ => Ok(string.Empty)));
        var tool = CreateTool(http);
        const string result =
            """{"error":"host_resolution_failed","message":"host_resolution_failed"}""";

        AssertFailureReceipt(tool, result, "WEB_FETCH_DNS_FAILURE");
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ShouldReturnTypedFailureReceipt()
    {
        var handler = new ThrowingFetchHandler(new OperationCanceledException());
        using var http = new HttpClient(handler);
        var tool = CreateTool(http);

        var result = await tool.ExecuteAsync("""{"url":"https://8.8.8.8/slow"}""");

        AssertFailureReceipt(tool, result, "WEB_FETCH_TIMEOUT");
    }

    private static void AssertFailureReceipt(WebFetchTool tool, string result, string expectedCode)
    {
        var receipt = ((IAgentTool)tool).CreateResultReceipt("call-failure", tool.Name, "{}", result);
        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be(expectedCode);
        receipt.ResultJson.Should().Be(result);
    }

    private static WebFetchTool CreateTool(HttpClient http) =>
        new(new WebApiClient(new WebToolOptions(), http));

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Content = new StringContent(string.Empty),
        };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class RecordingFetchHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingFetchHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
