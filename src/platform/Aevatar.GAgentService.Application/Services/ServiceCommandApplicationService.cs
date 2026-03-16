using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Internal;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceCommandApplicationService : IServiceCommandPort
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IServiceCommandTargetProvisioner _targetProvisioner;
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceDeploymentCatalogQueryReader _deploymentQueryReader;
    private readonly IServiceServingSetQueryReader _servingSetQueryReader;
    private readonly IServiceCatalogProjectionPort _catalogProjectionPort;
    private readonly IServiceRevisionCatalogProjectionPort _revisionProjectionPort;
    private readonly IServiceDeploymentCatalogProjectionPort _deploymentProjectionPort;
    private readonly IServiceServingSetProjectionPort _servingSetProjectionPort;
    private readonly IServiceRolloutProjectionPort _rolloutProjectionPort;
    private readonly IServiceTrafficViewProjectionPort _trafficViewProjectionPort;
    private readonly IServiceRevisionArtifactStore _artifactStore;

    public ServiceCommandApplicationService(
        IActorDispatchPort dispatchPort,
        IServiceCommandTargetProvisioner targetProvisioner,
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceCatalogProjectionPort catalogProjectionPort,
        IServiceRevisionCatalogProjectionPort revisionProjectionPort,
        IServiceDeploymentCatalogQueryReader deploymentQueryReader,
        IServiceServingSetQueryReader servingSetQueryReader,
        IServiceDeploymentCatalogProjectionPort deploymentProjectionPort,
        IServiceServingSetProjectionPort servingSetProjectionPort,
        IServiceRolloutProjectionPort rolloutProjectionPort,
        IServiceTrafficViewProjectionPort trafficViewProjectionPort,
        IServiceRevisionArtifactStore artifactStore)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _targetProvisioner = targetProvisioner ?? throw new ArgumentNullException(nameof(targetProvisioner));
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _deploymentQueryReader = deploymentQueryReader ?? throw new ArgumentNullException(nameof(deploymentQueryReader));
        _servingSetQueryReader = servingSetQueryReader ?? throw new ArgumentNullException(nameof(servingSetQueryReader));
        _catalogProjectionPort = catalogProjectionPort ?? throw new ArgumentNullException(nameof(catalogProjectionPort));
        _revisionProjectionPort = revisionProjectionPort ?? throw new ArgumentNullException(nameof(revisionProjectionPort));
        _deploymentProjectionPort = deploymentProjectionPort ?? throw new ArgumentNullException(nameof(deploymentProjectionPort));
        _servingSetProjectionPort = servingSetProjectionPort ?? throw new ArgumentNullException(nameof(servingSetProjectionPort));
        _rolloutProjectionPort = rolloutProjectionPort ?? throw new ArgumentNullException(nameof(rolloutProjectionPort));
        _trafficViewProjectionPort = trafficViewProjectionPort ?? throw new ArgumentNullException(nameof(trafficViewProjectionPort));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
        CreateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Spec.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
        UpdateServiceDefinitionCommand command,
        CancellationToken ct = default)
    {
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Spec.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForService(command.Spec.Identity), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
        CreateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Spec.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Spec.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Spec.Identity, command.Spec.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
        PrepareServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
        PublishServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRevisionCatalogTargetAsync(command.Identity, ct);
        await _revisionProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
        SetDefaultServingRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureDefinitionTargetAsync(command.Identity, ct);
        await _catalogProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
        ActivateServiceRevisionCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        await _deploymentProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, CorrelationForRevision(command.Identity, command.RevisionId), ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
        DeactivateServiceDeploymentCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureDeploymentTargetAsync(command.Identity, ct);
        await _deploymentProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.DeploymentId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
        ReplaceServiceServingTargetsCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var resolvedTargets = await ResolveTargetsAsync(command.Identity, command.Targets, ct);
        var actorId = await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(actorId, ct);
        return await DispatchAsync(
            actorId,
            new ReplaceServiceServingTargetsCommand
            {
                Identity = command.Identity?.Clone(),
                Targets = { resolvedTargets.Select(CloneTarget) },
                RolloutId = command.RolloutId ?? string.Empty,
                Reason = command.Reason ?? string.Empty,
            },
            CorrelationForService(command.Identity!),
            ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
        StartServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        var baselineTargets = command.BaselineTargets.Count > 0
            ? await ResolveTargetsAsync(command.Identity, command.BaselineTargets, ct)
            : await ResolveBaselineTargetsAsync(command.Identity, ct);
        var resolvedPlan = await ResolvePlanAsync(command.Identity, command.Plan, ct);
        return await DispatchAsync(
            actorId,
            new StartServiceRolloutCommand
            {
                Identity = command.Identity?.Clone(),
                Plan = resolvedPlan,
                BaselineTargets = { baselineTargets.Select(CloneTarget) },
            },
            $"{CorrelationForService(command.Identity!)}:{command.Plan?.RolloutId}",
            ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
        AdvanceServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
        PauseServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
        ResumeServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    public async Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
        RollbackServiceRolloutCommand command,
        CancellationToken ct = default)
    {
        await EnsureDefinitionExistsAsync(command.Identity, ct);
        var actorId = await _targetProvisioner.EnsureRolloutTargetAsync(command.Identity, ct);
        await _targetProvisioner.EnsureServingSetTargetAsync(command.Identity, ct);
        await EnsureServingProjectionsAsync(ServiceActorIds.ServingSet(command.Identity), ct);
        await _rolloutProjectionPort.EnsureProjectionAsync(actorId, ct);
        return await DispatchAsync(actorId, command, $"{CorrelationForService(command.Identity)}:{command.RolloutId}", ct);
    }

    private async Task EnsureDefinitionExistsAsync(ServiceIdentity identity, CancellationToken ct)
    {
        if (await _catalogQueryReader.GetAsync(identity, ct) == null)
            throw new InvalidOperationException($"Service definition '{ServiceKeys.Build(identity)}' was not found.");
    }

    private async Task EnsureServingProjectionsAsync(string actorId, CancellationToken ct)
    {
        await _servingSetProjectionPort.EnsureProjectionAsync(actorId, ct);
        await _trafficViewProjectionPort.EnsureProjectionAsync(actorId, ct);
    }

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

    private static string CorrelationForRevision(ServiceIdentity identity, string revisionId) =>
        $"{ServiceKeys.Build(identity)}:{revisionId ?? string.Empty}";

    private async Task<IReadOnlyList<ServiceServingTargetSpec>> ResolveBaselineTargetsAsync(
        ServiceIdentity identity,
        CancellationToken ct)
    {
        var baseline = await _servingSetQueryReader.GetAsync(identity, ct);
        if (baseline == null)
            return [];

        return baseline.Targets.Select(x => new ServiceServingTargetSpec
        {
            DeploymentId = x.DeploymentId,
            RevisionId = x.RevisionId,
            PrimaryActorId = x.PrimaryActorId,
            AllocationWeight = x.AllocationWeight,
            ServingState = ParseServingState(x.ServingState),
            EnabledEndpointIds = { x.EnabledEndpointIds },
        }).ToList();
    }

    private async Task<ServiceRolloutPlanSpec> ResolvePlanAsync(
        ServiceIdentity identity,
        ServiceRolloutPlanSpec plan,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var resolved = new ServiceRolloutPlanSpec
        {
            RolloutId = plan.RolloutId ?? string.Empty,
            DisplayName = plan.DisplayName ?? string.Empty,
        };
        foreach (var stage in plan.Stages)
        {
            resolved.Stages.Add(new ServiceRolloutStageSpec
            {
                StageId = stage.StageId ?? string.Empty,
                Targets = { (await ResolveTargetsAsync(identity, stage.Targets, ct)).Select(CloneTarget) },
            });
        }

        return resolved;
    }

    private async Task<IReadOnlyList<ServiceServingTargetSpec>> ResolveTargetsAsync(
        ServiceIdentity identity,
        IEnumerable<ServiceServingTargetSpec> targets,
        CancellationToken ct)
    {
        var deployments = await _deploymentQueryReader.GetAsync(identity, ct)
            ?? throw new InvalidOperationException($"Deployments for '{ServiceKeys.Build(identity)}' were not found.");
        var deploymentByRevision = deployments.Deployments
            .Where(x => string.Equals(x.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal))
            .GroupBy(x => x.RevisionId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var resolved = new List<ServiceServingTargetSpec>();
        foreach (var target in targets)
        {
            var resolvedTarget = await ResolveTargetAsync(identity, target, deploymentByRevision, ct);
            resolved.Add(resolvedTarget);
        }

        return resolved;
    }

    private async Task<ServiceServingTargetSpec> ResolveTargetAsync(
        ServiceIdentity identity,
        ServiceServingTargetSpec target,
        IReadOnlyDictionary<string, ServiceDeploymentSnapshot> deploymentByRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.RevisionId))
            throw new InvalidOperationException("revision_id is required for serving targets.");

        if (!deploymentByRevision.TryGetValue(target.RevisionId, out var deployment))
            throw new InvalidOperationException(
                $"Active deployment for '{ServiceKeys.Build(identity)}' revision '{target.RevisionId}' was not found.");

        var artifact = await _artifactStore.GetAsync(ServiceKeys.Build(identity), target.RevisionId, ct)
            ?? throw new InvalidOperationException(
                $"Prepared artifact for '{ServiceKeys.Build(identity)}' revision '{target.RevisionId}' was not found.");

        var resolved = CloneTarget(target);
        resolved.DeploymentId = deployment.DeploymentId;
        resolved.PrimaryActorId = deployment.PrimaryActorId;
        resolved.AllocationWeight = resolved.AllocationWeight == 0 ? 100 : resolved.AllocationWeight;
        resolved.ServingState = resolved.ServingState == ServiceServingState.Unspecified
            ? ServiceServingState.Active
            : resolved.ServingState;
        if (resolved.EnabledEndpointIds.Count == 0)
            resolved.EnabledEndpointIds.Add(artifact.Endpoints.Select(x => x.EndpointId));

        return resolved;
    }

    private static ServiceServingState ParseServingState(string value) =>
        Enum.TryParse<ServiceServingState>(value, out var parsed)
            ? parsed
            : ServiceServingState.Unspecified;

    private static ServiceServingTargetSpec CloneTarget(ServiceServingTargetSpec source) =>
        new()
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState,
            EnabledEndpointIds = { source.EnabledEndpointIds },
        };
}
