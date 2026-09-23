using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent : INyxRelayAppendOperationActorContext
{
    private static readonly TimeSpan AppendProgressDelay = TimeSpan.FromSeconds(2);
    // Lifecycle cancellation only; all business facts remain in the persisted actor state.
    private CancellationTokenSource _appendSignalLifetime = new();
    private TimeProvider AppendTimeProvider => Services.GetService<TimeProvider>() ?? TimeProvider.System;
    private long AppendNow => AppendTimeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private ConversationReplyLifecycleState? FindAppendLifecycle(string? correlationId)
    {
        var lifecycle = FindReplyLifecycle(correlationId, ConversationReplyLifecycleMode.NyxRelayText);
        return lifecycle?.NyxRelayTextDeliveryStrategy == NyxRelayTextDeliveryStrategy.AppendMessages
            ? lifecycle : null;
    }

    private static string AppendPlatform(ChatActivity? activity) =>
        NormalizeOptional(activity?.TransportExtras?.NyxPlatform) ?? activity?.ChannelId?.Value ?? string.Empty;

    private async Task InitializeAppendReplyAsync(NeedsLlmReplyEvent request)
    {
        if (!IsRelayActivity(request.Activity) ||
            FindReplyLifecycle(request.CorrelationId, ConversationReplyLifecycleMode.NyxRelayText) is not null)
            return;
        var profile = NyxRelayCapabilityProfiles.Resolve(AppendPlatform(request.Activity));
        if (profile.SupportsEdit || profile.ReplyMessageMultiplicity != ReplyMessageMultiplicity.Multiple)
            return;

        var dueAt = AppendNow + (long)AppendProgressDelay.TotalMilliseconds;
        await PersistDomainEventAsync(new ConversationReplyLifecycleChangedEvent
        {
            CorrelationId = request.CorrelationId,
            Mode = ConversationReplyLifecycleMode.NyxRelayText,
            Phase = ConversationReplyLifecyclePhase.TextIdle,
            ChangedAtUnixMs = AppendNow,
            NyxRelayTextDeliveryStrategy = NyxRelayTextDeliveryStrategy.AppendMessages,
            AppendNextSegmentIndex = 1,
            AppendOperationGeneration = 0,
            AppendMaxSegmentLength = profile.MaxMessageLength,
            AppendDeliveryDisposition = NyxRelayAppendDeliveryDisposition.NotSent,
            AppendProgressState = NyxRelayAppendProgressState.Waiting,
            AppendProgressDueAtUnixMs = dueAt,
        });
        await ScheduleAppendProgressAsync(request.CorrelationId, dueAt);
    }

    private async Task ScheduleAppendProgressAsync(string correlationId, long dueAt)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, dueAt - AppendNow));
        var signal = new NyxRelayAppendProgressDueEvent { CorrelationId = correlationId, FiredAtUnixMs = dueAt };
        await ScheduleSelfDurableTimeoutAsync(
            $"conversation-nyx-relay-append-progress:{correlationId}",
            delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1), signal);
        // Durable Orleans reminders have a minute-scale tick. This local accelerator only
        // publishes the same signal; duplicated or late arrivals reconcile inside this actor.
        _ = PublishAppendSignalAfterDelayAsync(EventPublisher, Id, signal, delay, AppendTimeProvider,
            _appendSignalLifetime.Token, Logger);
    }

    private static async Task PublishAppendSignalAfterDelayAsync<T>(
        IEventPublisher publisher, string actorId, T signal, TimeSpan delay,
        TimeProvider timeProvider, CancellationToken lifetime, ILogger logger) where T : IMessage
    {
        try
        {
            await Task.Delay(delay, timeProvider, lifetime).ConfigureAwait(false);
            await publisher.SendToAsync(actorId, signal, lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            // Durable recovery remains armed. Never log exception/body/credential data.
            logger.LogWarning("Nyx relay append local signal could not be published; durable callback remains armed");
        }
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        await _appendSignalLifetime.CancelAsync();
        _appendSignalLifetime.Dispose();
        await base.OnDeactivateAsync(ct);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleNyxRelayAppendProgressDueAsync(NyxRelayAppendProgressDueEvent evt)
    {
        var lifecycle = FindAppendLifecycle(evt.CorrelationId);
        var append = lifecycle?.NyxRelayAppendStreaming;
        if (append is null || append.ProgressState != NyxRelayAppendProgressState.Waiting ||
            IsReplyTurnFinalized(evt.CorrelationId) || evt.FiredAtUnixMs < append.ProgressDueAtUnixMs)
            return;
        if (append.InFlightOperation is not null)
        {
            if (append.InFlightOperation.Kind == NyxRelayAppendMessageKind.Content)
                await ChangeAppendAsync(lifecycle!, change => change.AppendProgressState = NyxRelayAppendProgressState.Skipped);
            return;
        }
        if (append.AcceptedSegmentCount > 0 || append.TerminalReason != NyxRelayAppendTerminalReason.Unspecified ||
            !string.IsNullOrEmpty(lifecycle!.PendingFinalizeCommandId))
        {
            await ChangeAppendAsync(lifecycle!, change => change.AppendProgressState = NyxRelayAppendProgressState.Skipped);
            return;
        }
        if (!string.IsNullOrEmpty(lifecycle!.PendingAccumulatedText))
        {
            await ContinueAppendAsync(evt.CorrelationId, progressDue: true);
            if (FindAppendLifecycle(evt.CorrelationId)?.NyxRelayAppendStreaming?.InFlightOperation is not null)
                return;
        }
        lifecycle = FindAppendLifecycle(evt.CorrelationId);
        if (lifecycle?.NyxRelayAppendStreaming is not { TerminalReason: NyxRelayAppendTerminalReason.Unspecified })
            return;
        if (Services.GetService<INyxRelayAppendOutboundPort>() is null)
        {
            await ChangeAppendAsync(lifecycle, change => change.AppendProgressState = NyxRelayAppendProgressState.Rejected);
            return;
        }
        await StartAppendOperationAsync(lifecycle, NyxRelayAppendMessageKind.Progress, "正在处理，请稍候...", false);
    }

    private async Task HandleAppendChunkAsync(LlmReplyStreamChunkEvent evt)
    {
        var lifecycle = FindAppendLifecycle(evt.CorrelationId);
        if (lifecycle is null || IsReplyTurnFinalized(evt.CorrelationId) ||
            !string.IsNullOrEmpty(lifecycle.PendingFinalizeCommandId) ||
            lifecycle.NyxRelayAppendStreaming.TerminalReason != NyxRelayAppendTerminalReason.Unspecified)
            return;
        await ChangeAppendAsync(lifecycle, change => change.QueuedAccumulatedText = evt.AccumulatedText);
        await ContinueAppendAsync(evt.CorrelationId);
    }

    private async Task<bool> TryCompleteAppendReplyAsync(LlmReplyReadyEvent evt, string commandId)
    {
        var lifecycle = FindAppendLifecycle(evt.CorrelationId);
        if (lifecycle is null)
            return false;
        var append = lifecycle.NyxRelayAppendStreaming;
        if (append.TerminalReason == NyxRelayAppendTerminalReason.PreDispatchFailure && !append.AnyRequestDispatched)
            return false; // Existing unconsumed terminal reply-token leaf owns this case.
        await ChangeAppendAsync(lifecycle, change =>
        {
            change.FinalizeText = evt.Outbound?.Text ?? string.Empty;
            change.QueuedAccumulatedText = evt.Outbound?.Text ?? string.Empty;
            change.FinalizeCommandId = commandId;
            change.NyxRelayTerminalState = evt.TerminalState;
            change.AppendedHistory.AddRange(evt.AppendedHistory.Select(entry => entry.Clone()));
        });
        await ContinueAppendAsync(evt.CorrelationId);
        return true;
    }

    private async Task ContinueAppendAsync(string correlationId, bool progressDue = false)
    {
        var lifecycle = FindAppendLifecycle(correlationId);
        if (lifecycle is null || IsReplyTurnFinalized(correlationId))
            return;
        var append = lifecycle.NyxRelayAppendStreaming;
        if (append.InFlightOperation is not null)
            return;
        var terminal = !string.IsNullOrEmpty(lifecycle.PendingFinalizeCommandId);
        if (append.TerminalReason != NyxRelayAppendTerminalReason.Unspecified)
        {
            if (terminal)
                await CompleteAppendDeliveryAsync(lifecycle);
            return;
        }
        var text = terminal ? lifecycle.PendingFinalizeText : lifecycle.PendingAccumulatedText;
        if (!text.StartsWith(lifecycle.LastFlushedText, StringComparison.Ordinal))
        {
            await StopAppendAsync(lifecycle, NyxRelayAppendTerminalReason.PrefixRewritten, false);
            if (terminal)
                await CompleteAppendDeliveryAsync(FindAppendLifecycle(correlationId)!);
            return;
        }
        var suffix = text[lifecycle.LastFlushedText.Length..];
        if (suffix.Length == 0)
        {
            if (terminal)
            {
                await ChangeAppendAsync(lifecycle, change => change.AppendDeliveryDisposition = append.AcceptedSegmentCount > 0
                    ? NyxRelayAppendDeliveryDisposition.AllAccepted : NyxRelayAppendDeliveryDisposition.NotSent);
                await CompleteAppendDeliveryAsync(FindAppendLifecycle(correlationId)!);
            }
            return;
        }
        var pending = FindPendingLlmReplyRequest(correlationId);
        var outbound = Services.GetService<INyxRelayAppendOutboundPort>();
        if (pending?.Activity is null || outbound is null)
        {
            await StopAppendAsync(lifecycle, NyxRelayAppendTerminalReason.PreDispatchFailure, false);
            if (terminal)
                await CompleteAppendDeliveryAsync(FindAppendLifecycle(correlationId)!);
            return;
        }
        string segment;
        try
        {
            segment = NyxRelayAppendSegmenter.Select(suffix, append.AcceptedSegmentCount, append.MaxSegmentLength,
                terminal, progressDue,
                raw => outbound.PrepareText(AppendPlatform(pending.Activity), pending.Activity.Conversation ?? new ConversationReference(), raw));
        }
        catch (Exception)
        {
            await StopAppendAsync(lifecycle, NyxRelayAppendTerminalReason.FormattingFailed, false);
            if (terminal)
                await CompleteAppendDeliveryAsync(FindAppendLifecycle(correlationId)!);
            return;
        }
        if (segment.Length == 0)
        {
            if (terminal)
            {
                // Blank text cannot be submitted as a body message. Complete honestly
                // without crediting the unsent suffix or leaving a terminal turn pending.
                await StopAppendAsync(lifecycle, NyxRelayAppendTerminalReason.FormattingFailed, false);
                await CompleteAppendDeliveryAsync(FindAppendLifecycle(correlationId)!);
            }
            return;
        }
        await StartAppendOperationAsync(lifecycle, NyxRelayAppendMessageKind.Content, segment,
            terminal && segment.Length == suffix.Length);
    }

    private async Task StartAppendOperationAsync(
        ConversationReplyLifecycleState lifecycle, NyxRelayAppendMessageKind kind, string text, bool final)
    {
        var append = lifecycle.NyxRelayAppendStreaming;
        var operation = new NyxRelayAppendOperation
        {
            Kind = kind,
            SegmentIndex = kind == NyxRelayAppendMessageKind.Progress ? 0 : append.NextSegmentIndex,
            OperationGeneration = append.OperationGeneration + 1,
            Text = text,
            IsFinal = final,
        };
        await ChangeAppendAsync(lifecycle, change =>
        {
            change.AppendInFlightOperation = operation;
            change.AppendOperationGeneration = operation.OperationGeneration;
            change.AppendOperationStepStarted = false;
            if (kind == NyxRelayAppendMessageKind.Content && append.ProgressState == NyxRelayAppendProgressState.Waiting)
                change.AppendProgressState = NyxRelayAppendProgressState.Skipped;
        });
        var timeout = new NyxRelayAppendOperationTimeoutFiredEvent
        {
            CorrelationId = lifecycle.CorrelationId,
            Kind = operation.Kind,
            SegmentIndex = operation.SegmentIndex,
            OperationGeneration = operation.OperationGeneration,
            FiredAtUnixMs = AppendNow + (long)StreamingFailureUpdateTimeout.TotalMilliseconds,
        };
        await ScheduleSelfDurableTimeoutAsync(
            $"conversation-nyx-relay-append:{lifecycle.CorrelationId}:{operation.OperationGeneration}",
            StreamingFailureUpdateTimeout, timeout);
        _ = PublishAppendSignalAfterDelayAsync(EventPublisher, Id, timeout, StreamingFailureUpdateTimeout,
            AppendTimeProvider, _appendSignalLifetime.Token, Logger);
        await PublishReplyOperationStepAsync(new ReplyOperationStepEvent
        {
            OperationId = $"{lifecycle.CorrelationId}:append:{operation.Kind}:{operation.SegmentIndex}:{operation.OperationGeneration}",
            OperationName = "nyx-relay-append",
            CorrelationId = lifecycle.CorrelationId,
            LeaseEpoch = operation.OperationGeneration,
            NyxRelayAppend = new NyxRelayAppendOperationStepPayload
            {
                Kind = operation.Kind, SegmentIndex = operation.SegmentIndex, OperationGeneration = operation.OperationGeneration,
            },
        }, CancellationToken.None);
    }

    async Task<NyxRelayAppendExecution?> INyxRelayAppendOperationActorContext.ClaimAppendOperationAsync(
        string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct)
    {
        var lifecycle = FindAppendLifecycle(correlationId);
        if (!MatchesAppend(lifecycle, step.Kind, step.SegmentIndex, step.OperationGeneration) ||
            lifecycle!.NyxRelayAppendStreaming.OperationStepStarted)
            return null;
        await ChangeAppendAsync(lifecycle, change => change.AppendOperationStepStarted = true);
        var activity = FindPendingLlmReplyRequest(correlationId)?.Activity;
        return new NyxRelayAppendExecution(activity?.Clone() ?? new ChatActivity(),
            lifecycle.NyxRelayAppendStreaming.InFlightOperation.Text, lifecycle.NyxRelayAppendStreaming.MaxSegmentLength);
    }

    async Task INyxRelayAppendOperationActorContext.MarkAppendDispatchedAsync(
        string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct)
    {
        var lifecycle = FindAppendLifecycle(correlationId);
        if (!MatchesAppend(lifecycle, step.Kind, step.SegmentIndex, step.OperationGeneration))
            throw new InvalidOperationException("append_operation_stale");
        await ChangeAppendAsync(lifecycle!, change => change.AppendAnyRequestDispatched = true);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleNyxRelayAppendOperationCompletedAsync(NyxRelayAppendOperationCompletedEvent evt)
    {
        var lifecycle = FindAppendLifecycle(evt.CorrelationId);
        if (!MatchesAppend(lifecycle, evt.Kind, evt.SegmentIndex, evt.OperationGeneration))
            return;
        var append = lifecycle!.NyxRelayAppendStreaming;
        var operation = append.InFlightOperation.Clone();
        var accepted = evt.State == NyxRelayTextOperationResultState.Succeeded && evt.RequestDispatched;
        var unknown = evt.State is NyxRelayTextOperationResultState.Faulted or NyxRelayTextOperationResultState.Unspecified;
        await ChangeAppendAsync(lifecycle, change =>
        {
            change.AppendInFlightOperationCleared = new Empty();
            change.AppendOperationStepStarted = false;
            if (evt.RequestDispatched)
                change.AppendAnyRequestDispatched = true;
            if (operation.Kind == NyxRelayAppendMessageKind.Progress)
            {
                change.AppendProgressState = accepted ? NyxRelayAppendProgressState.Accepted : unknown
                    ? NyxRelayAppendProgressState.DeliveryUnknown : NyxRelayAppendProgressState.Rejected;
            }
            else if (accepted)
            {
                change.AppendAcceptedSegmentCountDelta = 1;
                change.AppendNextSegmentIndex = operation.SegmentIndex + 1;
                change.AppendLastAcceptedPlatformMessageId = evt.RawResult?.PlatformMessageId ?? string.Empty;
                change.FlushedTextDelta = lifecycle.LastFlushedText + operation.Text;
            }
            else
            {
                change.AppendTerminalReason = unknown ? NyxRelayAppendTerminalReason.DeliveryUnknown : evt.RequestDispatched
                    ? NyxRelayAppendTerminalReason.Rejected : NyxRelayAppendTerminalReason.PreDispatchFailure;
                change.AppendDeliveryDisposition = unknown ? NyxRelayAppendDeliveryDisposition.DeliveryUnknown :
                    append.AcceptedSegmentCount > 0 ? NyxRelayAppendDeliveryDisposition.PartialAccepted : NyxRelayAppendDeliveryDisposition.NotSent;
            }
        });
        LogAppendResult(lifecycle, operation, evt.State, accepted ? string.Empty : unknown ? "append_delivery_unknown" :
            evt.RequestDispatched ? "append_rejected" : "append_pre_dispatch_failure");
        await ContinueAppendAsync(evt.CorrelationId);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleNyxRelayAppendOperationTimeoutFiredAsync(NyxRelayAppendOperationTimeoutFiredEvent evt)
    {
        var lifecycle = FindAppendLifecycle(evt.CorrelationId);
        if (!MatchesAppend(lifecycle, evt.Kind, evt.SegmentIndex, evt.OperationGeneration))
            return;
        await HandleNyxRelayAppendOperationCompletedAsync(new NyxRelayAppendOperationCompletedEvent
        {
            CorrelationId = evt.CorrelationId, Kind = evt.Kind, SegmentIndex = evt.SegmentIndex,
            OperationGeneration = evt.OperationGeneration, RequestDispatched = true,
            State = NyxRelayTextOperationResultState.Faulted,
            RawResult = new NyxRelayTextOperationRawResult { RawErrorCode = "append_delivery_unknown" },
        });
    }

    private static bool MatchesAppend(ConversationReplyLifecycleState? lifecycle,
        NyxRelayAppendMessageKind kind, int index, long generation) =>
        lifecycle?.NyxRelayAppendStreaming?.InFlightOperation is { } operation &&
        operation.Kind == kind && operation.SegmentIndex == index && operation.OperationGeneration == generation;

    private async Task StopAppendAsync(ConversationReplyLifecycleState lifecycle, NyxRelayAppendTerminalReason reason, bool unknown)
    {
        await ChangeAppendAsync(lifecycle, change =>
        {
            change.AppendTerminalReason = reason;
            change.AppendDeliveryDisposition = unknown ? NyxRelayAppendDeliveryDisposition.DeliveryUnknown :
                lifecycle.NyxRelayAppendStreaming.AcceptedSegmentCount > 0
                    ? NyxRelayAppendDeliveryDisposition.PartialAccepted : NyxRelayAppendDeliveryDisposition.NotSent;
        });
        Logger.LogWarning("Nyx relay append stopped: correlation={CorrelationId} strategy={Strategy} reason={Reason} acceptedSegments={AcceptedSegments} disposition={Disposition}",
            lifecycle.CorrelationId, lifecycle.NyxRelayTextDeliveryStrategy, reason,
            lifecycle.NyxRelayAppendStreaming.AcceptedSegmentCount,
            FindAppendLifecycle(lifecycle.CorrelationId)?.NyxRelayAppendStreaming?.DeliveryDisposition);
    }

    private Task ChangeAppendAsync(ConversationReplyLifecycleState lifecycle, Action<ConversationReplyLifecycleChangedEvent> update)
    {
        var change = new ConversationReplyLifecycleChangedEvent
        {
            CorrelationId = lifecycle.CorrelationId, Mode = ConversationReplyLifecycleMode.NyxRelayText,
            PreviousPhase = lifecycle.Phase, Phase = lifecycle.Phase, ChangedAtUnixMs = AppendNow,
        };
        update(change);
        return PersistDomainEventAsync(change);
    }

    private async Task CompleteAppendDeliveryAsync(ConversationReplyLifecycleState lifecycle)
    {
        var pending = FindPendingLlmReplyRequest(lifecycle.CorrelationId);
        var append = lifecycle.NyxRelayAppendStreaming;
        if (append.TerminalReason == NyxRelayAppendTerminalReason.PreDispatchFailure && !append.AnyRequestDispatched)
        {
            // This terminal continuation may follow an asynchronous preflight failure. Re-enter
            // the existing terminal leaf once, with its original credential restoration path.
            var ready = new LlmReplyReadyEvent
            {
                CorrelationId = lifecycle.CorrelationId, RunId = pending?.RunId ?? string.Empty,
                RegistrationId = pending?.RegistrationId ?? string.Empty, Activity = pending?.Activity?.Clone(),
                Outbound = new MessageContent { Text = lifecycle.PendingFinalizeText },
                TerminalState = lifecycle.PendingNyxRelayTerminalState,
            };
            ready.AppendedHistory.AddRange(lifecycle.PendingAppendedHistory.Select(entry => entry.Clone()));
            await HandleLlmReplyReadyAsync(ready);
            return;
        }
        var now = AppendNow;
        var allAccepted = append.DeliveryDisposition == NyxRelayAppendDeliveryDisposition.AllAccepted;
        IMessage delivery = allAccepted
            ? new LlmReplyDeliveredEvent
            {
                CorrelationId = lifecycle.CorrelationId, RunId = pending?.RunId ?? string.Empty,
                AckedAtUnixMs = now, ChannelMessageId = append.LastAcceptedPlatformMessageId,
            }
            : new LlmReplyDeliveryFailedEvent
            {
                CorrelationId = lifecycle.CorrelationId, RunId = pending?.RunId ?? string.Empty, FailedAtUnixMs = now,
                ErrorCode = $"append_{append.DeliveryDisposition}", ErrorMessage = "Relay append content was not fully accepted.",
            };
        var completed = new ConversationTurnCompletedEvent
        {
            CausationCommandId = lifecycle.PendingFinalizeCommandId,
            SentActivityId = append.LastAcceptedPlatformMessageId, AuthPrincipal = "bot",
            Conversation = pending?.Activity?.Conversation?.Clone() ?? State.Conversation?.Clone() ?? new ConversationReference(),
            Outbound = new MessageContent { Text = lifecycle.LastFlushedText }, CompletedAtUnixMs = now,
            OutboundDelivery = ToOutboundDeliveryReceipt(pending?.Activity?.OutboundDelivery),
        };
        // One final assistant entry, containing only the explicitly accepted user-visible prefix.
        // Tool/reasoning entries are never reclassified as delivered assistant content.
        foreach (var entry in lifecycle.PendingAppendedHistory)
        {
            if (!string.Equals(entry.Role, "assistant", StringComparison.OrdinalIgnoreCase) || entry.ToolCalls.Count > 0)
                completed.AppendedHistory.Add(entry.Clone());
        }
        if (lifecycle.LastFlushedText.Length > 0)
            completed.AppendedHistory.Add(new ConversationHistoryEntry { Role = "assistant", Content = lifecycle.LastFlushedText });
        var produced = BuildDeliveryProducedEvent(DeliveryKind.TextMessage,
            allAccepted ? DeliveryStatus.Succeeded : append.AnyRequestDispatched ? DeliveryStatus.FailedPostSend : DeliveryStatus.FailedPreSend,
            pending?.Activity, pending?.RunId, lifecycle.CorrelationId, lifecycle.PendingFinalizeCommandId,
            sourceEventId: lifecycle.CorrelationId, providerMessageId: append.LastAcceptedPlatformMessageId, cardId: string.Empty);
        // Completion and lifecycle cleanup share one commit. Recovery never observes a
        // pending turn whose no-replay fence was cleared before its completed command ID.
        await PersistDomainEventsAsync([delivery, produced, completed, new ConversationReplyLifecycleClearedEvent
        {
            CorrelationId = lifecycle.CorrelationId, Mode = ConversationReplyLifecycleMode.NyxRelayText,
            Reason = "append_completion", ClearedAtUnixMs = now,
        }]);
        Logger.LogInformation("Nyx relay append completed: correlation={CorrelationId} strategy={Strategy} disposition={Disposition} reason={Reason} acceptedSegments={AcceptedSegments}",
            lifecycle.CorrelationId, lifecycle.NyxRelayTextDeliveryStrategy, append.DeliveryDisposition, append.TerminalReason, append.AcceptedSegmentCount);
    }

    private void LogAppendResult(ConversationReplyLifecycleState lifecycle, NyxRelayAppendOperation operation,
        NyxRelayTextOperationResultState result, string errorCode)
    {
        var request = FindPendingLlmReplyRequest(lifecycle.CorrelationId);
        Logger.LogInformation("Nyx relay append operation: registration={RegistrationId} correlation={CorrelationId} platform={Platform} strategy={Strategy} kind={Kind} segment={SegmentIndex} generation={Generation} result={Result} errorCode={ErrorCode} disposition={Disposition}",
            request?.RegistrationId, lifecycle.CorrelationId, AppendPlatform(request?.Activity), lifecycle.NyxRelayTextDeliveryStrategy,
            operation.Kind, operation.SegmentIndex, operation.OperationGeneration, result, errorCode,
            FindAppendLifecycle(lifecycle.CorrelationId)?.NyxRelayAppendStreaming?.DeliveryDisposition);
    }

    private async Task RecoverAppendRepliesAsync()
    {
        _appendSignalLifetime = new CancellationTokenSource();
        foreach (var lifecycle in State.ActiveReplyLifecycles.Where(value =>
                     value.NyxRelayTextDeliveryStrategy == NyxRelayTextDeliveryStrategy.AppendMessages).ToArray())
        {
            var append = lifecycle.NyxRelayAppendStreaming;
            if (append.InFlightOperation is { } operation)
            {
                // Persisted in-flight work is uncertain after a restart, even if the process
                // stopped between reserving the operation and receiving the transport ACK.
                await HandleNyxRelayAppendOperationTimeoutFiredAsync(new NyxRelayAppendOperationTimeoutFiredEvent
                {
                    CorrelationId = lifecycle.CorrelationId, Kind = operation.Kind, SegmentIndex = operation.SegmentIndex,
                    OperationGeneration = operation.OperationGeneration, FiredAtUnixMs = AppendNow,
                });
            }
            else
                await ContinueAppendAsync(lifecycle.CorrelationId);
            if (FindAppendLifecycle(lifecycle.CorrelationId)?.NyxRelayAppendStreaming is { ProgressState: NyxRelayAppendProgressState.Waiting } current)
                await ScheduleAppendProgressAsync(lifecycle.CorrelationId, current.ProgressDueAtUnixMs);
        }
    }

    private static void ApplyAppendTransitionFacts(ConversationReplyLifecycleState lifecycle, ConversationReplyLifecycleChangedEvent evt)
    {
        if (evt.HasNyxRelayTextDeliveryStrategy)
            lifecycle.NyxRelayTextDeliveryStrategy = evt.NyxRelayTextDeliveryStrategy;
        if (lifecycle.NyxRelayTextDeliveryStrategy != NyxRelayTextDeliveryStrategy.AppendMessages)
            return;
        var append = lifecycle.NyxRelayAppendStreaming ??= new NyxRelayAppendStreamingState();
        if (evt.HasAppendNextSegmentIndex) append.NextSegmentIndex = evt.AppendNextSegmentIndex;
        if (evt.HasAppendAcceptedSegmentCountDelta) append.AcceptedSegmentCount += evt.AppendAcceptedSegmentCountDelta;
        if (evt.HasAppendLastAcceptedPlatformMessageId) append.LastAcceptedPlatformMessageId = evt.AppendLastAcceptedPlatformMessageId;
        if (evt.AppendInFlightChangeCase == ConversationReplyLifecycleChangedEvent.AppendInFlightChangeOneofCase.AppendInFlightOperation)
            append.InFlightOperation = evt.AppendInFlightOperation.Clone();
        else if (evt.AppendInFlightChangeCase == ConversationReplyLifecycleChangedEvent.AppendInFlightChangeOneofCase.AppendInFlightOperationCleared)
            append.InFlightOperation = null;
        if (evt.HasAppendOperationGeneration) append.OperationGeneration = evt.AppendOperationGeneration;
        if (evt.HasAppendMaxSegmentLength) append.MaxSegmentLength = evt.AppendMaxSegmentLength;
        if (evt.HasAppendDeliveryDisposition) append.DeliveryDisposition = evt.AppendDeliveryDisposition;
        if (evt.HasAppendProgressState) append.ProgressState = evt.AppendProgressState;
        if (evt.HasAppendTerminalReason) append.TerminalReason = evt.AppendTerminalReason;
        if (evt.HasAppendAnyRequestDispatched) append.AnyRequestDispatched |= evt.AppendAnyRequestDispatched;
        if (evt.HasAppendOperationStepStarted) append.OperationStepStarted = evt.AppendOperationStepStarted;
        if (evt.HasAppendLlmRunDispatched) append.LlmRunDispatched |= evt.AppendLlmRunDispatched;
        if (evt.HasAppendProgressDueAtUnixMs) append.ProgressDueAtUnixMs = evt.AppendProgressDueAtUnixMs;
    }
}
