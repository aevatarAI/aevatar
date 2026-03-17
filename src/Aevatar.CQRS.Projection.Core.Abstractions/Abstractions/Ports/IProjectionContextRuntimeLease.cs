namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionContextRuntimeLease<out TContext>
    where TContext : class, IProjectionMaterializationContext
{
    TContext Context { get; }
}
