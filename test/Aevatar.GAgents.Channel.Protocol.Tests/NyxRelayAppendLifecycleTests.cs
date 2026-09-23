using System.Reflection;
using System.Text;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class NyxRelayAppendLifecycleTests
{
    [Theory]
    [InlineData("telegram", 501, true)]
    [InlineData("telegram", 401, false)]
    [InlineData("unknown-chat", 501, true)]
    [InlineData("unknown-chat", 401, false)]
    [InlineData("telegram", 4096, true)]
    [InlineData("telegram", 4096, false)]
    [InlineData("unknown-chat", 2000, true)]
    [InlineData("unknown-chat", 2000, false)]
    public async Task SoftTargetExhaustion_InitialChunkStillDeliversExactTerminalBody(
        string platform, int prefixLength, bool whitespacePrefix)
    {
        await using var fixture = await Fixture.CreateAsync(platform: platform);
        await fixture.AdmitAsync();
        var prefix = whitespacePrefix
            ? new string(' ', prefixLength - 1) + "A"
            : "A" + new string('\u0301', prefixLength - 1);
        var text = prefix + "B";

        await fixture.ChunkAsync(text);

        fixture.Append.TerminalReason.ShouldBe(NyxRelayAppendTerminalReason.Unspecified);
        fixture.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Content);
        fixture.Append.InFlightOperation.Text.ShouldBe(prefix);
        fixture.Append.InFlightOperation.Text.Length.ShouldBeLessThanOrEqualTo(fixture.Append.MaxSegmentLength);
        fixture.Append.AcceptedSegmentCount.ShouldBe(0);
        await fixture.ExecuteAndCompleteNextAsync();
        fixture.Lifecycle.LastFlushedText.ShouldBe(prefix);
        await fixture.ReadyAsync(text);
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Outbound.Attempts.Select(attempt => attempt.Text).ShouldBe(new[] { prefix, "B" });
        (await fixture.EventsAsync<LlmReplyDeliveryFailedEvent>()).ShouldBeEmpty();
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).Count.ShouldBe(1);
        (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single().Outbound.Text.ShouldBe(text);
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(text);
        fixture.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
        fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Fact]
    public async Task WhitespaceOnlyTerminalBody_CompletesWithoutCreditingUnsentText()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ProgressDueAsync();
        await fixture.ExecuteAndCompleteNextAsync();

        await fixture.ReadyAsync("\n\n ");

        fixture.Outbound.Attempts.Single().Text.ShouldBe("正在处理，请稍候...");
        fixture.Agent.State.RetainedHistory.ShouldNotContain(entry => entry.Role == "assistant");
        fixture.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
        fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        fixture.Publisher.Steps.ShouldBeEmpty();
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).ShouldBeEmpty();
        (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single().Outbound.Text.ShouldBeEmpty();
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\t \r\n")]
    public async Task WhitespaceOnlyPendingBody_StillSendsProgress_ThenPreservesWhitespaceWithAnswer(string whitespace)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(whitespace);
        await fixture.ProgressDueAsync();

        fixture.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Progress);
        await fixture.ExecuteAndCompleteNextAsync();
        fixture.Append.AcceptedSegmentCount.ShouldBe(0);
        fixture.Lifecycle.LastFlushedText.ShouldBeEmpty();
        var answer = whitespace + "A visible answer.";
        await fixture.ReadyAsync(answer);
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Outbound.Attempts.Select(attempt => attempt.Text).ShouldBe(new[] { "正在处理，请稍候...", answer });
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(answer);
        fixture.Runner.FallbackReplies.ShouldBe(0);
        fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(" A")]
    [InlineData("\n\n好")]
    [InlineData("A")]
    [InlineData("👩🏽‍💻")]
    [InlineData(" 👩🏽‍💻")]
    public async Task PendingSingleVisibleGrapheme_SendsOneProgress_ThenExactTerminalBody(string text)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(text);
        await fixture.ProgressDueAsync();
        await fixture.ProgressDueAsync();

        fixture.Append.TerminalReason.ShouldBe(NyxRelayAppendTerminalReason.Unspecified);
        fixture.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Progress);
        await fixture.ExecuteAndCompleteNextAsync();
        await fixture.ProgressDueAsync();
        fixture.Publisher.Steps.ShouldBeEmpty();
        fixture.Append.AcceptedSegmentCount.ShouldBe(0);
        fixture.Lifecycle.LastFlushedText.ShouldBeEmpty();
        fixture.Lifecycle.PendingAccumulatedText.ShouldBe(text);

        await fixture.ReadyAsync(text);
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Outbound.Attempts.Select(attempt => attempt.Text).ShouldBe(new[] { "正在处理，请稍候...", text });
        (await fixture.EventsAsync<LlmReplyDeliveryFailedEvent>()).ShouldBeEmpty();
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).Count.ShouldBe(1);
        (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single().Outbound.Text.ShouldBe(text);
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(text);
        fixture.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
        fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Fact]
    public async Task OversizedTerminalGrapheme_FailsWithoutCreditingUnsentBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ProgressDueAsync();
        await fixture.ExecuteAndCompleteNextAsync();

        await fixture.ReadyAsync("A" + new string('\u0301', 4096));

        fixture.Outbound.Attempts.Single().Text.ShouldBe("正在处理，请稍候...");
        fixture.Publisher.Steps.ShouldBeEmpty();
        (await fixture.EventsAsync<ConversationReplyLifecycleChangedEvent>()).ShouldContain(evt =>
            evt.HasAppendTerminalReason && evt.AppendTerminalReason == NyxRelayAppendTerminalReason.FormattingFailed);
        (await fixture.EventsAsync<LlmReplyDeliveryFailedEvent>()).Single().ErrorCode.ShouldBe("append_NotSent");
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).ShouldBeEmpty();
        (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single().Outbound.Text.ShouldBeEmpty();
        fixture.Agent.State.RetainedHistory.ShouldNotContain(entry => entry.Role == "assistant");
        fixture.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
        fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Theory]
    [InlineData("hello ")]
    [InlineData("hello\n")]
    public async Task WhitespaceTerminalTail_RemainsAttachedToUnsentVisibleBody(string text)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(text);
        await fixture.ProgressDueAsync();
        await fixture.ExecuteAndCompleteNextAsync();
        fixture.Lifecycle.LastFlushedText.ShouldNotBeEmpty();
        await fixture.ReadyAsync(text);
        await fixture.ExecuteAndCompleteNextAsync();

        string.Concat(fixture.Outbound.Attempts.Select(attempt => attempt.Text)).ShouldBe(text);
        fixture.Outbound.Attempts.ShouldNotContain(attempt => string.IsNullOrWhiteSpace(attempt.Text));
        (await fixture.EventsAsync<LlmReplyDeliveryFailedEvent>()).ShouldBeEmpty();
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).Count.ShouldBe(1);
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(text);
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Theory]
    [InlineData(NyxRelayAppendSendState.Accepted, false)]
    [InlineData(NyxRelayAppendSendState.DeliveryUnknown, false)]
    [InlineData(NyxRelayAppendSendState.Accepted, true)]
    [InlineData(NyxRelayAppendSendState.DeliveryUnknown, true)]
    public async Task FailedInitialHandoff_ProgressDoesNotSuppressFirstSuccessfulRun(
        NyxRelayAppendSendState progressResult, bool recover)
    {
        var store = new InMemoryEventStore();
        var secrets = new InMemoryRuntimeSecretStore();
        await using var first = await Fixture.CreateAsync(store, runtimeSecretStore: secrets);
        first.Dispatcher.FailuresRemaining = 1;
        first.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(progressResult));
        await first.AdmitRelayAsync();
        first.Dispatcher.AttemptCount.ShouldBe(1);
        first.Dispatcher.Requests.ShouldBeEmpty();
        first.Append.LlmRunDispatched.ShouldBeFalse();
        first.Agent.State.PendingLlmReplyRequests.Single().RelayReplyTokenRef.Ref.ShouldNotBeNullOrWhiteSpace();
        await first.ProgressDueAsync();
        await first.ExecuteAndCompleteNextAsync();
        first.Append.AnyRequestDispatched.ShouldBeTrue();
        first.Append.LlmRunDispatched.ShouldBeFalse();

        Fixture active = first;
        if (recover)
        {
            await first.DeactivateAsync();
            active = await Fixture.CreateAsync(store, runtimeSecretStore: secrets);
        }
        try
        {
            if (!recover)
                await active.Agent.HandleDeferredLlmReplyDispatchRequestedAsync(new DeferredLlmReplyDispatchRequestedEvent
                {
                    CorrelationId = Fixture.Correlation,
                    RequestedAtUnixMs = active.Clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds(),
                });

            active.Dispatcher.Requests.Count.ShouldBe(1);
            active.Dispatcher.AttemptCount.ShouldBe(recover ? 1 : 2);
            active.Dispatcher.Requests.Single().ReplyToken.ShouldBe(Fixture.ReplyToken);
            active.Append.LlmRunDispatched.ShouldBeTrue();
            await active.ProgressDueAsync();
            active.Publisher.Steps.ShouldBeEmpty();
            await active.ReadyAsync("Answer from the first successful run.");
            await active.ExecuteAndCompleteNextAsync();

            active.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
            active.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
            active.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content
                .ShouldBe("Answer from the first successful run.");
            active.Runner.FallbackReplies.ShouldBe(0);
            first.Outbound.Attempts.Count(attempt => attempt.Text == "正在处理，请稍候...").ShouldBe(1);
            if (recover)
                active.Outbound.Attempts.ShouldNotContain(attempt => attempt.Text == "正在处理，请稍候...");
            await active.AssertNoPersistedReplyTokenAsync();
        }
        finally
        {
            if (recover)
                await active.DisposeAsync();
        }
    }

    [Fact]
    public async Task StalledProgressSend_IsCanceledAtDeadline_ReleasingSerialMailboxToDeliverQueuedBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Outbound.StallProgressUntilCanceled = true;
        await fixture.AdmitAsync();
        await fixture.ProgressDueAsync();
        var progressStep = fixture.Publisher.Steps.Dequeue();
        var mailbox = global::System.Threading.Channels.Channel.CreateUnbounded<IMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        fixture.Publisher.InboxSink = message => mailbox.Writer.TryWrite(message);
        fixture.Dispatch.InboxSink = completion => mailbox.Writer.TryWrite(completion);
        const string answer = "The body queued behind the stalled progress request.";
        mailbox.Writer.TryWrite(progressStep).ShouldBeTrue();
        mailbox.Writer.TryWrite(new LlmReplyStreamChunkEvent
        {
            CorrelationId = Fixture.Correlation,
            Activity = fixture.Activity.Clone(),
            AccumulatedText = answer,
        }).ShouldBeTrue();
        mailbox.Writer.TryWrite(fixture.CreateReady(answer)).ShouldBeTrue();
        var startedTurns = new List<string>();
        NyxRelayAppendOperationCompletedEvent? progressCompletion = null;

        async Task ProcessMailboxAsync()
        {
            await foreach (var message in mailbox.Reader.ReadAllAsync())
            {
                startedTurns.Add(message.Descriptor.Name);
                switch (message)
                {
                    case ReplyOperationStepEvent step:
                        await fixture.Agent.HandleReplyOperationStepAsync(step);
                        break;
                    case LlmReplyStreamChunkEvent chunk:
                        await fixture.Agent.HandleLlmReplyStreamChunkAsync(chunk);
                        break;
                    case LlmReplyReadyEvent ready:
                        await fixture.Agent.HandleLlmReplyReadyAsync(ready);
                        break;
                    case NyxRelayAppendProgressDueEvent due:
                        await fixture.Agent.HandleNyxRelayAppendProgressDueAsync(due);
                        break;
                    case NyxRelayAppendOperationTimeoutFiredEvent timeout:
                        await fixture.Agent.HandleNyxRelayAppendOperationTimeoutFiredAsync(timeout);
                        break;
                    case NyxRelayAppendOperationCompletedEvent completed:
                        if (completed.Kind == NyxRelayAppendMessageKind.Progress)
                            progressCompletion = completed.Clone();
                        await fixture.Agent.HandleNyxRelayAppendOperationCompletedAsync(completed);
                        if (completed.Kind == NyxRelayAppendMessageKind.Content)
                            mailbox.Writer.TryComplete();
                        break;
                }
            }
        }

        var serialDelivery = ProcessMailboxAsync();
        try
        {
            await fixture.Outbound.ProgressSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            startedTurns.ShouldBe(new[] { nameof(ReplyOperationStepEvent) });
            fixture.Lifecycle.PendingFinalizeCommandId.ShouldBeEmpty();
            fixture.Outbound.Attempts.Count.ShouldBe(1);

            fixture.Clock.Advance(TimeSpan.FromSeconds(9));
            fixture.Outbound.ProgressSendCanceled.Task.IsCompleted.ShouldBeFalse();
            startedTurns.ShouldBe(new[] { nameof(ReplyOperationStepEvent) });
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await serialDelivery.WaitAsync(TimeSpan.FromSeconds(5));

            (await fixture.Outbound.ProgressSendCanceled.Task).ShouldBeTrue();
            progressCompletion.ShouldNotBeNull();
            progressCompletion.RequestDispatched.ShouldBeTrue();
            progressCompletion.State.ShouldBe(NyxRelayTextOperationResultState.Faulted);
            fixture.Outbound.Attempts.Select(attempt => attempt.Text).ShouldBe(new[] { "正在处理，请稍候...", answer });
            fixture.Outbound.DispatchedCount.ShouldBe(2);
            fixture.Runner.FallbackReplies.ShouldBe(0);
            fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
            fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(answer);
            var transitions = await fixture.EventsAsync<ConversationReplyLifecycleChangedEvent>();
            transitions.ShouldContain(evt => evt.HasAppendProgressState && evt.AppendProgressState == NyxRelayAppendProgressState.DeliveryUnknown);
            transitions.Where(evt => evt.HasAppendAcceptedSegmentCountDelta).Sum(evt => evt.AppendAcceptedSegmentCountDelta).ShouldBe(1);
            (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).Count.ShouldBe(1);
            fixture.Publisher.Steps.ShouldBeEmpty();
        }
        finally
        {
            fixture.Outbound.ReleaseStalledProgress();
            mailbox.Writer.TryComplete();
            await serialDelivery.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LocalAccelerator_PublishesOnlyAtTwoSeconds_AndActorConsumesTheSignalExplicitly()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        var beforeDue = fixture.Agent.State.ToByteArray();

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1999));
        fixture.Publisher.ProgressDueSignals.ShouldBeEmpty();
        fixture.Agent.State.ToByteArray().ShouldBe(beforeDue);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var due = await fixture.Publisher.FirstProgressDue.Task.WaitAsync(TimeSpan.FromSeconds(5));

        due.CorrelationId.ShouldBe(Fixture.Correlation);
        fixture.Publisher.ProgressDueSignals.Count.ShouldBe(1);
        // Timer callbacks publish a signal only; processing still requires a separate actor turn.
        fixture.Agent.State.ToByteArray().ShouldBe(beforeDue);
        fixture.Outbound.Attempts.ShouldBeEmpty();
        fixture.Publisher.Steps.ShouldBeEmpty();
        await fixture.Agent.HandleNyxRelayAppendProgressDueAsync(due);
        fixture.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Progress);
        fixture.Publisher.Steps.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Deactivation_CancelsLocalAccelerator_WithoutPublishingOrMutatingActorState()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.DeactivateAsync();
        var deactivatedState = fixture.Agent.State.ToByteArray();

        fixture.Clock.Advance(TimeSpan.FromSeconds(3));

        fixture.Publisher.ProgressDueSignals.ShouldBeEmpty();
        fixture.Publisher.FirstProgressDue.Task.IsCompleted.ShouldBeFalse();
        fixture.Agent.State.ToByteArray().ShouldBe(deactivatedState);
        fixture.Outbound.Attempts.ShouldBeEmpty();
        fixture.Publisher.Steps.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdmissionWithoutText_ArmsTwoSecondProgressBeforeRunDispatch_AndSendsItOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();

        fixture.Append.ProgressState.ShouldBe(NyxRelayAppendProgressState.Waiting);
        fixture.Append.MaxSegmentLength.ShouldBe(4096);
        fixture.Append.InFlightOperation.ShouldBeNull();
        fixture.Dispatcher.Requests.Count.ShouldBe(1);
        fixture.Dispatcher.SawProgressBeforeDispatch.ShouldBeTrue();
        fixture.Clock.RequestedDelays.ShouldContain(TimeSpan.FromSeconds(2));
        fixture.Scheduler.Timeouts.Single(request => request.TriggerEnvelope.Payload.Is(NyxRelayAppendProgressDueEvent.Descriptor))
            .DueTime.ShouldBe(TimeSpan.FromSeconds(2));
        fixture.Outbound.Attempts.ShouldBeEmpty();

        await fixture.ProgressDueAsync();
        await fixture.ProgressDueAsync();
        fixture.Publisher.Steps.Count.ShouldBe(1);
        fixture.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Progress);
        fixture.Append.InFlightOperation.SegmentIndex.ShouldBe(0);
        await fixture.ExecuteAndCompleteNextAsync();
        await fixture.ProgressDueAsync();

        fixture.Outbound.Attempts.Single().Text.ShouldBe("正在处理，请稍候...");
        fixture.Append.ProgressState.ShouldBe(NyxRelayAppendProgressState.Accepted);
        fixture.Append.AcceptedSegmentCount.ShouldBe(0);
        fixture.Append.NextSegmentIndex.ShouldBe(1);
        fixture.Append.DeliveryDisposition.ShouldBe(NyxRelayAppendDeliveryDisposition.NotSent);
        fixture.Lifecycle.LastFlushedText.ShouldBeEmpty();
        fixture.Agent.State.RetainedHistory.ShouldBeEmpty();
        fixture.Publisher.Steps.ShouldBeEmpty();
        await fixture.AssertNoPersistedReplyTokenAsync();
    }

    [Fact]
    public async Task ProgressDueWithShortStableBody_SendsBodyInsteadOfProgress_AndTerminalFlushesTail()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        const string text = "A short answer.";
        await fixture.ChunkAsync(text);
        fixture.Publisher.Steps.ShouldBeEmpty();

        await fixture.ProgressDueAsync();

        fixture.Append.ProgressState.ShouldBe(NyxRelayAppendProgressState.Skipped);
        fixture.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Content);
        fixture.Append.InFlightOperation.SegmentIndex.ShouldBe(1);
        // The final grapheme is retained until another chunk or terminal establishes stability.
        fixture.Append.InFlightOperation.Text.ShouldBe(text[..^1]);
        await fixture.ExecuteAndCompleteNextAsync();
        await fixture.ReadyAsync(text);
        fixture.Append.InFlightOperation.Text.ShouldBe(".");
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Outbound.Attempts.Select(attempt => attempt.Text).ShouldBe(new[] { text[..^1], "." });
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(text);
        fixture.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Fact]
    public async Task GrowingChunks_DoNotChangeInflightText_AndDuplicateSignalsCannotAdvanceItTwice()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        var initial = new string('a', 401);
        await fixture.ChunkAsync(initial);
        var fixedOperation = fixture.Append.InFlightOperation.Clone();
        fixedOperation.Text.ShouldBe(new string('a', 400));

        await fixture.ChunkAsync(initial + new string('b', 1000));
        fixture.Append.InFlightOperation.ShouldBe(fixedOperation);
        fixture.Lifecycle.PendingAccumulatedText.ShouldBe(initial + new string('b', 1000));
        fixture.Publisher.Steps.Count.ShouldBe(1);

        var step = fixture.Publisher.Steps.Dequeue();
        await fixture.Agent.HandleReplyOperationStepAsync(step);
        await fixture.Agent.HandleReplyOperationStepAsync(step.Clone());
        fixture.Outbound.Attempts.Count.ShouldBe(1);
        fixture.Dispatch.Completions.Count.ShouldBe(1);
        var completion = fixture.Dispatch.Completions.Dequeue();
        await fixture.Agent.HandleNyxRelayAppendOperationCompletedAsync(completion);
        await fixture.Agent.HandleNyxRelayAppendOperationCompletedAsync(completion.Clone());
        await fixture.Agent.HandleNyxRelayAppendOperationTimeoutFiredAsync(Timeout(fixedOperation));

        fixture.Append.AcceptedSegmentCount.ShouldBe(1);
        fixture.Append.NextSegmentIndex.ShouldBe(2);
        fixture.Lifecycle.LastFlushedText.ShouldBe(fixedOperation.Text);
        fixture.Append.TerminalReason.ShouldBe(NyxRelayAppendTerminalReason.Unspecified);
        fixture.Publisher.Steps.ShouldBeEmpty();

        await fixture.ChunkAsync(initial + new string('b', 2100));
        var nextOperation = fixture.Append.InFlightOperation.Clone();
        nextOperation.OperationGeneration.ShouldBeGreaterThan(fixedOperation.OperationGeneration);
        await fixture.Agent.HandleNyxRelayAppendOperationCompletedAsync(completion.Clone());
        await fixture.Agent.HandleNyxRelayAppendOperationTimeoutFiredAsync(Timeout(fixedOperation));
        fixture.Append.InFlightOperation.ShouldBe(nextOperation);
        fixture.Append.AcceptedSegmentCount.ShouldBe(1);
    }

    [Fact]
    public async Task RewrittenAcceptedPrefix_StopsAppendAndCommitsOnlyTheAcceptedPrefix()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(new string('a', 401));
        await fixture.ExecuteAndCompleteNextAsync();
        var acceptedPrefix = fixture.Lifecycle.LastFlushedText;

        await fixture.ChunkAsync("rewritten answer");

        fixture.Append.TerminalReason.ShouldBe(NyxRelayAppendTerminalReason.PrefixRewritten);
        fixture.Append.DeliveryDisposition.ShouldBe(NyxRelayAppendDeliveryDisposition.PartialAccepted);
        fixture.Publisher.Steps.ShouldBeEmpty();
        await fixture.ReadyAsync("rewritten final answer");
        var completed = (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single();
        completed.Outbound.Text.ShouldBe(acceptedPrefix);
        completed.AppendedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(acceptedPrefix);
        fixture.Outbound.Attempts.Count.ShouldBe(1);
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Theory]
    [InlineData(NyxRelayAppendSendState.Rejected, NyxRelayAppendProgressState.Rejected)]
    [InlineData(NyxRelayAppendSendState.DeliveryUnknown, NyxRelayAppendProgressState.DeliveryUnknown)]
    public async Task ProgressFailure_DoesNotBlockBodyOrPolluteItsHistory(
        NyxRelayAppendSendState progressResult,
        NyxRelayAppendProgressState progressState)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(progressResult, ErrorCode: "progress_failed"));
        await fixture.AdmitAsync();
        await fixture.ProgressDueAsync();
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Append.ProgressState.ShouldBe(progressState);
        fixture.Append.AcceptedSegmentCount.ShouldBe(0);
        fixture.Append.DeliveryDisposition.ShouldBe(NyxRelayAppendDeliveryDisposition.NotSent);
        fixture.Append.AnyRequestDispatched.ShouldBeTrue();
        const string answer = "The final answer.";
        await fixture.ReadyAsync(answer);
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Outbound.Attempts.Select(attempt => attempt.Text).ShouldBe(new[] { "正在处理，请稍候...", answer });
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(answer);
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).Count.ShouldBe(1);
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Theory]
    [InlineData(NyxRelayAppendSendState.Accepted)]
    [InlineData(NyxRelayAppendSendState.Rejected)]
    [InlineData(NyxRelayAppendSendState.DeliveryUnknown)]
    public async Task AnyProgressDispatch_LocksOutTokenFallback_WhenBodyPreflightLaterFails(NyxRelayAppendSendState progressResult)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(progressResult));
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(NyxRelayAppendSendState.PreDispatchFailure,
            ErrorCode: "agent_key_unavailable"));
        await fixture.AdmitAsync();
        await fixture.ProgressDueAsync();
        await fixture.ExecuteAndCompleteNextAsync();
        await fixture.ReadyAsync("A real answer after progress.");
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Outbound.Attempts.Count.ShouldBe(2);
        fixture.Outbound.DispatchedCount.ShouldBe(1);
        fixture.Runner.FallbackReplies.ShouldBe(0);
        fixture.Agent.State.RetainedHistory.ShouldNotContain(entry => entry.Role == "assistant");
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).ShouldBeEmpty();
        (await fixture.EventsAsync<LlmReplyDeliveryFailedEvent>()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task FirstBodyPreDispatchFailure_PreservesOriginalTerminalTokenFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(NyxRelayAppendSendState.PreDispatchFailure,
            ErrorCode: "agent_key_unavailable"));
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(new string('a', 401));
        await fixture.ExecuteAndCompleteNextAsync();

        fixture.Append.AnyRequestDispatched.ShouldBeFalse();
        fixture.Append.TerminalReason.ShouldBe(NyxRelayAppendTerminalReason.PreDispatchFailure);
        await fixture.ReadyAsync("The original complete reply.");

        fixture.Outbound.DispatchedCount.ShouldBe(0);
        fixture.Runner.FallbackReplies.ShouldBe(1);
        fixture.Runner.LastFallbackToken.ShouldBe(Fixture.ReplyToken);
        fixture.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content
            .ShouldBe("The original complete reply.");
        fixture.Publisher.Steps.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(NyxRelayAppendSendState.Rejected, NyxRelayAppendDeliveryDisposition.PartialAccepted)]
    [InlineData(NyxRelayAppendSendState.DeliveryUnknown, NyxRelayAppendDeliveryDisposition.DeliveryUnknown)]
    public async Task LaterBodyFailure_RetainsOnlyEarlierAcceptedText_WithoutRetry(
        NyxRelayAppendSendState failure,
        NyxRelayAppendDeliveryDisposition expectedDisposition)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(NyxRelayAppendSendState.Accepted, "platform-first"));
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(failure, ErrorCode: "body_failed"));
        await fixture.AdmitAsync();
        var firstChunk = new string('a', 401);
        await fixture.ChunkAsync(firstChunk);
        await fixture.ExecuteAndCompleteNextAsync();
        var prefix = fixture.Lifecycle.LastFlushedText;
        await fixture.ReadyAsync(firstChunk + " remaining terminal tail");
        await fixture.ExecuteAndCompleteNextAsync();

        var transitions = await fixture.EventsAsync<ConversationReplyLifecycleChangedEvent>();
        transitions.ShouldContain(evt => evt.HasAppendDeliveryDisposition && evt.AppendDeliveryDisposition == expectedDisposition);
        var completed = (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single();
        completed.Outbound.Text.ShouldBe(prefix);
        completed.AppendedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(prefix);
        completed.AppendedHistory.Where(entry => entry.Role == "assistant").ShouldNotContain(entry => entry.Content.Contains("remaining"));
        fixture.Outbound.Attempts.Count.ShouldBe(2);
        fixture.Runner.FallbackReplies.ShouldBe(0);
        fixture.Publisher.Steps.ShouldBeEmpty();
        (await fixture.EventsAsync<LlmReplyDeliveredEvent>()).ShouldBeEmpty();
    }

    [Fact]
    public async Task TimeoutAfterDispatch_MarksUnknown_AndIgnoresLateSuccessfulCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(new string('a', 401));
        var operation = fixture.Append.InFlightOperation.Clone();
        var completion = await fixture.ExecuteNextAsync();

        await fixture.Agent.HandleNyxRelayAppendOperationTimeoutFiredAsync(Timeout(operation));
        await fixture.Agent.HandleNyxRelayAppendOperationCompletedAsync(completion);

        fixture.Append.DeliveryDisposition.ShouldBe(NyxRelayAppendDeliveryDisposition.DeliveryUnknown);
        fixture.Append.AcceptedSegmentCount.ShouldBe(0);
        fixture.Lifecycle.LastFlushedText.ShouldBeEmpty();
        await fixture.ReadyAsync(new string('a', 401));
        fixture.Outbound.Attempts.Count.ShouldBe(1);
        fixture.Publisher.Steps.ShouldBeEmpty();
        fixture.Agent.State.RetainedHistory.ShouldNotContain(entry => entry.Role == "assistant");
        fixture.Runner.FallbackReplies.ShouldBe(0);
    }

    [Fact]
    public async Task RecoveryWithInflightBody_DoesNotReplayOperationOrRestartActiveLlmRun()
    {
        var store = new InMemoryEventStore();
        await using (var first = await Fixture.CreateAsync(store))
        {
            await first.AdmitAsync();
            await first.ChunkAsync(new string('a', 401));
            first.Append.InFlightOperation.ShouldNotBeNull();
            first.Append.LlmRunDispatched.ShouldBeTrue();
            first.Outbound.Attempts.ShouldBeEmpty();
        }

        await using var recovered = await Fixture.CreateAsync(store);

        recovered.Append.InFlightOperation.ShouldBeNull();
        recovered.Append.DeliveryDisposition.ShouldBe(NyxRelayAppendDeliveryDisposition.DeliveryUnknown);
        recovered.Append.TerminalReason.ShouldBe(NyxRelayAppendTerminalReason.DeliveryUnknown);
        recovered.Outbound.Attempts.ShouldBeEmpty();
        recovered.Dispatcher.Requests.ShouldBeEmpty();
        recovered.Publisher.Steps.ShouldBeEmpty();
        await recovered.ProgressDueAsync();
        await recovered.ReadyAsync(new string('a', 401));
        recovered.Outbound.Attempts.ShouldBeEmpty();
        recovered.Runner.FallbackReplies.ShouldBe(0);
    }

    [Theory]
    [InlineData("telegram", 4096)]
    [InlineData("unknown-chat", 2000)]
    public async Task RecoveryAfterAcceptedRequestBeforeAppendInitialization_InitializesBeforeFirstHandoff(
        string platform, int limit)
    {
        var prefixStore = new InMemoryEventStore();
        var secrets = new InMemoryRuntimeSecretStore();
        await using (var first = await Fixture.CreateAsync(platform: platform, runtimeSecretStore: secrets))
        {
            await first.AdmitRelayAsync();
            var committed = await first.Store.GetEventsAsync(first.Agent.Id);
            var accepted = committed.Single(record => record.EventData.Is(NeedsLlmReplyEvent.Descriptor));
            var prefix = committed.TakeWhile(record => record.Version <= accepted.Version).ToArray();
            prefix.Last().EventData.Is(NeedsLlmReplyEvent.Descriptor).ShouldBeTrue();
            prefix.ShouldNotContain(record => record.EventData.Is(ConversationReplyLifecycleChangedEvent.Descriptor));
            var request = accepted.EventData.Unpack<NeedsLlmReplyEvent>();
            request.ReplyToken.ShouldBeEmpty();
            request.RelayReplyTokenRef.Ref.ShouldNotBeNullOrWhiteSpace();
            await prefixStore.AppendAsync(first.Agent.Id, prefix, expectedVersion: 0);
        }

        ConversationReplyLifecycleState? lifecycleAtHandoff = null;
        await using (var admitted = await Fixture.CreateAsync(prefixStore, platform: platform, runtimeSecretStore: secrets,
                         beforeDispatch: (actor, request) => lifecycleAtHandoff = actor.State.ActiveReplyLifecycles
                             .SingleOrDefault(lifecycle => lifecycle.CorrelationId == request.CorrelationId &&
                                 lifecycle.NyxRelayTextDeliveryStrategy == NyxRelayTextDeliveryStrategy.AppendMessages)?.Clone()))
        {
            lifecycleAtHandoff.ShouldNotBeNull();
            lifecycleAtHandoff.NyxRelayAppendStreaming.MaxSegmentLength.ShouldBe(limit);
            lifecycleAtHandoff.NyxRelayAppendStreaming.ProgressState.ShouldBe(NyxRelayAppendProgressState.Waiting);
            lifecycleAtHandoff.NyxRelayAppendStreaming.LlmRunDispatched.ShouldBeFalse();
            admitted.Dispatcher.Requests.Single().ReplyToken.ShouldBe(Fixture.ReplyToken);
            admitted.Dispatcher.SawProgressBeforeDispatch.ShouldBeTrue();
            admitted.Append.LlmRunDispatched.ShouldBeTrue();
            await admitted.AssertNoPersistedReplyTokenAsync();
        }

        await using var recovered = await Fixture.CreateAsync(prefixStore, platform: platform, runtimeSecretStore: secrets);
        recovered.Dispatcher.Requests.ShouldBeEmpty();
        (await recovered.EventsAsync<ConversationReplyLifecycleChangedEvent>()).Count(evt =>
            evt.HasNyxRelayTextDeliveryStrategy && evt.NyxRelayTextDeliveryStrategy == NyxRelayTextDeliveryStrategy.AppendMessages)
            .ShouldBe(1);
        await recovered.ProgressDueAsync();
        await recovered.ProgressDueAsync();
        recovered.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Progress);
        await recovered.ExecuteAndCompleteNextAsync();
        await recovered.ProgressDueAsync();
        recovered.Publisher.Steps.ShouldBeEmpty();
        recovered.Append.AcceptedSegmentCount.ShouldBe(0);
        recovered.Lifecycle.LastFlushedText.ShouldBeEmpty();

        var answer = new string('a', 401) + " recovered body.";
        await recovered.ChunkAsync(answer);
        recovered.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Content);
        await recovered.ExecuteAndCompleteNextAsync();
        recovered.Lifecycle.LastFlushedText.ShouldBe(new string('a', 400));
        await recovered.ReadyAsync(answer);
        await recovered.ExecuteAndCompleteNextAsync();

        recovered.Outbound.Attempts.First().Text.ShouldBe("正在处理，请稍候...");
        recovered.Outbound.Attempts.Count(attempt => attempt.Text == "正在处理，请稍候...").ShouldBe(1);
        string.Concat(recovered.Outbound.Attempts.Skip(1).Select(attempt => attempt.Text)).ShouldBe(answer);
        (await recovered.EventsAsync<LlmReplyDeliveryFailedEvent>()).ShouldBeEmpty();
        (await recovered.EventsAsync<LlmReplyDeliveredEvent>()).Count.ShouldBe(1);
        (await recovered.EventsAsync<ConversationTurnCompletedEvent>()).Single().Outbound.Text.ShouldBe(answer);
        recovered.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content.ShouldBe(answer);
        recovered.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
        recovered.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        recovered.Runner.FallbackReplies.ShouldBe(0);
        await recovered.AssertNoPersistedReplyTokenAsync();
    }

    [Fact]
    public async Task RecoveryWithInflightProgress_DoesNotRepeatProgress_AndStillAcceptsBody()
    {
        var store = new InMemoryEventStore();
        await using (var first = await Fixture.CreateAsync(store))
        {
            await first.AdmitAsync();
            await first.ProgressDueAsync();
            first.Append.InFlightOperation.Kind.ShouldBe(NyxRelayAppendMessageKind.Progress);
        }

        await using var recovered = await Fixture.CreateAsync(store);
        recovered.Append.ProgressState.ShouldBe(NyxRelayAppendProgressState.DeliveryUnknown);
        recovered.Append.InFlightOperation.ShouldBeNull();
        await recovered.ProgressDueAsync();
        recovered.Publisher.Steps.ShouldBeEmpty();
        await recovered.ReadyAsync("Answer after recovery.");
        await recovered.ExecuteAndCompleteNextAsync();

        recovered.Outbound.Attempts.Single().Text.ShouldBe("Answer after recovery.");
        recovered.Dispatcher.Requests.ShouldBeEmpty();
        recovered.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content
            .ShouldBe("Answer after recovery.");
    }

    [Theory]
    [InlineData(NyxRelayAppendSendState.Accepted)]
    [InlineData(NyxRelayAppendSendState.Rejected)]
    [InlineData(NyxRelayAppendSendState.DeliveryUnknown)]
    public async Task Completion_PreservesToolCallResultPairs_AlongsideHonestAcceptedBody(NyxRelayAppendSendState finalResult)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(NyxRelayAppendSendState.Accepted, "first-message"));
        fixture.Outbound.Results.Enqueue(new NyxRelayAppendSendResult(finalResult));
        await fixture.AdmitAsync();
        var text = new string('a', 401);
        await fixture.ChunkAsync(text);
        await fixture.ExecuteAndCompleteNextAsync();
        var prefix = fixture.Lifecycle.LastFlushedText;
        var fullAnswer = text + " final tail";
        await fixture.ReadyAsync(fullAnswer, includeToolHistory: true);
        await fixture.ExecuteAndCompleteNextAsync();

        var history = (await fixture.EventsAsync<ConversationTurnCompletedEvent>()).Single().AppendedHistory;
        var call = history.Single(entry => entry.Role == "assistant" && entry.ToolCalls.Count > 0);
        call.ToolCalls.Single().Id.ShouldBe("tool-call-test");
        var result = history.Single(entry => entry.Role == "tool");
        result.ToolCallId.ShouldBe("tool-call-test");
        result.Content.ShouldBe("tool result retained for the next turn");
        var body = history.Single(entry => entry.Role == "assistant" && entry.ToolCalls.Count == 0);
        body.Content.ShouldBe(finalResult == NyxRelayAppendSendState.Accepted ? fullAnswer : prefix);
        fixture.Outbound.Attempts.ShouldNotContain(attempt => attempt.Text.Contains("tool result"));
    }

    [Fact]
    public async Task RecoveryAfterCommittedTerminal_DoesNotRestartRunOrRepeatReply()
    {
        var store = new InMemoryEventStore();
        await using (var first = await Fixture.CreateAsync(store))
        {
            await first.AdmitAsync();
            await first.ReadyAsync("Already accepted answer.");
            await first.ExecuteAndCompleteNextAsync();
            first.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
            first.Agent.State.PendingLlmReplyRequests.ShouldBeEmpty();
        }

        await using var recovered = await Fixture.CreateAsync(store);
        await recovered.ProgressDueAsync();
        await recovered.ReadyAsync("Already accepted answer.");

        recovered.Dispatcher.Requests.ShouldBeEmpty();
        recovered.Outbound.Attempts.ShouldBeEmpty();
        recovered.Runner.FallbackReplies.ShouldBe(0);
        recovered.Publisher.Steps.ShouldBeEmpty();
        recovered.Agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        (await recovered.EventsAsync<ConversationTurnCompletedEvent>()).Count.ShouldBe(1);
        recovered.Agent.State.RetainedHistory.Where(entry => entry.Role == "assistant").Single().Content
            .ShouldBe("Already accepted answer.");
    }

    [Theory]
    [InlineData("lark", true)]
    [InlineData("feishu", true)]
    [InlineData("aurinko", true)]
    [InlineData("device", true)]
    [InlineData("telegram", false)]
    public async Task OtherStrategies_DoNotCreateAppendLifecycleOrProgressTimers(string platform, bool relay)
    {
        await using var fixture = await Fixture.CreateAsync(platform: platform, relay: relay);
        await fixture.AdmitAsync();

        fixture.Agent.State.ActiveReplyLifecycles.ShouldNotContain(lifecycle =>
            lifecycle.NyxRelayTextDeliveryStrategy == NyxRelayTextDeliveryStrategy.AppendMessages);
        fixture.Scheduler.Timeouts.ShouldNotContain(request =>
            request.TriggerEnvelope.Payload.Is(NyxRelayAppendProgressDueEvent.Descriptor));
        fixture.Clock.RequestedDelays.ShouldBeEmpty();
        fixture.Outbound.Attempts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("aurinko", 1)]
    [InlineData("device", 0)]
    public async Task SingleAndNoneProfiles_KeepTerminalOneShotOrFailClosed(string platform, int fallbackCount)
    {
        await using var fixture = await Fixture.CreateAsync(platform: platform);
        await fixture.AdmitAsync();
        await fixture.ChunkAsync(new string('a', 401));
        fixture.Outbound.Attempts.ShouldBeEmpty();
        await fixture.ReadyAsync("Terminal answer.");

        fixture.Runner.FallbackReplies.ShouldBe(fallbackCount);
        fixture.Outbound.Attempts.ShouldBeEmpty();
        fixture.Publisher.Steps.ShouldBeEmpty();
        if (platform == "device")
            (await fixture.EventsAsync<LlmReplyDeliveryFailedEvent>()).Single().ErrorCode.ShouldBe("relay_reply_not_supported");
    }

    private static NyxRelayAppendOperationTimeoutFiredEvent Timeout(NyxRelayAppendOperation operation) => new()
    {
        CorrelationId = Fixture.Correlation,
        Kind = operation.Kind,
        SegmentIndex = operation.SegmentIndex,
        OperationGeneration = operation.OperationGeneration,
        FiredAtUnixMs = long.MaxValue,
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Correlation = "corr-append-test";
        public const string RunId = "run-append-test";
        public const string ReplyToken = "ephemeral-reply-token-never-persist";
        private readonly ServiceProvider _services;
        private bool _deactivated;
        public ConversationGAgent Agent { get; }
        public IEventStore Store { get; }
        public ChatActivity Activity { get; }
        public RecordingPublisher Publisher { get; }
        public RecordingDispatchPort Dispatch { get; }
        public RecordingScheduler Scheduler { get; }
        public ControlledTimeProvider Clock { get; }
        public RecordingOutbound Outbound { get; }
        public RecordingRunDispatcher Dispatcher { get; }
        public RecordingRunner Runner { get; }
        public ConversationReplyLifecycleState Lifecycle => Agent.State.ActiveReplyLifecycles.Single(lifecycle =>
            lifecycle.CorrelationId == Correlation && lifecycle.NyxRelayTextDeliveryStrategy == NyxRelayTextDeliveryStrategy.AppendMessages);
        public NyxRelayAppendStreamingState Append => Lifecycle.NyxRelayAppendStreaming;

        private Fixture(ServiceProvider services, ConversationGAgent agent, IEventStore store, ChatActivity activity,
            RecordingPublisher publisher, RecordingDispatchPort dispatch, RecordingScheduler scheduler,
            ControlledTimeProvider clock, RecordingOutbound outbound, RecordingRunDispatcher dispatcher, RecordingRunner runner)
        {
            _services = services;
            Agent = agent;
            Store = store;
            Activity = activity;
            Publisher = publisher;
            Dispatch = dispatch;
            Scheduler = scheduler;
            Clock = clock;
            Outbound = outbound;
            Dispatcher = dispatcher;
            Runner = runner;
        }

        public static async Task<Fixture> CreateAsync(IEventStore? store = null, string platform = "telegram", bool relay = true,
            IRuntimeSecretStore? runtimeSecretStore = null, Action<ConversationGAgent, NeedsLlmReplyEvent>? beforeDispatch = null)
        {
            store ??= new InMemoryEventStore();
            var publisher = new RecordingPublisher();
            var dispatch = new RecordingDispatchPort();
            var scheduler = new RecordingScheduler();
            var clock = new ControlledTimeProvider();
            var outbound = new RecordingOutbound();
            var dispatcher = new RecordingRunDispatcher(scheduler);
            var runner = new RecordingRunner();
            var services = new ServiceCollection()
                .AddSingleton(store)
                .AddSingleton<IActorDispatchPort>(dispatch)
                .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
                .AddSingleton<EventSourcingRuntimeOptions>()
                .AddSingleton<IConversationTurnRunner>(runner)
                .AddSingleton<IChannelLlmReplyRunDispatcher>(dispatcher)
                .AddSingleton<INyxRelayAppendOutboundPort>(outbound)
                .AddSingleton<IRuntimeSecretStore>(runtimeSecretStore ?? new InMemoryRuntimeSecretStore())
                .AddSingleton<TimeProvider>(clock)
                .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
                .BuildServiceProvider();
            var agent = new ConversationGAgent
            {
                Services = services,
                EventPublisher = publisher,
                EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
            };
            var current = agent.GetType();
            MethodInfo? setId = null;
            while (current is not null && setId is null)
            {
                setId = current.GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
                current = current.BaseType;
            }
            setId.ShouldNotBeNull();
            setId.Invoke(agent, ["conversation-append-test"]);
            dispatcher.BeforeDispatch = request => beforeDispatch?.Invoke(agent, request);
            await agent.ActivateAsync();
            var activity = new ChatActivity
            {
                Id = "inbound-message-test",
                Type = ActivityType.Message,
                ChannelId = new ChannelId { Value = platform },
                Bot = new BotInstanceId { Value = "bot-append-test" },
                Conversation = new ConversationReference
                {
                    Channel = new ChannelId { Value = platform },
                    Bot = new BotInstanceId { Value = "bot-append-test" },
                    Scope = ConversationScope.DirectMessage,
                    CanonicalKey = "conversation-key-test",
                },
                Content = new MessageContent { Text = "question" },
                OutboundDelivery = relay ? new OutboundDeliveryContext
                {
                    ReplyMessageId = "original-relay-message-test",
                    CorrelationId = Correlation,
                } : null,
                TransportExtras = new TransportExtras { NyxPlatform = relay ? platform : string.Empty },
            };
            return new Fixture(services, agent, store, activity, publisher, dispatch, scheduler, clock, outbound, dispatcher, runner);
        }

        public Task AdmitAsync() => Agent.HandleInboundActivityAsync(Activity);

        public Task AdmitRelayAsync() => Agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = Activity.Clone(),
            CorrelationId = Correlation,
            ReplyToken = ReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        });

        public Task ProgressDueAsync() => Agent.HandleNyxRelayAppendProgressDueAsync(new NyxRelayAppendProgressDueEvent
        {
            CorrelationId = Correlation,
            FiredAtUnixMs = Clock.GetUtcNow().AddSeconds(2).ToUnixTimeMilliseconds(),
        });

        public Task ChunkAsync(string text) => Agent.HandleLlmReplyStreamChunkAsync(new LlmReplyStreamChunkEvent
        {
            CorrelationId = Correlation,
            Activity = Activity.Clone(),
            AccumulatedText = text,
            ReplyToken = ReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        });

        public Task ReadyAsync(string text, bool includeToolHistory = false) =>
            Agent.HandleLlmReplyReadyAsync(CreateReady(text, includeToolHistory));

        public LlmReplyReadyEvent CreateReady(string text, bool includeToolHistory = false)
        {
            var ready = new LlmReplyReadyEvent
            {
                CorrelationId = Correlation,
                RunId = RunId,
                RegistrationId = "registration-append-test",
                Activity = Activity.Clone(),
                Outbound = new MessageContent { Text = text },
                TerminalState = LlmReplyTerminalState.Completed,
                ReplyToken = ReplyToken,
                ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            };
            ready.AppendedHistory.Add(new ConversationHistoryEntry { Role = "user", Content = "question" });
            if (includeToolHistory)
            {
                var call = new ConversationHistoryEntry { Role = "assistant" };
                call.ToolCalls.Add(new ConversationToolCallEntry
                {
                    Id = "tool-call-test", Name = "lookup", ArgumentsJson = "{}",
                });
                ready.AppendedHistory.Add(call);
                ready.AppendedHistory.Add(new ConversationHistoryEntry
                {
                    Role = "tool", ToolCallId = "tool-call-test", Content = "tool result retained for the next turn",
                });
            }
            ready.AppendedHistory.Add(new ConversationHistoryEntry { Role = "assistant", Content = text });
            return ready;
        }

        public async Task<NyxRelayAppendOperationCompletedEvent> ExecuteNextAsync()
        {
            Publisher.Steps.ShouldNotBeEmpty();
            await Agent.HandleReplyOperationStepAsync(Publisher.Steps.Dequeue());
            Dispatch.Completions.ShouldNotBeEmpty();
            return Dispatch.Completions.Dequeue();
        }

        public async Task ExecuteAndCompleteNextAsync() =>
            await Agent.HandleNyxRelayAppendOperationCompletedAsync(await ExecuteNextAsync());

        public async Task<List<T>> EventsAsync<T>() where T : IMessage<T>, new() =>
            (await Store.GetEventsAsync(Agent.Id)).Where(record => record.EventData.Is(new T().Descriptor))
            .Select(record => record.EventData.Unpack<T>()).ToList();

        public async Task AssertNoPersistedReplyTokenAsync()
        {
            var sentinel = Encoding.UTF8.GetBytes(ReplyToken);
            Agent.State.ToByteArray().AsSpan().IndexOf(sentinel).ShouldBe(-1);
            foreach (var record in await Store.GetEventsAsync(Agent.Id))
                record.EventData.Value.Span.IndexOf(sentinel).ShouldBe(-1);
        }

        public async Task DeactivateAsync()
        {
            if (!_deactivated)
            {
                await Agent.DeactivateAsync();
                _deactivated = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DeactivateAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingOutbound : INyxRelayAppendOutboundPort
    {
        private readonly TaskCompletionSource _stalledProgress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Queue<NyxRelayAppendSendResult> Results { get; } = new();
        public List<(string Text, string ReplyMessageId, int MaxLength)> Attempts { get; } = new();
        public bool StallProgressUntilCanceled { get; set; }
        public TaskCompletionSource ProgressSendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ProgressSendCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DispatchedCount { get; private set; }
        public string PrepareText(string platform, ConversationReference conversation, string rawText) => rawText;
        public void ReleaseStalledProgress() => _stalledProgress.TrySetCanceled();

        public async Task<NyxRelayAppendSendResult> SendAsync(ChatActivity activity, string rawText, int maxLength,
            Func<CancellationToken, Task> onDispatch, CancellationToken ct)
        {
            Attempts.Add((rawText, activity.OutboundDelivery?.ReplyMessageId ?? string.Empty, maxLength));
            if (string.IsNullOrWhiteSpace(PrepareText(activity.ChannelId.Value, activity.Conversation, rawText)))
                return new NyxRelayAppendSendResult(NyxRelayAppendSendState.PreDispatchFailure, ErrorCode: "append_empty_text");
            var result = Results.Count > 0 ? Results.Dequeue()
                : new NyxRelayAppendSendResult(NyxRelayAppendSendState.Accepted, $"platform-{Attempts.Count}");
            if (result.State != NyxRelayAppendSendState.PreDispatchFailure)
            {
                await onDispatch(ct);
                DispatchedCount++;
            }
            if (StallProgressUntilCanceled && rawText == "正在处理，请稍候...")
            {
                ProgressSendEntered.TrySetResult();
                try
                {
                    await _stalledProgress.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    ProgressSendCanceled.TrySetResult(ct.IsCancellationRequested);
                    throw;
                }
            }
            return result;
        }
    }

    private sealed class RecordingRunner : IConversationTurnRunner
    {
        public int FallbackReplies { get; private set; }
        public string? LastFallbackToken { get; private set; }

        public Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) => Task.FromResult(ConversationTurnResult.LlmReplyRequested(new NeedsLlmReplyEvent
            {
                CorrelationId = Fixture.Correlation,
                RunId = Fixture.RunId,
                TargetActorId = "conversation-append-test",
                RegistrationId = "registration-append-test",
                Activity = activity.Clone(),
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyToken = Fixture.ReplyToken,
                ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            }));

        public Task<ConversationTurnResult> RunLlmReplyAsync(LlmReplyReadyEvent reply, ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            if (runtimeContext.DeferRelayTextReply)
                return Task.FromResult(ConversationTurnResult.RelayTextDeferred(reply.Outbound));
            FallbackReplies++;
            LastFallbackToken = runtimeContext.NyxRelayReplyToken?.ReplyToken;
            return Task.FromResult(ConversationTurnResult.Sent("fallback-message", reply.Outbound.Clone(), "bot",
                reply.Activity.OutboundDelivery?.Clone()));
        }

        public Task<ConversationTurnResult> RunContinueAsync(ConversationContinueRequestedEvent command, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected continue invocation.");

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(LlmReplyStreamChunkEvent chunk, string? currentPlatformMessageId,
            NyxRelayTextOperationKind operation, ConversationTurnRuntimeContext runtimeContext, CancellationToken ct) =>
            throw new InvalidOperationException("Append must not execute the edit renderer.");
    }

    private sealed class RecordingRunDispatcher(RecordingScheduler scheduler) : IChannelLlmReplyRunDispatcher
    {
        public List<NeedsLlmReplyEvent> Requests { get; } = new();
        public int FailuresRemaining { get; set; }
        public int AttemptCount { get; private set; }
        public bool SawProgressBeforeDispatch { get; private set; }
        public Action<NeedsLlmReplyEvent>? BeforeDispatch { get; set; }
        public Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
        {
            AttemptCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromException(new InvalidOperationException("initial_handoff_unavailable"));
            }
            BeforeDispatch?.Invoke(request);
            SawProgressBeforeDispatch = scheduler.Timeouts.Any(timeout =>
                timeout.TriggerEnvelope.Payload.Is(NyxRelayAppendProgressDueEvent.Descriptor));
            Requests.Add(request.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public Queue<ReplyOperationStepEvent> Steps { get; } = new();
        public Action<IMessage>? InboxSink { get; set; }
        public List<NyxRelayAppendProgressDueEvent> ProgressDueSignals { get; } = new();
        public TaskCompletionSource<NyxRelayAppendProgressDueEvent> FirstProgressDue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync<T>(T evt, TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default, EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null)
            where T : IMessage => Task.CompletedTask;

        public Task SendToAsync<T>(string targetActorId, T evt, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where T : IMessage
        {
            if (evt is ReplyOperationStepEvent step && InboxSink is null)
                Steps.Enqueue(step.Clone());
            if (evt is NyxRelayAppendProgressDueEvent due)
            {
                ProgressDueSignals.Add(due.Clone());
                FirstProgressDue.TrySetResult(due.Clone());
            }
            InboxSink?.Invoke(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public Queue<NyxRelayAppendOperationCompletedEvent> Completions { get; } = new();
        public Action<NyxRelayAppendOperationCompletedEvent>? InboxSink { get; set; }
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope.Payload.Is(NyxRelayAppendOperationCompletedEvent.Descriptor))
            {
                var completion = envelope.Payload.Unpack<NyxRelayAppendOperationCompletedEvent>();
                if (InboxSink is null)
                    Completions.Enqueue(completion);
                else
                    InboxSink(completion);
            }
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = new();
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Append must use one-shot signals.");
        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Timer callbacks run only when the test advances logical time. The publisher keeps their
    // signals queued until the test explicitly delivers a separate actor turn.
    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly List<ControlledTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> RequestedDelays { get; } = new();
        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue(_now);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            var timer = new ControlledTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        private sealed class ControlledTimer(ControlledTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private DateTimeOffset? _dueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                    return false;
                if (period != global::System.Threading.Timeout.InfiniteTimeSpan)
                    throw new InvalidOperationException("The append accelerator must use a one-shot timer.");
                _dueAt = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                return true;
            }

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || _dueAt is null || _dueAt > now)
                    return;
                _dueAt = null;
                callback(state);
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
