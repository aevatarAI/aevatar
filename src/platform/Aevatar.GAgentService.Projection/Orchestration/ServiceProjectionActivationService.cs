using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class ServiceProjectionActivationService<TContext>
    : ProjectionActivationServiceBase<ServiceProjectionRuntimeLease<TContext>, TContext, IReadOnlyList<string>>
    where TContext : class, IProjectionContext
{
    private readonly ServiceProjectionDescriptor<TContext> _descriptor;

    public ServiceProjectionActivationService(
        ServiceProjectionDescriptor<TContext> descriptor,
        IProjectionLifecycleService<TContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    protected override TContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = input;
        _ = commandId;
        _ = ct;
        return _descriptor.CreateContext(rootEntityId, projectionName);
    }

    protected override ServiceProjectionRuntimeLease<TContext> CreateRuntimeLease(TContext context) =>
        new(_descriptor.GetRootActorId(context), context);
}
