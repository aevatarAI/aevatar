using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Propagation;

public static class EnvelopeDeliveryProvenanceSemantics
{
    public static EventEnvelope CloneForRawDispatch(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var admitted = envelope.Clone();
        if (admitted.Runtime is not null)
            admitted.Runtime.DeliveryProvenance = null;
        return admitted;
    }

    public static void StampAuthenticatedActorOrigin(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrWhiteSpace(actorId))
        {
            if (envelope.Runtime is not null)
                envelope.Runtime.DeliveryProvenance = null;
            return;
        }

        envelope.EnsureRuntime().DeliveryProvenance = new EnvelopeDeliveryProvenance
        {
            AuthenticatedActorId = actorId,
        };
    }
}
