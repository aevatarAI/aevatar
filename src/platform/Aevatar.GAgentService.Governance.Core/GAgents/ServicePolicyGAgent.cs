using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Governance.Core.GAgents;

public sealed class ServicePolicyGAgent : GAgentBase<ServicePolicyCatalogState>
{
    public ServicePolicyGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServicePolicyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: true);
        if (State.Policies.ContainsKey(command.Spec.PolicyId))
            throw new InvalidOperationException($"Policy '{command.Spec.PolicyId}' already exists.");

        await PersistDomainEventAsync(new ServicePolicyCreatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleUpdateAsync(UpdateServicePolicyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: false);
        if (!State.Policies.ContainsKey(command.Spec.PolicyId))
            throw new InvalidOperationException($"Policy '{command.Spec.PolicyId}' was not found.");

        await PersistDomainEventAsync(new ServicePolicyUpdatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleRetireAsync(RetireServicePolicyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        EnsurePolicyExists(command.PolicyId);

        await PersistDomainEventAsync(new ServicePolicyRetiredEvent
        {
            Identity = command.Identity.Clone(),
            PolicyId = command.PolicyId ?? string.Empty,
        });
    }

    protected override ServicePolicyCatalogState TransitionState(ServicePolicyCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServicePolicyCreatedEvent>(ApplyCreated)
            .On<ServicePolicyUpdatedEvent>(ApplyUpdated)
            .On<ServicePolicyRetiredEvent>(ApplyRetired)
            .OrCurrent();

    private static ServicePolicyCatalogState ApplyCreated(ServicePolicyCatalogState state, ServicePolicyCreatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Spec?.Identity?.Clone() ?? new ServiceIdentity();
        next.Policies[evt.Spec?.PolicyId ?? string.Empty] = new ServicePolicyRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServicePolicySpec(),
            Retired = false,
        };
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, evt.Spec?.PolicyId, "created");
        return next;
    }

    private static ServicePolicyCatalogState ApplyUpdated(ServicePolicyCatalogState state, ServicePolicyUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Policies[evt.Spec?.PolicyId ?? string.Empty] = new ServicePolicyRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServicePolicySpec(),
            Retired = false,
        };
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, evt.Spec?.PolicyId, "updated");
        return next;
    }

    private static ServicePolicyCatalogState ApplyRetired(ServicePolicyCatalogState state, ServicePolicyRetiredEvent evt)
    {
        var next = state.Clone();
        var record = next.Policies[evt.PolicyId];
        record.Retired = true;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.PolicyId, "retired");
        return next;
    }

    private static void ValidateSpec(ServicePolicySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Identity);
        _ = ServiceKeys.Build(spec.Identity);
        if (string.IsNullOrWhiteSpace(spec.PolicyId))
            throw new InvalidOperationException("policy_id is required.");
    }

    private void EnsureCatalogIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service policy catalog '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service policy catalog actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private void EnsurePolicyExists(string policyId)
    {
        if (string.IsNullOrWhiteSpace(policyId))
            throw new InvalidOperationException("policy_id is required.");
        if (!State.Policies.ContainsKey(policyId))
            throw new InvalidOperationException($"Policy '{policyId}' was not found.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string? policyId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{policyId ?? "unknown"}:{suffix}";
    }
}
