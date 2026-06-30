using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.deployment-manager")]
public sealed class ServiceDeploymentManagerGAgent : GAgentBase<ServiceDeploymentState>
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;
    private readonly IActivationCapabilityViewReader _capabilityViewReader;
    private readonly IActivationAdmissionEvaluator _admissionEvaluator;
    private readonly IServiceRuntimeActivator _runtimeActivator;

    public ServiceDeploymentManagerGAgent(
        IActorDispatchPort dispatchPort,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IActivationCapabilityViewReader capabilityViewReader,
        IActivationAdmissionEvaluator admissionEvaluator,
        IServiceRuntimeActivator runtimeActivator)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        _capabilityViewReader = capabilityViewReader ?? throw new ArgumentNullException(nameof(capabilityViewReader));
        _admissionEvaluator = admissionEvaluator ?? throw new ArgumentNullException(nameof(admissionEvaluator));
        _runtimeActivator = runtimeActivator ?? throw new ArgumentNullException(nameof(runtimeActivator));
        InitializeId();
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleActivateAsync(ActivateServiceRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeploymentIdentity(command.Identity, allowInitialize: true);
        if (string.IsNullOrWhiteSpace(command.RevisionId))
            throw new InvalidOperationException("revision_id is required.");

        var revisionCatalog = await _revisionCatalogQueryReader.GetAsync(command.Identity, CancellationToken.None);
        // Refactor (iter100/cluster-100): Old activation read prepared artifacts from a process-local store. / New activation consumes the projected revision readmodel catalog.
        // The bind chain dispatches prepare->publish->activate fire-and-forget, so the revision-catalog
        // projection can lag behind the just-committed prepare event when this handler runs. Treat an
        // unmaterialized prepared artifact as transient and re-arm a bounded self-continuation
        // (delay/timeout 事件化 + self continuation 事件化) instead of failing the activation terminally.
        if (!revisionCatalog.TryGetPreparedArtifact(command.RevisionId, out var artifact))
        {
            if (revisionCatalog.IsRevisionPreparationFailed(command.RevisionId))
                throw new InvalidOperationException(
                    $"Prepared artifact for '{ServiceKeys.Build(command.Identity)}' revision '{command.RevisionId}' failed preparation.");

            await ReArmActivationForProjectionLagAsync(command);
            return;
        }

        var currentState = State.Clone();
        var capabilityView = await _capabilityViewReader.GetAsync(command.Identity, command.RevisionId, CancellationToken.None);
        var admissionDecision = await _admissionEvaluator.EvaluateAsync(
            new ActivationAdmissionRequest
            {
                CapabilityView = capabilityView,
            },
            CancellationToken.None);
        EnsureActivationAllowed(admissionDecision);

        var existingActive = currentState.Deployments.Values.FirstOrDefault(x =>
            string.Equals(x.RevisionId, command.RevisionId, StringComparison.Ordinal) &&
            x.Status == ServiceDeploymentStatus.Active &&
            !string.IsNullOrWhiteSpace(x.PrimaryActorId));
        if (existingActive != null)
        {
            await DispatchResolvedServingTargetsAsync(
                command.Identity,
                command.RevisionId,
                existingActive.DeploymentId,
                existingActive.PrimaryActorId,
                artifact,
                CancellationToken.None);
            return;
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
        await DispatchResolvedServingTargetsAsync(
            command.Identity,
            command.RevisionId,
            activation.DeploymentId,
            activation.PrimaryActorId,
            artifact,
            CancellationToken.None);
    }

    // Bounded retry budget for tolerating revision-catalog projection lag during activation.
    private const string ActivationProjectionRetryCallbackPrefix = "service-deployment-activation-projection-retry";
    private static readonly TimeSpan ActivationProjectionRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationProjectionRetryBudget = TimeSpan.FromMinutes(5);

    private async Task ReArmActivationForProjectionLagAsync(ActivateServiceRevisionCommand command)
    {
        var nowUtc = DateTime.UtcNow;
        var deadlineUtc = command.ActivationDeadlineAt?.ToDateTime() ?? nowUtc + ActivationProjectionRetryBudget;
        if (nowUtc >= deadlineUtc)
        {
            // Projection never caught up within the bounded budget: surface the honest terminal failure
            // so the runtime/operator can observe it (matches the pre-fix behaviour, just deferred).
            throw new InvalidOperationException(
                $"Prepared artifact for '{ServiceKeys.Build(command.Identity)}' revision '{command.RevisionId}' was not found before the activation deadline.");
        }

        var remaining = deadlineUtc - nowUtc;
        var dueTime = remaining < ActivationProjectionRetryInterval ? remaining : ActivationProjectionRetryInterval;

        var retryCommand = command.Clone();
        retryCommand.ActivationDeadlineAt = Timestamp.FromDateTime(deadlineUtc);

        Logger.LogInformation(
            "Activation deferred: revision-catalog projection not yet visible for service '{ServiceKey}' revision '{RevisionId}'. Re-arming activation in {DueSeconds:F1}s (deadline {Deadline:O}).",
            ServiceKeys.Build(command.Identity),
            command.RevisionId,
            dueTime.TotalSeconds,
            deadlineUtc);

        // Revision-scoped callback id so concurrent activations of different revisions on this deployment
        // actor coalesce per-revision (a re-arm replaces only its own prior pending retry, not another's).
        await ScheduleSelfDurableTimeoutAsync(
            $"{ActivationProjectionRetryCallbackPrefix}:{command.RevisionId}",
            dueTime,
            retryCommand);
    }

    [EventHandler]
    public async Task HandleDeactivateAsync(DeactivateServiceDeploymentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureDeploymentIdentity(command.Identity, allowInitialize: false);
        if (string.IsNullOrWhiteSpace(command.DeploymentId))
            throw new InvalidOperationException("deployment_id is required.");

        if (!State.Deployments.TryGetValue(command.DeploymentId, out var deployment) ||
            string.IsNullOrWhiteSpace(deployment.PrimaryActorId) ||
            deployment.Status != ServiceDeploymentStatus.Active)
        {
            return;
        }

        await _runtimeActivator.DeactivateAsync(
            new ServiceRuntimeDeactivationRequest(
                command.Identity.Clone(),
                deployment.DeploymentId,
                deployment.RevisionId,
                deployment.PrimaryActorId),
            CancellationToken.None);

        await PersistDomainEventAsync(new ServiceDeploymentDeactivatedEvent
        {
            Identity = command.Identity.Clone(),
            DeploymentId = deployment.DeploymentId,
            RevisionId = deployment.RevisionId,
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
        next.Deployments[evt.DeploymentId] = new ServiceDeploymentRecord
        {
            DeploymentId = evt.DeploymentId ?? string.Empty,
            RevisionId = evt.RevisionId ?? string.Empty,
            PrimaryActorId = evt.PrimaryActorId ?? string.Empty,
            Status = evt.Status,
            ActivatedAt = evt.ActivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            UpdatedAt = evt.ActivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
        };
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "activated");
        return next;
    }

    private static ServiceDeploymentState ApplyDeactivated(ServiceDeploymentState state, ServiceDeploymentDeactivatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        if (next.Deployments.TryGetValue(evt.DeploymentId, out var deployment))
        {
            next.Deployments[evt.DeploymentId] = new ServiceDeploymentRecord
            {
                DeploymentId = deployment.DeploymentId,
                RevisionId = deployment.RevisionId,
                PrimaryActorId = deployment.PrimaryActorId,
                Status = ServiceDeploymentStatus.Deactivated,
                ActivatedAt = deployment.ActivatedAt?.Clone(),
                UpdatedAt = evt.DeactivatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            };
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.DeploymentId, "deactivated");
        return next;
    }

    private static ServiceDeploymentState ApplyHealthChanged(ServiceDeploymentState state, ServiceDeploymentHealthChangedEvent evt)
    {
        var next = state.Clone();
        if (next.Deployments.TryGetValue(evt.DeploymentId, out var deployment))
        {
            next.Deployments[evt.DeploymentId] = new ServiceDeploymentRecord
            {
                DeploymentId = deployment.DeploymentId,
                RevisionId = deployment.RevisionId,
                PrimaryActorId = deployment.PrimaryActorId,
                Status = evt.Status,
                ActivatedAt = deployment.ActivatedAt?.Clone(),
                UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            };
        }

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

    private async Task DispatchResolvedServingTargetsAsync(
        ServiceIdentity identity,
        string revisionId,
        string deploymentId,
        string primaryActorId,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct)
    {
        var actorId = ServiceActorIds.ServingSet(identity);
        await _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                new ReplaceResolvedServiceServingTargetsCommand
                {
                    Identity = identity.Clone(),
                    Reason = "deployment activation",
                    Targets =
                    {
                        new ServiceServingTargetSpec
                        {
                            DeploymentId = deploymentId ?? string.Empty,
                            RevisionId = revisionId ?? string.Empty,
                            PrimaryActorId = primaryActorId ?? string.Empty,
                            AllocationWeight = 100,
                            ServingState = ServiceServingState.Active,
                            EnabledEndpointIds = { artifact.Endpoints.Select(x => x.EndpointId) },
                        },
                    },
                }),
            ct);
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.deployment", actorId),
            Propagation = new EnvelopePropagation(),
        };
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
