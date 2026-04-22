using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomSessionEventProjector
    : ProjectionSessionEventProjectorBase<StreamingProxyRoomSessionProjectionContext, StreamingProxyRoomSessionEnvelope>
{
    public StreamingProxyRoomSessionEventProjector(
        IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope> sessionEventHub)
        : base(sessionEventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<StreamingProxyRoomSessionEnvelope>> ResolveSessionEventEntries(
        StreamingProxyRoomSessionProjectionContext context,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.SessionId))
            return EmptyEntries;

        return
        [
            new ProjectionSessionEventEntry<StreamingProxyRoomSessionEnvelope>(
                context.RootActorId,
                context.SessionId,
                new StreamingProxyRoomSessionEnvelope
                {
                    Envelope = envelope,
                }),
        ];
    }
}
