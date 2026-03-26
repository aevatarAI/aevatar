using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowQueryApplicationService : IScopeWorkflowQueryPort
{
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IWorkflowActorBindingReader _workflowActorBindingReader;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeWorkflowQueryApplicationService(
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IOptions<ScopeWorkflowCapabilityOptions> options)
    {
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _workflowActorBindingReader = workflowActorBindingReader ?? throw new ArgumentNullException(nameof(workflowActorBindingReader));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("User workflow capability options are required.");
    }

    public async Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var resolvedAppId = ScopeWorkflowCapabilityConventions.ResolveAppId(_options, appId: null);
        return await ListAsync(scopeId, resolvedAppId, ct);
    }

    public async Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
        string scopeId,
        string appId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var resolvedAppId = ScopeWorkflowCapabilityConventions.ResolveAppId(_options, appId);
        var services = await _serviceLifecycleQueryPort.ListServicesAsync(
            _options.TenantId,
            resolvedAppId,
            _options.BuildNamespace(normalizedScopeId),
            _options.ListTake,
            ct);

        var summaries = new List<ScopeWorkflowSummary>(services.Count);
        foreach (var service in services.OrderByDescending(static x => x.UpdatedAt))
        {
            summaries.Add(await BuildWorkflowSummaryAsync(
                normalizedScopeId,
                resolvedAppId,
                service,
                BuildIdentity(normalizedScopeId, service.ServiceId, resolvedAppId),
                service.ServiceId,
                service.DisplayName,
                fallbackWorkflowName: null,
                fallbackActiveRevisionId: service.ActiveServingRevisionId,
                fallbackDeploymentId: service.DeploymentId,
                fallbackActorId: service.PrimaryActorId,
                ct));
        }

        return summaries;
    }

    public async Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var resolvedAppId = ScopeWorkflowCapabilityConventions.ResolveAppId(_options, appId: null);
        return await GetByWorkflowIdAsync(scopeId, resolvedAppId, workflowId, ct);
    }

    public async Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
        string scopeId,
        string appId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var resolvedAppId = ScopeWorkflowCapabilityConventions.ResolveAppId(_options, appId);
        var normalizedWorkflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(workflowId);
        var identity = BuildIdentity(normalizedScopeId, normalizedWorkflowId, resolvedAppId);
        var serviceSnapshot = await GetExistingServiceAsync(identity, ct);
        if (serviceSnapshot == null)
            return null;

        return await BuildWorkflowSummaryAsync(
            normalizedScopeId,
            resolvedAppId,
            serviceSnapshot,
            identity,
            normalizedWorkflowId,
            serviceSnapshot.DisplayName,
            fallbackWorkflowName: null,
            fallbackActiveRevisionId: serviceSnapshot.ActiveServingRevisionId,
            fallbackDeploymentId: serviceSnapshot.DeploymentId,
            fallbackActorId: serviceSnapshot.PrimaryActorId,
            ct);
    }

    public async Task<ScopeWorkflowSummary?> GetByActorIdAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default)
    {
        var resolvedAppId = ScopeWorkflowCapabilityConventions.ResolveAppId(_options, appId: null);
        return await GetByActorIdAsync(scopeId, resolvedAppId, actorId, ct);
    }

    public async Task<ScopeWorkflowSummary?> GetByActorIdAsync(
        string scopeId,
        string appId,
        string actorId,
        CancellationToken ct = default)
    {
        var normalizedActorId = ScopeWorkflowCapabilityOptions.NormalizeRequired(actorId, nameof(actorId));
        var binding = await _workflowActorBindingReader.GetAsync(normalizedActorId, ct);
        var resolvedDefinitionActorId = !string.IsNullOrWhiteSpace(binding?.EffectiveDefinitionActorId)
            ? binding.EffectiveDefinitionActorId
            : normalizedActorId;
        var workflows = await ListAsync(scopeId, appId, ct);
        return workflows.FirstOrDefault(workflow =>
            string.Equals(workflow.ActorId, resolvedDefinitionActorId, StringComparison.Ordinal));
    }

    internal Task<ServiceCatalogSnapshot?> GetExistingServiceAsync(
        ServiceIdentity identity,
        CancellationToken ct) =>
        _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);

    internal ServiceIdentity BuildIdentity(string scopeId, string workflowId, string? appId = null) =>
        ScopeWorkflowCapabilityConventions.BuildIdentity(_options, scopeId, workflowId, appId);

    private async Task<ScopeWorkflowSummary> BuildWorkflowSummaryAsync(
        string scopeId,
        string appId,
        ServiceCatalogSnapshot? serviceSnapshot,
        ServiceIdentity identity,
        string workflowId,
        string fallbackDisplayName,
        string? fallbackWorkflowName,
        string fallbackActiveRevisionId,
        string fallbackDeploymentId,
        string fallbackActorId,
        CancellationToken ct)
    {
        var snapshotMatchesActivation =
            serviceSnapshot != null &&
            string.Equals(serviceSnapshot.ActiveServingRevisionId, fallbackActiveRevisionId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(serviceSnapshot.PrimaryActorId);

        var activeRevisionId = snapshotMatchesActivation ? serviceSnapshot!.ActiveServingRevisionId : fallbackActiveRevisionId;
        var actorId = snapshotMatchesActivation ? serviceSnapshot!.PrimaryActorId : fallbackActorId;
        var deploymentId = snapshotMatchesActivation ? serviceSnapshot!.DeploymentId : fallbackDeploymentId;
        var displayName = serviceSnapshot?.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = fallbackDisplayName;

        var workflowName = ScopeWorkflowCapabilityConventions.NormalizeOptional(fallbackWorkflowName);
        if (!string.IsNullOrWhiteSpace(actorId))
        {
            var binding = await _workflowActorBindingReader.GetAsync(actorId, ct);
            if (!string.IsNullOrWhiteSpace(binding?.WorkflowName))
                workflowName = binding.WorkflowName;
        }

        return new ScopeWorkflowSummary(
            scopeId,
            serviceSnapshot?.AppId?.Trim() is { Length: > 0 } snapshotAppId ? snapshotAppId : appId,
            workflowId,
            displayName,
            serviceSnapshot?.ServiceKey ?? ServiceKeys.Build(identity),
            workflowName,
            actorId,
            activeRevisionId,
            deploymentId,
            serviceSnapshot?.DeploymentStatus?.Trim() is { Length: > 0 } deploymentStatus ? deploymentStatus : "active",
            serviceSnapshot?.UpdatedAt ?? DateTimeOffset.UtcNow);
    }
}
