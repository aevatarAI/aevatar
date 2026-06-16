using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Configuration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class LlmSessionObservationProjectionPort
    : EventSinkProjectionLifecyclePortBase<ILlmSessionObservationProjectionLease, LlmSessionObservationRuntimeLease, EventEnvelope>,
      ILlmSessionObservationProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<LlmSessionObservationRuntimeLease> _attachExistingLeaseLookup;

    public LlmSessionObservationProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeReleaseService<LlmSessionObservationRuntimeLease> releaseService,
        LlmSessionObservationSessionEventHub sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<LlmSessionObservationRuntimeLease> attachExistingLeaseLookup)
        : base(
            () => options.Enabled,
            releaseService,
            sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ?? throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    public async Task<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?> AttachExistingResponseProjectionAsync(
        string actorId,
        string responseId,
        IEventSink<EventEnvelope> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(responseId))
        {
            return null;
        }

        var lease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId.Trim(),
                ProjectionKind = ServiceProjectionKinds.LlmSessionObservation,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = responseId.Trim(),
            },
            ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>(lease, liveSinkLease);
    }
}
