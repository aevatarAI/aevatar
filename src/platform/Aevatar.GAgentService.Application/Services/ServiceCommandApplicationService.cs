using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceCommandApplicationService : IServiceCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceCatalogProjectionPort _catalogProjectionPort;
    private readonly IServiceRevisionCatalogProjectionPort _revisionProjectionPort;

    public ServiceCommandApplicationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IServiceCatalogProjectionPort catalogProjectionPort,
        IServiceRevisionCatalogProjectionPort revisionProjectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _catalogProjectionPort = catalogProjectionPort ?? throw new ArgumentNullException(nameof(catalogProjectionPort));
        _revisionProjectionPort = revisionProjectionPort ?? throw new ArgumentNullException(nameof(revisionProjectionPort));
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
        CreateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = ServiceActorIds.Definition(command.Spec.Identity);
        await EnsureActorAsync<ServiceDefinitionGAgent>(actorId, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
        UpdateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = ServiceActorIds.Definition(command.Spec.Identity);
        await EnsureActorAsync<ServiceDefinitionGAgent>(actorId, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
        CreateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = ServiceActorIds.RevisionCatalog(command.Spec.Identity);
        await EnsureActorAsync<ServiceRevisionCatalogGAgent>(actorId, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Spec.Identity, command.Spec.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
        PrepareServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = ServiceActorIds.RevisionCatalog(command.Identity);
        await EnsureActorAsync<ServiceRevisionCatalogGAgent>(actorId, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
        PublishServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = ServiceActorIds.RevisionCatalog(command.Identity);
        await EnsureActorAsync<ServiceRevisionCatalogGAgent>(actorId, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
        SetDefaultServingRevisionCommand command,
        CancellationToken ct = default)
    {
        var actorId = ServiceActorIds.Definition(command.Identity);
        await EnsureActorAsync<ServiceDefinitionGAgent>(actorId, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ActivateServingRevisionAsync(
        ActivateServingRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = ServiceActorIds.Deployment(command.Identity);
        await EnsureActorAsync<ServiceDeploymentManagerGAgent>(actorId, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    private async Task EnsureDefinitionExistsAsync(ServiceIdentity identity, CancellationToken ct)
    {
        var actorId = ServiceActorIds.Definition(identity);
        if (!await _runtime.ExistsAsync(actorId))
        {
            throw new InvalidOperationException($"Service definition '{ServiceKeys.Build(identity)}' was not found.");
        }
    }

    private async Task EnsureActorAsync<TAgent>(string actorId, CancellationToken ct)
        where TAgent : IAgent
    {
        if (!await _runtime.ExistsAsync(actorId))
        {
            _ = await _runtime.CreateAsync<TAgent>(actorId, ct);
            return;
        }

        _ = await _runtime.GetAsync(actorId)
            ?? throw new InvalidOperationException($"Actor '{actorId}' was not found after existence check.");
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
