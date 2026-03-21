namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionRuntimeLeaseBase : IProjectionRuntimeLease
{
    protected ProjectionRuntimeLeaseBase(string rootEntityId)
    {
        ArgumentNullException.ThrowIfNull(rootEntityId);
        RootEntityId = rootEntityId;
    }

    public string RootEntityId { get; }
}

public abstract class EventSinkProjectionRuntimeLeaseBase<TEvent>
    : ProjectionRuntimeLeaseBase
    where TEvent : class
{
    protected EventSinkProjectionRuntimeLeaseBase(string rootEntityId)
        : base(rootEntityId)
    {
    }
}
