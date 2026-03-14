using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Application.Internal;
using Google.Protobuf;

namespace Aevatar.GAgentService.Governance.Application.Services;

public sealed class ServiceGovernanceCommandApplicationService : IServiceGovernanceCommandPort
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceGovernanceCommandTargetProvisioner _targetProvisioner;
    private readonly IServiceBindingProjectionPort _bindingProjectionPort;
    private readonly IServiceEndpointCatalogProjectionPort _endpointCatalogProjectionPort;
    private readonly IServicePolicyProjectionPort _policyProjectionPort;

    public ServiceGovernanceCommandApplicationService(
        IActorDispatchPort dispatchPort,
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceGovernanceCommandTargetProvisioner targetProvisioner,
        IServiceBindingProjectionPort bindingProjectionPort,
        IServiceEndpointCatalogProjectionPort endpointCatalogProjectionPort,
        IServicePolicyProjectionPort policyProjectionPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _targetProvisioner = targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner));
        _bindingProjectionPort = bindingProjectionPort ?? throw new ArgumentNullException(nameof(bindingProjectionPort));
        _endpointCatalogProjectionPort = endpointCatalogProjectionPort ?? throw new ArgumentNullException(nameof(endpointCatalogProjectionPort));
        _policyProjectionPort = policyProjectionPort ?? throw new ArgumentNullException(nameof(policyProjectionPort));
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(
        CreateServiceBindingCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsureBindingCatalogTargetAsync(command.Spec.Identity, ct);
        await _bindingProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForBinding(command.Spec.Identity, command.Spec.BindingId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(
        UpdateServiceBindingCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsureBindingCatalogTargetAsync(command.Spec.Identity, ct);
        await _bindingProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForBinding(command.Spec.Identity, command.Spec.BindingId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(
        RetireServiceBindingCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureBindingCatalogTargetAsync(command.Identity, ct);
        await _bindingProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForBinding(command.Identity, command.BindingId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(
        CreateServiceEndpointCatalogCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsureEndpointCatalogTargetAsync(command.Spec.Identity, ct);
        await _endpointCatalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(
        UpdateServiceEndpointCatalogCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsureEndpointCatalogTargetAsync(command.Spec.Identity, ct);
        await _endpointCatalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(
        CreateServicePolicyCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsurePolicyCatalogTargetAsync(command.Spec.Identity, ct);
        await _policyProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForPolicy(command.Spec.Identity, command.Spec.PolicyId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(
        UpdateServicePolicyCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsurePolicyCatalogTargetAsync(command.Spec.Identity, ct);
        await _policyProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForPolicy(command.Spec.Identity, command.Spec.PolicyId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(
        RetireServicePolicyCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsurePolicyCatalogTargetAsync(command.Identity, ct);
        await _policyProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForPolicy(command.Identity, command.PolicyId), ct);
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

    private static string CorrelationForBinding(ServiceIdentity identity, string bindingId) =>
        $"{ServiceKeys.Build(identity)}:binding:{bindingId ?? string.Empty}";

    private static string CorrelationForPolicy(ServiceIdentity identity, string policyId) =>
        $"{ServiceKeys.Build(identity)}:policy:{policyId ?? string.Empty}";
}
