namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreBindingAvailability
{
    bool IsConfigured { get; }

    string AvailabilityReason { get; }
}
