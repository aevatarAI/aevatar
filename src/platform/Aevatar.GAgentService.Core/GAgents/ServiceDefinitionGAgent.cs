using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ServiceDefinitionGAgent : GAgentBase<ServiceDefinitionState>
{
    public ServiceDefinitionGAgent()
    {
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
    }

    [EventHandler]
    public async Task HandleRepublishAsync(RepublishServiceDefinitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);

        await PersistDomainEventAsync(new ServiceDefinitionRepublishedEvent
        {
            Spec = State.Spec?.Clone() ?? new ServiceDefinitionSpec(),
        });
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
    }

    protected override ServiceDefinitionState TransitionState(ServiceDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceDefinitionCreatedEvent>(ApplyCreated)
            .On<ServiceDefinitionUpdatedEvent>(ApplyUpdated)
            .On<ServiceDefinitionRepublishedEvent>(ApplyRepublished)
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
        next.Spec = evt.Spec?.Clone() ?? new ServiceDefinitionSpec();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "updated");
        return next;
    }

    private static ServiceDefinitionState ApplyRepublished(ServiceDefinitionState state, ServiceDefinitionRepublishedEvent evt)
    {
        var next = state.Clone();
        next.Spec = evt.Spec?.Clone() ?? new ServiceDefinitionSpec();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "republished");
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
}
