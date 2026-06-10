using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.AGUI.Contracts;
using Google.Protobuf;

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

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>(lease, liveSinkLease);
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

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) ||
            payload?.Is(RoleChatSessionCompletedEvent.Descriptor) != true)
        {
            return EmptyEntries;
        }

        var completed = payload.Unpack<RoleChatSessionCompletedEvent>();
        return NyxIdChatCompletionAguiFrameBuilder.Build(context, completed)
            .Select(frame => Entry(context, frame))
            .ToArray();
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
