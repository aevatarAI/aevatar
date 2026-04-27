using Aevatar.GAgents.ChannelRuntime.WorkflowModules;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.WorkflowModules;

/// <summary>
/// Pins the response classification matrix for <see cref="TwitterPublishModule"/> against the
/// 5 NyxID-proxy shapes the issue (#216) calls out. The module wiring (item resolution, Lark
/// surfacing) is exercised in higher-level integration tests; this file is the unit-level
/// contract for "given a downstream response, what user-facing classification falls out".
/// </summary>
public sealed class TwitterPublishOutcomeTests
{
    [Fact]
    public void ClassifyTwitterResponse_ReturnsTweetUrl_When_Twitter201Success()
    {
        // Twitter v2 returns `{ "data": { "id": "<id>", "text": "..." } }` on success; NyxID
        // forwards verbatim, so the absence of `error` plus a present `data.id` is the success
        // signal. The URL uses the no-handle form so we don't need a separate /users/me call.
        var response = """{"data":{"id":"1234567890","text":"hello world"}}""";

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeTrue();
        outcome.TweetUrl.Should().Be("https://x.com/i/web/status/1234567890");
        outcome.ErrorCode.Should().BeEmpty();
        outcome.HttpStatus.Should().Be(201);
    }

    [Fact]
    public void ClassifyTwitterResponse_ReturnsOauthRequired_When_Proxy401()
    {
        // NyxID wraps 4xx as `{ "error": true, "status": <http>, "body": "<raw>" }`. 401 is the
        // common "user has not connected Twitter at NyxID" path; the Lark message must steer
        // them at NyxID's re-authorization flow rather than asking ops to look at scopes.
        var response = """{"error": true, "status": 401, "body": "{\"title\":\"Unauthorized\"}"}""";

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_oauth_required");
        outcome.HttpStatus.Should().Be(401);
        outcome.LarkMessage.ToLowerInvariant().Should().Contain("oauth");
    }

    [Fact]
    public void ClassifyTwitterResponse_ReturnsAccessDenied_When_Proxy403()
    {
        var response = """{"error": true, "status": 403, "body": "{\"detail\":\"client app missing oauth permissions\"}"}""";

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_proxy_access_denied");
        outcome.HttpStatus.Should().Be(403);
    }

    [Fact]
    public void ClassifyTwitterResponse_ReturnsRateLimited_When_Proxy429()
    {
        var response = """{"error": true, "status": 429, "body": "{\"title\":\"Too Many Requests\"}"}""";

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_rate_limited");
        outcome.HttpStatus.Should().Be(429);
        // Rate-limit Lark message should include the numerical hint so users self-serve a retry.
        outcome.LarkMessage.Should().Contain("429");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void ClassifyTwitterResponse_ReturnsUpstreamError_When_Proxy5xx(int status)
    {
        var response = $$"""{"error": true, "status": {{status}}, "body": "{\"title\":\"Server Error\"}"}""";

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_upstream_error");
        outcome.HttpStatus.Should().Be(status);
        outcome.LarkMessage.Should().Contain(status.ToString());
    }

    [Fact]
    public void ClassifyTwitterResponse_ReturnsGenericRejection_When_OtherStatus()
    {
        // 422 (Unprocessable Entity) is what Twitter returns for things like duplicate-tweet
        // and content-policy violations. Don't bucket as 401/403/429/5xx — surface verbatim so
        // the user can read the actual rejection reason (e.g. "duplicate content").
        var response = """{"error": true, "status": 422, "body": "{\"title\":\"You attempted to create a Tweet with content that has already been posted recently.\"}"}""";

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_publish_rejected");
        outcome.HttpStatus.Should().Be(422);
        outcome.LarkMessage.Should().Contain("422");
    }

    [Fact]
    public void ClassifyTwitterResponse_HandlesEmptyResponse()
    {
        // An empty proxy body should not silently look like success; surface as failure with a
        // distinct code so logs don't conflate "Twitter accepted but didn't return a body" with
        // "publish actually went through".
        var outcome = TwitterPublishModule.ClassifyTwitterResponse(string.Empty);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_publish_empty_response");
    }

    [Fact]
    public void ClassifyTwitterResponse_HandlesUnparseableJson()
    {
        // NyxID is supposed to return JSON, but if a transport-layer error returned plain text
        // we should not crash — emit a categorized failure code and the test verifies the
        // module's robustness against malformed input.
        var outcome = TwitterPublishModule.ClassifyTwitterResponse("<html>internal error</html>");

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_publish_unparseable_response");
    }

    [Fact]
    public void ClassifyTwitterResponse_RecognizesTwitterNativeErrorsArrayShape()
    {
        // PR #461 review item #2: Twitter v2 sometimes returns the native error shape with no
        // NyxID-wrap envelope, e.g. duplicate-tweet (code 187) or content-policy violations.
        // The classifier must surface the Twitter `message` text in the Lark surfacing so the
        // user reads the actual rejection reason, not a generic "publish failed".
        var response = """
            {
              "title": "Conflict",
              "detail": "You attempted to create a Tweet with content that has already been posted recently.",
              "errors": [
                {"message": "duplicate content", "code": 187}
              ]
            }
            """;

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_publish_rejected");
        outcome.LarkMessage.Should().Contain("duplicate content");
        outcome.LarkMessage.Should().Contain("187");
    }

    [Fact]
    public void ClassifyTwitterResponse_RecognizesTwitterNativeRfc7807Shape_WithoutErrorsArray()
    {
        // RFC 7807 Problem Details — Twitter v2 occasionally omits the `errors` array but
        // still provides `title` / `detail`. Don't fall through to "unexpected_shape" in this
        // case; treat as a native rejection so the user sees Twitter's text.
        var response = """
            {
              "title": "tweet_create_error",
              "detail": "Your account is temporarily restricted from creating Tweets."
            }
            """;

        var outcome = TwitterPublishModule.ClassifyTwitterResponse(response);

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_publish_rejected");
        outcome.LarkMessage.Should().Contain("temporarily restricted");
    }

    [Fact]
    public void ClassifyTwitterResponse_FailsWithUnexpectedShape_When_NoSuccessNoErrorEnvelope()
    {
        // Empty object — neither success nor any of the recognized error shapes. Must not
        // silently look like success; classify as `twitter_publish_unexpected_shape` so logs
        // surface the anomaly.
        var outcome = TwitterPublishModule.ClassifyTwitterResponse("{}");

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be("twitter_publish_unexpected_shape");
    }
}
