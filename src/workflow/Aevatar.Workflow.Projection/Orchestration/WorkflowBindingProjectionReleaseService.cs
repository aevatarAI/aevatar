using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingProjectionReleaseService
    : ProjectionReleaseServiceBase<WorkflowBindingRuntimeLease, WorkflowBindingProjectionContext, IReadOnlyList<string>>
{
    public WorkflowBindingProjectionReleaseService(
        IProjectionLifecycleService<WorkflowBindingProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override WorkflowBindingProjectionContext ResolveContext(WorkflowBindingRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
