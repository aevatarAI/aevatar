using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
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
    Task<INyxIdChatSessionProjectionLease?> EnsureChatProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default);

    // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
    //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
    //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
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
public sealed class NyxIdChatSessionRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<AGUIEvent>,
      INyxIdChatSessionProjectionLease,
      IProjectionPortSessionLease,
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
    public string ScopeId => RootEntityId;
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
    private readonly IActorRuntime _runtime;

    public NyxIdChatSessionProjectionPort(
        IProjectionScopeActivationService<NyxIdChatSessionRuntimeLease> activationService,
        IProjectionScopeReleaseService<NyxIdChatSessionRuntimeLease> releaseService,
        IProjectionSessionEventHub<AGUIEvent> sessionEventHub,
        IActorRuntime runtime)
        : base(static () => true, activationService, releaseService, sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<INyxIdChatSessionProjectionLease?> EnsureChatProjectionAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);

    // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
    //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
    //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
    public async Task<EventSinkProjectionAttachment<INyxIdChatSessionProjectionLease>?> AttachExistingChatProjectionAsync(
        string actorId,
        string sessionId,
        IEventSink<AGUIEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            actorId,
            NyxIdChatProjectionKinds.ChatSession,
            ProjectionRuntimeMode.SessionObservation,
            sessionId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new NyxIdChatSessionRuntimeLease(new NyxIdChatSessionProjectionContext
        {
            RootActorId = actorId,
            ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
            SessionId = sessionId,
        });
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
        // Refactor (iter1/cluster-004):
        //   Old pattern: NyxID SSE endpoints subscribed to raw EventEnvelope and inferred terminal state.
        //   New principle: Projection consumes EventEnvelope once and publishes typed AGUIEvent session frames.
        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        var mapped = ScopeGAgentAguiEventMapper.TryMap(envelope);
        if (mapped == null)
            return EmptyEntries;

        FillSessionDefaults(context, mapped);
        if (mapped.EventCase != AGUIEvent.EventOneofCase.TextMessageEnd)
            return [Entry(context, mapped)];

        return
        [
            Entry(context, mapped),
            Entry(context, new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = context.RootActorId,
                    RunId = context.SessionId,
                },
            }),
        ];
    }

    private static ProjectionSessionEventEntry<AGUIEvent> Entry(
        NyxIdChatSessionProjectionContext context,
        AGUIEvent evt) =>
        new(context.RootActorId, context.SessionId, evt);

    private static void FillSessionDefaults(NyxIdChatSessionProjectionContext context, AGUIEvent evt)
    {
        if (evt.TextMessageStart != null && string.IsNullOrWhiteSpace(evt.TextMessageStart.MessageId))
            evt.TextMessageStart.MessageId = context.SessionId;
        if (evt.TextMessageContent != null && string.IsNullOrWhiteSpace(evt.TextMessageContent.MessageId))
            evt.TextMessageContent.MessageId = context.SessionId;
        if (evt.TextMessageEnd != null && string.IsNullOrWhiteSpace(evt.TextMessageEnd.MessageId))
            evt.TextMessageEnd.MessageId = context.SessionId;
    }
}

internal static class NyxIdChatProjectionKinds
{
    public const string ChatSession = "nyxid-chat-session";
}
