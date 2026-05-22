using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Application.Internal;
using Google.Protobuf;

namespace Aevatar.GAgentService.Governance.Application.Services;

public sealed class ServiceGovernanceCommandApplicationService : IServiceGovernanceCommandPort
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceGovernanceCommandTargetProvisioner _targetProvisioner;

    public ServiceGovernanceCommandApplicationService(
        IActorDispatchPort dispatchPort,
        IServiceGovernanceCommandTargetProvisioner targetProvisioner)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _targetProvisioner = targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner));
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(
        CreateServiceBindingCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForBinding(command.Spec.Identity, command.Spec.BindingId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(
        UpdateServiceBindingCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForBinding(command.Spec.Identity, command.Spec.BindingId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(
        RetireServiceBindingCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForBinding(command.Identity, command.BindingId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(
        CreateServiceEndpointCatalogCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(
        UpdateServiceEndpointCatalogCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(
        CreateServicePolicyCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForPolicy(command.Spec.Identity, command.Spec.PolicyId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(
        UpdateServicePolicyCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Spec.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForPolicy(command.Spec.Identity, command.Spec.PolicyId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(
        RetireServicePolicyCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureConfigurationTargetAsync(command.Identity, ct);
        return await DispatchAsync(actorId, command, CorrelationForPolicy(command.Identity, command.PolicyId), ct);
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

    private static string CorrelationForBinding(ServiceIdentity identity, string bindingId) =>
        $"{ServiceKeys.Build(identity)}:binding:{bindingId ?? string.Empty}";

    private static string CorrelationForPolicy(ServiceIdentity identity, string policyId) =>
        $"{ServiceKeys.Build(identity)}:policy:{policyId ?? string.Empty}";
}
