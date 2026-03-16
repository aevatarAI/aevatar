using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core.Ports;

public interface IServiceServingTargetResolver
{
    Task<IReadOnlyList<ServiceServingTargetSpec>> ResolveTargetsAsync(
        ServiceIdentity identity,
        IEnumerable<ServiceServingTargetSpec> targets,
        CancellationToken ct = default);
}
