using System.Net;
using System.Text.Json;
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

        handler.RequestUrls.Should().ContainSingle("https://8.8.8.8/large");
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
}
