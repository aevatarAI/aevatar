using Aevatar.GAgentService.Abstractions.Commands;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceCommandPort
{
    Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
        CreateServiceDefinitionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
        UpdateServiceDefinitionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
        CreateServiceRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
        PrepareServiceRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
        PublishServiceRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
        SetDefaultServingRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> ActivateServingRevisionAsync(
        ActivateServingRevisionCommand command,
        CancellationToken ct = default);
}
