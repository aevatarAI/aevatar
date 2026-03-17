using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Governance.Infrastructure.Migration;

public sealed class DefaultServiceGovernanceLegacyImporter : IServiceGovernanceLegacyImporter
{
    private readonly IEventStore _eventStore;
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceConfigurationProjectionPort _projectionPort;

    public DefaultServiceGovernanceLegacyImporter(
        IEventStore eventStore,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IServiceConfigurationProjectionPort projectionPort)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<bool> ImportIfNeededAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var configurationActorId = ServiceActorIds.Configuration(identity);
        if (await _eventStore.GetVersionAsync(configurationActorId, ct) > 0)
            return false;

        var importedState = await BuildLegacyStateAsync(identity, ct);
        if (importedState == null)
            return false;

        if (!await _runtime.ExistsAsync(configurationActorId))
            _ = await _runtime.CreateAsync<ServiceConfigurationGAgent>(configurationActorId, ct);

        await _projectionPort.EnsureProjectionAsync(configurationActorId, ct);
        await _dispatchPort.DispatchAsync(
            configurationActorId,
            BuildImportEnvelope(configurationActorId, importedState),
            ct);
        return true;
    }

    private async Task<ServiceConfigurationState?> BuildLegacyStateAsync(ServiceIdentity identity, CancellationToken ct)
    {
        var configuration = new ServiceConfigurationState
        {
            Identity = identity.Clone(),
        };
        var hasLegacyData = false;

        var bindingEvents = await _eventStore.GetEventsAsync(ServiceActorIds.Bindings(identity), ct: ct);
        if (bindingEvents.Count > 0)
        {
            hasLegacyData = true;
            FoldBindings(configuration, bindingEvents);
        }

        var endpointEvents = await _eventStore.GetEventsAsync(ServiceActorIds.EndpointCatalog(identity), ct: ct);
        if (endpointEvents.Count > 0)
        {
            hasLegacyData = true;
            FoldEndpointCatalog(configuration, endpointEvents);
        }

        var policyEvents = await _eventStore.GetEventsAsync(ServiceActorIds.Policies(identity), ct: ct);
        if (policyEvents.Count > 0)
        {
            hasLegacyData = true;
            FoldPolicies(configuration, policyEvents);
        }

        return hasLegacyData ? configuration : null;
    }

    private static void FoldBindings(ServiceConfigurationState configuration, IReadOnlyList<StateEvent> events)
    {
        foreach (var committed in events)
        {
            var payload = committed.EventData;
            if (payload.Is(ServiceBindingCreatedEvent.Descriptor))
            {
                var evt = payload.Unpack<ServiceBindingCreatedEvent>();
                configuration.Bindings[evt.Spec.BindingId] = new ServiceBindingRecordState
                {
                    Spec = evt.Spec.Clone(),
                    Retired = false,
                };
                continue;
            }

            if (payload.Is(ServiceBindingUpdatedEvent.Descriptor))
            {
                var evt = payload.Unpack<ServiceBindingUpdatedEvent>();
                configuration.Bindings[evt.Spec.BindingId] = new ServiceBindingRecordState
                {
                    Spec = evt.Spec.Clone(),
                    Retired = false,
                };
                continue;
            }

            if (payload.Is(ServiceBindingRetiredEvent.Descriptor) &&
                configuration.Bindings.TryGetValue(payload.Unpack<ServiceBindingRetiredEvent>().BindingId, out var binding))
            {
                binding.Retired = true;
            }
        }
    }

    private static void FoldEndpointCatalog(ServiceConfigurationState configuration, IReadOnlyList<StateEvent> events)
    {
        foreach (var committed in events)
        {
            var payload = committed.EventData;
            if (payload.Is(ServiceEndpointCatalogCreatedEvent.Descriptor))
            {
                configuration.EndpointCatalog = payload.Unpack<ServiceEndpointCatalogCreatedEvent>().Spec.Clone();
                continue;
            }

            if (payload.Is(ServiceEndpointCatalogUpdatedEvent.Descriptor))
                configuration.EndpointCatalog = payload.Unpack<ServiceEndpointCatalogUpdatedEvent>().Spec.Clone();
        }
    }

    private static void FoldPolicies(ServiceConfigurationState configuration, IReadOnlyList<StateEvent> events)
    {
        foreach (var committed in events)
        {
            var payload = committed.EventData;
            if (payload.Is(ServicePolicyCreatedEvent.Descriptor))
            {
                var evt = payload.Unpack<ServicePolicyCreatedEvent>();
                configuration.Policies[evt.Spec.PolicyId] = new ServicePolicyRecordState
                {
                    Spec = evt.Spec.Clone(),
                    Retired = false,
                };
                continue;
            }

            if (payload.Is(ServicePolicyUpdatedEvent.Descriptor))
            {
                var evt = payload.Unpack<ServicePolicyUpdatedEvent>();
                configuration.Policies[evt.Spec.PolicyId] = new ServicePolicyRecordState
                {
                    Spec = evt.Spec.Clone(),
                    Retired = false,
                };
                continue;
            }

            if (payload.Is(ServicePolicyRetiredEvent.Descriptor) &&
                configuration.Policies.TryGetValue(payload.Unpack<ServicePolicyRetiredEvent>().PolicyId, out var policy))
            {
                policy.Retired = true;
            }
        }
    }

    private static EventEnvelope BuildImportEnvelope(string actorId, ServiceConfigurationState state)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ImportLegacyServiceConfigurationCommand
            {
                State = state.Clone(),
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.governance.legacy-import", actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = $"{ServiceKeys.Build(state.Identity)}:legacy-import",
            },
        };
    }
}
