using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdLLMProviderErrorClassificationTests
{
    [Fact]
    public void ClassifyUpstreamFailure_ShouldReportServiceUnavailable_For503()
    {
        var route = CreateRoute("/api/v1/proxy/s/chrono-llm-2", "gpt-5");
        var body = "{\"error\":{\"message\":\"Service temporarily unavailable\",\"type\":\"api_error\"}}";

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("boom"), 503, body, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.ServiceUnavailable);
        ex.Status.Should().Be(503);
        ex.RouteName.Should().Be("/api/v1/proxy/s/chrono-llm-2");
        ex.Model.Should().Be("gpt-5");
        ex.Message.Should().Contain("temporarily unavailable");
        ex.Message.Should().Contain("HTTP 503");
        ex.Message.Should().Contain("/api/v1/proxy/s/chrono-llm-2");
        ex.Message.Should().Contain("gpt-5");
        ex.Message.Should().Contain("retry shortly");
        ex.Message.Should().Contain("Service temporarily unavailable");
        ex.Message.Should().NotContain("{\"error\"");
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldReportRateLimited_For429()
    {
        var route = CreateRoute("/api/v1/proxy/s/chrono-llm", "gpt-4");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("boom"), 429, body: null, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.RateLimited);
        ex.Status.Should().Be(429);
        ex.Message.Should().Contain("rate limited");
        ex.Message.Should().Contain("HTTP 429");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void ClassifyUpstreamFailure_ShouldReportAuthFailure_For401Or403(int status)
    {
        var route = CreateRoute("gateway", "gpt-4");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("boom"), status, body: null, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.AuthenticationFailed);
        ex.Message.Should().Contain($"HTTP {status}");
        ex.Message.Should().Contain("signing in again");
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldReportUpstreamServerError_ForOther5xx()
    {
        var route = CreateRoute("gateway", "gpt-4");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("boom"), 502, body: null, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.UpstreamServerError);
        ex.Message.Should().Contain("HTTP 502");
        ex.Message.Should().Contain("unhealthy");
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldReportRequestRejected_ForOther4xx()
    {
        var route = CreateRoute("gateway", "gpt-4");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("boom"), 404, body: null, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.RequestRejected);
        ex.Message.Should().Contain("HTTP 404");
        ex.Message.Should().Contain("Check the model id");
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldReportProviderError_AndPreserveSourceMessage_WhenStatusMissing()
    {
        var route = CreateRoute("gateway", "gpt-4");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("network down"), status: null, body: null, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.ProviderError);
        ex.Message.Should().Contain("Provider error");
        ex.Message.Should().Contain("gateway");
        ex.Message.Should().Contain("gpt-4");
        ex.Message.Should().Contain("network down");
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldFallBackToTypeName_WhenSourceMessageIsBlank()
    {
        var route = CreateRoute("gateway", "gpt-4");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new InvalidOperationException(string.Empty), status: null, body: null, route);

        ex.Kind.Should().Be(NyxIdUpstreamFailureKind.ProviderError);
        ex.Message.Should().Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldIncludeNonJsonBodySummary()
    {
        var route = CreateRoute("gateway", "gpt-4");
        var body = "plain text failure reason";

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(new Exception("boom"), 500, body, route);

        ex.Message.Should().Contain("plain text failure reason");
    }

    [Fact]
    public void ExtractUpstreamStatusAndBody_ShouldReturnNullsForNonClientResultException()
    {
        var ex = new InvalidOperationException("local failure", new TimeoutException("timed out"));

        var (status, body) = NyxIdLLMProvider.ExtractUpstreamStatusAndBody(ex);

        status.Should().BeNull();
        body.Should().BeNull();
    }

    [Fact]
    public void ClassifyUpstreamFailure_ShouldPreserveInnerException()
    {
        var route = CreateRoute("gateway", "gpt-4");
        var inner = new InvalidOperationException("boom");

        var ex = NyxIdLLMProvider.ClassifyUpstreamFailure(inner, 503, body: null, route);

        ex.InnerException.Should().BeSameAs(inner);
    }

    private static NyxIdResolvedRoute CreateRoute(string routeName, string model)
    {
        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = model,
            Metadata = new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "test-token",
            },
        };

        return new NyxIdResolvedRoute(
            routeName,
            new Uri("https://nyx.example.com/"),
            request,
            "test-token");
    }
}
