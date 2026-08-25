using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Runtime.Actors;

public interface IRuntimeActorStateSchemaContextBinder
{
    IDisposable Bind(RuntimeActorIdentity identity);
}

public interface IRuntimeFleetReconcileDeliveryAttestationBinder
{
    IDisposable Bind(RuntimeFleetReconcileDeliveryAttestation attestation);
}
