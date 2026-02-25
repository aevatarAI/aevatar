namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionStoreDispatchOptions
{
    public int MaxWriteAttempts { get; set; } = 3;
}
