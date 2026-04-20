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

    Task<ServiceCommandAcceptedReceipt> RepublishServiceAsync(
        RepublishServiceDefinitionCommand command,
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

    Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
        RetireServiceRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
        SetDefaultServingRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
        ActivateServiceRevisionCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
        DeactivateServiceDeploymentCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
        ReplaceServiceServingTargetsCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
        StartServiceRolloutCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
        AdvanceServiceRolloutCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
        PauseServiceRolloutCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
        ResumeServiceRolloutCommand command,
        CancellationToken ct = default);

    Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
        RollbackServiceRolloutCommand command,
        CancellationToken ct = default);
}
