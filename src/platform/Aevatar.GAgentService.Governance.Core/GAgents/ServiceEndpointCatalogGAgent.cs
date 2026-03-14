using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgentService.Governance.Core.GAgents;

public sealed class ServiceEndpointCatalogGAgent : GAgentBase<ServiceEndpointCatalogState>
{
    public ServiceEndpointCatalogGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServiceEndpointCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: true);
        if (State.Spec?.Identity != null && !string.IsNullOrWhiteSpace(State.Spec.Identity.ServiceId))
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
        ValidateSpec(command.Spec);
        EnsureCatalogIdentity(command.Spec.Identity, allowInitialize: false);

        await PersistDomainEventAsync(new ServiceEndpointCatalogUpdatedEvent
        {
            Spec = command.Spec.Clone(),
        });
    }

    protected override ServiceEndpointCatalogState TransitionState(ServiceEndpointCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceEndpointCatalogCreatedEvent>(ApplyCreated)
            .On<ServiceEndpointCatalogUpdatedEvent>(ApplyUpdated)
            .OrCurrent();

    private static ServiceEndpointCatalogState ApplyCreated(ServiceEndpointCatalogState state, ServiceEndpointCatalogCreatedEvent evt)
    {
        var next = state.Clone();
        next.Spec = evt.Spec?.Clone() ?? new ServiceEndpointCatalogSpec();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "created");
        return next;
    }

    private static ServiceEndpointCatalogState ApplyUpdated(ServiceEndpointCatalogState state, ServiceEndpointCatalogUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Spec = evt.Spec?.Clone() ?? new ServiceEndpointCatalogSpec();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "updated");
        return next;
    }

    private static void ValidateSpec(ServiceEndpointCatalogSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Identity);
        _ = ServiceKeys.Build(spec.Identity);
        if (spec.Endpoints.Count == 0)
            throw new InvalidOperationException("endpoint catalog requires at least one endpoint.");
    }

    private void EnsureCatalogIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Spec?.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service endpoint catalog '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service endpoint catalog actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:endpoint-catalog:{suffix}";
    }
}
