using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowQueryApplicationService : IScopeWorkflowQueryPort, IScopeWorkflowCatalogueCommittedSourcePort
{
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IWorkflowActorBindingReader _workflowActorBindingReader;
    private readonly IReadOnlyList<IScopeWorkflowPublishedServiceDescriptorSource> _descriptorSources;
    private readonly ScopeWorkflowCapabilityOptions _options;

    public ScopeWorkflowQueryApplicationService(
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IOptions<ScopeWorkflowCapabilityOptions> options,
        IEnumerable<IScopeWorkflowPublishedServiceDescriptorSource>? descriptorSources = null)
    {
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _workflowActorBindingReader = workflowActorBindingReader ?? throw new ArgumentNullException(nameof(workflowActorBindingReader));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("User workflow capability options are required.");
        _descriptorSources = descriptorSources?.ToArray() ?? [];
    }

    public async Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        return await ListCoreAsync(normalizedScopeId, _options.ListTake, applyResultTake: true, ct);
    }

    public async Task<IReadOnlyList<ScopeWorkflowSummary>> ListCatalogueAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        return await ListCoreAsync(normalizedScopeId, int.MaxValue, applyResultTake: false, ct);
    }

    private async Task<IReadOnlyList<ScopeWorkflowSummary>> ListCoreAsync(
        string normalizedScopeId,
        int sourceTake,
        bool applyResultTake,
        CancellationToken ct)
    {
        var services = await _serviceLifecycleQueryPort.ListServicesAsync(
            normalizedScopeId,
            ScopeWorkflowCapabilityOptions.NormalizeRequired(_options.ServiceAppId, nameof(_options.ServiceAppId)),
            ScopeWorkflowCapabilityOptions.NormalizeRequired(_options.ServiceNamespace, nameof(_options.ServiceNamespace)),
            sourceTake,
            ct);

        var summaries = new List<ScopeWorkflowSummary>(services.Count);
        foreach (var service in services.OrderByDescending(static x => x.UpdatedAt))
        {
            var identity = BuildIdentity(normalizedScopeId, service.ServiceId);
            var deploymentCatalog = await _serviceLifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
            var activeDeployment = ResolveActiveDeployment(service, deploymentCatalog);
            summaries.Add(await BuildWorkflowSummaryAsync(
                normalizedScopeId,
                service,
                identity,
                service.ServiceId,
                service.DisplayName,
                fallbackWorkflowName: null,
                fallbackActiveRevisionId: activeDeployment?.RevisionId ?? service.ActiveServingRevisionId,
                fallbackDeploymentId: activeDeployment?.DeploymentId ?? service.DeploymentId,
                fallbackActorId: activeDeployment?.PrimaryActorId ?? service.PrimaryActorId,
                fallbackDeploymentStatus: activeDeployment?.Status ?? service.DeploymentStatus,
                ct));
        }

        foreach (var source in _descriptorSources)
        {
            var descriptors = await source.ListAsync(normalizedScopeId, sourceTake, ct);
            foreach (var descriptor in descriptors)
            {
                var normalizedDescriptor = NormalizeDescriptor(normalizedScopeId, descriptor);
                var identity = BuildIdentity(normalizedDescriptor);
                var service = await GetExistingServiceAsync(identity, ct);
                if (service == null)
                    continue;

                var deploymentCatalog = await _serviceLifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
                var activeDeployment = ResolveActiveDeployment(service, deploymentCatalog);
                summaries.Add(await BuildWorkflowSummaryAsync(
                    normalizedScopeId,
                    service,
                    identity,
                    normalizedDescriptor.WorkflowId,
                    normalizedDescriptor.DisplayName,
                    fallbackWorkflowName: null,
                    fallbackActiveRevisionId: activeDeployment?.RevisionId ?? service.ActiveServingRevisionId,
                    fallbackDeploymentId: activeDeployment?.DeploymentId ?? service.DeploymentId,
                    fallbackActorId: activeDeployment?.PrimaryActorId ?? service.PrimaryActorId,
                    fallbackDeploymentStatus: activeDeployment?.Status ?? service.DeploymentStatus,
                    ct));
            }
        }

        var workflows = summaries
            .GroupBy(static workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .Where(static group => group
                .Select(workflow => workflow.ServiceKey)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Count() == 1)
            .Select(static group => group.OrderByDescending(workflow => workflow.UpdatedAt).First())
            .OrderByDescending(static workflow => workflow.UpdatedAt);

        return (applyResultTake ? workflows.Take(_options.ListTake) : workflows).ToArray();
    }

    public async Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedWorkflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(workflowId);
        var descriptors = new List<ScopeWorkflowPublishedServiceDescriptor>();
        foreach (var source in _descriptorSources)
        {
            var matches = await source.FindByWorkflowIdAsync(normalizedScopeId, normalizedWorkflowId, ct);
            descriptors.AddRange(matches.Select(descriptor => NormalizeDescriptor(normalizedScopeId, descriptor)));
        }

        var distinctDescriptors = descriptors
            .Where(descriptor => string.Equals(descriptor.WorkflowId, normalizedWorkflowId, StringComparison.Ordinal))
            .GroupBy(static descriptor =>
                (descriptor.ServiceAppId, descriptor.ServiceNamespace, descriptor.PublishedServiceId))
            .Select(static group => group.OrderByDescending(descriptor => descriptor.UpdatedAt).First())
            .ToArray();
        if (distinctDescriptors.Length > 1)
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Stale,
                Workflow: null,
                Reason: "published_service_descriptor_ambiguous");
        }

        var conventionalIdentity = BuildIdentity(normalizedScopeId, normalizedWorkflowId);
        var conventionalService = await GetExistingServiceAsync(conventionalIdentity, ct);
        var identity = conventionalIdentity;
        var serviceSnapshot = conventionalService;
        if (distinctDescriptors.Length == 1)
        {
            var explicitIdentity = BuildIdentity(distinctDescriptors[0]);
            identity = explicitIdentity;
            serviceSnapshot = HasSameIdentity(conventionalIdentity, explicitIdentity)
                ? conventionalService
                : await GetExistingServiceAsync(explicitIdentity, ct);
        }

        if (serviceSnapshot == null)
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                Workflow: null,
                Reason: "service_catalog_missing");
        }

        var deploymentCatalog = await _serviceLifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
        var deployment = ResolveActiveDeployment(serviceSnapshot, deploymentCatalog);
        if (deployment == null)
        {
            if (HasCompleteCatalogRuntimeFacts(serviceSnapshot) && deploymentCatalog?.Deployments.Count > 0)
            {
                return new ScopeWorkflowLookupResult(
                    ScopeWorkflowLookupStatus.Stale,
                    Workflow: null,
                    Reason: "deployment_readmodel_mismatched");
            }

            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "deployment_readmodel_missing");
        }

        if (string.IsNullOrWhiteSpace(deployment.RevisionId) ||
            string.IsNullOrWhiteSpace(deployment.DeploymentId) ||
            string.IsNullOrWhiteSpace(deployment.PrimaryActorId))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "deployment_runtime_facts_missing");
        }

        var binding = await _workflowActorBindingReader.GetAsync(deployment.PrimaryActorId, ct);
        if (binding == null || string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotReady,
                Workflow: null,
                Reason: "workflow_actor_binding_missing");
        }

        if (!string.Equals(binding.EffectiveDefinitionActorId, deployment.PrimaryActorId, StringComparison.Ordinal))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Stale,
                Workflow: null,
                Reason: "workflow_actor_binding_mismatched");
        }

        if (!string.IsNullOrWhiteSpace(binding.WorkflowId) &&
            !string.Equals(binding.WorkflowId.Trim(), normalizedWorkflowId, StringComparison.Ordinal))
        {
            return new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Stale,
                Workflow: null,
                Reason: "workflow_actor_binding_workflow_mismatched");
        }

        var summary = BuildWorkflowSummary(
            normalizedScopeId,
            serviceSnapshot,
            identity,
            normalizedWorkflowId,
            serviceSnapshot.DisplayName,
            binding.WorkflowName,
            deployment.RevisionId,
            deployment.DeploymentId,
            deployment.PrimaryActorId,
            deployment.Status);

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

    private static ServiceIdentity BuildIdentity(ScopeWorkflowPublishedServiceDescriptor descriptor) =>
        new()
        {
            TenantId = descriptor.ScopeId,
            AppId = descriptor.ServiceAppId,
            Namespace = descriptor.ServiceNamespace,
            ServiceId = descriptor.PublishedServiceId,
        };

    private static bool HasSameIdentity(ServiceIdentity left, ServiceIdentity right) =>
        string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) &&
        string.Equals(left.AppId, right.AppId, StringComparison.Ordinal) &&
        string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal) &&
        string.Equals(left.ServiceId, right.ServiceId, StringComparison.Ordinal);

    private static ScopeWorkflowPublishedServiceDescriptor NormalizeDescriptor(
        string expectedScopeId,
        ScopeWorkflowPublishedServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var scopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(descriptor.ScopeId, nameof(descriptor.ScopeId));
        if (!string.Equals(scopeId, expectedScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Scope workflow descriptor source returned a descriptor for another scope.");

        return descriptor with
        {
            ScopeId = scopeId,
            WorkflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(descriptor.WorkflowId),
            ServiceAppId = ScopeWorkflowCapabilityOptions.NormalizeRequired(descriptor.ServiceAppId, nameof(descriptor.ServiceAppId)),
            ServiceNamespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(descriptor.ServiceNamespace, nameof(descriptor.ServiceNamespace)),
            PublishedServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(descriptor.PublishedServiceId, nameof(descriptor.PublishedServiceId)),
            DisplayName = ScopeWorkflowCapabilityConventions.ResolveDisplayName(descriptor.DisplayName, descriptor.WorkflowId),
        };
    }

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
        string fallbackDeploymentStatus,
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
            fallbackActorId,
            fallbackDeploymentStatus);
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
        string actorId,
        string deploymentStatus)
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
            deploymentStatus.Trim() is { Length: > 0 } resolvedDeploymentStatus ? resolvedDeploymentStatus : ServiceDeploymentStatus.Unspecified.ToString(),
            serviceSnapshot.UpdatedAt)
        {
            ServiceAppId = identity.AppId,
            ServiceNamespace = identity.Namespace,
            PublishedServiceId = identity.ServiceId,
        };
    }

    private static ServiceDeploymentSnapshot? ResolveActiveDeployment(
        ServiceCatalogSnapshot serviceSnapshot,
        ServiceDeploymentCatalogSnapshot? deploymentCatalog)
    {
        if (deploymentCatalog == null)
            return null;

        if (HasCompleteCatalogRuntimeFacts(serviceSnapshot))
        {
            return deploymentCatalog.Deployments.FirstOrDefault(x =>
                string.Equals(x.DeploymentId, serviceSnapshot.DeploymentId, StringComparison.Ordinal) &&
                string.Equals(x.RevisionId, serviceSnapshot.ActiveServingRevisionId, StringComparison.Ordinal) &&
                string.Equals(x.PrimaryActorId, serviceSnapshot.PrimaryActorId, StringComparison.Ordinal));
        }

        return deploymentCatalog.Deployments
            .Where(static x => string.Equals(x.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static x => x.UpdatedAt)
            .FirstOrDefault();
    }

    private static bool HasCompleteCatalogRuntimeFacts(ServiceCatalogSnapshot serviceSnapshot) =>
        !string.IsNullOrWhiteSpace(serviceSnapshot.ActiveServingRevisionId) &&
        !string.IsNullOrWhiteSpace(serviceSnapshot.DeploymentId) &&
        !string.IsNullOrWhiteSpace(serviceSnapshot.PrimaryActorId);
}
