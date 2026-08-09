using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.ExternalCapabilities;

internal sealed class DeferredManagedServiceApiSkillDiscoveryPort(
    IServiceScopeFactory scopeFactory) : IManagedCodexServiceApiSkillDiscoveryPort
{
    public async Task<ManagedCodexServiceApiSkillDiscoveryResult> DiscoverAsync(
        ManagedCodexServiceApiSkillDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = ActivatorUtilities.CreateInstance<ManagedServiceApiSkillDiscoveryService>(
            scope.ServiceProvider);
        return await service.DiscoverAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
