using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Governance.Core.GAgents;

public sealed class ServiceBindingManagerGAgent : GAgentBase<ServiceBindingCatalogState>
{
    public ServiceBindingManagerGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServiceBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: true);
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
        ValidateSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: false);
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
        EnsureCatalogIdentity(command.Identity, allowInitialize: false);
        EnsureBindingExists(command.BindingId);

        await PersistDomainEventAsync(new ServiceBindingRetiredEvent
        {
            Identity = command.Identity.Clone(),
            BindingId = command.BindingId ?? string.Empty,
        });
    }

    protected override ServiceBindingCatalogState TransitionState(ServiceBindingCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceBindingCreatedEvent>(ApplyCreated)
            .On<ServiceBindingUpdatedEvent>(ApplyUpdated)
            .On<ServiceBindingRetiredEvent>(ApplyRetired)
            .OrCurrent();

    private static ServiceBindingCatalogState ApplyCreated(ServiceBindingCatalogState state, ServiceBindingCreatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Spec?.Identity?.Clone() ?? new ServiceIdentity();
        next.Bindings[evt.Spec?.BindingId ?? string.Empty] = new ServiceBindingRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServiceBindingSpec(),
            Retired = false,
        };
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, evt.Spec?.BindingId, "created");
        return next;
    }

    private static ServiceBindingCatalogState ApplyUpdated(ServiceBindingCatalogState state, ServiceBindingUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Bindings[evt.Spec?.BindingId ?? string.Empty] = new ServiceBindingRecordState
        {
            Spec = evt.Spec?.Clone() ?? new ServiceBindingSpec(),
            Retired = false,
        };
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, evt.Spec?.BindingId, "updated");
        return next;
    }

    private static ServiceBindingCatalogState ApplyRetired(ServiceBindingCatalogState state, ServiceBindingRetiredEvent evt)
    {
        var next = state.Clone();
        var record = next.Bindings[evt.BindingId];
        record.Retired = true;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.BindingId, "retired");
        return next;
    }

    private static void ValidateSpec(ServiceBindingSpec spec)
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

    private void EnsureCatalogIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service binding catalog '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service binding catalog actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private void EnsureBindingExists(string bindingId)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
            throw new InvalidOperationException("binding_id is required.");
        if (!State.Bindings.ContainsKey(bindingId))
            throw new InvalidOperationException($"Binding '{bindingId}' was not found.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string? bindingId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{bindingId ?? "unknown"}:{suffix}";
    }
}
