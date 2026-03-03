namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Runtime lease contract for projection port lifecycle orchestration.
/// </summary>
public interface IProjectionRuntimeLease
{
    string RootEntityId { get; }

    int GetLiveSinkSubscriptionCount();
}
