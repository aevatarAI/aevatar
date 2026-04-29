namespace Aevatar.Studio.Projection.Continuations;

internal interface IStudioMemberBindingObservationPort
{
    Task EnsureObservationAsync(string rootActorId, CancellationToken ct = default);
}
