using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.definition")]
public sealed class ServiceDefinitionGAgent : GAgentBase<ServiceDefinitionState>
{
    private readonly IActorDispatchPort _dispatchPort;

    public ServiceDefinitionGAgent(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServiceDefinitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        var currentSpec = State.Spec?.Clone();
        if (currentSpec?.Identity != null && !string.IsNullOrWhiteSpace(currentSpec.Identity.ServiceId))
            throw new InvalidOperationException($"Service definition '{ServiceKeys.Build(command.Spec.Identity)}' already exists.");

        await PersistDomainEventAsync(new ServiceDefinitionCreatedEvent
        {
            Spec = command.Spec.Clone(),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleUpdateAsync(UpdateServiceDefinitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        EnsureExistingIdentity(command.Spec.Identity);
        await PersistDomainEventAsync(new ServiceDefinitionUpdatedEvent
        {
            Spec = command.Spec.Clone(),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleUpdateExternalExposureAsync(UpdateServiceExternalExposureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);
        await PersistDomainEventAsync(new ServiceExternalExposureUpdatedEvent
        {
            Identity = command.Identity.Clone(),
            ExternalExposure = command.ExternalExposure?.Clone() ?? new ExternalExposure(),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);
        if (string.IsNullOrWhiteSpace(command.RevisionId))
            throw new InvalidOperationException("revision_id is required.");

        await PersistDomainEventAsync(new DefaultServingRevisionChangedEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId,
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    protected override ServiceDefinitionState TransitionState(ServiceDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceDefinitionCreatedEvent>(ApplyCreated)
            .On<ServiceDefinitionUpdatedEvent>(ApplyUpdated)
            .On<ServiceExternalExposureUpdatedEvent>(ApplyExternalExposureUpdated)
            .On<DefaultServingRevisionChangedEvent>(ApplyDefaultServingRevisionChanged)
            .OrCurrent();

    private static ServiceDefinitionState ApplyCreated(ServiceDefinitionState state, ServiceDefinitionCreatedEvent evt)
    {
        var next = state.Clone();
        next.Spec = evt.Spec?.Clone() ?? new ServiceDefinitionSpec();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "created");
        return next;
    }

    private static ServiceDefinitionState ApplyUpdated(ServiceDefinitionState state, ServiceDefinitionUpdatedEvent evt)
    {
        var next = state.Clone();
        var spec = evt.Spec?.Clone() ?? new ServiceDefinitionSpec();
        if (spec.ExternalExposure == null && state.Spec?.ExternalExposure != null)
            spec.ExternalExposure = state.Spec.ExternalExposure.Clone();
        next.Spec = spec;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "updated");
        return next;
    }

    private static ServiceDefinitionState ApplyExternalExposureUpdated(
        ServiceDefinitionState state,
        ServiceExternalExposureUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Spec.ExternalExposure = evt.ExternalExposure?.Clone() ?? new ExternalExposure();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, "external-exposure-updated");
        return next;
    }

    private static ServiceDefinitionState ApplyDefaultServingRevisionChanged(
        ServiceDefinitionState state,
        DefaultServingRevisionChangedEvent evt)
    {
        var next = state.Clone();
        next.DefaultServingRevisionId = evt.RevisionId ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"default-serving:{evt.RevisionId}");
        return next;
    }

    private static void ValidateSpec(ServiceDefinitionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Identity == null)
            throw new InvalidOperationException("service identity is required.");
        _ = ServiceKeys.Build(spec.Identity);
        if (spec.Endpoints.Count == 0)
            throw new InvalidOperationException("service endpoints are required.");
        if (spec.ExternalExposure != null &&
            string.IsNullOrWhiteSpace(spec.ExternalExposure.NyxidSlug) &&
            spec.ExternalExposure.RegisteredAt == null)
        {
            throw new InvalidOperationException(
                "external_exposure.nyxid_slug or external_exposure.registered_at is required when external_exposure is specified.");
        }
    }

    private void EnsureExistingIdentity(ServiceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Spec?.Identity?.Clone();
        var existing = currentIdentity == null ? string.Empty : ServiceKeys.Build(currentIdentity);
        if (existing.Length == 0)
            throw new InvalidOperationException($"Service definition '{requested}' does not exist.");
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service definition actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{suffix}";
    }

    private Task DispatchInvocationCatalogObservationAsync(CancellationToken ct)
    {
        var spec = State.Spec;
        var identity = spec?.Identity;
        if (identity == null || string.IsNullOrWhiteSpace(identity.ServiceId))
            return Task.CompletedTask;

        var actorId = ServiceActorIds.InvocationCatalog(identity);
        return _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                new ObserveServiceInvocationCatalogCommand
                {
                    Identity = identity.Clone(),
                    ServiceEndpoints = { spec!.Endpoints.Select(ToDescriptor) },
                    SourceCatalogVersion = State.LastAppliedEventVersion,
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ct);
    }

    private static ServiceEndpointDescriptor ToDescriptor(ServiceEndpointSpec endpoint) =>
        new()
        {
            EndpointId = endpoint.EndpointId ?? string.Empty,
            DisplayName = endpoint.DisplayName ?? string.Empty,
            Kind = endpoint.Kind,
            RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
            Description = endpoint.Description ?? string.Empty,
        };

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.definition", actorId),
            Propagation = new EnvelopePropagation(),
        };
}
