using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceCommandApplicationService : IServiceCommandPort
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceCommandTargetProvisioner _targetProvisioner;

    public ServiceCommandApplicationService(
        IActorDispatchPort dispatchPort,
        IServiceCommandTargetProvisioner targetProvisioner)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _targetProvisioner = targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner));
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
        CreateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Spec.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
        UpdateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Spec.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ReconcileExternalExposureAsync(
        ReconcileExternalExposureCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetireExternalExposureAsync(
        RetireExternalExposureCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
        CreateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Spec.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Spec.Identity, command.Spec.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
        PrepareServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
        PublishServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
        RetireServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
        ActivateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
        DeactivateServiceDeploymentCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.DeploymentId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
        ReplaceServiceServingTargetsCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Identity!), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
        StartServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command.Plan);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity!)}:{command.Plan.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
        AdvanceServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
        PauseServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
        ResumeServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
        RollbackServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureInvocationCatalogTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    private async Task<ServiceCommandAcceptedReceipt> DispatchAsync(
        string actorId,
        IMessage command,
        string correlationId,
        CancellationToken ct)
    {
        var envelope = ServiceCommandEnvelopeFactory.Create(actorId, command, correlationId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct);
        return new ServiceCommandAcceptedReceipt(actorId, envelope.Id, correlationId);
    }

    private static string CorrelationForService(ServiceIdentity identity) => ServiceKeys.Build(identity);

    private static string CorrelationForRevision(ServiceIdentity identity, string revisionId) =>
        $"{ServiceKeys.Build(identity)}:{revisionId ?? string.Empty}";
}
