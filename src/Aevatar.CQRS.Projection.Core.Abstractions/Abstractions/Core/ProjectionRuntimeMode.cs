namespace Aevatar.CQRS.Projection.Core.Abstractions;

public enum ProjectionRuntimeMode
{
    DurableMaterialization = 0,
    SessionObservation = 1,
}
