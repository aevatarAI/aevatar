using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ServiceDeploymentManagerGAgent : GAgentBase<ServiceDeploymentState>
{
    private readonly IServiceRevisionArtifactStore _artifactStore;
    private readonly IActivationCapabilityViewReader _capabilityViewReader;
    private readonly IActivationAdmissionEvaluator _admissionEvaluator;
    private readonly IServiceRuntimeActivator _runtimeActivator;

    public ServiceDeploymentManagerGAgent(
        IServiceRevisionArtifactStore artifactStore,
        IActivationCapabilityViewReader capabilityViewReader,
        IActivationAdmissionEvaluator admissionEvaluator,
        IServiceRuntimeActivator runtimeActivator)
    {
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _capabilityViewReader = capabilityViewReader ?? throw new ArgumentNullException(nameof(capabilityViewReader));
        _admissionEvaluator = admissionEvaluator ?? throw new ArgumentNullException(nameof(admissionEvaluator));
        _runtimeActivator = runtimeActivator ?? throw new ArgumentNullException(nameof(runtimeActivator));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleActivateAsync(ActivateServingRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeploymentIdentity(command.Identity, allowInitialize: true);
        if (string.IsNullOrWhiteSpace(command.RevisionId))
            throw new InvalidOperationException("revision_id is required.");

        var serviceKey = ServiceKeys.Build(command.Identity);
        var artifact = await _artifactStore.GetAsync(serviceKey, command.RevisionId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Prepared artifact was not found for '{serviceKey}' revision '{command.RevisionId}'.");
        var currentState = State.Clone();
        var capabilityView = await _capabilityViewReader.GetAsync(command.Identity, command.RevisionId, CancellationToken.None);
        var admissionDecision = await _admissionEvaluator.EvaluateAsync(
            new ActivationAdmissionRequest
            {
                CapabilityView = capabilityView,
            },
            CancellationToken.None);
        EnsureActivationAllowed(admissionDecision);

        if (!string.IsNullOrWhiteSpace(currentState.ActiveDeploymentId) &&
            !string.IsNullOrWhiteSpace(currentState.PrimaryActorId))
        {
            await _runtimeActivator.DeactivateAsync(
                new ServiceRuntimeDeactivationRequest(
                    command.Identity.Clone(),
                    currentState.ActiveDeploymentId,
                    currentState.ActiveRevisionId,
                    currentState.PrimaryActorId),
                CancellationToken.None);

            await PersistDomainEventAsync(new ServiceDeploymentDeactivatedEvent
            {
                Identity = command.Identity.Clone(),
                DeploymentId = currentState.ActiveDeploymentId,
                RevisionId = currentState.ActiveRevisionId,
                DeactivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        var activation = await _runtimeActivator.ActivateAsync(
            new ServiceRuntimeActivationRequest(
                command.Identity.Clone(),
                artifact,
                command.RevisionId,
                Id,
                capabilityView),
            CancellationToken.None);

        await PersistDomainEventAsync(new ServiceDeploymentActivatedEvent
        {
            Identity = command.Identity.Clone(),
            DeploymentId = activation.DeploymentId,
            RevisionId = command.RevisionId,
            PrimaryActorId = activation.PrimaryActorId,
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleDeactivateAsync(DeactivateServiceDeploymentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeploymentIdentity(command.Identity, allowInitialize: false);
        var currentState = State.Clone();
        if (string.IsNullOrWhiteSpace(currentState.ActiveDeploymentId) || string.IsNullOrWhiteSpace(currentState.PrimaryActorId))
            return;

        await _runtimeActivator.DeactivateAsync(
            new ServiceRuntimeDeactivationRequest(
                command.Identity.Clone(),
                currentState.ActiveDeploymentId,
                currentState.ActiveRevisionId,
                currentState.PrimaryActorId),
            CancellationToken.None);

        await PersistDomainEventAsync(new ServiceDeploymentDeactivatedEvent
        {
            Identity = command.Identity.Clone(),
            DeploymentId = currentState.ActiveDeploymentId,
            RevisionId = currentState.ActiveRevisionId,
            DeactivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override ServiceDeploymentState TransitionState(ServiceDeploymentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceDeploymentActivatedEvent>(ApplyActivated)
            .On<ServiceDeploymentDeactivatedEvent>(ApplyDeactivated)
            .On<ServiceDeploymentHealthChangedEvent>(ApplyHealthChanged)
            .OrCurrent();

    private static ServiceDeploymentState ApplyActivated(ServiceDeploymentState state, ServiceDeploymentActivatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? new ServiceIdentity();
        next.ActiveDeploymentId = evt.DeploymentId ?? string.Empty;
        next.ActiveRevisionId = evt.RevisionId ?? string.Empty;
        next.PrimaryActorId = evt.PrimaryActorId ?? string.Empty;
        next.Status = evt.Status;
        next.ActivatedAt = evt.ActivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.UpdatedAt = evt.ActivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "activated");
        return next;
    }

    private static ServiceDeploymentState ApplyDeactivated(ServiceDeploymentState state, ServiceDeploymentDeactivatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        next.Status = ServiceDeploymentStatus.Deactivated;
        next.UpdatedAt = evt.DeactivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "deactivated");
        return next;
    }

    private static ServiceDeploymentState ApplyHealthChanged(ServiceDeploymentState state, ServiceDeploymentHealthChangedEvent evt)
    {
        var next = state.Clone();
        next.Status = evt.Status;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "health");
        return next;
    }

    private void EnsureDeploymentIdentity(ServiceIdentity identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service deployment '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service deployment actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static string BuildEventId(ServiceIdentity? identity, string? deploymentId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{deploymentId ?? "none"}:{suffix}";
    }

    private static void EnsureActivationAllowed(ActivationAdmissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Allowed)
            return;

        var reason = decision.Violations.Count == 0
            ? "activation admission rejected."
            : string.Join("; ", decision.Violations.Select(x => $"{x.Code}:{x.SubjectId}:{x.Message}"));
        throw new InvalidOperationException(reason);
    }
}
