using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerStreamingReplySinkTests
{
    private const string OkSendResponse = """{"code":0,"msg":"success","data":{"message_id":"om_initial"}}""";
    private const string OkEditResponse = """{"code":0,"msg":"success","data":{}}""";

    [Fact]
    public async Task FirstDelta_SendsLarkPost_CapturingMessageIdFromResponse()
    {
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler);

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
    public async Task SecondDelta_EditsCapturedMessageIdWithPut()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse);
        var sink = CreateSink(handler);

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
    public async Task ActorApprovedSnapshots_SendEachRequestedEdit()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse, OkEditResponse);
        var sink = CreateSink(handler);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
        await sink.OnDeltaAsync("first chunk plus", CancellationToken.None);
        await sink.OnDeltaAsync("first chunk plus more", CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
        handler.Requests[2].Method.Should().Be(HttpMethod.Put);
        using var body = JsonDocument.Parse(handler.Bodies[2]!);
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        content.RootElement.GetProperty("text").GetString().Should().Be("first chunk plus more");
    }

    [Fact]
    public async Task FinalizeAsync_SendsActorApprovedFinalEdit()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse);
        var sink = CreateSink(handler);

        await sink.OnDeltaAsync("first chunk", CancellationToken.None);
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
        var sink = CreateSink(handler);

        await sink.FinalizeAsync("Summary report — no measurable activity in the last 24h.", CancellationToken.None);

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
            primary: new LarkReceiveTarget("oc_dm_chat_1", "chat_id", FellBackToPrefixInference: false),
            fallback: new LarkReceiveTarget("on_user_1", "union_id", FellBackToPrefixInference: false));

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
            primary: new LarkReceiveTarget("oc_dm_chat_1", "chat_id", FellBackToPrefixInference: false),
            fallback: new LarkReceiveTarget("on_user_1", "union_id", FellBackToPrefixInference: false));

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
            primary: new LarkReceiveTarget("on_user_1", "union_id", FellBackToPrefixInference: false),
            fallback: null);

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
        // summary, so we throw and HandleTriggerAsync persists Failed.
        var handler = new SequencedHandler(
            OkSendResponse,
            """{"code":230002,"msg":"Bot is not in the chat"}""");
        var sink = CreateSink(handler);

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
        var sink = CreateSink(handler);

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
    public async Task MidStreamEditCapReached_SealsMessage_FinalizePostsCompleteTextAsFreshMessage()
    {
        // Lark caps the total number of edits per message (lark_code=230072). The old code kept
        // PUT-editing past the cap (a 155-edit storm in prod 2026-06-15) and the final edit threw,
        // failing the run → MaxRetryAttempts re-fire → a brand-new digest message each loop = spam.
        // New behaviour: seal on 230072, suppress further mid-stream edits, and deliver the
        // complete text as ONE fresh POST at finalize so the run COMPLETES (no throw → no retry).
        var editCapReached =
            """{"code":230072,"msg":"The message has reached the number of times it can be edited."}""";
        var handler = new SequencedHandler(
            OkSendResponse,                                                          // initial POST -> om_initial
            editCapReached,                                                          // first edit -> 230072 (cap)
            """{"code":0,"msg":"success","data":{"message_id":"om_final"}}""");      // finalize -> fresh POST

        var sink = CreateSink(handler);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);                // POST om_initial
        await sink.OnDeltaAsync("chunk one two", CancellationToken.None);           // PUT -> 230072 -> seal
        await sink.OnDeltaAsync("chunk one two three", CancellationToken.None);     // sealed -> no request
        await sink.FinalizeAsync("chunk one two three FINAL", CancellationToken.None); // fresh POST

        handler.Requests.Should().HaveCount(3, "sealed mid-stream snapshot sends nothing; finalize posts a fresh message");
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        handler.Requests[2].RequestUri!.AbsolutePath
            .Should().Be("/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages", "the fresh delivery is a new message, not an edit");
        using var body = JsonDocument.Parse(handler.Bodies[2]!);
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        content.RootElement.GetProperty("text").GetString().Should().Be("chunk one two three FINAL");
    }

    [Fact]
    public async Task FinalEdit_EditCapReached_PostsCompleteTextAsFreshMessage_DoesNotThrow()
    {
        // Edit-cap (230072) at finalize must NOT throw (throwing failed the run → re-fire → spam).
        // Distinct from FinalEdit_LarkRejection_ThrowsRejectionMessage: other final-edit codes
        // (e.g. 230002 bot-not-in-chat) still throw — only the terminal edit-count cap falls back
        // to a fresh POST so the user still gets the complete report.
        var editCapReached =
            """{"code":230072,"msg":"The message has reached the number of times it can be edited."}""";
        var handler = new SequencedHandler(
            OkSendResponse,                                                          // initial POST
            editCapReached,                                                          // FINAL edit -> 230072
            """{"code":0,"msg":"success","data":{"message_id":"om_final"}}""");      // fallback fresh POST

        var sink = CreateSink(handler);

        await sink.OnDeltaAsync("chunk one", CancellationToken.None);
        await sink.FinalizeAsync("chunk one final", CancellationToken.None);         // edit 230072 -> fresh POST

        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        using var body = JsonDocument.Parse(handler.Bodies[2]!);
        var contentString = body.RootElement.GetProperty("content").GetString();
        using var content = JsonDocument.Parse(contentString!);
        content.RootElement.GetProperty("text").GetString().Should().Be("chunk one final");
    }

    [Fact]
    public async Task TruncatesPayloadAtLarkBodyLimit_WithMarker()
    {
        var handler = new SequencedHandler(OkSendResponse);
        var sink = CreateSink(handler);

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
    public async Task DuplicateActorApprovedText_SendsRequestedEdit()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse, OkEditResponse);
        var sink = CreateSink(handler);

        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);
        await sink.OnDeltaAsync("hello", CancellationToken.None);

        handler.Requests.Should().HaveCount(3, "the actor-owned run state, not this transport sink, suppresses duplicate snapshots");
    }

    [Fact]
    public async Task FinalizeAsync_TextMatchesLastSent_SendsActorApprovedFinalEdit()
    {
        var handler = new SequencedHandler(OkSendResponse, OkEditResponse);
        var sink = CreateSink(handler);

        await sink.OnDeltaAsync("complete final text", CancellationToken.None);
        await sink.FinalizeAsync("complete final text", CancellationToken.None);

        handler.Requests.Should().HaveCount(2, "final duplicate suppression belongs to SkillRunnerGAgent's actor-owned run state");
    }

    [Fact]
    public void Source_ShouldNotContainSinkOwnedTimerOrPendingDispatchState()
    {
        // Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
        //   Old pattern: sink owned timer callbacks, pending output, and callback-timed Lark dispatch loops.
        //   New principle: SkillRunnerGAgent owns coalescing and calls the sink only with approved snapshots.
        var source = File.ReadAllText(GetProductionSourcePath());

        source.Should().NotContain("_flushTimer");
        source.Should().NotContain("CreateTimer");
        source.Should().NotContain("_pendingText");
        source.Should().NotContain("_dispatchInProgress");
        source.Should().NotContain(string.Concat("Task", ".Run"));
        source.Should().NotContain("_ = DispatchAsync");
        source.Should().NotContain("_ = DispatchLoopAsync");
    }

    private static SkillRunnerStreamingReplySink CreateSink(HttpMessageHandler handler) =>
        CreateSink(
            handler,
            primary: new LarkReceiveTarget("oc_chat_1", "chat_id", FellBackToPrefixInference: false),
            fallback: null);

    private static SkillRunnerStreamingReplySink CreateSink(
        HttpMessageHandler handler,
        LarkReceiveTarget primary,
        LarkReceiveTarget? fallback)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        return new SkillRunnerStreamingReplySink(
            new LarkOutboundDispatcher(client, NullLogger<LarkOutboundDispatcher>.Instance),
            new LarkSendNewMessageRequest(
                "nyx-api-key",
                "api-lark-bot",
                MessageType: "text",
                ContentJson: string.Empty,
                PrimaryTarget: primary,
                FallbackTarget: fallback),
            BuildRejectionMessage,
            NullLogger<SkillRunnerStreamingReplySink>.Instance,
            client);
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
                : (HttpStatusCode.OK, """{"code":0,"msg":"success","data":{"message_id":"om_success"}}""");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string GetProductionSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "agents",
                "Aevatar.GAgents.Scheduled",
                "SkillRunnerStreamingReplySink.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate SkillRunnerStreamingReplySink.cs from test output directory.");
    }
}
