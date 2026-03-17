using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class UserWorkflowQueryApplicationService : IUserWorkflowQueryPort
{
    private readonly IServiceLifecycleQueryPort _serviceLifecycleQueryPort;
    private readonly IWorkflowActorBindingReader _workflowActorBindingReader;
    private readonly UserWorkflowCapabilityOptions _options;

    public UserWorkflowQueryApplicationService(
        IServiceLifecycleQueryPort serviceLifecycleQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IOptions<UserWorkflowCapabilityOptions> options)
    {
        _serviceLifecycleQueryPort = serviceLifecycleQueryPort ?? throw new ArgumentNullException(nameof(serviceLifecycleQueryPort));
        _workflowActorBindingReader = workflowActorBindingReader ?? throw new ArgumentNullException(nameof(workflowActorBindingReader));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new InvalidOperationException("User workflow capability options are required.");
    }

    public async Task<IReadOnlyList<UserWorkflowSummary>> ListAsync(
        string userId,
        CancellationToken ct = default)
    {
        var normalizedUserId = UserWorkflowCapabilityOptions.NormalizeRequired(userId, nameof(userId));
        var services = await _serviceLifecycleQueryPort.ListServicesAsync(
            _options.TenantId,
            _options.AppId,
            _options.BuildNamespace(normalizedUserId),
            _options.ListTake,
            ct);

        var summaries = new List<UserWorkflowSummary>(services.Count);
        foreach (var service in services.OrderByDescending(static x => x.UpdatedAt))
        {
            summaries.Add(await BuildWorkflowSummaryAsync(
                normalizedUserId,
                service,
                BuildIdentity(normalizedUserId, service.ServiceId),
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

    public async Task<UserWorkflowSummary?> GetByWorkflowIdAsync(
        string userId,
        string workflowId,
        CancellationToken ct = default)
    {
        var normalizedUserId = UserWorkflowCapabilityOptions.NormalizeRequired(userId, nameof(userId));
        var normalizedWorkflowId = UserWorkflowCapabilityConventions.NormalizeWorkflowId(workflowId);
        var identity = BuildIdentity(normalizedUserId, normalizedWorkflowId);
        var serviceSnapshot = await GetExistingServiceAsync(identity, ct);
        if (serviceSnapshot == null)
            return null;

        return await BuildWorkflowSummaryAsync(
            normalizedUserId,
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

    public async Task<UserWorkflowSummary?> GetByActorIdAsync(
        string userId,
        string actorId,
        CancellationToken ct = default)
    {
        var normalizedActorId = UserWorkflowCapabilityOptions.NormalizeRequired(actorId, nameof(actorId));
        var workflows = await ListAsync(userId, ct);
        return workflows.FirstOrDefault(workflow =>
            string.Equals(workflow.ActorId, normalizedActorId, StringComparison.Ordinal));
    }

    internal Task<ServiceCatalogSnapshot?> GetExistingServiceAsync(
        ServiceIdentity identity,
        CancellationToken ct) =>
        _serviceLifecycleQueryPort.GetServiceAsync(identity, ct);

    internal ServiceIdentity BuildIdentity(string userId, string workflowId) =>
        UserWorkflowCapabilityConventions.BuildIdentity(_options, userId, workflowId);

    private async Task<UserWorkflowSummary> BuildWorkflowSummaryAsync(
        string userId,
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

        var workflowName = UserWorkflowCapabilityConventions.NormalizeOptional(fallbackWorkflowName);
        if (!string.IsNullOrWhiteSpace(actorId))
        {
            var binding = await _workflowActorBindingReader.GetAsync(actorId, ct);
            if (!string.IsNullOrWhiteSpace(binding?.WorkflowName))
                workflowName = binding.WorkflowName;
        }

        return new UserWorkflowSummary(
            userId,
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
