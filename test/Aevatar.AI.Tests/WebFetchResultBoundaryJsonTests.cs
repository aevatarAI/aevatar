using System.Text.Json;
using Aevatar.AI.ToolProviders.Web;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class WebFetchResultBoundaryJsonTests
{
    [Fact]
    public void FetchResultBoundaryJson_ShouldRoundTripTypedDto()
    {
        var result = new WebFetchResult(
            200,
            "text/html",
            "<html>fresh</html>",
            "https://example.com/final",
            "https://example.com/start");

        var json = WebToolResultBoundaryJson.ToBoundaryJson(result);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("url").GetString().Should().Be("https://example.com/start");
        root.GetProperty("status_code").GetInt32().Should().Be(200);
        root.GetProperty("content_type").GetString().Should().Be("text/html");
        root.GetProperty("content").GetString().Should().Be("<html>fresh</html>");
        root.GetProperty("redirect_url").GetString().Should().Be("https://example.com/final");
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();

        var roundTrip = WebToolResultBoundaryJson.ParseFetchPayload(json);

        roundTrip.Should().Be(result);
    }

    [Fact]
    public void FetchResultBoundaryJson_ShouldMapNullBodyToEmptyContentAndOmitBlankRedirect()
    {
        var result = new WebFetchResult(
            204,
            "text/plain",
            null,
            " ",
            "https://example.com/empty");

        var json = WebToolResultBoundaryJson.ToBoundaryJson(result);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("content").GetString().Should().BeEmpty();
        root.TryGetProperty("redirect_url", out _).Should().BeFalse();
        root.GetProperty("truncated").GetBoolean().Should().BeFalse();

        WebToolResultBoundaryJson.ParseFetchPayload(json).Should().Be(
            new WebFetchResult(
                204,
                "text/plain",
                string.Empty,
                null,
                "https://example.com/empty"));
    }

    [Fact]
    public void FetchResultBoundaryJson_ShouldRoundTripTypedErrorWithoutRawBody()
    {
        var result = new WebFetchResult(
            503,
            "error",
            null,
            null,
            "https://example.com/failure",
            new WebToolError("WEB_FETCH_HTTP_503", "The web request failed."));

        var json = WebToolResultBoundaryJson.ToBoundaryJson(result);

        json.Should().Be(
            """{"error":"WEB_FETCH_HTTP_503","message":"The web request failed."}""");
        WebToolResultBoundaryJson.ParseFetchPayload(json).Error.Should().Be(result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("[1,2,3]")]
    public void FetchResultFromJson_ShouldTolerateMalformedJson(string payload)
    {
        var result = WebToolResultBoundaryJson.ParseFetchPayload(payload);

        result.Should().Be(new WebFetchResult(0, string.Empty, string.Empty, null, string.Empty));
    }
}
