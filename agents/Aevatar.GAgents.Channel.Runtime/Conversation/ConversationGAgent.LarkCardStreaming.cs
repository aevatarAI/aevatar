using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ConversationGAgent
{
    // Refactor (iter20/cluster-004):
    //   Old pattern: ConversationGAgent 持有 actor token registry + 可见回复状态部分仅在内存
    //   New principle: 删 actor token registry,credentials runtime-only,可见回复 lifecycle 持久到 ConversationGAgent state
    /// <summary>
    /// Per-turn phase of the Lark CardKit streaming pipeline. Distinct from
    /// <see cref="NyxRelayStreamingPhase"/> (which models channel-relay edit-message
    /// streaming): card streaming has its own lifecycle (allocate card entity, bind to
    /// chat, stream element content, close streaming mode) and goes through the API-key
    /// proxy directly rather than channel-relay's <c>/reply{,/update}</c> surface.
    /// </summary>
    /// <remarks>
    /// Fallback semantics: when card creation fails (<see cref="CreationFailed"/>), the
    /// dispatcher routes the turn to the legacy text-edit sink (<c>NyxRelayStreamingPhase</c>
    /// machine). Once <see cref="Streaming"/> is reached, the card path owns the turn —
    /// mid-stream rate-limit / table-limit failures terminate the turn at
    /// <see cref="Terminated"/> with the last flushed text persisted as partial.
    /// </remarks>
    private enum LarkCardStreamingPhase
    {
        Idle,
        Creating,
        Streaming,
        Completed,
        Aborted,
        Terminated,
        CreationFailed,
    }

    private enum LarkCardStreamingGuardSource
    {
        AcceptInterimChunk,
        Finalize,
    }

    /// <summary>
    /// Actor-scoped streaming state for one CardKit-driven turn, backed by
    /// ConversationGAgentState.ActiveReplyLifecycles.
    /// </summary>
    /// <param name="Phase">Lifecycle phase; gates interim updates and finalization.</param>
    /// <param name="CardId">
    /// CardKit card entity id returned by <c>cardkit/v1/cards</c>. Null until
    /// <see cref="LarkCardStreamingPhase.Streaming"/>; required for every element-content
    /// and settings update afterwards.
    /// </param>
    /// <param name="CardMessageId">
    /// Lark IM message id returned by the <c>im/v1/messages</c> send that bound the card
    /// to a chat. Used by the unavailable-guard to detect upstream message recall.
    /// </param>
    /// <param name="OriginalCardId">
    /// Preserved card id for terminal full-card update if mid-stream we fall back to text
    /// patch (table-limit class errors). Currently always equal to <see cref="CardId"/>;
    /// reserved for the mid-stream-fallback follow-up (#589 Scope D).
    /// </param>
    /// <param name="LastFlushedText">
    /// Last text successfully streamed into the card element. Persisted as the user-visible
    /// terminal state when finalization fails after streaming started.
    /// </param>
    /// <param name="Sequence">
    /// Monotonic counter passed to every CardKit write. Pre-incremented before each call;
    /// Lark rejects stale writes deterministically.
    /// </param>
    /// <param name="StreamingElementId">
    /// Element id within the card to stream into. Defaults to <c>streaming_main</c>;
    /// must match the card template's element naming.
    /// </param>
    /// <param name="TerminalReason">Diagnostic reason captured on entry to terminal phases.</param>
    private sealed record LarkCardStreamingState(
        LarkCardStreamingPhase Phase,
        string? CardId,
        string? CardMessageId,
        string? OriginalCardId,
        string LastFlushedText,
        long Sequence,
        string StreamingElementId,
        string? TerminalReason)
    {
        public const string DefaultStreamingElementId = "streaming_main";

        public static LarkCardStreamingState Initial { get; } = new(
            LarkCardStreamingPhase.Idle,
            CardId: null,
            CardMessageId: null,
            OriginalCardId: null,
            LastFlushedText: string.Empty,
            Sequence: 0,
            StreamingElementId: DefaultStreamingElementId,
            TerminalReason: null);

        /// <summary>Phase permits accepting a new chunk (initial or interim).</summary>
        public bool AllowsInterimEdit =>
            Phase is LarkCardStreamingPhase.Idle
                  or LarkCardStreamingPhase.Streaming;

        /// <summary>
        /// Card creation already failed — dispatcher should route subsequent chunks to the
        /// text-edit sink for the rest of this turn.
        /// </summary>
        public bool AllowsTextEditFallback =>
            Phase is LarkCardStreamingPhase.Idle
                  or LarkCardStreamingPhase.CreationFailed;

        /// <summary>Phase permits attempting a finalize (close streaming + optional final update).</summary>
        public bool AllowsFinalize =>
            Phase is LarkCardStreamingPhase.Streaming;
    }

    private static bool IsTerminalLarkCardStreamingPhase(LarkCardStreamingPhase phase) =>
        phase is LarkCardStreamingPhase.Completed
              or LarkCardStreamingPhase.Aborted
              or LarkCardStreamingPhase.Terminated
              or LarkCardStreamingPhase.CreationFailed;

    private static bool IsLegalLarkCardStreamingTransition(LarkCardStreamingPhase from, LarkCardStreamingPhase to) =>
        (from, to) switch
        {
            (LarkCardStreamingPhase.Idle, LarkCardStreamingPhase.Creating) => true,

            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Streaming) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.CreationFailed) => true,
            (LarkCardStreamingPhase.Creating, LarkCardStreamingPhase.Terminated) => true,

            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Streaming) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Completed) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Aborted) => true,
            (LarkCardStreamingPhase.Streaming, LarkCardStreamingPhase.Terminated) => true,

            _ => false,
        };

    // Refactor (iter20/cluster-004):
    //   Old pattern: Card streaming lifecycle lived in a process-memory dictionary beside the reply token registry.
    //   New principle: Rehydrate card lifecycle from actor-owned persisted state; keep credentials runtime-only.
    private LarkCardStreamingState GetOrInitLarkCardStreamingState(string correlationId)
    {
        var lifecycle = FindReplyLifecycle(correlationId, ConversationReplyLifecycleMode.LarkCard);
        if (lifecycle is null)
            return LarkCardStreamingState.Initial;

        return new LarkCardStreamingState(
            ToLarkCardStreamingPhase(lifecycle.Phase),
            NormalizeOptional(lifecycle.CardId),
            NormalizeOptional(lifecycle.CardMessageId),
            NormalizeOptional(lifecycle.OriginalCardId),
            lifecycle.LastFlushedText ?? string.Empty,
            lifecycle.Sequence,
            NormalizeOptional(lifecycle.StreamingElementId) ?? LarkCardStreamingState.DefaultStreamingElementId,
            NormalizeOptional(lifecycle.TerminalReason));
    }

    private static bool ShouldSkipLarkCardStreamingForUnavailable(
        LarkCardStreamingState state,
        LarkCardStreamingGuardSource source) =>
        source switch
        {
            LarkCardStreamingGuardSource.AcceptInterimChunk => !state.AllowsInterimEdit,
            LarkCardStreamingGuardSource.Finalize => !state.AllowsFinalize,
            _ => false,
        };

    // Refactor (iter20/cluster-004):
    //   Old pattern: Card phase transitions updated only process-local state.
    //   New principle: Persist each card lifecycle change through ConversationReplyLifecycleChangedEvent.
    private async Task<LarkCardStreamingState> TransitionLarkCardStreamingPhaseAsync(
        string correlationId,
        LarkCardStreamingState current,
        LarkCardStreamingPhase next,
        string? terminalReason = null,
        Func<LarkCardStreamingState, LarkCardStreamingState>? fieldUpdate = null)
    {
        if (!IsLegalLarkCardStreamingTransition(current.Phase, next))
        {
            Logger.LogWarning(
                "Illegal Lark card streaming phase transition {From}->{To} for correlation={CorrelationId}; keeping current state",
                current.Phase, next, correlationId);
            return current;
        }

        var carried = fieldUpdate?.Invoke(current) ?? current;
        var updated = carried with
        {
            Phase = next,
            TerminalReason = IsTerminalLarkCardStreamingPhase(next)
                ? (terminalReason ?? carried.TerminalReason)
                : carried.TerminalReason,
        };
        await PersistDomainEventAsync(new ConversationReplyLifecycleChangedEvent
        {
            Lifecycle = ToLifecycleState(correlationId, updated),
            ChangedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        return updated;
    }

    private static LarkCardStreamingPhase ToLarkCardStreamingPhase(ConversationReplyLifecyclePhase phase) =>
        phase switch
        {
            ConversationReplyLifecyclePhase.LarkCardCreating => LarkCardStreamingPhase.Creating,
            ConversationReplyLifecyclePhase.LarkCardStreaming => LarkCardStreamingPhase.Streaming,
            ConversationReplyLifecyclePhase.LarkCardCompleted => LarkCardStreamingPhase.Completed,
            ConversationReplyLifecyclePhase.LarkCardAborted => LarkCardStreamingPhase.Aborted,
            ConversationReplyLifecyclePhase.LarkCardTerminated => LarkCardStreamingPhase.Terminated,
            ConversationReplyLifecyclePhase.LarkCardCreationFailed => LarkCardStreamingPhase.CreationFailed,
            _ => LarkCardStreamingPhase.Idle,
        };

    private static ConversationReplyLifecyclePhase ToLifecyclePhase(LarkCardStreamingPhase phase) =>
        phase switch
        {
            LarkCardStreamingPhase.Creating => ConversationReplyLifecyclePhase.LarkCardCreating,
            LarkCardStreamingPhase.Streaming => ConversationReplyLifecyclePhase.LarkCardStreaming,
            LarkCardStreamingPhase.Completed => ConversationReplyLifecyclePhase.LarkCardCompleted,
            LarkCardStreamingPhase.Aborted => ConversationReplyLifecyclePhase.LarkCardAborted,
            LarkCardStreamingPhase.Terminated => ConversationReplyLifecyclePhase.LarkCardTerminated,
            LarkCardStreamingPhase.CreationFailed => ConversationReplyLifecyclePhase.LarkCardCreationFailed,
            _ => ConversationReplyLifecyclePhase.Unspecified,
        };

    private static ConversationReplyLifecycleState ToLifecycleState(
        string correlationId,
        LarkCardStreamingState state) =>
        new()
        {
            CorrelationId = correlationId,
            Mode = ConversationReplyLifecycleMode.LarkCard,
            Phase = ToLifecyclePhase(state.Phase),
            CardId = state.CardId ?? string.Empty,
            CardMessageId = state.CardMessageId ?? string.Empty,
            OriginalCardId = state.OriginalCardId ?? string.Empty,
            LastFlushedText = state.LastFlushedText ?? string.Empty,
            Sequence = state.Sequence,
            StreamingElementId = state.StreamingElementId ?? LarkCardStreamingState.DefaultStreamingElementId,
            TerminalReason = state.TerminalReason ?? string.Empty,
            UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private IConversationCardTurnRunner ResolveCardRunner() =>
        Services.GetService<IConversationCardTurnRunner>() ?? new NullConversationCardTurnRunner();

    /// <summary>
    /// Drives one CardKit-mode streaming chunk. Returns true when the card handler owns the
    /// outcome (Idle->Creating[->Streaming], Streaming->Streaming, terminal-drop) and false
    /// only when the caller should fall through to the legacy text-edit path —
    /// CreationFailed phase signals "card path is dead for this turn, route the rest of the
    /// chunks through edit-message streaming."
    /// </summary>
    private async Task<bool> HandleLarkCardStreamingChunkCoreAsync(
        LlmReplyCardStreamChunkEvent evt,
        string correlationId)
    {
        var state = GetOrInitLarkCardStreamingState(correlationId);

        // Already-decided text-edit fallback: let the caller continue down the text-edit path.
        if (state.Phase is LarkCardStreamingPhase.CreationFailed)
        {
            await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
            return false;
        }

        if (ShouldSkipLarkCardStreamingForUnavailable(state, LarkCardStreamingGuardSource.AcceptInterimChunk))
            return true;

        var runtimeContext = BuildNyxRelayRuntimeContext(
            evt.CorrelationId,
            evt.Activity,
            evt.ReplyToken,
            evt.ReplyTokenExpiresAtUnixMs);
        var runner = ResolveCardRunner();

        if (state.Phase is LarkCardStreamingPhase.Idle)
        {
            await TransitionLarkCardStreamingPhaseAsync(correlationId, state, LarkCardStreamingPhase.Creating);
            var creating = GetOrInitLarkCardStreamingState(correlationId);
            ConversationCardCreateResult createResult;
            try
            {
                // Bound the CardKit create round-trip so a stuck NyxID/Lark upstream can't
                // pin the actor turn forever. Mirrors the text-edit streaming path's
                // per-call cap (StreamingFailureUpdateTimeout); on timeout, the catch
                // below routes the turn to the text-edit fallback path.
                using var createCts = new CancellationTokenSource(StreamingFailureUpdateTimeout);
                createResult = await runner.RunCardCreateAsync(
                    evt,
                    creating.StreamingElementId,
                    runtimeContext,
                    createCts.Token);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Card create threw; falling back to text-edit. correlation={CorrelationId}", evt.CorrelationId);
                await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    creating,
                    LarkCardStreamingPhase.CreationFailed,
                    terminalReason: $"create_threw:{ex.GetType().Name}");
                return false;
            }

            if (!createResult.Success)
            {
                if (createResult.IsPostSendFailure)
                {
                    // Card was already sent to the chat — falling back to text-edit would
                    // produce a duplicate visible reply. Terminate the turn at Terminated and
                    // persist a partial-card record using the orphan card_message_id so the
                    // event store has a terminal entry. The runner has already attempted a
                    // best-effort streaming-mode close on the orphan card.
                    Logger.LogWarning(
                        "Card post-send failure (create+send succeeded, first stream failed); terminating turn without text-edit fallback. correlation={CorrelationId}, code={ErrorCode}, cardId={CardId}",
                        evt.CorrelationId,
                        createResult.ErrorCode,
                        createResult.CardId);
                    var terminated = await TransitionLarkCardStreamingPhaseAsync(
                        correlationId,
                        creating,
                        LarkCardStreamingPhase.Terminated,
                        terminalReason: $"create_post_send_failed:{createResult.ErrorCode}",
                        fieldUpdate: s => s with
                        {
                            CardId = createResult.CardId,
                            CardMessageId = createResult.CardMessageId,
                            OriginalCardId = createResult.CardId,
                        });
                    await PersistCardStreamedCompletionAsync(
                        correlationId,
                        BuildLlmReplyCommandId(evt.CorrelationId),
                        evt.Activity,
                        terminated.CardMessageId ?? string.Empty,
                        terminated.LastFlushedText);
                    return true;
                }

                Logger.LogInformation(
                    "Card create failed; falling back to text-edit for the rest of this turn. correlation={CorrelationId}, code={ErrorCode}, rateLimited={RateLimited}, tableLimit={TableLimit}, cardUnavailable={CardUnavailable}",
                    evt.CorrelationId,
                    createResult.ErrorCode,
                    createResult.IsRateLimited,
                    createResult.IsTableLimitExceeded,
                    createResult.IsCardUnavailable);
                await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    creating,
                    LarkCardStreamingPhase.CreationFailed,
                    terminalReason: $"create_failed:{createResult.ErrorCode}");
                return false;
            }

            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                creating,
                LarkCardStreamingPhase.Streaming,
                fieldUpdate: s => s with
                {
                    CardId = createResult.CardId,
                    CardMessageId = createResult.CardMessageId,
                    OriginalCardId = createResult.CardId,
                    LastFlushedText = evt.AccumulatedText,
                    Sequence = 1,
                });
            return true;
        }

        // Streaming: interim element-content update. Sequence pre-incremented; on success
        // record the new sequence + last-flushed text so finalize knows whether to write.
        var nextSequence = state.Sequence + 1;
        ConversationCardStreamResult streamResult;
        try
        {
            // Per-frame cap so a hung CardKit update can't pin the actor turn forever.
            // On timeout the frame is dropped and the next chunk will retry the slot.
            using var streamCts = new CancellationTokenSource(StreamingFailureUpdateTimeout);
            streamResult = await runner.RunCardStreamAsync(
                evt,
                state.CardId ?? string.Empty,
                state.StreamingElementId,
                nextSequence,
                runtimeContext,
                streamCts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Card stream threw; dropping frame. correlation={CorrelationId}, seq={Sequence}", evt.CorrelationId, nextSequence);
            return true;
        }

        if (!streamResult.Success)
        {
            if (streamResult.IsRateLimited)
            {
                // Recoverable: skip the frame, keep sequence unchanged so the next chunk
                // re-uses this slot.
                Logger.LogDebug(
                    "Card stream rate-limited; dropping frame. correlation={CorrelationId}, seq={Sequence}",
                    evt.CorrelationId, nextSequence);
                return true;
            }
            if (streamResult.IsTableLimitExceeded || streamResult.IsCardUnavailable)
            {
                Logger.LogWarning(
                    "Card stream terminal failure; ending turn. correlation={CorrelationId}, code={ErrorCode}",
                    evt.CorrelationId, streamResult.ErrorCode);
                var terminated = await TransitionLarkCardStreamingPhaseAsync(
                    correlationId,
                    state,
                    LarkCardStreamingPhase.Terminated,
                    terminalReason: $"stream_failed:{streamResult.ErrorCode}");
                // Persist the partial-card terminal record so the event store records the
                // turn even though LlmReplyReady has not arrived yet. Without this the
                // ProcessedCommandIds guard in HandleLlmReplyReadyAsync would still see no
                // matching entry, fall through to the legacy reply path, and post a
                // duplicate text reply on top of the visible card.
                await PersistCardStreamedCompletionAsync(
                    correlationId,
                    BuildLlmReplyCommandId(evt.CorrelationId),
                    evt.Activity,
                    terminated.CardMessageId ?? string.Empty,
                    terminated.LastFlushedText);
                return true;
            }
            Logger.LogInformation(
                "Card stream non-terminal failure; continuing. correlation={CorrelationId}, code={ErrorCode}",
                evt.CorrelationId, streamResult.ErrorCode);
            return true;
        }

        await TransitionLarkCardStreamingPhaseAsync(
            correlationId,
            state,
            LarkCardStreamingPhase.Streaming,
            fieldUpdate: s => s with
            {
                LastFlushedText = evt.AccumulatedText,
                Sequence = nextSequence,
            });
        return true;
    }

    /// <summary>
    /// Drives the card-mode finalize when <see cref="TryCompleteStreamedReplyAsync"/> sees a
    /// live Streaming phase. Persists a <c>ConversationTurnCompletedEvent</c> with
    /// <c>SentActivityId="lark-card-stream:{cardMessageId}"</c> so observers can distinguish
    /// the card path from the legacy <c>nyx-relay-stream:</c> path.
    /// </summary>
    private async Task<bool> TryCompleteCardStreamedReplyAsync(
        LlmReplyReadyEvent evt,
        string correlationId,
        string commandId,
        ChatActivity? referenceActivity)
    {
        var state = GetOrInitLarkCardStreamingState(correlationId);
        // Idle: card path was never started for this turn (or already cleaned up); let the
        // legacy edit-message finalize path handle it. CreationFailed: card create rejected
        // pre-send, which already routed the chunks to the text-edit sink, so the text-edit
        // finalize must run too. Both → return false to fall through.
        if (state.Phase is LarkCardStreamingPhase.Idle)
            return false;
        if (state.Phase is LarkCardStreamingPhase.CreationFailed)
        {
            await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
            return false;
        }

        // Already-terminal card phase (post-send-failure, mid-stream rate/unavailable, or
        // a previous finalize): persistence already happened at the transition site, so
        // simply consume the ready event without running text-edit finalize. The
        // ProcessedCommandIds guard in HandleLlmReplyReadyAsync also short-circuits late
        // ready events, but returning true here keeps the contract explicit.
        if (state.Phase is LarkCardStreamingPhase.Completed
                       or LarkCardStreamingPhase.Aborted
                       or LarkCardStreamingPhase.Terminated)
        {
            await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
            return true;
        }

        // Phase is Streaming or Creating. Creating during finalize is unexpected (card.create
        // is synchronous within a single chunk's handler); treat it as Streaming with no
        // prior interim text. Anything else falls through to text-edit, but the explicit
        // guards above mean we only reach this point with phase=Streaming/Creating.
        var finalText = evt.Outbound?.Text ?? string.Empty;
        var finalDiffers = !string.IsNullOrWhiteSpace(finalText)
            && !string.Equals(finalText, state.LastFlushedText, StringComparison.Ordinal);

        var runtimeContext = BuildNyxRelayRuntimeContext(
            evt.CorrelationId,
            evt.Activity,
            evt.ReplyToken,
            evt.ReplyTokenExpiresAtUnixMs);
        var runner = ResolveCardRunner();
        var nextSequence = state.Sequence + 1;
        var activityForToken = referenceActivity ?? evt.Activity ?? new ChatActivity();

        ConversationCardFinalizeResult finalizeResult;
        try
        {
            // Per-call cap so a hung CardKit finalize can't pin the actor turn forever.
            // On timeout the catch below persists the last-flushed partial and transitions
            // to Terminated, matching the existing finalize-throw recovery.
            using var finalizeCts = new CancellationTokenSource(StreamingFailureUpdateTimeout);
            finalizeResult = await runner.RunCardFinalizeAsync(
                activityForToken,
                state.CardId ?? string.Empty,
                state.StreamingElementId,
                finalText,
                finalDiffers,
                nextSequence,
                runtimeContext,
                finalizeCts.Token);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Card finalize threw; persisting last flushed partial. correlation={CorrelationId}", evt.CorrelationId);
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Terminated,
                terminalReason: $"finalize_threw:{ex.GetType().Name}");
            await PersistCardStreamedCompletionAsync(
                correlationId,
                commandId,
                evt.Activity,
                state.CardMessageId ?? string.Empty,
                state.LastFlushedText);
            return true;
        }

        // visibleText must match what the user actually sees on the card. Two failure modes:
        //   * Final stream write failed                  → card shows LastFlushedText
        //   * Final stream succeeded but close-streaming failed → card shows finalText, just
        //     with a still-blinking cursor. Persist finalText so the durable record agrees
        //     with the visible state.
        var visibleText = finalizeResult.FinalTextWritten ? finalText : state.LastFlushedText;
        if (finalizeResult.Success)
        {
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Completed,
                terminalReason: "completed");
        }
        else
        {
            Logger.LogWarning(
                "Card finalize failed; persisting partial. correlation={CorrelationId}, code={ErrorCode}",
                evt.CorrelationId, finalizeResult.ErrorCode);
            await TransitionLarkCardStreamingPhaseAsync(
                correlationId,
                state,
                LarkCardStreamingPhase.Terminated,
                terminalReason: $"finalize_failed:{finalizeResult.ErrorCode}");
        }

        await PersistCardStreamedCompletionAsync(
            correlationId,
            commandId,
            evt.Activity,
            state.CardMessageId ?? string.Empty,
            visibleText);
        return true;
    }

    /// <summary>
    /// Persists the terminal <c>ConversationTurnCompletedEvent</c> for a card-streamed turn.
    /// Decoupled from the inbound event type so both the LlmReplyReady finalize path and the
    /// mid-stream Terminated path (post-send-failure / table-limit / unavailable, observed
    /// while still processing chunks) can share one writer.
    /// </summary>
    // Refactor (iter20/cluster-004):
    //   Old pattern: Card completion removed process-local token/card registries after writing only completion.
    //   New principle: Persist delivered + completion facts and clear the actor-owned lifecycle state explicitly.
    private async Task PersistCardStreamedCompletionAsync(
        string correlationId,
        string commandId,
        ChatActivity? eventActivity,
        string cardMessageId,
        string outboundText)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var completed = new ConversationTurnCompletedEvent
        {
            ProcessedActivityId = string.Empty,
            CausationCommandId = commandId,
            SentActivityId = $"lark-card-stream:{cardMessageId}",
            AuthPrincipal = "bot",
            Conversation = eventActivity?.Conversation?.Clone()
                           ?? State.Conversation?.Clone()
                           ?? new ConversationReference(),
            Outbound = new MessageContent { Text = outboundText },
            CompletedAtUnixMs = nowMs,
            OutboundDelivery = ToOutboundDeliveryReceipt(eventActivity?.OutboundDelivery),
        };
        var delivered = new LlmReplyDeliveredEvent
        {
            CorrelationId = correlationId,
            RunId = correlationId,
            AckedAtUnixMs = nowMs,
            ChannelMessageId = $"lark-card-stream:{cardMessageId}",
        };
        await PersistDomainEventAsync(delivered);
        await ClearReplyLifecycleAsync(correlationId, ConversationReplyLifecycleMode.LarkCard, "card_fallback_or_terminal");
        await PersistDomainEventAsync(completed);
        Logger.LogInformation(
            "Completed card-streamed LLM reply: correlation={CorrelationId} cardMessageId={CardMessageId} conversation={Key}",
            correlationId,
            cardMessageId,
            completed.Conversation?.CanonicalKey);
    }
}
