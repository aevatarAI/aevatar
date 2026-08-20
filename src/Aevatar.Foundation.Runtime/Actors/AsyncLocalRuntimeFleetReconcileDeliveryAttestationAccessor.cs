using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Runtime.Actors;

public sealed class AsyncLocalRuntimeFleetReconcileDeliveryAttestationAccessor
    : IRuntimeFleetReconcileDeliveryAttestationReader,
      IRuntimeFleetReconcileDeliveryAttestationBinder
{
    private static readonly AsyncLocal<RuntimeFleetReconcileDeliveryAttestation?> CurrentContext = new();

    public RuntimeFleetReconcileDeliveryAttestation? Current => CurrentContext.Value;

    public IDisposable Bind(RuntimeFleetReconcileDeliveryAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        var previous = CurrentContext.Value;
        CurrentContext.Value = attestation;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(
        RuntimeFleetReconcileDeliveryAttestation? previous) : IDisposable
    {
        private RuntimeFleetReconcileDeliveryAttestation? _previous = previous;

        public void Dispose()
        {
            CurrentContext.Value = _previous;
            _previous = null;
        }
    }
}
