using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Governance.Core.GAgents;

public sealed class ServiceConfigurationGAgent : GAgentBase<ServiceConfigurationState>
{
    public ServiceConfigurationGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServiceBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateBindingSpec(command.Spec);
        EnsureConfigurationIdentity(command.Spec.Identity, allowInitialize: true);
        if (State.Bindings.ContainsKey(command.Spec.BindingId))
            throw new InvalidOperationException($"Binding '{command.Spec.BindingId}' already exists.");

        await PersistDomainEventAsync(new ServiceBindingCreatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleUpdateAsync(UpdateServiceBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateBindingSpec(command.Spec);
        EnsureConfigurationIdentity(command.Spec.Identity, allowInitialize: false);
        if (!State.Bindings.ContainsKey(command.Spec.BindingId))
            throw new InvalidOperationException($"Binding '{command.Spec.BindingId}' was not found.");

        await PersistDomainEventAsync(new ServiceBindingUpdatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleRetireAsync(RetireServiceBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureConfigurationIdentity(command.Identity, allowInitialize: false);
        EnsureBindingExists(command.BindingId);

        await PersistDomainEventAsync(new ServiceBindingRetiredEvent
        {
            Identity = command.Identity.Clone(),
            BindingId = command.BindingId ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServiceEndpointCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEndpointCatalog(command.Spec);
        EnsureConfigurationIdentity(command.Spec.Identity, allowInitialize: true);
        if (State.EndpointCatalog?.Identity != null && !string.IsNullOrWhiteSpace(State.EndpointCatalog.Identity.ServiceId))
            throw new InvalidOperationException($"Endpoint catalog '{ServiceKeys.Build(command.Spec.Identity)}' already exists.");

        await PersistDomainEventAsync(new ServiceEndpointCatalogCreatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleUpdateAsync(UpdateServiceEndpointCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEndpointCatalog(command.Spec);
        EnsureConfigurationIdentity(command.Spec.Identity, allowInitialize: false);
        EnsureEndpointCatalogExists();

        await PersistDomainEventAsync(new ServiceEndpointCatalogUpdatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServicePolicyCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidatePolicySpec(command.Spec);
        EnsureConfigurationIdentity(command.Spec.Identity, allowInitialize: true);
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
        ValidatePolicySpec(command.Spec);
        EnsureConfigurationIdentity(command.Spec.Identity, allowInitialize: false);
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
        EnsureConfigurationIdentity(command.Identity, allowInitialize: false);
        EnsurePolicyExists(command.PolicyId);

        await PersistDomainEventAsync(new ServicePolicyRetiredEvent
        {
            Identity = command.Identity.Clone(),
            PolicyId = command.PolicyId ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleImportAsync(ImportLegacyServiceConfigurationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.State);
        ArgumentNullException.ThrowIfNull(command.State.Identity);
        EnsureImportedConfigurationState(command.State);

        if (HasInitializedConfiguration())
        {
            EnsureConfigurationIdentity(command.State.Identity, allowInitialize: false);
            return;
        }

        await PersistDomainEventAsync(new LegacyServiceConfigurationImportedEvent
        {
            State = command.State.Clone(),
        });
    }

    protected override ServiceConfigurationState TransitionState(ServiceConfigurationState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<LegacyServiceConfigurationImportedEvent>(ApplyLegacyImported)
            .On<ServiceBindingCreatedEvent>(ApplyBindingCreated)
            .On<ServiceBindingUpdatedEvent>(ApplyBindingUpdated)
            .On<ServiceBindingRetiredEvent>(ApplyBindingRetired)
            .On<ServiceEndpointCatalogCreatedEvent>(ApplyEndpointCatalogCreated)
            .On<ServiceEndpointCatalogUpdatedEvent>(ApplyEndpointCatalogUpdated)
            .On<ServicePolicyCreatedEvent>(ApplyPolicyCreated)
            .On<ServicePolicyUpdatedEvent>(ApplyPolicyUpdated)
            .On<ServicePolicyRetiredEvent>(ApplyPolicyRetired)
            .OrCurrent();

    private static ServiceConfigurationState ApplyLegacyImported(ServiceConfigurationState state, LegacyServiceConfigurationImportedEvent evt)
    {
        var next = evt.State?.Clone() ?? new ServiceConfigurationState();
        Stamp(
            next,
            state.LastAppliedEventVersion + 1,
            BuildEventId(next.Identity, "configuration", "legacy-imported"));
        return next;
    }

    private static ServiceConfigurationState ApplyBindingCreated(ServiceConfigurationState state, ServiceBindingCreatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Spec?.Identity?.Clone() ?? new ServiceIdentity();
        next.Bindings[evt.Spec?.BindingId ?? string.Empty] = new ServiceBindingRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServiceBindingSpec(),
            Retired = false,
        };
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Spec?.Identity, evt.Spec?.BindingId, "binding-created"));
        return next;
    }

    private static ServiceConfigurationState ApplyBindingUpdated(ServiceConfigurationState state, ServiceBindingUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Bindings[evt.Spec?.BindingId ?? string.Empty] = new ServiceBindingRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServiceBindingSpec(),
            Retired = false,
        };
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Spec?.Identity, evt.Spec?.BindingId, "binding-updated"));
        return next;
    }

    private static ServiceConfigurationState ApplyBindingRetired(ServiceConfigurationState state, ServiceBindingRetiredEvent evt)
    {
        var next = state.Clone();
        var record = next.Bindings[evt.BindingId];
        record.Retired = true;
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Identity, evt.BindingId, "binding-retired"));
        return next;
    }

    private static ServiceConfigurationState ApplyEndpointCatalogCreated(ServiceConfigurationState state, ServiceEndpointCatalogCreatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Spec?.Identity?.Clone() ?? next.Identity ?? new ServiceIdentity();
        next.EndpointCatalog = evt.Spec?.Clone() ?? new ServiceEndpointCatalogSpec();
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Spec?.Identity, "endpoint-catalog", "created"));
        return next;
    }

    private static ServiceConfigurationState ApplyEndpointCatalogUpdated(ServiceConfigurationState state, ServiceEndpointCatalogUpdatedEvent evt)
    {
        var next = state.Clone();
        next.EndpointCatalog = evt.Spec?.Clone() ?? new ServiceEndpointCatalogSpec();
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Spec?.Identity, "endpoint-catalog", "updated"));
        return next;
    }

    private static ServiceConfigurationState ApplyPolicyCreated(ServiceConfigurationState state, ServicePolicyCreatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Spec?.Identity?.Clone() ?? next.Identity ?? new ServiceIdentity();
        next.Policies[evt.Spec?.PolicyId ?? string.Empty] = new ServicePolicyRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServicePolicySpec(),
            Retired = false,
        };
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Spec?.Identity, evt.Spec?.PolicyId, "policy-created"));
        return next;
    }

    private static ServiceConfigurationState ApplyPolicyUpdated(ServiceConfigurationState state, ServicePolicyUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Policies[evt.Spec?.PolicyId ?? string.Empty] = new ServicePolicyRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServicePolicySpec(),
            Retired = false,
        };
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Spec?.Identity, evt.Spec?.PolicyId, "policy-updated"));
        return next;
    }

    private static ServiceConfigurationState ApplyPolicyRetired(ServiceConfigurationState state, ServicePolicyRetiredEvent evt)
    {
        var next = state.Clone();
        var record = next.Policies[evt.PolicyId];
        record.Retired = true;
        Stamp(next, state.LastAppliedEventVersion + 1, BuildEventId(evt.Identity, evt.PolicyId, "policy-retired"));
        return next;
    }

    private static void Stamp(ServiceConfigurationState state, long version, string eventId)
    {
        state.LastAppliedEventVersion = version;
        state.LastEventId = eventId;
    }

    private void EnsureConfigurationIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service configuration '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service configuration actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private void EnsureBindingExists(string bindingId)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new InvalidOperationException("binding_id is required.");
        if (!State.Bindings.ContainsKey(bindingId))
            throw new InvalidOperationException($"Binding '{bindingId}' was not found.");
    }

    private void EnsureEndpointCatalogExists()
    {
        if (State.EndpointCatalog?.Identity == null ||
            string.IsNullOrWhiteSpace(State.EndpointCatalog.Identity.ServiceId))
        {
            var requested = ServiceKeys.Build(State.Identity);
            throw new InvalidOperationException($"Endpoint catalog '{requested}' does not exist.");
        }
    }

    private void EnsurePolicyExists(string policyId)
    {
        if (string.IsNullOrWhiteSpace(policyId))
            throw new InvalidOperationException("policy_id is required.");
        if (!State.Policies.ContainsKey(policyId))
            throw new InvalidOperationException($"Policy '{policyId}' was not found.");
    }

    private bool HasInitializedConfiguration()
    {
        return State.Identity != null &&
               !string.IsNullOrWhiteSpace(State.Identity.ServiceId);
    }

    private static void ValidateBindingSpec(ServiceBindingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Identity);
        _ = ServiceKeys.Build(spec.Identity);
        if (string.IsNullOrWhiteSpace(spec.BindingId))
            throw new InvalidOperationException("binding_id is required.");
        if (spec.BindingKind == ServiceBindingKind.Unspecified)
            throw new InvalidOperationException("binding_kind is required.");
        if (spec.TargetCase == ServiceBindingSpec.TargetOneofCase.None)
            throw new InvalidOperationException("binding target is required.");
        if (spec.BindingKind == ServiceBindingKind.Service &&
            spec.TargetCase != ServiceBindingSpec.TargetOneofCase.ServiceRef)
        {
            throw new InvalidOperationException("service binding requires service_ref target.");
        }

        if (spec.BindingKind == ServiceBindingKind.Connector &&
            spec.TargetCase != ServiceBindingSpec.TargetOneofCase.ConnectorRef)
        {
            throw new InvalidOperationException("connector binding requires connector_ref target.");
        }

        if (spec.BindingKind == ServiceBindingKind.Secret &&
            spec.TargetCase != ServiceBindingSpec.TargetOneofCase.SecretRef)
        {
            throw new InvalidOperationException("secret binding requires secret_ref target.");
        }
    }

    private static void ValidateEndpointCatalog(ServiceEndpointCatalogSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Identity);
        _ = ServiceKeys.Build(spec.Identity);
        if (spec.Endpoints.Count == 0)
            throw new InvalidOperationException("endpoint catalog requires at least one endpoint.");
    }

    private static void ValidatePolicySpec(ServicePolicySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Identity);
        _ = ServiceKeys.Build(spec.Identity);
        if (string.IsNullOrWhiteSpace(spec.PolicyId))
            throw new InvalidOperationException("policy_id is required.");
    }

    private static void EnsureImportedConfigurationState(ServiceConfigurationState state)
    {
        _ = ServiceKeys.Build(state.Identity);

        foreach (var (_, binding) in state.Bindings)
        {
            ValidateBindingSpec(binding.Spec);
        }

        if (state.EndpointCatalog?.Identity != null &&
            !string.IsNullOrWhiteSpace(state.EndpointCatalog.Identity.ServiceId))
        {
            ValidateEndpointCatalog(state.EndpointCatalog);
        }

        foreach (var (_, policy) in state.Policies)
        {
            ValidatePolicySpec(policy.Spec);
        }
    }

    private static string BuildEventId(ServiceIdentity? identity, string? entityId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{entityId ?? "configuration"}:{suffix}";
    }
}
