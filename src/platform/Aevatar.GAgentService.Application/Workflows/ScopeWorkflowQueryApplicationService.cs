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
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var services = await _serviceLifecycleQueryPort.ListServicesAsync(
            normalizedScopeId,
            ScopeWorkflowCapabilityOptions.NormalizeRequired(_options.ServiceAppId, nameof(_options.ServiceAppId)),
            ScopeWorkflowCapabilityOptions.NormalizeRequired(_options.ServiceNamespace, nameof(_options.ServiceNamespace)),
            _options.ListTake,
            ct);

        var summaries = new List<ScopeWorkflowSummary>(services.Count);
        foreach (var service in services.OrderByDescending(static x => x.UpdatedAt))
        {
            summaries.Add(await BuildWorkflowSummaryAsync(
                normalizedScopeId,
                service,
                BuildIdentity(normalizedScopeId, service.ServiceId),
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

    public async Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(workflowId);
        var identity = BuildIdentity(normalizedScopeId, normalizedWorkflowId);
        var serviceSnapshot = await GetExistingServiceAsync(identity, ct);
        if (serviceSnapshot == null)
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                Workflow: null,
                Reason: "service_catalog_missing");
        }

        if (string.IsNullOrWhiteSpace(serviceSnapshot.ActiveServingRevisionId) ||
            string.IsNullOrWhiteSpace(serviceSnapshot.DeploymentId) ||
            string.IsNullOrWhiteSpace(serviceSnapshot.PrimaryActorId))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "service_catalog_runtime_facts_missing");
        }

        var deploymentCatalog = await _serviceLifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
        var deployment = deploymentCatalog?.Deployments.FirstOrDefault(x =>
            string.Equals(x.DeploymentId, serviceSnapshot.DeploymentId, StringComparison.Ordinal));
        if (deployment == null)
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "deployment_readmodel_missing");
        }

        if (!string.Equals(deployment.RevisionId, serviceSnapshot.ActiveServingRevisionId, StringComparison.Ordinal) ||
            !string.Equals(deployment.PrimaryActorId, serviceSnapshot.PrimaryActorId, StringComparison.Ordinal))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Stale,
                Workflow: null,
                Reason: "deployment_readmodel_mismatched");
        }

        if (!string.Equals(deployment.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(serviceSnapshot.DeploymentStatus, ServiceDeploymentStatus.Active.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "deployment_not_active");
        }

        var binding = await _workflowActorBindingReader.GetAsync(serviceSnapshot.PrimaryActorId, ct);
        if (binding == null || string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "workflow_actor_binding_missing");
        }

        if (!string.Equals(binding.EffectiveDefinitionActorId, serviceSnapshot.PrimaryActorId, StringComparison.Ordinal))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Stale,
                Workflow: null,
                Reason: "workflow_actor_binding_mismatched");
        }

        var summary = BuildWorkflowSummary(
            normalizedScopeId,
            serviceSnapshot,
            identity,
            normalizedWorkflowId,
            serviceSnapshot.DisplayName,
            binding.WorkflowName,
            serviceSnapshot.ActiveServingRevisionId,
            serviceSnapshot.DeploymentId,
            serviceSnapshot.PrimaryActorId);

        return new ScopeWorkflowLookupResult(
            ScopeWorkflowLookupStatus.Runnable,
            summary,
            Reason: "runnable");
    }

    public async Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var result = await LookupByWorkflowIdAsync(scopeId, workflowId, ct);
        return result.IsRunnable ? result.Workflow : null;
    }

    public async Task<ScopeWorkflowSummary?> GetByActorIdAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default)
    {
        var normalizedActorId = ScopeWorkflowCapabilityOptions.NormalizeRequired(actorId, nameof(actorId));
        var binding = await _workflowActorBindingReader.GetAsync(normalizedActorId, ct);
        var resolvedDefinitionActorId = !string.IsNullOrWhiteSpace(binding?.EffectiveDefinitionActorId)
            ? binding.EffectiveDefinitionActorId
            : normalizedActorId;
        var workflows = await ListAsync(scopeId, ct);
        var workflow = workflows.FirstOrDefault(workflow =>
            string.Equals(workflow.ActorId, resolvedDefinitionActorId, StringComparison.Ordinal));
        if (workflow == null)
            return null;

        var lookup = await LookupByWorkflowIdAsync(scopeId, workflow.WorkflowId, ct);
        return lookup.IsRunnable ? lookup.Workflow : null;
    }

    internal Task<ServiceCatalogSnapshot?> GetExistingServiceAsync(
        ServiceIdentity identity,
        CancellationToken ct) =>
        _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);

    internal ServiceIdentity BuildIdentity(string scopeId, string workflowId) =>
        ScopeWorkflowCapabilityConventions.BuildIdentity(_options, scopeId, workflowId);

    private async Task<ScopeWorkflowSummary> BuildWorkflowSummaryAsync(
        string scopeId,
        ServiceCatalogSnapshot serviceSnapshot,
        ServiceIdentity identity,
        string workflowId,
        string fallbackDisplayName,
        string? fallbackWorkflowName,
        string fallbackActiveRevisionId,
        string fallbackDeploymentId,
        string fallbackActorId,
        CancellationToken ct)
    {
        var workflowName = ScopeWorkflowCapabilityConventions.NormalizeOptional(fallbackWorkflowName);
        if (!string.IsNullOrWhiteSpace(fallbackActorId))
        {
            var binding = await _workflowActorBindingReader.GetAsync(fallbackActorId, ct);
            if (!string.IsNullOrWhiteSpace(binding?.WorkflowName))
                workflowName = binding.WorkflowName;
        }

        return BuildWorkflowSummary(
            scopeId,
            serviceSnapshot,
            identity,
            workflowId,
            fallbackDisplayName,
            workflowName,
            fallbackActiveRevisionId,
            fallbackDeploymentId,
            fallbackActorId);
    }

    private static ScopeWorkflowSummary BuildWorkflowSummary(
        string scopeId,
        ServiceCatalogSnapshot serviceSnapshot,
        ServiceIdentity identity,
        string workflowId,
        string fallbackDisplayName,
        string? workflowName,
        string activeRevisionId,
        string deploymentId,
        string actorId)
    {
        var displayName = serviceSnapshot.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = fallbackDisplayName;

        return new ScopeWorkflowSummary(
            scopeId,
            workflowId,
            displayName,
            serviceSnapshot.ServiceKey ?? ServiceKeys.Build(identity),
            ScopeWorkflowCapabilityConventions.NormalizeOptional(workflowName),
            actorId,
            activeRevisionId,
            deploymentId,
            serviceSnapshot.DeploymentStatus.Trim() is { Length: > 0 } deploymentStatus ? deploymentStatus : ServiceDeploymentStatus.Unspecified.ToString(),
            serviceSnapshot.UpdatedAt);
    }
}
