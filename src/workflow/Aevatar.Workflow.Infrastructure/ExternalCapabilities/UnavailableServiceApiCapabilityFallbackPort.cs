using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Infrastructure.ExternalCapabilities;

public sealed class UnavailableServiceApiCapabilityFallbackPort :
    IServiceApiCapabilityFallbackPort
{
    public Task<ServiceApiWorkflowCapabilityDiscoveryResult> ResolveAsync(
        ResolveServiceApiCapabilityFallbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ServiceApiWorkflowCapabilityDiscoveryResult
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                FallbackExhausted = new ServiceApiFallbackExhausted
                {
                    Reason = ServiceApiFallbackExhaustedReason.FallbackUnavailable,
                    SafeMessage = "No admitted Service API fallback contract is available.",
                },
            },
        });
    }
}
