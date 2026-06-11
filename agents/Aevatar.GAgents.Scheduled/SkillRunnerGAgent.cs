using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter1/cluster-001):
//   Old pattern: SkillRunnerGAgent pushed execution summaries into the well-known catalog actor.
//   New principle: Runner-owned committed events are the execution fact source for catalog projection.
public sealed class SkillRunnerGAgent : AIGAgentBase<SkillRunnerState>
{
    private readonly NyxIdApiClient? _nyxIdApiClient;
    private readonly ILarkOutboundDispatcher? _larkOutboundDispatcher;
    private readonly IOwnerLlmConfigSource? _ownerLlmConfigSource;
    private readonly IClock _clock;
    private readonly ITimeZoneResolver _timeZoneResolver;
    // Per-run counter for nyxid_proxy outcomes, populated by the instance-owned
    // NyxIdProxyToolFailureCountingMiddleware appended to the tool-call middleware chain.
    // The runner reads it after each ChatStreamAsync to enforce the safety net for issue
    // #439 — see EnsureToolStatusAllowsCompletion.
    private readonly SkillRunnerToolFailureCounter _toolFailureCounter;
    private ChannelScheduleRunner? _scheduler;
    private Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease? _retryLease;

    public SkillRunnerGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        NyxIdApiClient? nyxIdApiClient = null,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        IToolApprovalHandler? approvalHandler = null,
        IClock? clock = null,
        ITimeZoneResolver? timeZoneResolver = null,
        ILarkOutboundDispatcher? larkOutboundDispatcher = null)
        : this(
            BuildToolMiddlewareChain(toolMiddlewares),
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            llmMiddlewares,
            toolSources,
            nyxIdApiClient,
            ownerLlmConfigSource,
            approvalHandler,
            clock,
            timeZoneResolver,
            larkOutboundDispatcher)
    {
    }

    private SkillRunnerGAgent(
        ToolMiddlewareChain toolMiddlewareChain,
        ILLMProviderFactory? llmProviderFactory,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares,
        IEnumerable<IAgentToolSource>? toolSources,
        NyxIdApiClient? nyxIdApiClient,
        IOwnerLlmConfigSource? ownerLlmConfigSource,
        IToolApprovalHandler? approvalHandler,
        IClock? clock,
        ITimeZoneResolver? timeZoneResolver,
        ILarkOutboundDispatcher? larkOutboundDispatcher)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewareChain.Middlewares,
            llmMiddlewares,
            toolSources,
            approvalHandler)
    {
        _nyxIdApiClient = nyxIdApiClient;
        _larkOutboundDispatcher = larkOutboundDispatcher;
        _ownerLlmConfigSource = ownerLlmConfigSource;
        _clock = clock ?? new SystemClock();
        _timeZoneResolver = timeZoneResolver ?? new TimeZoneResolver();
        _toolFailureCounter = toolMiddlewareChain.Counter;
    }

    private readonly record struct ToolMiddlewareChain(
        IReadOnlyList<IToolCallMiddleware> Middlewares,
        SkillRunnerToolFailureCounter Counter);

    /// <summary>Test-only accessor for the per-run nyxid_proxy counter.</summary>
    internal SkillRunnerToolFailureCounter ToolFailureCounterForTesting => _toolFailureCounter;

    private static ToolMiddlewareChain BuildToolMiddlewareChain(
        IEnumerable<IToolCallMiddleware>? input)
    {
        var counter = new SkillRunnerToolFailureCounter();
        var combined = (input ?? Array.Empty<IToolCallMiddleware>()).ToList();
        combined.Add(new NyxIdProxyToolFailureCountingMiddleware(counter));
        return new ToolMiddlewareChain(combined, counter);
    }

    private ChannelScheduleRunner Scheduler => _scheduler ??= new ChannelScheduleRunner(
        callbackId: SkillRunnerDefaults.TriggerCallbackId,
        schedulableSource: () => State,
        triggerFactory: () => new TriggerSkillRunnerExecutionCommand { Reason = "schedule" },
        persistNextRunEventAsync: nextRunUtc => PersistDomainEventAsync(new SkillRunnerNextRunScheduledEvent
        {
            NextRunAt = Timestamp.FromDateTimeOffset(nextRunUtc),
        }),
        scheduleTimeoutAsync: (id, dueTime, evt, ct) => ScheduleSelfDurableTimeoutAsync(id, dueTime, evt, ct: ct),
        cancelCallbackAsync: (lease, ct) => CancelDurableCallbackAsync(lease, ct),
        clock: _clock,
        timeZoneResolver: _timeZoneResolver,
        logger: Logger,
        ownerDescription: $"Skill runner {Id}");

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await Scheduler.BootstrapOnActivateAsync(ct);
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(SkillRunnerState state)
    {
        return new AIAgentConfigStateOverrides
        {
            HasProviderName = !string.IsNullOrWhiteSpace(state.ProviderName),
            ProviderName = state.ProviderName,
            HasModel = !string.IsNullOrWhiteSpace(state.Model),
            Model = state.Model,
            HasSystemPrompt = !string.IsNullOrWhiteSpace(state.SkillContent),
            SystemPrompt = state.SkillContent,
            HasTemperature = state.HasTemperature,
            Temperature = state.HasTemperature ? state.Temperature : null,
            HasMaxTokens = state.HasMaxTokens,
            MaxTokens = state.HasMaxTokens ? state.MaxTokens : null,
            HasMaxToolRounds = state.HasMaxToolRounds,
            MaxToolRounds = state.HasMaxToolRounds ? state.MaxToolRounds : null,
            HasMaxHistoryMessages = state.HasMaxHistoryMessages,
            MaxHistoryMessages = state.HasMaxHistoryMessages ? state.MaxHistoryMessages : null,
        };
    }

    protected override SkillRunnerState TransitionState(SkillRunnerState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<SkillRunnerInitializedEvent>(ApplyInitialized)
            .On<SkillRunnerNextRunScheduledEvent>(ApplyNextRunScheduled)
            .On<SkillRunnerExecutionCompletedEvent>(ApplyCompleted)
            .On<SkillRunnerExecutionFailedEvent>(ApplyFailed)
            .On<SkillRunnerExecutionRejectedEvent>(ApplyRejected)
            .On<SkillRunnerDisabledEvent>(ApplyDisabled)
            .On<SkillRunnerEnabledEvent>(ApplyEnabled)
            .OrCurrent();

    [EventHandler]
    public async Task HandleInitializeAsync(InitializeSkillRunnerCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SkillContent))
        {
            Logger.LogWarning("Skill runner {ActorId} initialization ignored because skill_content is empty", Id);
            return;
        }

        var initialized = new SkillRunnerInitializedEvent
        {
            SkillName = command.SkillName?.Trim() ?? string.Empty,
            TemplateName = command.TemplateName?.Trim() ?? string.Empty,
            SkillContent = command.SkillContent,
            ExecutionPrompt = command.ExecutionPrompt?.Trim() ?? string.Empty,
            ScheduleCron = command.ScheduleCron?.Trim() ?? string.Empty,
            ScheduleTimezone = NormalizeTimezone(command.ScheduleTimezone),
            OutboundConfig = command.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig(),
            Enabled = command.Enabled,
            ScopeId = command.ScopeId?.Trim() ?? string.Empty,
            ProviderName = NormalizeProviderName(command.ProviderName),
            Model = command.Model?.Trim() ?? string.Empty,
            RequiresNyxidProxySuccess = command.RequiresNyxidProxySuccess,
        };

        if (command.HasTemperature)
            initialized.Temperature = command.Temperature;
        if (command.HasMaxTokens)
            initialized.MaxTokens = command.MaxTokens;
        if (command.HasMaxToolRounds)
            initialized.MaxToolRounds = command.MaxToolRounds;
        if (command.HasMaxHistoryMessages)
            initialized.MaxHistoryMessages = command.MaxHistoryMessages;

        await PersistDomainEventAsync(initialized);

        // Refactor (iter89/cluster-089-scheduled-runner-wall-clock):
        //   Old: SkillRunnerGAgent sampled DateTimeOffset.UtcNow and cron helper resolved timezone inline.
        //   New: ChannelScheduleRunner owns injected clock/timezone dependencies and samples once for this turn.
        await Scheduler.ScheduleNextRunAsync(CancellationToken.None);
        await UpsertRegistryAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleTriggerAsync(TriggerSkillRunnerExecutionCommand command)
    {
        if (!State.Enabled)
        {
            Logger.LogInformation("Skill runner {ActorId} ignored trigger because it is disabled", Id);
            await PersistDomainEventAsync(new SkillRunnerExecutionRejectedEvent
            {
                RejectedAt = Timestamp.FromDateTimeOffset(_clock.UtcNow),
                Reason = SkillRunnerDefaults.RejectionReasonRunnerDisabled,
            });
            return;
        }

        var now = _clock.UtcNow;
        try
        {
            var output = await ExecuteSkillAsync(now, command.Reason, CancellationToken.None);
            // Streaming-edit delivery happens in-line during ExecuteSkillAsync via the
            // SkillRunnerStreamingReplySink (POST initial + PUT each delta — Lark's text-edit
            // verb; PATCH on the same path is reserved for cards). When streaming can't be
            // configured (no NyxID client, missing outbound config) ExecuteSkillAsync falls
            // back to a one-shot SendOutputAsync at finalize, so we never need a second
            // outbound call here. Persist the run as completed using the buffered final text.
            await PersistDomainEventAsync(new SkillRunnerExecutionCompletedEvent
            {
                CompletedAt = Timestamp.FromDateTimeOffset(now),
                Output = output,
            });

            await CancelRetryLeaseAsync(CancellationToken.None);
            await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Skill runner {ActorId} execution failed (attempt={Attempt})",
                Id,
                command.RetryAttempt);

            if (command.RetryAttempt < SkillRunnerDefaults.MaxRetryAttempts)
            {
                await ScheduleRetryAsync(command.RetryAttempt + 1, CancellationToken.None);
                return;
            }

            await PersistDomainEventAsync(new SkillRunnerExecutionFailedEvent
            {
                FailedAt = Timestamp.FromDateTimeOffset(now),
                Error = ex.Message,
            });

            await TrySendFailureAsync(ex.Message, CancellationToken.None);
            await Scheduler.ScheduleNextRunAsync(now, CancellationToken.None);
        }
    }

    private async Task ScheduleRetryAsync(int retryAttempt, CancellationToken ct)
    {
        await CancelRetryLeaseAsync(ct);
        _retryLease = await ScheduleSelfDurableTimeoutAsync(
            SkillRunnerDefaults.RetryCallbackId,
            SkillRunnerDefaults.RetryBackoff,
            new TriggerSkillRunnerExecutionCommand { Reason = "retry", RetryAttempt = retryAttempt },
            ct: ct);
        Logger.LogInformation(
            "Skill runner {ActorId} scheduled retry attempt {Attempt} in {Backoff}",
            Id, retryAttempt, SkillRunnerDefaults.RetryBackoff);
    }

    private async Task CancelRetryLeaseAsync(CancellationToken ct)
    {
        if (_retryLease == null)
            return;
        await CancelDurableCallbackAsync(_retryLease, ct);
        _retryLease = null;
    }

    [EventHandler]
    public async Task HandleDisableAsync(DisableSkillRunnerCommand command)
    {
        await Scheduler.CancelAsync(CancellationToken.None);
        await CancelRetryLeaseAsync(CancellationToken.None);

        await PersistDomainEventAsync(new SkillRunnerDisabledEvent
        {
            Reason = command.Reason?.Trim() ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleEnableAsync(EnableSkillRunnerCommand command)
    {
        if (!State.Enabled)
        {
            await PersistDomainEventAsync(new SkillRunnerEnabledEvent
            {
                Reason = command.Reason?.Trim() ?? string.Empty,
            });
        }

        await Scheduler.ScheduleNextRunAsync(CancellationToken.None);
    }

    private async Task<string> ExecuteSkillAsync(DateTimeOffset now, string? reason, CancellationToken ct)
    {
        // Reset before each run so retries / scheduled triggers each see a clean slate.
        // The counter is populated by NyxIdProxyToolFailureCountingMiddleware as the LLM
        // fans out nyxid_proxy calls inside the ChatStreamAsync loop.
        _toolFailureCounter.Reset();

        var prompt = BuildExecutionPrompt(now, reason);
        var metadata = await BuildExecutionMetadataAsync(ct);
        var llmControl = await BuildExecutionLlmControlAsync(ct);
        var requestId = Guid.NewGuid().ToString("N");
        var toolContext = llmControl.ToToolContext(BuildExecutionToolContext(requestId, metadata));
        var content = new StringBuilder();

        var sink = TryCreateStreamingSink();
        var streamingState = sink is null
            ? null
            : new SkillRunnerStreamingRunState(sink, SkillRunnerDefaults.StreamingEditThrottle, TimeProvider.System);
        try
        {
            await foreach (var chunk in ChatStreamAsync(
                               [ContentPart.TextPart(prompt)],
                               requestId,
                               llmControl,
                               toolContext,
                               metadata,
                               ct))
            {
                if (string.IsNullOrEmpty(chunk.DeltaContent))
                    continue;
                content.Append(chunk.DeltaContent);
                if (streamingState is not null)
                    // Per-delta `content.ToString()` is O(n) per call → O(n²) for the whole
                    // turn. Acceptable for bounded skill output (≤30 KB capped, and the
                    // actor-owned streaming state dedupes against `_lastEmittedText` so most allocations don't even
                    // make it onto the wire). If a future skill produces materially longer
                    // output, switch the sink contract to `(StringBuilder, Range)` snapshots
                    // or a `ReadOnlyMemory<char>` view so the accumulator isn't re-stringified
                    // every delta.
                    await streamingState.OnDeltaAsync(content.ToString(), ct);
            }

            var output = content.ToString().Trim();
            if (string.IsNullOrWhiteSpace(output))
                output = "No update generated.";

            // Issue #439 safety net (PR #471 + this PR): refuse to record fake-success runs.
            // Two failure modes are caught here:
            //   * all-fail — every nyxid_proxy call failed, the LLM's plain-text output is
            //     structurally indistinguishable from a real "no activity" report;
            //   * never-called — when State.RequiresNyxidProxySuccess is set, a run that
            //     completes with zero successful nyxid_proxy calls means the LLM bypassed
            //     tools entirely and produced text from prior context (the original #439
            //     symptom: 52 commits in 24h reported as "No meaningful public GitHub
            //     activity"). The original safety net only covered the all-fail case
            //     (failureCount > 0); this gap was flagged in PR #471 review and is closed
            //     here for fetch-and-summarize templates that opt in.
            // Throw before delivery so HandleTriggerAsync's catch path persists
            // SkillRunnerExecutionFailedEvent instead of a clean SkillRunnerExecutionCompletedEvent —
            // must fire BEFORE chunked dispatch so we don't post part-1 of a report
            // we're about to flag as failed.
            EnsureToolStatusAllowsCompletion(
                _toolFailureCounter.FailureCount,
                _toolFailureCounter.SuccessCount,
                State.RequiresNyxidProxySuccess);

            // Issue #423 §C — chunked delivery for outputs that exceed the Lark body cap.
            // For ≤30 KB outputs the chunker returns a single-element list and the dispatch
            // loop below collapses to the existing single-message path. Larger outputs split
            // at `\n\n` boundaries (or hard-split for no-paragraph inputs) so the full report
            // lands as a sequence of "[part k/N]" messages instead of being silently truncated
            // at the cap.
            var chunks = SkillRunnerOutputChunker.Split(output);
            await DispatchOutputChunksAsync(streamingState, chunks, ct);

            return output;
        }
        finally
        {
            sink?.Dispose();
        }
    }

    /// <summary>
    /// Sends the chunk sequence produced by <see cref="SkillRunnerOutputChunker.Split"/>:
    /// chunk[0] flows through the streaming-edit sink (so the in-flight message the user has
    /// been watching grow lands cleanly as "part 1"); chunks 1..N go as fresh one-shot POSTs
    /// through the primary <see cref="SendOutputAsync(string, CancellationToken)"/> path
    /// since edit-in-place only applies to the message captured at sink-creation time.
    /// </summary>
    /// <remarks>
    /// Failure semantics match the pre-chunking single-message path: any send rejection
    /// throws and propagates to <c>HandleTriggerAsync</c>'s retry/persist contract. A failure
    /// on chunk N &gt; 0 means chunks 0..N-1 already landed in chat — that's intentional
    /// partial visibility. Atomic multi-message delivery would require either a Lark-side
    /// transactional API (none exists) or buffering until all chunks succeed (loses the
    /// streaming-edit UX), neither of which is worth the complexity here.
    /// </remarks>
    private async Task DispatchOutputChunksAsync(
        SkillRunnerStreamingRunState? streamingState,
        IReadOnlyList<string> chunks,
        CancellationToken ct)
    {
        if (chunks.Count == 0)
            return;

        if (streamingState is not null)
            await streamingState.FinalizeAsync(chunks[0], ct);
        else
            // No streaming sink (no NyxID client, missing outbound config, or tests injecting
            // a null client). Fall back to a one-shot send so the user still receives the
            // report and tests that don't construct a sink keep working.
            await SendOutputAsync(chunks[0], ct);

        for (var i = 1; i < chunks.Count; i++)
            await SendOutputAsync(chunks[i], ct);
    }

    /// <summary>
    /// Constructs the streaming-edit sink for this run, or returns null when streaming cannot be
    /// configured. The sink writes the first non-empty delta as a Lark
    /// <c>POST /open-apis/im/v1/messages</c> (capturing the returned <c>message_id</c>) and edits
    /// the same message via <c>PUT /open-apis/im/v1/messages/{id}</c> for every later delta —
    /// so the user sees the skill output land and grow in place rather than receiving one wall of
    /// text after the LLM finishes. PUT is the correct verb for editing text/post messages;
    /// <c>PATCH</c> on the same path is reserved for editing interactive cards (see
    /// <c>SkillRunnerStreamingReplySink.EditAsync</c> for the verb-split rationale).
    /// </summary>
    private SkillRunnerStreamingReplySink? TryCreateStreamingSink()
    {
        // Issue #439: when the run
        // is gated by EnsureToolStatusAllowsCompletion (RequiresNyxidProxySuccess set),
        // streaming each delta would POST/PUT the partial text to Lark live — i.e. a
        // hallucinated report would already be visible in the user's DM by the
        // time the guard fires, and each retry would repost it. Disable live streaming
        // for those skills so the message only POSTs through the chunked-dispatch path
        // AFTER the guard has confirmed at least one nyxid_proxy success. Trade-off: the
        // user no longer sees the report grow live, but output integrity wins over the
        // streaming-edit UX for fetch-and-summarize skills.
        if (State.RequiresNyxidProxySuccess)
            return null;

        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            // Tests and very early bootstrap can run without an injected NyxID client; falling
            // back to one-shot SendOutputAsync is correct, but a log line makes the degradation
            // visible (otherwise streaming-edit silently never engages and the only symptom is
            // the wall-of-text UX users complained about in #423).
            Logger.LogWarning(
                "Skill runner {ActorId} has no NyxIdApiClient registered; streaming-edit delivery is disabled, falling back to one-shot SendOutputAsync.",
                Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxApiKey) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning(
                "Skill runner {ActorId} has incomplete outbound config (NyxApiKey/NyxProviderSlug/ConversationId); streaming-edit delivery is disabled, falling back to one-shot SendOutputAsync.",
                Id);
            return null;
        }

        var primary = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);

        return new SkillRunnerStreamingReplySink(
            ResolveLarkOutboundDispatcher(client),
            new LarkSendNewMessageRequest(
                State.OutboundConfig.NyxApiKey,
                State.OutboundConfig.NyxProviderSlug,
                MessageType: "text",
                ContentJson: string.Empty,
                PrimaryTarget: primary,
                FallbackTarget: ResolveFallbackTarget()),
            BuildLarkRejectionMessage,
            Logger,
            client);
    }

    /// <summary>
    /// Actor-owned coalescing state for one scheduled output stream.
    /// </summary>
    /// <remarks>
    /// Refactor (iter15/cluster-027-streaming-reply-timer-business-dispatch):
    ///   Old pattern: timer callback directly inspected/mutated pending output and performed Lark POST/PUT from callback timing
    ///   New principle: ExecuteSkillAsync owns throttle state, emitted text, and final dispatch ordering before calling the Lark transport sink.
    /// </remarks>
    private sealed class SkillRunnerStreamingRunState
    {
        private readonly SkillRunnerStreamingReplySink _sink;
        private readonly TimeSpan _throttle;
        private readonly TimeProvider _timeProvider;
        private string _lastEmittedText = string.Empty;
        private DateTimeOffset _lastEmitAt = DateTimeOffset.MinValue;
        private int _chunksEmitted;
        private string _pendingText = string.Empty;

        public SkillRunnerStreamingRunState(
            SkillRunnerStreamingReplySink sink,
            TimeSpan throttle,
            TimeProvider timeProvider)
        {
            _sink = sink;
            _throttle = throttle < TimeSpan.Zero ? TimeSpan.Zero : throttle;
            _timeProvider = timeProvider;
        }

        public Task OnDeltaAsync(string accumulatedText, CancellationToken ct) =>
            TryDispatchAsync(accumulatedText, isFinal: false, ct);

        public Task FinalizeAsync(string finalText, CancellationToken ct) =>
            TryDispatchAsync(finalText, isFinal: true, ct);

        private async Task TryDispatchAsync(string text, bool isFinal, CancellationToken ct)
        {
            var capped = SkillRunnerStreamingReplySink.TruncateForLark(text);
            if (string.IsNullOrWhiteSpace(capped))
                return;

            if (string.Equals(capped, _lastEmittedText, StringComparison.Ordinal))
            {
                if (isFinal || string.Equals(capped, _pendingText, StringComparison.Ordinal))
                    ClearPending();
                return;
            }

            if (!isFinal)
            {
                var elapsed = _timeProvider.GetUtcNow() - _lastEmitAt;
                if (elapsed < _throttle)
                {
                    StashPending(capped);
                    return;
                }
            }

            await _sink.DispatchAsync(capped, isFinal, ct).ConfigureAwait(false);
            if (_sink.ChunksEmitted > _chunksEmitted)
            {
                _lastEmittedText = capped;
                _lastEmitAt = _timeProvider.GetUtcNow();
                _chunksEmitted = _sink.ChunksEmitted;
                if (isFinal || string.Equals(_pendingText, capped, StringComparison.Ordinal))
                    ClearPending();
            }
        }

        private void StashPending(string text)
        {
            _pendingText = text;
        }

        private void ClearPending()
        {
            _pendingText = string.Empty;
        }
    }

    /// <summary>
    /// Runner-layer safety net for issue #439. Two fake-success modes are caught here:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>all-fail</b> (<paramref name="failureCount"/> &gt; 0, <paramref name="successCount"/> == 0):
    ///     every nyxid_proxy call failed, but the LLM's plain-text output is structurally
    ///     indistinguishable from a real "no activity" report. The prompt-layer §9 Source
    ///     health footer can be dropped by a weaker model, and the runner has no other way
    ///     to tell.
    ///   </description></item>
    ///   <item><description>
    ///     <b>never-called</b> (<paramref name="requiresNyxidProxySuccess"/> == true,
    ///     <paramref name="successCount"/> == 0): the LLM bypassed tools entirely and produced
    ///     text from prior context. For fetch-and-summarize skills this is
    ///     exactly the original #439 symptom (52 commits in 24h reported as "No meaningful
    ///     public GitHub activity"). Skills that don't depend on tool data (e.g. pure LLM
    ///     transformations) leave the flag false and pass through.
    ///   </description></item>
    /// </list>
    /// Throwing here routes through HandleTriggerAsync's existing catch path, which preserves
    /// the retry budget and (after retries are exhausted) persists SkillRunnerExecutionFailedEvent
    /// so <c>/agent-status</c> reports a non-zero <c>error_count</c> with a meaningful
    /// <c>last_error</c> instead of a fake-success run. Mixed runs (any successful nyxid_proxy
    /// call) still complete normally — partial data is more useful than a blanket failure, and
    /// the prompt-layer Source health footer surfaces the failed queries.
    /// </summary>
    internal static void EnsureToolStatusAllowsCompletion(
        int failureCount,
        int successCount,
        bool requiresNyxidProxySuccess)
    {
        if (failureCount > 0 && successCount == 0)
        {
            throw new InvalidOperationException(
                $"All {failureCount} nyxid_proxy tool call(s) in this run failed; refusing to record an empty-day report as a successful execution. " +
                "Inspect the previous attempt's tool output for the underlying NyxID/upstream error envelope.");
        }

        if (requiresNyxidProxySuccess && successCount == 0)
        {
            throw new InvalidOperationException(
                "Skill requires at least one successful nyxid_proxy tool call but completed with zero. " +
                "The LLM produced output without fetching source data (e.g. hallucinated a report from prior context). " +
                "Refusing to record this run as a successful execution.");
        }
    }

    private AgentToolExecutionContext BuildExecutionToolContext(
        string requestId,
        IReadOnlyDictionary<string, string>? metadata) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Caller = new AgentToolCallerContext(State.ScopeId, State.ScopeId, requestId),
            Channel = new AgentToolChannelContext(
                null,
                null,
                State.ScopeId,
                null,
                null),
            ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata),
        };

    private Task SendOutputAsync(string output, CancellationToken ct) =>
        SendOutputAsync(output, providerSlugOverride: null, ct);

    /// <summary>
    /// Posts <paramref name="output"/> as a Lark text message. <paramref name="providerSlugOverride"/>
    /// is non-null only on the failure-notification fallback path (#423 §C); when set, the proxy
    /// routing slug temporarily replaces the agent's primary <c>NyxProviderSlug</c> so a message
    /// can still reach the user via the inbound channel-bot after the primary outbound has been
    /// rejected (e.g. cross-tenant <c>99992364</c>). All other call sites — main report send,
    /// chunked overflow continuations — pass <c>null</c> and stay on the primary slug.
    /// </summary>
    private async Task SendOutputAsync(string output, string? providerSlugOverride, CancellationToken ct)
    {
        var client = _nyxIdApiClient ?? Services.GetService<NyxIdApiClient>();
        if (client is null)
        {
            Logger.LogWarning("Skill runner {ActorId} has no NyxIdApiClient registered; skipping outbound delivery", Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxApiKey) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.NyxProviderSlug) ||
            string.IsNullOrWhiteSpace(State.OutboundConfig?.ConversationId))
        {
            Logger.LogWarning("Skill runner {ActorId} has incomplete outbound config; skipping outbound delivery", Id);
            return;
        }

        var slug = string.IsNullOrWhiteSpace(providerSlugOverride)
            ? State.OutboundConfig.NyxProviderSlug
            : providerSlugOverride!;

        var deliveryTarget = LarkConversationTargets.Resolve(
            State.OutboundConfig.LarkReceiveId,
            State.OutboundConfig.LarkReceiveIdType,
            State.OutboundConfig.ConversationId);
        if (deliveryTarget.FellBackToPrefixInference)
        {
            // No typed receive_id captured at create time; only legacy state predating the
            // typed fields hits this path. Keep the breadcrumb so format drift is observable
            // when the prefix heuristic stops matching.
            Logger.LogDebug(
                "Skill runner {ActorId} resolved Lark receive target by prefix inference (legacy state): conversationId={ConversationId}, receiveIdType={ReceiveIdType}",
                Id,
                State.OutboundConfig.ConversationId,
                deliveryTarget.ReceiveIdType);
        }

        var outcome = await ResolveLarkOutboundDispatcher(client).SendNewMessageAsync(
            new LarkSendNewMessageRequest(
                State.OutboundConfig.NyxApiKey,
                slug,
                MessageType: "text",
                ContentJson: JsonSerializer.Serialize(new { text = output }),
                PrimaryTarget: deliveryTarget,
                FallbackTarget: ResolveFallbackTarget()),
            ct);

        if (!outcome.Succeeded)
        {
            // Surface downstream rejection so HandleTriggerAsync sees a real failure instead of
            // persisting SkillRunnerExecutionCompletedEvent on a silently-dropped Lark response.
            // The Error field on SkillRunnerExecutionFailedEvent ends up in `/agent-status`'s
            // `last_error`, so for known recurring stale-state codes we expand the bare Lark
            // message into actionable recovery guidance — otherwise the user sees a cryptic
            // `99992361 open_id cross app` and has no way to know they need to rebuild the
            // agent.
            throw new InvalidOperationException(BuildLarkRejectionMessage(outcome.LarkCode, outcome.Detail));
        }
    }

    private LarkReceiveTarget? ResolveFallbackTarget()
    {
        var fallbackId = State.OutboundConfig.LarkReceiveIdFallback?.Trim();
        var fallbackType = State.OutboundConfig.LarkReceiveIdTypeFallback?.Trim();
        return string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType)
            ? null
            : new LarkReceiveTarget(fallbackId, fallbackType, FellBackToPrefixInference: false);
    }

    private ILarkOutboundDispatcher ResolveLarkOutboundDispatcher(NyxIdApiClient client) =>
        _larkOutboundDispatcher ?? Services.GetService<ILarkOutboundDispatcher>() ?? new LarkOutboundDispatcher(client, Logger);

    private static string BuildLarkRejectionMessage(int? larkCode, string detail)
    {
        if (larkCode == LarkBotErrorCodes.OpenIdCrossApp)
        {
            // The agent's persisted OutboundConfig was captured before union_id ingress existed
            // (PR #409 added that), so `LarkReceiveIdType=open_id` is permanently scoped to a
            // different Lark app than the customer outbound. Self-heal is not possible without
            // a fresh ingress event carrying union_id; the user must rebuild the agent.
            return
                $"Lark message delivery rejected (code={larkCode}): {detail}. " +
                "This agent was created before cross-app union_id ingress existed; " +
                "delete and recreate it (`/agents` → Delete → recreate) to pick up the cross-app safe target.";
        }

        if (larkCode == LarkBotErrorCodes.UserIdCrossTenant)
        {
            // Even union_id is rejected — the relay-side ingress and outbound apps are in
            // different Lark tenants. No user-id-based identifier survives that boundary;
            // recreating the agent makes the new chat_id-preferred path take effect (chat_id
            // bypasses user-id translation entirely as long as the same app is on both ends).
            return
                $"Lark message delivery rejected (code={larkCode}): {detail}. " +
                "The outbound Lark app is in a different tenant than the inbound app, so " +
                "user-id translation is impossible. Delete and recreate the agent " +
                "(`/agents` → Delete → recreate) so the new chat_id-preferred outbound path " +
                "takes effect, or align the NyxID `s/api-lark-bot` proxy with the channel-bot that " +
                "received the inbound event.";
        }

        return larkCode is { } code
            ? $"Lark message delivery rejected (code={code}): {detail}"
            : $"Lark message delivery rejected: {detail}";
    }

    /// <summary>
    /// Best-effort delivery of a failure-notification message after the run has already failed.
    /// Issue #423 §C: when the primary outbound proxy was just rejected with a structural code
    /// (e.g. cross-tenant <c>99992364</c>), retrying through the same proxy obviously also
    /// fails. If a failure-notification slug was captured at agent-create time (the inbound
    /// channel-bot the user just successfully messaged), try IT first so the user actually
    /// sees that the run failed; on its failure, fall back to the primary slug as a last
    /// resort so single-tenant deployments (no separate failure slug) still get the same
    /// single-attempt behavior they had before this fix.
    /// </summary>
    /// <remarks>
    /// All exceptions are swallowed — by definition we are already in the failure path, and a
    /// failed notification must not raise above HandleTriggerAsync's own
    /// <c>SkillRunnerExecutionFailedEvent</c> persist (which is what surfaces
    /// <c>last_error</c> in <c>/agent-status</c> regardless of whether the user got a Lark
    /// message). Logs a warning for each unsuccessful attempt so the regression is observable.
    /// </remarks>
    private async Task TrySendFailureAsync(string error, CancellationToken ct)
    {
        var message = $"Skill runner failed: {error}";
        var failureSlug = State.OutboundConfig?.FailureNotificationProviderSlug?.Trim();
        var primarySlug = State.OutboundConfig?.NyxProviderSlug?.Trim();

        // Try the captured failure-notification slug first when it is set AND distinct from
        // the primary slug. Equal values would just hit the same proxy again, so we skip the
        // duplicate POST and go straight to the primary path.
        if (!string.IsNullOrEmpty(failureSlug) &&
            !string.Equals(failureSlug, primarySlug, StringComparison.Ordinal))
        {
            try
            {
                await SendOutputAsync(message, providerSlugOverride: failureSlug, ct);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Skill runner {ActorId} failed-notification via failure-notification slug rejected; falling back to primary slug",
                    Id);
            }
        }

        try
        {
            await SendOutputAsync(message, providerSlugOverride: null, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Skill runner {ActorId} failed to send failure notification", Id);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildExecutionMetadataAsync(CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.ConversationId] = State.OutboundConfig?.ConversationId ?? string.Empty,
        };

        return metadata;
    }

    private async Task<LLMControlContext> BuildExecutionLlmControlAsync(CancellationToken ct)
    {
        var control = new LLMControlContext(
            NyxIdAccessToken: State.OutboundConfig?.NyxApiKey,
            NyxIdOrgToken: State.OutboundConfig?.NyxApiKey,
            SenderNyxIdAccessToken: null,
            ModelOverride: null,
            NyxIdRoutePreference: null,
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null);

        // Pin the bot owner's pre-configured model + NyxID route + tool-round cap onto the
        // outbound typed LLM control, the same pattern AgentRunGAgent applies for
        // nyxid-chat. Without this, scheduled runs fall through to NyxIdLLMProvider's
        // compile-time defaults (`gpt-5.4` against `/api/v1/llm/gateway/v1/`), which the
        // gateway routes to the OpenAI provider — failing for bot owners who pre-configured
        // a custom NyxID service like `chrono-llm` at `/api/v1/proxy/s/chrono-llm`. The
        // source is bound once via constructor injection (LocalActorRuntime activates agents
        // through ActivatorUtilities so DI fills the optional ctor param at activation
        // time); a per-execution `Services.GetService<>` lookup would be redundant and was
        // dropped per codex's PR #509 partial dissent on r3159047120.
        return await OwnerLlmConfigApplier.ApplyAsync(
            control,
            State.ScopeId,
            _ownerLlmConfigSource,
            Logger,
            actorLabel: "Skill runner",
            actorId: Id,
            ct);
    }

    private string BuildExecutionPrompt(DateTimeOffset now, string? reason)
    {
        var prompt = string.IsNullOrWhiteSpace(State.ExecutionPrompt)
            ? "Execute the configured skill now and return plain text only."
            : State.ExecutionPrompt;
        return $"{prompt}\nCurrent UTC time: {now:O}\nTrigger reason: {(string.IsNullOrWhiteSpace(reason) ? "manual" : reason)}";
    }

    private async Task UpsertRegistryAsync(CancellationToken ct)
    {
        var ownerScope = State.OutboundConfig?.OwnerScope;

        var command = new UserAgentCatalogUpsertCommand
        {
            AgentId = Id,
            ConversationId = State.OutboundConfig?.ConversationId ?? string.Empty,
            NyxProviderSlug = State.OutboundConfig?.NyxProviderSlug ?? string.Empty,
            NyxApiKey = State.OutboundConfig?.NyxApiKey ?? string.Empty,
            AgentType = SkillRunnerDefaults.AgentType,
            TemplateName = State.TemplateName ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
            ApiKeyId = State.OutboundConfig?.ApiKeyId ?? string.Empty,
            ScheduleCron = State.ScheduleCron ?? string.Empty,
            ScheduleTimezone = State.ScheduleTimezone ?? string.Empty,
            LarkReceiveId = State.OutboundConfig?.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType = State.OutboundConfig?.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback = State.OutboundConfig?.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback = State.OutboundConfig?.LarkReceiveIdTypeFallback ?? string.Empty,
        };

        // Refactor (iter92/cluster-092):
        //   Old: write path simultaneously emitted deprecated `Platform`/`OwnerNyxUserId`.
        //   New: write path emits only `OwnerScope`; legacy fields are retained only in
        //   the no-`OwnerScope` fallback branch for backwards compatibility.
        if (ownerScope is not null)
        {
            command.OwnerScope = ownerScope.Clone();
        }
        else
        {
#pragma warning disable CS0612 // legacy field write only for pre-owner_scope state
            var legacyOwnerNyxUserId = State.OutboundConfig?.OwnerNyxUserId ?? string.Empty;
            var legacyPlatform = ResolvePlatform(State.OutboundConfig?.Platform);
            command.Platform = legacyPlatform;
            command.OwnerNyxUserId = legacyOwnerNyxUserId;
            var legacyScope = OwnerScope.FromLegacyFields(legacyOwnerNyxUserId, legacyPlatform);
#pragma warning restore CS0612
            if (legacyScope is not null)
                command.OwnerScope = legacyScope;
        }

        await UserAgentCatalogStoreCommands.DispatchUpsertAsync(Services, Id, command, ct);
    }

    private static SkillRunnerState ApplyInitialized(SkillRunnerState current, SkillRunnerInitializedEvent evt)
    {
        var next = current.Clone();
        next.SkillName = evt.SkillName ?? string.Empty;
        next.TemplateName = evt.TemplateName ?? string.Empty;
        next.SkillContent = evt.SkillContent ?? string.Empty;
        next.ExecutionPrompt = evt.ExecutionPrompt ?? string.Empty;
        next.ScheduleCron = evt.ScheduleCron ?? string.Empty;
        next.ScheduleTimezone = NormalizeTimezone(evt.ScheduleTimezone);
        next.OutboundConfig = evt.OutboundConfig?.Clone() ?? new SkillRunnerOutboundConfig();
        next.Enabled = evt.Enabled;
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.ProviderName = NormalizeProviderName(evt.ProviderName);
        next.Model = evt.Model ?? string.Empty;
        // Legacy actors created before proto field 16 existed replay an init event whose
        // RequiresNyxidProxySuccess deserializes as false, which would let them keep the
        // pre-#439 zero-tool-call fake-success path — making post-fix behavior depend on
        // creation time rather than template semantics. Derive the effective flag from
        // the template name so known fetch-and-summarize skills get the safety net on
        // replay regardless of when the actor was created. New templates that need this
        // protection should be added to RequiresProxySuccessByTemplate.
        next.RequiresNyxidProxySuccess = evt.RequiresNyxidProxySuccess
            || RequiresProxySuccessByTemplate(evt.TemplateName);

        // Missing sampling fields intentionally use upstream model defaults;
        // missing runner limits fall back to SkillRunner defaults.
        if (evt.HasTemperature)
            next.Temperature = evt.Temperature;
        else
            next.ClearTemperature();
        if (evt.HasMaxTokens)
            next.MaxTokens = evt.MaxTokens;
        else
            next.ClearMaxTokens();

        next.MaxToolRounds = evt.HasMaxToolRounds ? evt.MaxToolRounds : SkillRunnerDefaults.DefaultMaxToolRounds;
        next.MaxHistoryMessages = evt.HasMaxHistoryMessages ? evt.MaxHistoryMessages : SkillRunnerDefaults.DefaultMaxHistoryMessages;
        return next;
    }

    private static SkillRunnerState ApplyNextRunScheduled(SkillRunnerState current, SkillRunnerNextRunScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextRunAt = evt.NextRunAt;
        return next;
    }

    private static SkillRunnerState ApplyCompleted(SkillRunnerState current, SkillRunnerExecutionCompletedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.CompletedAt;
        next.LastOutput = evt.Output ?? string.Empty;
        next.LastError = string.Empty;
        next.ErrorCount = 0;
        return next;
    }

    private static SkillRunnerState ApplyFailed(SkillRunnerState current, SkillRunnerExecutionFailedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.FailedAt;
        next.LastError = evt.Error ?? string.Empty;
        next.ErrorCount += 1;
        return next;
    }

    private static SkillRunnerState ApplyRejected(SkillRunnerState current, SkillRunnerExecutionRejectedEvent evt)
    {
        var next = current.Clone();
        next.LastRunAt = evt.RejectedAt;
        next.LastError = evt.Reason ?? string.Empty;
        next.ErrorCount += 1;
        return next;
    }

    private static SkillRunnerState ApplyDisabled(SkillRunnerState current, SkillRunnerDisabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextRunAt = null;
        return next;
    }

    private static SkillRunnerState ApplyEnabled(SkillRunnerState current, SkillRunnerEnabledEvent _)
    {
        var next = current.Clone();
        next.Enabled = true;
        return next;
    }

    /// <summary>
    /// Templates whose runs MUST observe at least one successful nyxid_proxy call to be
    /// considered successful. Used by <see cref="ApplyInitialized"/> as the legacy-actor
    /// default when the persisted init event predates proto field 16. Add new templates
    /// here when they're fetch-and-summarize style (the LLM bypassing tools and producing
    /// text from prior context is a fake-success failure mode for them).
    /// </summary>
    internal static bool RequiresProxySuccessByTemplate(string? templateName) =>
        // Reserved for future fetch-and-summarize templates that need the runner-layer
        // safety net (issue #439). Currently empty: no in-tree template needs the
        // legacy proto-field-16-default backfill. Keep the method so tests + the apply
        // path don't need to special-case "no templates" — just add new entries here.
        templateName is not null && false;

    private static string NormalizeProviderName(string? providerName) =>
        string.IsNullOrWhiteSpace(providerName) ? SkillRunnerDefaults.DefaultProviderName : providerName.Trim();

    private static string NormalizeTimezone(string? scheduleTimezone) =>
        string.IsNullOrWhiteSpace(scheduleTimezone) ? SkillRunnerDefaults.DefaultTimezone : scheduleTimezone.Trim();

    private static string ResolvePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? SkillRunnerDefaults.DefaultPlatform : platform.Trim();
}
