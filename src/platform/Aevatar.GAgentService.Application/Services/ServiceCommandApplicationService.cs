using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
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
    private readonly IEventStore _eventStore;
    private readonly IServiceCommandTargetProvisioner _targetProvisioner;
    private readonly IServiceCatalogProjectionPort _catalogProjectionPort;
    private readonly IServiceRevisionCatalogProjectionPort _revisionProjectionPort;
    private readonly IServiceDeploymentCatalogProjectionPort _deploymentProjectionPort;
    private readonly IServiceServingSetProjectionPort _servingSetProjectionPort;
    private readonly IServiceRolloutProjectionPort _rolloutProjectionPort;
    private readonly IServiceTrafficViewProjectionPort _trafficViewProjectionPort;

    public ServiceCommandApplicationService(
        IActorDispatchPort dispatchPort,
        IEventStore eventStore,
        IServiceCommandTargetProvisioner targetProvisioner,
        IServiceCatalogProjectionPort catalogProjectionPort,
        IServiceRevisionCatalogProjectionPort revisionProjectionPort,
        IServiceDeploymentCatalogProjectionPort deploymentProjectionPort,
        IServiceServingSetProjectionPort servingSetProjectionPort,
        IServiceRolloutProjectionPort rolloutProjectionPort,
        IServiceTrafficViewProjectionPort trafficViewProjectionPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _targetProvisioner = targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner));
        _catalogProjectionPort = catalogProjectionPort ?? throw new ArgumentNullException(nameof(catalogProjectionPort));
        _revisionProjectionPort = revisionProjectionPort ?? throw new ArgumentNullException(nameof(revisionProjectionPort));
        _deploymentProjectionPort = deploymentProjectionPort ?? throw new ArgumentNullException(nameof(deploymentProjectionPort));
        _servingSetProjectionPort = servingSetProjectionPort ?? throw new ArgumentNullException(nameof(servingSetProjectionPort));
        _rolloutProjectionPort = rolloutProjectionPort ?? throw new ArgumentNullException(nameof(rolloutProjectionPort));
        _trafficViewProjectionPort = trafficViewProjectionPort ?? throw new ArgumentNullException(nameof(trafficViewProjectionPort));
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
        CreateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Spec.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
        UpdateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Spec.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
        CreateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Spec.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Spec.Identity, command.Spec.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
        PrepareServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
        PublishServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
        RetireServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
        SetDefaultServingRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
        ActivateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await _deploymentProjectionPort.EnsureProjectionAsync(actorId, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
        DeactivateServiceDeploymentCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        await _deploymentProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.DeploymentId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
        ReplaceServiceServingTargetsCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Identity!), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
        StartServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command.Plan);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity!)}:{command.Plan.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
        AdvanceServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceRolloutCommandAcceptedReceipt> PauseServiceRolloutAsync(
        PauseServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        var currentStatus = await GetRolloutStatusAsync(actorId, ct);
        var receipt = await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
        return CreateRolloutReceipt(
            receipt,
            currentStatus != ServiceRolloutStatus.InProgress,
            currentStatus != ServiceRolloutStatus.InProgress ? currentStatus : ServiceRolloutStatus.Paused);
    }

    public async Task<ServiceRolloutCommandAcceptedReceipt> ResumeServiceRolloutAsync(
        ResumeServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        var currentStatus = await GetRolloutStatusAsync(actorId, ct);
        var receipt = await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
        return CreateRolloutReceipt(
            receipt,
            currentStatus != ServiceRolloutStatus.Paused,
            currentStatus != ServiceRolloutStatus.Paused ? currentStatus : ServiceRolloutStatus.InProgress);
    }

    public async Task<ServiceRolloutCommandAcceptedReceipt> RollbackServiceRolloutAsync(
        RollbackServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        var currentStatus = await GetRolloutStatusAsync(actorId, ct);
        var receipt = await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
        var wasNoOp = currentStatus is ServiceRolloutStatus.Completed or ServiceRolloutStatus.RolledBack;
        return CreateRolloutReceipt(
            receipt,
            wasNoOp,
            wasNoOp ? currentStatus : ServiceRolloutStatus.RolledBack);
    }

    private async Task EnsureServingProjectionsAsync(string actorId, CancellationToken ct)
    {
        await _servingSetProjectionPort.EnsureProjectionAsync(actorId, ct);
        await _trafficViewProjectionPort.EnsureProjectionAsync(actorId, ct);
    }

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

    private async Task<ServiceRolloutStatus> GetRolloutStatusAsync(string actorId, CancellationToken ct)
    {
        var events = await _eventStore.GetEventsAsync(actorId, ct: ct);
        var status = ServiceRolloutStatus.Unspecified;
        foreach (var stateEvent in events)
            status = ApplyRolloutStatus(status, stateEvent);

        return status;
    }

    private static ServiceRolloutCommandAcceptedReceipt CreateRolloutReceipt(
        ServiceCommandAcceptedReceipt receipt,
        bool wasNoOp,
        ServiceRolloutStatus status) =>
        new(
            receipt.TargetActorId,
            receipt.CommandId,
            receipt.CorrelationId,
            wasNoOp,
            status.ToString());

    private static ServiceRolloutStatus ApplyRolloutStatus(ServiceRolloutStatus currentStatus, StateEvent stateEvent)
    {
        if (stateEvent.EventData?.Is(ServiceRolloutStartedEvent.Descriptor) == true ||
            stateEvent.EventData?.Is(ServiceRolloutStageAdvancedEvent.Descriptor) == true ||
            stateEvent.EventData?.Is(ServiceRolloutResumedEvent.Descriptor) == true)
        {
            return ServiceRolloutStatus.InProgress;
        }

        if (stateEvent.EventData?.Is(ServiceRolloutPausedEvent.Descriptor) == true)
            return ServiceRolloutStatus.Paused;

        if (stateEvent.EventData?.Is(ServiceRolloutCompletedEvent.Descriptor) == true)
            return ServiceRolloutStatus.Completed;

        if (stateEvent.EventData?.Is(ServiceRolloutRolledBackEvent.Descriptor) == true)
            return ServiceRolloutStatus.RolledBack;

        if (stateEvent.EventData?.Is(ServiceRolloutFailedEvent.Descriptor) == true)
            return ServiceRolloutStatus.Failed;

        return currentStatus;
    }
}
