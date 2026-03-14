using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingProjectionActivationService
    : ProjectionActivationServiceBase<WorkflowBindingRuntimeLease, WorkflowBindingProjectionContext, IReadOnlyList<string>>
{
    public WorkflowBindingProjectionActivationService(
        IProjectionLifecycleService<WorkflowBindingProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override WorkflowBindingProjectionContext CreateContext(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct)
    {
        _ = projectionName;
        _ = input;
        _ = commandId;
        _ = ct;

        return new WorkflowBindingProjectionContext
        {
            ProjectionId = $"{rootEntityId}:binding",
            RootActorId = rootEntityId,
        };
    }

    protected override WorkflowBindingRuntimeLease CreateRuntimeLease(WorkflowBindingProjectionContext context) =>
        new(context);
}
