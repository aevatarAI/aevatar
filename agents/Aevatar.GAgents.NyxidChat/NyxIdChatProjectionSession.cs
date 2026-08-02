using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AGUI.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Projection Pipeline lease for NyxID chat SSE sessions. The associated
/// projector consumes EventEnvelope input and exposes typed AGUIEvent frames.
/// </summary>
public interface INyxIdChatSessionProjectionLease
{
    string ActorId { get; }
    string SessionId { get; }
}

/// <summary>
/// Projection Pipeline port for NyxID chat SSE sessions. It activates an
/// actorized session whose projector input is EventEnvelope and whose output is AGUIEvent.
/// </summary>
public interface INyxIdChatSessionProjectionPort
    : IEventSinkProjectionLifecyclePort<INyxIdChatSessionProjectionLease, AGUIEvent>
{
    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    Task<EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default);
}

/// <summary>
/// Actorized Projection Pipeline context for NyxID chat SSE sessions. It binds
/// EventEnvelope projector input to one typed AGUIEvent session sink.
/// </summary>
public sealed class NyxIdChatSessionProjectionContext : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}

/// <summary>
/// Runtime lease for NyxID chat Projection Pipeline sessions. It carries the
/// EventEnvelope projector scope and typed AGUIEvent session identity.
/// </summary>
// Refactor (issue-377): Old pattern: runtime lease implemented IProjectionPortSessionLease.
// Refactor (issue-377): Old pattern: ScopeId was a RootActorId alias on the lease.
// Refactor (issue-377): New principle: NyxID chat session context owns RootActorId + SessionId.
// Refactor (issue-377): New principle: lifecycle attach reads the typed context directly.
public sealed class NyxIdChatSessionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<AGUIEvent>,
      INyxIdChatSessionProjectionLease,
      IProjectionContextRuntimeLease<NyxIdChatSessionProjectionContext>
{
    public NyxIdChatSessionRuntimeLease(NyxIdChatSessionProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public string ActorId => RootEntityId;
    public string SessionId { get; }
    public NyxIdChatSessionProjectionContext Context { get; }
}

/// <summary>
/// Lifecycle adapter for NyxID chat Projection Pipeline sessions. It attaches
/// typed AGUIEvent sinks to sessions whose projector input is EventEnvelope.
/// </summary>
// Refactor (iter37/cluster-037-agent-session-observation-attach-only):
//   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
//   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
public sealed class NyxIdChatSessionProjectionPort
    : EventSinkProjectionLifecyclePortBase<INyxIdChatSessionProjectionLease, NyxIdChatSessionRuntimeLease, AGUIEvent>,
      INyxIdChatSessionProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<NyxIdChatSessionRuntimeLease> _attachExistingLeaseLookup;

    public NyxIdChatSessionProjectionPort(
        IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<NyxIdChatSessionRuntimeLease> attachExistingLeaseLookup)
        : base(static () => true, releaseService, sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    // Refactor (iter51/issue-898-projection-attach-existing-side-read):
    //   Old pattern: Feature projection ports duplicated IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()) for attach-existing checks (post-#884 #884 fixed 3 ports but more remained).
    //   New principle: All attach-existing lease lookups go through typed IProjectionScopeAttachExistingLeaseLookup<TLease>; CI guard prevents recurrence.
    // Refactor (iter45/issue-867-session-projection-ensure-surface):
    //   Old pattern: Projection session ports exposed Ensure*ProjectionAsync activation surfaces next to attach-only observation APIs, allowing command/request paths to reactivate sessions.
    //   New principle: Public observation ports expose attach-existing only; projection-owned lifecycle activates sessions through committed-state/startup/background binders.
    public async Task<EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default)
    {
        // Refactor (iter101/cluster-104): Old chat session port inherited direct ensure activation; new request-facing surface attaches only to an existing projection session.
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var lease = await _attachExistingLeaseLookup.TryGetAsync(new ProjectionScopeStartRequest
        {
            RootActorId = actorId,
            ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
            Mode = ProjectionRuntimeMode.SessionObservation,
            SessionId = sessionId,
        }, ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(
            lease,
            new SequencedAguiEventSink(sink),
            ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>(lease, liveSinkLease);
    }

    private sealed class SequencedAguiEventSink(IEventSink<AGUIEvent> inner) : IEventSink<AGUIEvent>
    {
        private readonly HashSet<ByteString> _deliveredAtLatestSequence = [];
        private long _latestSequence;

        public void Push(AGUIEvent evt)
        {
            if (ShouldDeliver(evt))
                inner.Push(evt);
        }

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default) =>
            ShouldDeliver(evt)
                ? inner.PushAsync(evt, ct)
                : ValueTask.CompletedTask;

        public void Complete()
        {
        }

        public IAsyncEnumerable<AGUIEvent> ReadAllAsync(CancellationToken ct = default) =>
            inner.ReadAllAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private bool ShouldDeliver(AGUIEvent evt)
        {
            ArgumentNullException.ThrowIfNull(evt);

            if (evt.Sequence <= 0)
                return true;

            if (evt.Sequence < _latestSequence)
                return false;

            if (evt.Sequence > _latestSequence)
            {
                _latestSequence = evt.Sequence;
                _deliveredAtLatestSequence.Clear();
            }

            return _deliveredAtLatestSequence.Add(evt.ToByteString());
        }
    }
}

/// <summary>
/// Event codec for NyxID chat Projection Pipeline sessions. The projector
/// consumes EventEnvelope input and persists typed AGUIEvent session frames.
/// </summary>
public sealed class NyxIdChatSessionEventCodec : IProjectionSessionEventCodec<AGUIEvent>
{
    public string Channel => "nyxid-chat-session";
    public string GetEventType(AGUIEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.EventCase == AGUIEvent.EventOneofCase.None
            ? AGUIEvent.Descriptor.FullName
            : evt.EventCase.ToString();
    }
    public ByteString Serialize(AGUIEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }
    public AGUIEvent? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
            return null;

        try
        {
            var decoded = AGUIEvent.Parser.ParseFrom(payload);
            return string.Equals(GetEventType(decoded), eventType, StringComparison.Ordinal)
                ? decoded
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}

/// <summary>
/// Session projector for NyxID chat SSE frames in the unified Projection
/// Pipeline. It consumes EventEnvelope input and emits typed AGUIEvent frames.
/// </summary>
public sealed class NyxIdChatSessionEventProjector
    : ProjectionSessionEventProjectorBase<NyxIdChatSessionProjectionContext, AGUIEvent>
{
    public NyxIdChatSessionEventProjector(IProjectionSessionEventHub<AGUIEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> ResolveSessionEventEntries(
        NyxIdChatSessionProjectionContext context,
        EventEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var payload,
                out _,
                out var stateVersion) ||
            payload == null)
        {
            return EmptyEntries;
        }

        if (payload.Is(RoleChatSessionProgressedEvent.Descriptor))
        {
            var progress = payload.Unpack<RoleChatSessionProgressedEvent>();
            if (progress.Sequence <= 0 ||
                !string.Equals(progress.SessionId, context.SessionId, StringComparison.Ordinal))
                return EmptyEntries;

            return BuildProgressEntries(context, [progress]);
        }

        if (payload.Is(RoleChatSessionCompletedEvent.Descriptor))
        {
            var completion = payload.Unpack<RoleChatSessionCompletedEvent>();
            if (!string.Equals(completion.SessionId, context.SessionId, StringComparison.Ordinal))
                return EmptyEntries;

            return BuildProgressEntries(context, completion.TerminalProgress);
        }

        if (payload.Is(NyxIdChatTurnStartedEvent.Descriptor))
        {
            var started = payload.Unpack<NyxIdChatTurnStartedEvent>();
            if (!MatchesControllerState(context, started.State))
                return EmptyEntries;
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildStarted(
                    context.RootActorId,
                    context.SessionId,
                    started.State));
        }

        if (payload.Is(NyxIdChatOperationProgressedEvent.Descriptor))
        {
            var progressed = payload.Unpack<NyxIdChatOperationProgressedEvent>();
            if (!MatchesControllerKey(context, progressed.Progress?.Key))
                return EmptyEntries;
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildProgressed(
                    context.SessionId,
                    progressed));
        }

        if (payload.Is(NyxIdChatOperationReconciledEvent.Descriptor))
        {
            var reconciled = payload.Unpack<NyxIdChatOperationReconciledEvent>();
            if (!MatchesControllerKey(context, reconciled.Result?.Key) ||
                !MatchesControllerState(context, reconciled.State))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildReconciled(
                    context.RootActorId,
                    context.SessionId,
                    reconciled));
        }

        if (payload.Is(NyxIdChatLateOperationEvidenceCommittedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatLateOperationEvidenceCommittedEvent>();
            if (!MatchesControllerKey(context, committed.Key) ||
                !MatchesControllerState(context, committed.State))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildLateOperationEvidence(
                    committed,
                    committed.ProgressSequence));
        }

        if (payload.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatControlFenceCommittedEvent>();
            if (!string.Equals(committed.Fence?.TurnId, context.SessionId, StringComparison.Ordinal))
                return EmptyEntries;
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildControlChanged(
                    context.RootActorId,
                    context.SessionId,
                    committed,
                    committed.State?.ProgressSequence ?? 0));
        }

        if (payload.Is(NyxIdChatActionRequestedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatActionRequestedEvent>();
            if (!string.Equals(committed.Request?.ConversationActorId, context.RootActorId, StringComparison.Ordinal) ||
                !string.Equals(committed.Request?.OriginTurnId, context.SessionId, StringComparison.Ordinal))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildActionRequested(
                    context.RootActorId,
                    context.SessionId,
                    committed,
                    committed.State?.ProgressSequence ?? 0));
        }

        if (payload.Is(NyxIdChatInputRequestedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatInputRequestedEvent>();
            if (!string.Equals(
                    committed.PendingInput?.TurnId,
                    context.SessionId,
                    StringComparison.Ordinal) ||
                !MatchesControllerState(context, committed.State))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildInputRequested(committed));
        }

        if (payload.Is(NyxIdChatInputResolutionCommittedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatInputResolutionCommittedEvent>();
            if (!MatchesControllerState(context, committed.State))
                return EmptyEntries;
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildInputChanged(committed));
        }

        if (payload.Is(NyxIdChatApprovalResolutionCommittedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatApprovalResolutionCommittedEvent>();
            if (!MatchesControllerState(context, committed.State))
                return EmptyEntries;
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildApprovalChanged(committed));
        }

        if (payload.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatContinuationAdmissionCommittedEvent>();
            var sessionTurnId = committed.Admission?.Kind == NyxIdChatContinuationKind.Action
                ? committed.Admission.ContinuationTurnId
                : committed.Admission?.OriginTurnId;
            if (!string.Equals(
                    sessionTurnId,
                    context.SessionId,
                    StringComparison.Ordinal))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildContinuationChanged(
                    context.RootActorId,
                    context.SessionId,
                    committed,
                    committed.State?.ProgressSequence ?? 0));
        }

        if (payload.Is(NyxIdChatStepControlCommittedEvent.Descriptor))
        {
            var committed = payload.Unpack<NyxIdChatStepControlCommittedEvent>();
            if (!string.Equals(
                    committed.Result?.ConversationActorId,
                    context.RootActorId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    committed.Result?.TurnId,
                    context.SessionId,
                    StringComparison.Ordinal) ||
                !MatchesControllerState(context, committed.State))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildStepControlChanged(
                    committed,
                    committed.State?.ProgressSequence ?? 0));
        }

        if (payload.Is(NyxIdChatTurnAdmissionRejectedEvent.Descriptor))
        {
            var rejected = payload.Unpack<NyxIdChatTurnAdmissionRejectedEvent>();
            if (!string.Equals(
                    rejected.ConversationActorId,
                    context.RootActorId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    rejected.RequestedTurnId,
                    context.SessionId,
                    StringComparison.Ordinal))
            {
                return EmptyEntries;
            }
            return Entries(
                context,
                NyxIdChatConversationAguiFrameBuilder.BuildTurnAdmissionRejected(
                    rejected,
                    stateVersion));
        }

        if (payload.Is(RoleChatSessionConflictEvent.Descriptor))
        {
            var conflict = payload.Unpack<RoleChatSessionConflictEvent>();
            return BuildCommandAttemptRejectionEntries(
                context,
                stateVersion,
                conflict.SessionId,
                conflict.SafeMessage);
        }

        if (payload.Is(RoleChatCommandAttemptRejectedEvent.Descriptor))
        {
            var rejected = payload.Unpack<RoleChatCommandAttemptRejectedEvent>();
            return BuildCommandAttemptRejectionEntries(
                context,
                stateVersion,
                rejected.RequestedSessionId,
                rejected.SafeMessage);
        }

        return EmptyEntries;
    }

    private static bool MatchesControllerState(
        NyxIdChatSessionProjectionContext context,
        NyxIdChatConversationGAgentState? state) =>
        state?.ActiveTurn is not null &&
        string.Equals(state.ConversationActorId, context.RootActorId, StringComparison.Ordinal) &&
        string.Equals(state.ActiveTurn.TurnId, context.SessionId, StringComparison.Ordinal);

    private static bool MatchesControllerKey(
        NyxIdChatSessionProjectionContext context,
        NyxIdChatOperationKey? key) =>
        key is not null &&
        string.Equals(key.ConversationActorId, context.RootActorId, StringComparison.Ordinal) &&
        string.Equals(key.TurnId, context.SessionId, StringComparison.Ordinal);

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> Entries(
        NyxIdChatSessionProjectionContext context,
        IEnumerable<AGUIEvent> frames) =>
        frames.Select(frame => Entry(context, frame)).ToArray();

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> BuildCommandAttemptRejectionEntries(
        NyxIdChatSessionProjectionContext context,
        long stateVersion,
        string sessionId,
        string safeMessage)
    {
        if (!string.Equals(sessionId, context.SessionId, StringComparison.Ordinal))
            return EmptyEntries;

        return
        [
            Entry(context, new AGUIEvent
            {
                Sequence = stateVersion,
                RunError = new RunErrorEvent
                {
                    RunId = context.SessionId,
                    Code = "IDEMPOTENCY_CONFLICT",
                    Message = string.IsNullOrWhiteSpace(safeMessage)
                        ? "This client request id was already used for different input."
                        : safeMessage,
                },
            }),
        ];
    }

    private static IReadOnlyList<ProjectionSessionEventEntry<AGUIEvent>> BuildProgressEntries(
        NyxIdChatSessionProjectionContext context,
        IEnumerable<RoleChatSessionProgressedEvent> progressEvents)
    {
        var entries = new List<ProjectionSessionEventEntry<AGUIEvent>>();
        foreach (var progress in progressEvents)
        {
            if (progress.Sequence <= 0 ||
                !string.Equals(progress.SessionId, context.SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var frame in BuildProgressFrames(context, progress))
            {
                frame.Sequence = progress.Sequence;
                entries.Add(Entry(context, frame));
            }
        }

        return entries;
    }

    private static IReadOnlyList<AGUIEvent> BuildProgressFrames(
        NyxIdChatSessionProjectionContext context,
        RoleChatSessionProgressedEvent progress)
    {
        switch (progress.PayloadCase)
        {
            case RoleChatSessionProgressedEvent.PayloadOneofCase.TextStarted:
                return
                [
                    new AGUIEvent
                    {
                        TextMessageStart = new Aevatar.AGUI.Contracts.TextMessageStartEvent
                        {
                            MessageId = context.SessionId,
                            Role = "assistant",
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.TextDelta:
                return
                [
                    new AGUIEvent
                    {
                        TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
                        {
                            MessageId = context.SessionId,
                            Delta = progress.TextDelta.Delta,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ReasoningDelta:
                return
                [
                    new AGUIEvent
                    {
                        Custom = new CustomEvent
                        {
                            Name = "aevatar.llm.reasoning",
                            Payload = Any.Pack(progress.ReasoningDelta),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.Media:
                if (progress.Media.Part == null)
                    return Array.Empty<AGUIEvent>();
                return
                [
                    new AGUIEvent
                    {
                        Custom = new CustomEvent
                        {
                            Name = "MEDIA_CONTENT",
                            Payload = Any.Pack(new MediaContentEvent
                            {
                                SessionId = context.SessionId,
                                AgentId = progress.Media.AgentId,
                                Part = progress.Media.Part.Clone(),
                            }),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolStarted:
                return
                [
                    new AGUIEvent
                    {
                        ToolCallStart = new ToolCallStartEvent
                        {
                            ToolCallId = progress.ToolStarted.CallId,
                            ToolName = progress.ToolStarted.ToolName,
                            Presentation = Aevatar.AI.Abstractions.ToolProviders.ToolPresentationDescriptors.Snapshot(
                                progress.ToolStarted.Presentation,
                                progress.ToolStarted.ToolName),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolCompleted:
                if (progress.ToolCompleted.Result == null)
                    return Array.Empty<AGUIEvent>();
                return
                [
                    new AGUIEvent
                    {
                        ToolCallEnd = new ToolCallEndEvent
                        {
                            ToolCallId = progress.ToolCompleted.Result.CallId,
                            Result = ResolveToolResult(progress.ToolCompleted.Result),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.Usage:
                if (progress.Usage.Usage == null)
                    return Array.Empty<AGUIEvent>();
                return
                [
                    new AGUIEvent
                    {
                        Usage = new UsageEvent
                        {
                            Available = true,
                            PromptTokens = progress.Usage.Usage.PromptTokens,
                            CompletionTokens = progress.Usage.Usage.CompletionTokens,
                            TotalTokens = progress.Usage.Usage.TotalTokens,
                            Model = string.IsNullOrWhiteSpace(progress.Usage.Model)
                                ? null
                                : progress.Usage.Model,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.TextEnded:
                return
                [
                    new AGUIEvent
                    {
                        TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent
                        {
                            MessageId = string.IsNullOrWhiteSpace(progress.TextEnded.MessageId)
                                ? context.SessionId
                                : progress.TextEnded.MessageId,
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.AuthorizationRequired:
                if (progress.AuthorizationRequired.AuthorizationRequired == null)
                    return Array.Empty<AGUIEvent>();
                return
                [
                    new AGUIEvent
                    {
                        Custom = new CustomEvent
                        {
                            Name = "nyxid.authorization.required",
                            Payload = Any.Pack(progress.AuthorizationRequired.AuthorizationRequired),
                        },
                    },
                ];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.ToolApprovalRequired:
                if (progress.ToolApprovalRequired.Pending == null)
                    return Array.Empty<AGUIEvent>();
                var approvalFrame = NyxIdChatCompletionAguiFrameBuilder.BuildPendingApprovalFrame(
                    progress.ToolApprovalRequired.Pending);
                return approvalFrame == null ? Array.Empty<AGUIEvent>() : [approvalFrame];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.Terminal:
                return [BuildTerminalFrame(context, progress.Terminal)];
            case RoleChatSessionProgressedEvent.PayloadOneofCase.Replay:
                return progress.Replay.Snapshot == null
                    ? Array.Empty<AGUIEvent>()
                    : NyxIdChatCompletionAguiFrameBuilder.Build(context, progress.Replay.Snapshot);
            default:
                return Array.Empty<AGUIEvent>();
        }
    }

    private static AGUIEvent BuildTerminalFrame(
        NyxIdChatSessionProjectionContext context,
        RoleChatTerminalProgress terminal)
    {
        if (terminal.Outcome is
            RoleChatSessionOutcome.Failed or
            RoleChatSessionOutcome.OutcomeUncertain)
        {
            return new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    RunId = context.SessionId,
                    Code = string.IsNullOrWhiteSpace(terminal.FailureCode)
                        ? "CHAT_REQUEST_FAILED"
                        : terminal.FailureCode,
                    Message = string.IsNullOrWhiteSpace(terminal.SafeMessage)
                        ? "The chat request failed. Please try again."
                        : terminal.SafeMessage,
                },
            };
        }

        return new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = context.RootActorId,
                RunId = context.SessionId,
                Result = Any.Pack(new StringValue { Value = terminal.FinalContent ?? string.Empty }),
                Status = terminal.Outcome == RoleChatSessionOutcome.Blocked
                    ? RunCompletionStatus.Blocked
                    : RunCompletionStatus.Completed,
            },
        };
    }

    private static string ResolveToolResult(ToolResultEvent result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResultJson))
            return result.ResultJson;
        if (!string.IsNullOrWhiteSpace(result.Error))
            return result.Error;
        return result.Success ? "Tool completed." : "Tool failed.";
    }

    private static ProjectionSessionEventEntry<AGUIEvent> Entry(
        NyxIdChatSessionProjectionContext context,
        AGUIEvent evt) =>
        new(context.RootActorId, context.SessionId, evt);
}

internal static class NyxIdChatProjectionKinds
{
    public const string ChatSession = "nyxid-chat-session";
}
