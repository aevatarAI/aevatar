using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerStreamingReplySinkTests
{
    private const string OkSendResponse = """{"code":0,"msg":"success","data":{"message_id":"om_initial"}}""";
    private const string OkEditResponse = """{"code":0,"msg":"success","data":{}}""";

    [Fact]
    public async Task FirstDelta_SendsLarkPost_CapturingMessageIdFromResponse()
    {
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages");
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        sink.PlatformMessageId.Should().Be("om_initial");
        sink.ChunksEmitted.Should().Be(1);
    }

    [Fact]
    public async Task SecondDelta_PatchesCapturedMessageId()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
        await sink.OnDeltaAsync("first chunk and more", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[1].RequestUri!.AbsolutePath
            .Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages/om_initial");

        // Edit body shape: PUT for text/post requires both `msg_type` AND `content`. Lark
        // splits the edit-message verbs by msg_type — PUT for text/post, PATCH for cards —
        // so the wrong verb (or omitting msg_type) makes Lark reject every later edit and
        // streaming-edit silently stops growing past the placeholder.
        using var body = JsonDocument.Parse(handler.Bodies[1]!);
        body.RootElement.GetProperty("msg_type").GetString().Should().Be("text");
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        content.RootElement.GetProperty("text").GetString().Should().Be("first chunk and more");
    }

    [Fact]
    public async Task DeltasInsideThrottle_AreCollapsedToLatestEditWhenTimerFires()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse);
        var sink = CreateSink(handler, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await sink.OnDeltaAsync("first chunk plus", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await sink.OnDeltaAsync("first chunk plus more", CancellationToken.None);

        // Inside the throttle window: only the initial POST went out. Two later deltas are stashed.
        handler.Requests.Should().ContainSingle();

        time.Advance(TimeSpan.FromMilliseconds(800));

        // Crossing the throttle boundary fires the deferred timer; the LATEST stashed text edits
        // (collapse-on-latest), not every individual delta.
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        using var body = JsonDocument.Parse(handler.Bodies[1]!);
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        content.RootElement.GetProperty("text").GetString().Should().Be("first chunk plus more");
    }

    [Fact]
    public async Task FinalizeAsync_BypassesThrottleAndPatchesFinalText()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse);
        var sink = CreateSink(handler, throttleMs: 750, out var time);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await sink.FinalizeAsync("first chunk plus final", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        using var body = JsonDocument.Parse(handler.Bodies[1]!);
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        content.RootElement.GetProperty("text").GetString().Should().Be("first chunk plus final");
    }

    [Fact]
    public async Task FinalizeAsync_NoDeltasEverStreamed_FallsBackToSinglePost()
    {
        // Empty-day case where the LLM produced output but each chunk was empty: the foreach in
        // ExecuteSkillAsync skipped every iteration so the sink never saw OnDeltaAsync. Finalize
        // still has to deliver the run output so the user gets the report — the sink does the
        // first POST even though nothing streamed.
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.FinalizeAsync("Daily report — no measurable activity in the last 24h.", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        sink.PlatformMessageId.Should().Be("om_initial");
    }

    [Fact]
    public async Task InitialPost_RejectedAsBotNotInChat_ViaHttp400Envelope_RetriesOnceWithFallbackTarget()
    {
        // Production failures arrive through `NyxIdApiClient.SendAsync` as an HTTP-400 Nyx
        // envelope (`{"error": true, "status": 400, "body": "<raw json>"}`) — the same
        // wrapping shape pinned for the non-streaming path in
        // `SkillRunnerGAgentTests.SendOutputAsync_ShouldRetryWithFallback_When_PrimaryRejectedAsBotNotInChat_ViaHttp400Envelope`.
        // The streaming sink relies on the same `LarkProxyResponse.TryGetError` parser, but
        // pin the wrapped shape end-to-end here so a regression in either layer fails this
        // test loud (and not the more visible HTTP-200 plain-Lark-error test).
        // NyxIdApiClient.SendAsync wraps every non-2xx as `{"error":true,"status":N,"body":<raw>}`,
        // so the mock returns the RAW Lark JSON with HTTP 400 here — the wrapping happens in
        // the client, not the test handler.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{"code":230002,"msg":"Bot is not in the chat"}"""),
            (HttpStatusCode.OK, """{"code":0,"msg":"success","data":{"message_id":"om_fallback"}}"""));
        var sink = CreateSink(
            handler,
            throttleMs: 0,
            primary: new LarkReceiveTarget("oc_dm_chat_1", "chat_id", FellBackToPrefixInference: false),
            fallback: new LarkReceiveTarget("on_user_1", "union_id", FellBackToPrefixInference: false),
            out _);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        sink.PlatformMessageId.Should().Be("om_fallback");
    }

    [Fact]
    public async Task InitialPost_RejectedAsBotNotInChat_RetriesOnceWithFallbackTarget()
    {
        // Reviewer concern (codex-bot, P1, PR #412): chat_id-first regresses cross-app same-tenant
        // deployments where the outbound app is not in the inbound DM chat. The streaming-edit
        // path must preserve that recovery — same fallback retry the non-streaming
        // SendOutputAsync uses.
        var handler = new SequencedHandler(
            """{"code":230002,"msg":"Bot is not in the chat"}""",
            """{"code":0,"msg":"success","data":{"message_id":"om_fallback"}}""");
        var sink = CreateSink(
            handler,
            throttleMs: 0,
            primary: new LarkReceiveTarget("oc_dm_chat_1", "chat_id", FellBackToPrefixInference: false),
            fallback: new LarkReceiveTarget("on_user_1", "union_id", FellBackToPrefixInference: false),
            out _);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.Query.Should().Contain("receive_id_type=chat_id");
        handler.Requests[1].RequestUri!.Query.Should().Contain("receive_id_type=union_id");
        handler.Bodies[1].Should().Contain("\"receive_id\":\"on_user_1\"");
        sink.PlatformMessageId.Should().Be("om_fallback");
    }

    [Fact]
    public async Task InitialPost_RejectedWithDifferentLarkCode_DoesNotTriggerFallback()
    {
        // Only `230002 bot not in chat` triggers the fallback. Cross-tenant (99992364) etc. are
        // unrecoverable and propagate at finalize time so the user sees the actionable hint.
        // Queue the rejection twice — mid-stream OnDelta retries on every dispatch (transient
        // semantics), so finalize re-issues the POST and observes the same rejection.
        var handler = new SequencedHandler(
            """{"code":99992364,"msg":"user id cross tenant"}""",
            """{"code":99992364,"msg":"user id cross tenant"}""");
        var sink = CreateSink(
            handler,
            throttleMs: 0,
            primary: new LarkReceiveTarget("on_user_1", "union_id", FellBackToPrefixInference: false),
            fallback: null,
            out _);

        // Mid-stream rejection is swallowed (the run is still producing chunks). Only finalize
        // raises.
        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
        handler.Requests.Should().ContainSingle();

        Func<Task> finalize = () => sink.FinalizeAsync("first chunk and final", CancellationToken.None);

        var assertion = await finalize.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*99992364*");
        handler.Requests.Should().HaveCount(2, "primary-only target retries on finalize POST");
    }

    [Fact]
    public async Task FinalEdit_LarkRejection_ThrowsRejectionMessage()
    {
        // Mid-stream edit (PUT) errors are swallowed (transient: rate-limit, timeout). The
        // FINAL edit is the contract for the run — if it fails the user never sees the complete
        // daily, so we throw and HandleTriggerAsync persists Failed.
        var handler = new SequencedHandler(
            OkSendResponse,
            """{"code":230002,"msg":"Bot is not in the chat"}""");
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);

        Func<Task> finalize = () => sink.FinalizeAsync("first chunk and final", CancellationToken.None);

        var assertion = await finalize.Should().ThrowAsync<InvalidOperationException>();
        assertion.WithMessage("*230002*");
    }

    [Fact]
    public async Task MidStreamEditRejection_DoesNotThrow_NextDeltaRetries()
    {
        // Transient edit (PUT) failures (rate-limit, single-edit blip) must not abort the run.
        // The sink logs and continues; the next delta retries against the same message_id.
        var handler = new SequencedHandler(
            OkSendResponse,
            """{"code":230020,"msg":"transient rate limit"}""",
            OkEditResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
        await sink.OnDeltaAsync("first chunk plus", CancellationToken.None);
        await sink.OnDeltaAsync("first chunk plus more", CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[2].Method.Should().Be(HttpMethod.Put);
        // Final emitted text reflects the latest delta (rejection didn't lose the accumulator).
        sink.ChunksEmitted.Should().Be(2, "the rejected edit doesn't count, but the first POST and successful PUT do");
    }

    [Fact]
    public async Task TruncatesPayloadAtLarkBodyLimit_WithMarker()
    {
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        // Massively exceeds the 30K cap so we can verify the truncation marker survives JSON
        // round-trip without re-checking the exact tail bytes.
        var oversized = new string('A', SkillRunnerStreamingReplySink.MaxLarkTextLength + 5_000);
        await sink.OnDeltaAsync(oversized, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        var sent = content.RootElement.GetProperty("text").GetString()!;

        sent.Length.Should().Be(SkillRunnerStreamingReplySink.MaxLarkTextLength);
        sent.Should().EndWith("…[truncated]");
    }

    [Fact]
    public async Task DuplicateText_DoesNotEmitRedundantEdit()
    {
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task FinalizeAsync_TextMatchesLastEmitted_DoesNotEmitFinalEdit()
    {
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler, throttleMs: 0, out _);

        await sink.OnDeltaAsync("complete final text", CancellationToken.None);
        await sink.FinalizeAsync("complete final text", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
    }

    private static SkillRunnerStreamingReplySink CreateSink(
        HttpMessageHandler handler,
        int throttleMs,
        out FakeTimeProvider timeProvider) =>
        CreateSink(
            handler,
            throttleMs,
            primary: new LarkReceiveTarget("oc_chat_1", "chat_id", FellBackToPrefixInference: false),
            fallback: null,
            out timeProvider);

    private static SkillRunnerStreamingReplySink CreateSink(
        HttpMessageHandler handler,
        int throttleMs,
        LarkReceiveTarget primary,
        LarkReceiveTarget? fallback,
        out FakeTimeProvider timeProvider)
    {
        timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 28, 9, 0, 0, TimeSpan.Zero));
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        return new SkillRunnerStreamingReplySink(
            client,
            nyxApiKey: "nyx-api-key",
            nyxProviderSlug: "api-lark-bot",
            primaryTarget: primary,
            fallbackTarget: fallback,
            rejectionMessageBuilder: BuildRejectionMessage,
            throttle: TimeSpan.FromMilliseconds(throttleMs),
            timeProvider: timeProvider,
            logger: NullLogger<SkillRunnerStreamingReplySink>.Instance);
    }

    /// <summary>
    /// Tests do not need to mirror the production rejection-builder shape (that lives on
    /// <c>SkillRunnerGAgent.BuildLarkRejectionMessage</c> and is covered by <c>SkillRunnerGAgentTests</c>);
    /// the sink only needs the builder to produce a string containing the lark code so the
    /// finalize-time exception is identifiable.
    /// </summary>
    private static string BuildRejectionMessage(int? larkCode, string detail) =>
        larkCode is { } code
            ? $"Lark message delivery rejected (code={code}): {detail}"
            : $"Lark message delivery rejected: {detail}";

    /// <summary>
    /// Returns a different response per request in the order given; falls back to a generic
    /// 200/success body if the test runs more dispatches than queued responses (lets a test
    /// focus on the first N interactions without padding the queue). Supports two queueing
    /// shapes: a bare JSON string (always 200 OK — covers the Lark business-error-on-200
    /// path) and a <see cref="HttpStatusCode"/>-paired tuple (covers the
    /// <c>NyxIdApiClient.SendAsync</c> wrapping path where HTTP non-2xx becomes a
    /// <c>{"error":true,"status":N,"body":"&lt;raw json&gt;"}</c> envelope).
    /// </summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        public SequencedHandler(params string[] responses)
            : this(responses.Select(r => (HttpStatusCode.OK, r)).ToArray()) { }

        public SequencedHandler(params (HttpStatusCode Status, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, """{"code":0,"msg":"success"}""");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
