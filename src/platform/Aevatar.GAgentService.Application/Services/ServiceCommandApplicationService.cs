using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceCommandApplicationService : IServiceCommandPort
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceCommandTargetProvisioner _targetProvisioner;
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceCatalogProjectionPort _catalogProjectionPort;
    private readonly IServiceRevisionCatalogProjectionPort _revisionProjectionPort;

    public ServiceCommandApplicationService(
        IActorDispatchPort dispatchPort,
        IServiceCommandTargetProvisioner targetProvisioner,
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceCatalogProjectionPort catalogProjectionPort,
        IServiceRevisionCatalogProjectionPort revisionProjectionPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _targetProvisioner = targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner));
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _catalogProjectionPort = catalogProjectionPort ?? throw new ArgumentNullException(nameof(catalogProjectionPort));
        _revisionProjectionPort = revisionProjectionPort ?? throw new ArgumentNullException(nameof(revisionProjectionPort));
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
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Spec.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Spec.Identity, command.Spec.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
        PrepareServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
        PublishServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
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

    public async Task<ServiceCommandAcceptedReceipt> ActivateServingRevisionAsync(
        ActivateServingRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    private async Task EnsureDefinitionExistsAsync(ServiceIdentity identity, CancellationToken ct)
    {
        if (await _catalogQueryReader.GetAsync(identity, ct) == null)
            throw new InvalidOperationException($"Service definition '{ServiceKeys.Build(identity)}' was not found.");
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
}
