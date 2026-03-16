namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionContextRuntimeLease<out TContext>
    where TContext : class, IProjectionContext
{
    TContext Context { get; }
}
