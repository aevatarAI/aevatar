using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Contexts;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

public sealed class ServicePolicyProjectionReleaseService
    : ProjectionReleaseServiceBase<ServicePolicyRuntimeLease, ServicePolicyProjectionContext, IReadOnlyList<string>>
{
    public ServicePolicyProjectionReleaseService(
        IProjectionLifecycleService<ServicePolicyProjectionContext, IReadOnlyList<string>> lifecycle)
        : base(lifecycle)
    {
    }

    protected override ServicePolicyProjectionContext ResolveContext(ServicePolicyRuntimeLease runtimeLease) =>
        runtimeLease.Context;
}
