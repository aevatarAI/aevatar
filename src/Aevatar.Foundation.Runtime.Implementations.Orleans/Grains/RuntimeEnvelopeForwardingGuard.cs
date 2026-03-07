using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class RuntimeEnvelopeForwardingGuard
{
    private readonly Func<string> _actorIdAccessor;

    public RuntimeEnvelopeForwardingGuard(Func<string> actorIdAccessor)
    {
        _actorIdAccessor = actorIdAccessor ?? throw new ArgumentNullException(nameof(actorIdAccessor));
    }

    public bool ShouldDrop(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var actorId = _actorIdAccessor();
        if (PublisherChainMetadata.ShouldDropForReceiver(envelope, actorId))
            return true;

        switch (envelope.Direction)
        {
            case EventDirection.Self:
            case EventDirection.Up:
                return false;
            case EventDirection.Down:
            case EventDirection.Both:
                if (StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, actorId))
                    return StreamForwardingRules.IsTransitOnlyForwarding(envelope);

                return envelope.Metadata.TryGetValue(EnvelopeMetadataKeys.SourceActorId, out var sourceActorId) &&
                       string.Equals(sourceActorId, actorId, StringComparison.Ordinal);
            default:
                return true;
        }
    }
}
