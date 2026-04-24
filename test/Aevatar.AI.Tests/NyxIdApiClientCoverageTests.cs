using System.Net;
using System.Reflection;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdApiClientCoverageTests
{
    [Fact]
    public async Task RefreshSessionAsync_ShouldValidateInput_AndParseSuccessResponse()
    {
        var missingTokenClient = CreateClient("""{"access_token":"ignored"}""");
        var missingToken = await missingTokenClient.RefreshSessionAsync("   ", CancellationToken.None);
        missingToken.Succeeded.Should().BeFalse();
        missingToken.Detail.Should().Be("missing_refresh_token");

        var handler = new CaptureHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"access-1","refresh_token":"refresh-2","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        var result = await client.RefreshSessionAsync("refresh-token", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().Be("access-1");
        result.RefreshToken.Should().Be("refresh-2");
        result.ExpiresIn.Should().Be(3600);
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://nyx.example.com/api/v1/auth/refresh");
        handler.LastRequest.Headers.Authorization.Should().BeNull();
        handler.LastRequestBody.Should().Contain("\"refresh_token\":\"refresh-token\"");
    }

    [Fact]
    public async Task RefreshSessionAsync_ShouldSurfaceErrorDetails_ForKnownFailureShapes()
    {
        var errorClient = CreateClient("""{"error":true,"status":401,"body":"expired","message":"denied"}""");
        var error = await errorClient.RefreshSessionAsync("refresh-token", CancellationToken.None);
        error.Succeeded.Should().BeFalse();
        error.Detail.Should().Be("nyx_status=401 body=expired message=denied");

        var missingAccessClient = CreateClient("""{"refresh_token":"refresh-2","expires_in":1800}""");
        var missingAccess = await missingAccessClient.RefreshSessionAsync("refresh-token", CancellationToken.None);
        missingAccess.Succeeded.Should().BeFalse();
        missingAccess.Detail.Should().Be("invalid_refresh_response missing_access_token");
    }

    [Fact]
    public async Task RefreshSessionAsync_AndSendChannelRelayReply_ShouldHandleInvalidJsonResponses()
    {
        var refreshClient = CreateClient("not-json");
        var refreshResult = await refreshClient.RefreshSessionAsync("refresh-token", CancellationToken.None);

        refreshResult.Succeeded.Should().BeFalse();
        refreshResult.Detail.Should().Be("invalid_error_envelope response_length=8");

        var replyClient = CreateClient("not-json");
        var replyResult = await replyClient.SendChannelRelayTextReplyAsync(
            "token",
            "message-1",
            "hello",
            CancellationToken.None);

        replyResult.Succeeded.Should().BeFalse();
        replyResult.Detail.Should().Be("invalid_error_envelope response_length=8");
    }

    [Fact]
    public async Task SendChannelRelayTextReplyAsync_ShouldValidateRequiredInputs_AndSurfaceErrors()
    {
        var client = CreateClient("""{"error":true,"status":502,"body":"gateway","message":"offline"}""");

        (await client.SendChannelRelayTextReplyAsync(" ", "message-1", "hello", CancellationToken.None))
            .Should()
            .BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_access_token"));
        (await client.SendChannelRelayTextReplyAsync("token", " ", "hello", CancellationToken.None))
            .Should()
            .BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_message_id"));
        (await client.SendChannelRelayTextReplyAsync("token", "message-1", " ", CancellationToken.None))
            .Should()
            .BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_text"));

        var error = await client.SendChannelRelayTextReplyAsync("token", "message-1", "hello", CancellationToken.None);
        error.Succeeded.Should().BeFalse();
        error.Detail.Should().Be("nyx_status=502 body=gateway message=offline");
    }

    [Fact]
    public async Task UpdateChannelRelayReplyAsync_ShouldValidateInputs()
    {
        var client = CreateClient("""{"upstream_message_id":"om_upstream","edited_at":"2026-04-24T09:00:00Z"}""");

        (await client.UpdateChannelRelayReplyAsync(" ", "om_upstream", new ChannelRelayReplyBody("hi"), CancellationToken.None))
            .Should()
            .BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_access_token"));
        (await client.UpdateChannelRelayReplyAsync("token", " ", new ChannelRelayReplyBody("hi"), CancellationToken.None))
            .Should()
            .BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_platform_message_id"));
        (await client.UpdateChannelRelayReplyAsync("token", "om_upstream", new ChannelRelayReplyBody(null), CancellationToken.None))
            .Should()
            .BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_payload"));
    }

    [Fact]
    public async Task UpdateChannelRelayReplyAsync_ShouldPostExpectedRequestAndParseUpstreamId()
    {
        var handler = new CaptureHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"upstream_message_id":"om_abc123","edited_at":"2026-04-24T09:00:00Z"}""",
                Encoding.UTF8,
                "application/json"),
        });
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);

        var result = await client.UpdateChannelRelayTextReplyAsync(
            "token",
            "om_abc123",
            "hello world",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.PlatformMessageId.Should().Be("om_abc123");
        result.EditUnsupported.Should().BeFalse();
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/v1/channel-relay/reply/update");
        handler.LastRequestBody.Should().Contain("\"message_id\":\"om_abc123\"");
        handler.LastRequestBody.Should().Contain("\"text\":\"hello world\"");
    }

    [Fact]
    public async Task UpdateChannelRelayReplyAsync_ShouldMarkEditUnsupported_For501Responses()
    {
        var client = CreateClient("""{"error":true,"status":501,"body":"{\"code\":\"edit_unsupported\"}","message":"edit unsupported"}""");

        var result = await client.UpdateChannelRelayTextReplyAsync("token", "om_abc", "hi", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.EditUnsupported.Should().BeTrue();
        result.Detail.Should().Contain("nyx_status=501");
    }

    [Fact]
    public async Task UpdateChannelRelayReplyAsync_ShouldNotMarkEditUnsupported_ForGenericErrors()
    {
        var client = CreateClient("""{"error":true,"status":500,"body":"boom","message":"internal"}""");

        var result = await client.UpdateChannelRelayTextReplyAsync("token", "om_abc", "hi", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.EditUnsupported.Should().BeFalse();
        result.Detail.Should().Contain("nyx_status=500");
    }

    [Fact]
    public async Task UpdateChannelRelayTextReplyAsync_ShouldRejectEmptyText()
    {
        var client = CreateClient("""{"upstream_message_id":"om_abc"}""");

        var result = await client.UpdateChannelRelayTextReplyAsync("token", "om_abc", "   ", CancellationToken.None);

        result.Should().BeEquivalentTo(new NyxIdChannelRelayReplyResult(false, Detail: "missing_reply_text"));
    }

    [Fact]
    public void TryParseErrorEnvelope_ShouldHandleEmptyInvalidAndStructuredResponses()
    {
        InvokeTryParseErrorEnvelope(string.Empty).Should().Be((true, "empty_response"));
        InvokeTryParseErrorEnvelope("""{"ok":true}""").Should().Be((false, string.Empty));
        InvokeTryParseErrorEnvelope("""{"error":true,"status":503,"body":"offline","message":"down"}""")
            .Should()
            .Be((true, "nyx_status=503 body=offline message=down"));

        var invalid = InvokeTryParseErrorEnvelope("not-json");
        invalid.Matched.Should().BeTrue();
        invalid.Detail.Should().StartWith("invalid_error_envelope response_length=");
    }

    private static NyxIdApiClient CreateClient(string responseBody)
    {
        var handler = new CaptureHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        return new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler),
            NullLogger<NyxIdApiClient>.Instance);
    }

    private static (bool Matched, string Detail) InvokeTryParseErrorEnvelope(string response)
    {
        var method = typeof(NyxIdApiClient).GetMethod(
            "TryParseErrorEnvelope",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = [response, null];
        var matched = (bool)method.Invoke(null, args)!;
        return (matched, (string)args[1]!);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CaptureHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
