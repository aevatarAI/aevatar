using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Telegram;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayAppendOutboundPortTests
{
    private const string AgentKey = "nyxid_agent_secret_only_at_outbound";

    [Theory]
    [InlineData("telegram", 4096, true)]
    [InlineData("telegram", 4096, false)]
    [InlineData("unknown-chat", 2000, true)]
    [InlineData("unknown-chat", 2000, false)]
    public async Task SoftTargetExhaustion_InitialAndTerminalSegmentsReachRealOutboundUnchanged(
        string platform, int limit, bool whitespacePrefix)
    {
        var fixture = await CreateAsync(platform: platform);
        var text = whitespacePrefix ? new string(' ', 900) + "AB" : "A" + new string('\u0301', 800) + "B";
        var prefix = NyxRelayAppendSegmenter.Select(text, 0, limit, false, false,
            raw => fixture.Port.PrepareText(platform, fixture.Activity.Conversation, raw));
        prefix.Should().Be(text[..^1]);
        var first = await fixture.Port.SendAsync(fixture.Activity, prefix, limit,
            _ => Task.CompletedTask, CancellationToken.None);
        first.State.Should().Be(NyxRelayAppendSendState.Accepted);
        using var firstPayload = JsonDocument.Parse(fixture.Handler.Body!);
        var sentPrefix = firstPayload.RootElement.GetProperty("reply").GetProperty("text").GetString();
        var tail = NyxRelayAppendSegmenter.Select(text[prefix.Length..], 1, limit, true, false,
            raw => fixture.Port.PrepareText(platform, fixture.Activity.Conversation, raw));
        var last = await fixture.Port.SendAsync(fixture.Activity, tail, limit,
            _ => Task.CompletedTask, CancellationToken.None);
        last.State.Should().Be(NyxRelayAppendSendState.Accepted);
        using var lastPayload = JsonDocument.Parse(fixture.Handler.Body!);

        (sentPrefix + lastPayload.RootElement.GetProperty("reply").GetProperty("text").GetString()).Should().Be(text);
        fixture.Handler.Count.Should().Be(2);
    }

    [Theory]
    [InlineData("\n\n", 5000, "")]
    [InlineData("", 4096, " ")]
    [InlineData("\n\n", 5000, "\n ")]
    public async Task WhitespaceBoundaries_TerminalSegmentsStaySendableAndPreserveAllRawText(
        string leading, int bodyLength, string trailing)
    {
        var fixture = await CreateAsync();
        var text = leading + new string('a', bodyLength) + trailing;
        var accepted = string.Empty;
        var count = 0;
        while (accepted.Length < text.Length)
        {
            var segment = NyxRelayAppendSegmenter.Select(text[accepted.Length..], count, 4096, true, false,
                raw => fixture.Port.PrepareText("telegram", fixture.Activity.Conversation, raw));
            segment.Should().NotBeEmpty();
            var result = await fixture.Port.SendAsync(fixture.Activity, segment, 4096,
                _ => Task.CompletedTask, CancellationToken.None);
            result.State.Should().Be(NyxRelayAppendSendState.Accepted);
            accepted += segment;
            count++;
        }

        accepted.Should().Be(text);
        fixture.Handler.Count.Should().Be(count);
        count.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("hello ")]
    [InlineData("hello\n")]
    [InlineData("  answer  ")]
    public async Task WhitespaceBoundaries_ProgressCandidateLeavesSendableTerminalSuffix(string text)
    {
        var fixture = await CreateAsync();
        var prefix = NyxRelayAppendSegmenter.Select(text, 0, 4096, false, true,
            raw => fixture.Port.PrepareText("telegram", fixture.Activity.Conversation, raw));
        prefix.Should().NotBeEmpty();
        var first = await fixture.Port.SendAsync(fixture.Activity, prefix, 4096,
            _ => Task.CompletedTask, CancellationToken.None);
        first.State.Should().Be(NyxRelayAppendSendState.Accepted);
        var tail = NyxRelayAppendSegmenter.Select(text[prefix.Length..], 1, 4096, true, false,
            raw => fixture.Port.PrepareText("telegram", fixture.Activity.Conversation, raw));
        var last = await fixture.Port.SendAsync(fixture.Activity, tail, 4096,
            _ => Task.CompletedTask, CancellationToken.None);

        last.State.Should().Be(NyxRelayAppendSendState.Accepted);
        (prefix + tail).Should().Be(text);
        fixture.Handler.Count.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_ResolvesVaultKeyAndMarksDispatchImmediatelyBeforeSingleRequest()
    {
        var fixture = await CreateAsync();
        var trace = new List<string>();
        fixture.Handler.OnRequest = () => trace.Add("http");

        var result = await fixture.Port.SendAsync(fixture.Activity, " hello_* ", 4096,
            _ => { trace.Add("dispatch"); return Task.CompletedTask; }, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.Accepted);
        result.PlatformMessageId.Should().Be("platform-alpha");
        trace.Should().Equal("dispatch", "http");
        fixture.Handler.Count.Should().Be(1);
        fixture.Handler.Authorization.Should().Be($"Bearer {AgentKey}");
        using var payload = JsonDocument.Parse(fixture.Handler.Body!);
        payload.RootElement.GetProperty("message_id").GetString().Should().Be("inbound-alpha");
        payload.RootElement.GetProperty("reply").GetProperty("text").GetString().Should().Be(" hello\\_\\* ");
        result.ToString().Should().NotContain(AgentKey);
    }

    [Fact]
    public async Task PrepareText_PreservesWhitespaceUnicodeAndAllTextBeyondNativeComposerLimit()
    {
        var fixture = await CreateAsync();
        var raw = "  " + new string('*', 4097) + "e\u0301😀\n";

        var prepared = fixture.Port.PrepareText("telegram", fixture.Activity.Conversation, raw);

        prepared.Should().Be("  " + string.Concat(Enumerable.Repeat("\\*", 4097)) + "e\u0301😀\n");
        prepared.Length.Should().BeGreaterThan(4096);
        fixture.Port.PrepareText("unknown-chat", fixture.Activity.Conversation, raw).Should().Be(raw);
    }

    [Fact]
    public async Task PrepareText_FormatterExceptionDoesNotEscapeWithRawDetails()
    {
        var fixture = await CreateAsync(composer: new ThrowingFormatter());

        var act = () => fixture.Port.PrepareText("telegram", fixture.Activity.Conversation, "hello");

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Be("append_text_preparation_failed");
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_RejectsFormattedOverflowBeforeDispatchWithoutTruncation()
    {
        var fixture = await CreateAsync();
        var dispatched = false;

        var result = await fixture.Port.SendAsync(fixture.Activity, new string('*', 2049), 4096,
            _ => { dispatched = true; return Task.CompletedTask; }, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.PreDispatchFailure);
        result.ErrorCode.Should().Be("append_segment_too_long");
        dispatched.Should().BeFalse();
        fixture.Handler.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("wrong-fingerprint")]
    [InlineData("wrong-scope")]
    [InlineData("tombstoned")]
    [InlineData("ambiguous")]
    [InlineData("missing-anchor")]
    public async Task SendAsync_InvalidCredentialOrTargetFailsBeforeDispatch(string failure)
    {
        var fixture = await CreateAsync();
        switch (failure)
        {
            case "missing-key": fixture.Registration.ChannelAgentKey = null; break;
            case "wrong-fingerprint": fixture.Registration.ChannelAgentKey.SecretReference.Fingerprint = "different"; break;
            case "wrong-scope": fixture.Activity.TransportExtras.NyxRegistrationScopeId = "scope-other"; break;
            case "tombstoned": fixture.Registration.Tombstoned = true; break;
            case "ambiguous":
                fixture.ByIdentity.ListByNyxAgentApiKeyIdAsync("key-alpha", Arg.Any<CancellationToken>())
                    .Returns(new[] { fixture.Registration, fixture.Registration.Clone() });
                break;
            case "missing-anchor": fixture.Activity.OutboundDelivery.ReplyMessageId = ""; break;
        }
        var dispatched = false;

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096,
            _ => { dispatched = true; return Task.CompletedTask; }, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.PreDispatchFailure);
        dispatched.Should().BeFalse();
        fixture.Handler.Count.Should().Be(0);
        result.ToString().Should().NotContain(AgentKey);
        result.ToString().Should().NotContain("Bearer");
    }

    [Theory]
    [InlineData(401, NyxRelayAppendSendState.Rejected)]
    [InlineData(403, NyxRelayAppendSendState.Rejected)]
    [InlineData(422, NyxRelayAppendSendState.DeliveryUnknown)]
    [InlineData(429, NyxRelayAppendSendState.DeliveryUnknown)]
    [InlineData(500, NyxRelayAppendSendState.DeliveryUnknown)]
    [InlineData(502, NyxRelayAppendSendState.DeliveryUnknown)]
    [InlineData(408, NyxRelayAppendSendState.DeliveryUnknown)]
    public async Task SendAsync_SeparatesExplicitRefusalFromUnknownWithoutEchoingResponse(int status, NyxRelayAppendSendState expected)
    {
        var fixture = await CreateAsync((HttpStatusCode)status, $"{{\"error\":\"{AgentKey} Bearer private body\"}}");
        var dispatches = 0;

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096,
            _ => { dispatches++; return Task.CompletedTask; }, CancellationToken.None);

        result.State.Should().Be(expected);
        result.HttpStatus.Should().Be(status);
        dispatches.Should().Be(1);
        fixture.Handler.Count.Should().Be(1);
        result.ToString().Should().NotContain(AgentKey).And.NotContain("private body");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"message_id\":\"\"}")]
    [InlineData("{\"error\":true,\"message_id\":\"reply-alpha\"}")]
    public async Task SendAsync_InvalidSuccessResponseIsUnknown(string body)
    {
        var fixture = await CreateAsync(HttpStatusCode.OK, body);

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096, _ => Task.CompletedTask, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.DeliveryUnknown);
        fixture.Handler.Count.Should().Be(1);
    }

    [Theory]
    [InlineData("{\"message_id\":\"reply-alpha\"}")]
    [InlineData("{\"message_id\":\"reply-alpha\",\"platform_message_id\":null}")]
    [InlineData("{\"message_id\":\"reply-alpha\",\"platform_message_id\":\"\"}")]
    public async Task SendAsync_AcceptsDurableReplyIdWithoutOptionalPlatformId(string body)
    {
        var fixture = await CreateAsync(HttpStatusCode.OK, body);

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096, _ => Task.CompletedTask, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.Accepted);
        result.PlatformMessageId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("upstream_message_id")]
    [InlineData("platform_message_id")]
    public async Task SendAsync_AcceptsKnownNumericPlatformMessageId(string field)
    {
        var fixture = await CreateAsync(HttpStatusCode.OK, $"{{\"message_id\":\"reply-alpha\",\"{field}\":12345}}");

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096, _ => Task.CompletedTask, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.Accepted);
        result.PlatformMessageId.Should().Be("12345");
    }

    [Fact]
    public async Task SendAsync_DnsFailureDoesNotUseConfiguredPublicTransportFallback()
    {
        var fixture = await CreateAsync();
        fixture.Handler.Exception = new HttpRequestException(HttpRequestError.NameResolutionError, AgentKey);

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096, _ => Task.CompletedTask, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.DeliveryUnknown);
        fixture.Handler.Count.Should().Be(1);
        fixture.Handler.Host.Should().Be("internal.nyx.example");
        result.ToString().Should().NotContain(AgentKey);
    }

    [Fact]
    public async Task SendAsync_CancellationAfterDispatchIsUnknownWithoutRetry()
    {
        var fixture = await CreateAsync();
        fixture.Handler.Exception = new OperationCanceledException(AgentKey);

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096, _ => Task.CompletedTask, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.DeliveryUnknown);
        fixture.Handler.Count.Should().Be(1);
        result.ToString().Should().NotContain(AgentKey);
    }

    [Fact]
    public async Task SendAsync_CancelledBeforeDispatchDoesNotLockOrSend()
    {
        var fixture = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var dispatched = false;

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096,
            _ => { dispatched = true; return Task.CompletedTask; }, cancellation.Token);

        result.State.Should().Be(NyxRelayAppendSendState.PreDispatchFailure);
        dispatched.Should().BeFalse();
        fixture.Handler.Count.Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_RejectsCredentialEchoInSuccessIdentifiers()
    {
        var fixture = await CreateAsync(HttpStatusCode.OK,
            $"{{\"message_id\":\"reply-alpha\",\"platform_message_id\":\"{AgentKey}\"}}");

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096, _ => Task.CompletedTask, CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.DeliveryUnknown);
        result.ToString().Should().NotContain(AgentKey);
    }

    [Fact]
    public async Task SendAsync_FailedDispatchCommitDoesNotSendAndIsConservativelyUnknown()
    {
        var fixture = await CreateAsync();

        var result = await fixture.Port.SendAsync(fixture.Activity, "hello", 4096,
            _ => Task.FromException(new InvalidOperationException(AgentKey)), CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.DeliveryUnknown);
        fixture.Handler.Count.Should().Be(0);
        result.ToString().Should().NotContain(AgentKey);
    }

    [Theory]
    [InlineData("DispatchFence", 0, 0, "append_dispatch_fence_failed")]
    [InlineData("HttpSend", 1, 0, "append_outcome_unknown")]
    [InlineData("ResponseBody", 1, 200, "append_outcome_unknown")]
    public async Task SendAsync_FailureDiagnosticsIdentifyStageWithoutExposingSensitiveData(
        string stage, int expectedSends, int expectedStatus, string expectedCode)
    {
        const string privateReply = "private append reply";
        const string privateBody = "private response body";
        var logger = new RecordingAppendLogger();
        var fixture = await CreateAsync(body: $"{privateBody} {AgentKey}", logger: logger);
        var sensitiveError = new InvalidOperationException($"{AgentKey} {privateReply} {privateBody}");
        if (stage == "HttpSend")
            fixture.Handler.Exception = new HttpRequestException("private transport failure", sensitiveError);

        var result = await fixture.Port.SendAsync(fixture.Activity, privateReply, 4096,
            _ => stage == "DispatchFence"
                ? Task.FromException(new IOException("private commit failure", sensitiveError))
                : Task.CompletedTask,
            CancellationToken.None);

        result.State.Should().Be(NyxRelayAppendSendState.DeliveryUnknown);
        result.ErrorCode.Should().Be(expectedCode);
        result.HttpStatus.Should().Be(expectedStatus);
        fixture.Handler.Count.Should().Be(expectedSends);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain($"stage={stage}").And.Contain($"httpStatus={expectedStatus}");
        if (stage != "ResponseBody")
            entry.Message.Should().Contain("causeType=InvalidOperationException");
        entry.Exception.Should().BeNull("raw exceptions may contain credentials and reply text");
        (entry.Message + result).Should().NotContain(AgentKey).And.NotContain(privateReply)
            .And.NotContain(privateBody).And.NotContain("private transport failure").And.NotContain("private commit failure");
    }

    [Fact]
    public async Task Renderer_DeadlineCancelsStalledResponseBodyAndPublishesOneUnknownCompletion()
    {
        var fixture = await CreateAsync();
        var time = new FakeTimeProvider();
        var stalledBody = new CancellationBoundReadStream();
        fixture.Handler.ResponseContent = new StreamContent(stalledBody);
        var context = CreateRendererContext(fixture.Activity);
        var renderer = new NyxRelayAppendReplyStreamRenderer(fixture.Port, time, TimeSpan.FromSeconds(10));
        using var callerCancellation = new CancellationTokenSource();
        var execution = renderer.ExecuteAsync(context.Actor, context.Step, callerCancellation.Token);

        try
        {
            var bodyToken = await stalledBody.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Handler.Count.Should().Be(1);
            time.Advance(TimeSpan.FromSeconds(9));
            execution.IsCompleted.Should().BeFalse();
            bodyToken.IsCancellationRequested.Should().BeFalse();
            context.Completions.Should().BeEmpty();

            time.Advance(TimeSpan.FromSeconds(1));
            await execution.WaitAsync(TimeSpan.FromSeconds(5));

            bodyToken.IsCancellationRequested.Should().BeTrue();
            stalledBody.ReadCancelled.Task.IsCompletedSuccessfully.Should().BeTrue();
            stalledBody.Disposed.Should().BeTrue();
            callerCancellation.IsCancellationRequested.Should().BeFalse();
            var completion = context.Completions.Should().ContainSingle().Subject;
            completion.Token.Should().Be(callerCancellation.Token);
            completion.Event.RequestDispatched.Should().BeTrue();
            completion.Event.State.Should().Be(NyxRelayTextOperationResultState.Faulted);
            completion.Event.Kind.Should().Be(NyxRelayAppendMessageKind.Content);
            completion.Event.SegmentIndex.Should().Be(2);
            completion.Event.OperationGeneration.Should().Be(17);
            completion.Event.RawResult.HttpStatus.Should().Be(200);
            completion.Event.RawResult.RawErrorCode.Should().NotBeNullOrWhiteSpace();
            completion.Event.RawResult.ExceptionMessage.Should().BeEmpty();
            completion.Event.RawResult.RawErrorSummary.Should().BeEmpty();
            completion.Event.ToString().Should().NotContain(AgentKey);
            context.DispatchCount.Should().Be(1);
            time.Advance(TimeSpan.FromMinutes(1));
            fixture.Handler.Count.Should().Be(1);
            context.Completions.Should().ContainSingle();
        }
        finally
        {
            // Release the real pending read even when a regression leaves it without a deadline.
            await ReleaseExecutionAsync(execution, callerCancellation);
        }
    }

    [Fact]
    public async Task Renderer_DeadlineCancelsStalledRegistrationBeforeDispatch()
    {
        var fixture = await CreateAsync();
        var time = new FakeTimeProvider();
        var lookupStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ByIdentity.ListByNyxAgentApiKeyIdAsync("key-alpha", Arg.Any<CancellationToken>())
            .Returns(call => WaitForCancellationAsync<IReadOnlyList<ChannelBotRegistrationEntry>>(
                call.ArgAt<CancellationToken>(1), lookupStarted, lookupCancelled));
        var context = CreateRendererContext(fixture.Activity);
        var renderer = new NyxRelayAppendReplyStreamRenderer(fixture.Port, time, TimeSpan.FromSeconds(10));
        using var callerCancellation = new CancellationTokenSource();
        var execution = renderer.ExecuteAsync(context.Actor, context.Step, callerCancellation.Token);

        try
        {
            var lookupToken = await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(9));
            execution.IsCompleted.Should().BeFalse();
            lookupToken.IsCancellationRequested.Should().BeFalse();
            fixture.Handler.Count.Should().Be(0);

            time.Advance(TimeSpan.FromSeconds(1));
            await execution.WaitAsync(TimeSpan.FromSeconds(5));

            lookupCancelled.Task.IsCompletedSuccessfully.Should().BeTrue();
            callerCancellation.IsCancellationRequested.Should().BeFalse();
            var completion = context.Completions.Should().ContainSingle().Subject;
            completion.Token.Should().Be(callerCancellation.Token);
            completion.Event.RequestDispatched.Should().BeFalse();
            completion.Event.State.Should().Be(NyxRelayTextOperationResultState.Failed);
            completion.Event.ToString().Should().NotContain(AgentKey);
            context.DispatchCount.Should().Be(0);
            fixture.Handler.Count.Should().Be(0);
            time.Advance(TimeSpan.FromMinutes(1));
            context.Completions.Should().ContainSingle();
        }
        finally
        {
            await ReleaseExecutionAsync(execution, callerCancellation);
        }
    }

    private static RendererContext CreateRendererContext(ChatActivity activity) => new(activity);

    private static async Task ReleaseExecutionAsync(Task execution, CancellationTokenSource callerCancellation)
    {
        callerCancellation.Cancel();
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            // A regressed renderer can still be awaiting IO until the test releases it.
        }
    }

    private static async Task<T> WaitForCancellationAsync<T>(CancellationToken ct,
        TaskCompletionSource<CancellationToken> started, TaskCompletionSource cancelled)
    {
        var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() =>
        {
            cancelled.TrySetResult();
            pending.TrySetCanceled(ct);
        });
        started.TrySetResult(ct);
        return await pending.Task;
    }

    private static async Task<Fixture> CreateAsync(HttpStatusCode status = HttpStatusCode.OK,
        string body = "{\"message_id\":\"reply-alpha\",\"platform_message_id\":\"platform-alpha\"}",
        IMessageComposer? composer = null, string platform = "telegram", ILogger<NyxIdApiClient>? logger = null)
    {
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-alpha", "key-alpha", AgentKey, "append-outbound-test"));
        var registration = new ChannelBotRegistrationEntry
        {
            Id = "registration-alpha", ScopeId = "scope-alpha", Platform = platform, NyxAgentApiKeyId = "key-alpha",
            ChannelAgentKey = new ChannelAgentKeyCredential { ApiKeyId = "key-alpha", SecretReference = stored.Reference },
        };
        var byIdentity = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        byIdentity.ListByNyxAgentApiKeyIdAsync("key-alpha", Arg.Any<CancellationToken>()).Returns(new[] { registration });
        var query = Substitute.For<IChannelBotRegistrationRuntimeQueryPort>();
        query.GetAsync("registration-alpha", Arg.Any<CancellationToken>()).Returns(registration);
        var handler = new RecordingHandler(status, body);
        var client = new NyxIdApiClient(new NyxIdToolOptions
        {
            InternalApiBaseUrl = "https://internal.nyx.example", ApiBaseUrl = "https://internal.nyx.example",
            PublicTransportFallbackBaseUrl = "https://public.nyx.example",
        }, new HttpClient(handler), new NyxIdApiClientTransportPolicy(), logger ?? NullLogger<NyxIdApiClient>.Instance);
        var port = new NyxRelayAppendOutboundPort(client, query, byIdentity, vault, [composer ?? new TelegramMessageComposer()]);
        var activity = new ChatActivity
        {
            Bot = BotInstanceId.From("key-alpha"), ChannelId = ChannelId.From(platform),
            Conversation = ConversationReference.Create(ChannelId.From(platform), BotInstanceId.From("key-alpha"),
                ConversationScope.DirectMessage, "conversation-alpha", "dm", "sender-alpha"),
            OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = "inbound-alpha", CorrelationId = "correlation-alpha" },
            TransportExtras = new TransportExtras { NyxPlatform = platform, NyxAgentApiKeyId = "key-alpha", NyxRegistrationScopeId = "scope-alpha" },
        };
        return new Fixture(port, handler, activity, registration, byIdentity);
    }

    private sealed record Fixture(NyxRelayAppendOutboundPort Port, RecordingHandler Handler, ChatActivity Activity,
        ChannelBotRegistrationEntry Registration, IChannelBotRegistrationQueryByNyxIdentityPort ByIdentity);

    private sealed class RendererContext(ChatActivity activity) : IReplyOperationActorContext, INyxRelayAppendOperationActorContext
    {
        public IReplyOperationActorContext Actor => this;
        public int DispatchCount { get; private set; }
        public List<(NyxRelayAppendOperationCompletedEvent Event, CancellationToken Token)> Completions { get; } = [];
        public ReplyOperationStepEvent Step { get; } = new()
        {
            CorrelationId = "correlation-alpha",
            NyxRelayAppend = new NyxRelayAppendOperationStepPayload
            {
                Kind = NyxRelayAppendMessageKind.Content, SegmentIndex = 2, OperationGeneration = 17,
            },
        };

        public Task<NyxRelayAppendExecution?> ClaimAppendOperationAsync(
            string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct) =>
            Task.FromResult<NyxRelayAppendExecution?>(new(activity, "hello", 4096));

        public Task MarkAppendDispatchedAsync(string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DispatchCount++;
            return Task.CompletedTask;
        }

        public Task DispatchReplyOperationCompletionAsync(IMessage evt, string correlationId, string operationName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Completions.Add(((NyxRelayAppendOperationCompletedEvent)evt, ct));
            return Task.CompletedTask;
        }

        public bool MatchesNyxRelayTextInFlight(string correlationId, NyxRelayTextOperationKind operation, long sequence, long generation) => false;
        public bool MatchesLarkCardInFlight(string correlationId, LarkCardOperationPhase operation, long sequence, long generation, string? cardId) => false;
        public ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(string? correlationId, ChatActivity? sourceActivity,
            string? replyToken, long replyTokenExpiresAtUnixMs) => ConversationTurnRuntimeContext.Empty;
        public void RestoreRuntimeTransportCredentials(ChatActivity? sourceActivity, ConversationTurnRuntimeContext runtimeContext) { }
    }

    private sealed class CancellationBoundReadStream : Stream
    {
        public TaskCompletionSource<CancellationToken> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            new(WaitForCancellationAsync<int>(ct, ReadStarted, ReadCancelled));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingFormatter : IMessageComposer, ILosslessPlainTextFormatter
    {
        public ChannelId Channel => ChannelId.From("telegram");
        public string PreparePlainText(string text, ConversationReference conversation) => throw new InvalidOperationException(AgentKey);
        public object Compose(MessageContent intent, ComposeContext context) => throw new NotSupportedException();
        public ComposeCapability Evaluate(MessageContent intent, ComposeContext context) => ComposeCapability.Exact;
    }

    private sealed class RecordingAppendLogger : ILogger<NyxIdApiClient>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? Host { get; private set; }
        public Action? OnRequest { get; set; }
        public Exception? Exception { get; set; }
        public HttpContent? ResponseContent { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            OnRequest?.Invoke();
            Host = request.RequestUri?.Host;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            if (Exception is not null)
                throw Exception;
            return new HttpResponseMessage(status)
            {
                Content = ResponseContent ?? new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
