using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Ports;

public interface IServiceGovernanceCommandPort
{
    Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(
        CreateServiceBindingCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(
        UpdateServiceBindingCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(
        RetireServiceBindingCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(
        CreateServiceEndpointCatalogCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(
        UpdateServiceEndpointCatalogCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(
        CreateServicePolicyCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(
        UpdateServicePolicyCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(
        RetireServicePolicyCommand command,
        CancellationToken ct = default);
}
