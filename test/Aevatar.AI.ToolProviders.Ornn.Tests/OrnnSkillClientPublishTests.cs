using System.Net;
using System.Net.Http.Headers;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnSkillClientPublishTests
{
    [Fact]
    public async Task PublishSkillAsync_ShouldUploadZipThroughNyxIdProxy()
    {
        var zipBytes = new byte[] { 9, 8, 7 };
        var handler = new CapturingHandler("""{ "data": { "guid": "skill-1" } }""");
        var client = CreateClient(handler);

        var result = await client.PublishSkillAsync("caller-token", zipBytes);

        result.Succeeded.Should().BeTrue();
        result.RawResponse.Should().Contain("skill-1");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri!.AbsoluteUri.Should().Be("https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills");
        request.Uri.Query.Should().NotContain("skip_validation");
        request.Authorization!.Parameter.Should().Be("caller-token");
        request.ContentType.Should().Be("application/zip");
        request.Body.Should().Equal(zipBytes);
    }

    [Fact]
    public async Task PublishSkillAsync_ShouldSurfaceProxyError()
    {
        var handler = new CapturingHandler("""{ "error": "nope" }""", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.PublishSkillAsync("token", [1]);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
        result.Failure!.Kind.Should().Be(OrnnSkillMutationFailureKind.Rejected);
        result.Failure.Code.Should().Be("ornn_publish_http_error");
        result.Failure.Message.Should().Contain("status=500");
        result.Failure.HttpStatus.Should().Be(500);
        result.Failure.Outcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task PublishSkillAsync_ShouldReturnActionableTimeoutError()
    {
        var handler = new HangingHandler();
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(150));

        var result = await client.PublishSkillAsync("token", [1]);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
        result.Failure!.Kind.Should().Be(OrnnSkillMutationFailureKind.Timeout);
        result.Failure.Code.Should().Be("ornn_publish_timeout");
        result.Failure.Message.Should().Contain("budget");
        result.Failure.HttpStatus.Should().BeNull();
        result.Failure.Outcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task PublishSkillAsync_ShouldPropagateCallerCancellation()
    {
        var handler = new HangingHandler();
        var client = CreateClient(handler, TimeSpan.FromSeconds(10));
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await client.PublishSkillAsync("token", [1], callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PublishSkillAsync_WhenTransportFails_ShouldReturnUncertainTypedFailure()
    {
        var client = CreateClient(new ThrowingHandler());

        var result = await client.PublishSkillAsync("token", [1]);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
        result.Failure!.Kind.Should().Be(OrnnSkillMutationFailureKind.Transport);
        result.Failure.Code.Should().Be("ornn_publish_transport_error");
        result.Failure.Message.Should().Contain("transport failed");
        result.Failure.HttpStatus.Should().BeNull();
        result.Failure.Outcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
    }

    private static OrnnSkillClient CreateClient(
        HttpMessageHandler handler,
        TimeSpan? perCallTimeout = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        return new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn" },
            nyxClient,
            perCallTimeout ?? TimeSpan.FromSeconds(30));
    }

    private sealed class CapturingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content == null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken)));

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs))
                await tcs.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection failed");
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        AuthenticationHeaderValue? Authorization,
        string? ContentType,
        byte[] Body);
}
