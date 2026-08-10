using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Application.Workflows;

public enum ScopeWorkflowArchiveRejectionKind
{
    NotFound = 0,
    Conflict = 1,
}

public sealed class ScopeWorkflowArchiveRejectedException : InvalidOperationException
{
    public ScopeWorkflowArchiveRejectedException(
        ScopeWorkflowArchiveRejectionKind kind,
        string code,
        string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public ScopeWorkflowArchiveRejectionKind Kind { get; }

    public string Code { get; }
}

public sealed class ScopeWorkflowArchiveApplicationService : IScopeWorkflowArchiveCommandPort
{
    private readonly IScopeWorkflowQueryPort _workflowQueryPort;
    private readonly IServiceCommandPort _serviceCommandPort;

    public ScopeWorkflowArchiveApplicationService(
        IScopeWorkflowQueryPort workflowQueryPort,
        IServiceCommandPort serviceCommandPort)
    {
        _workflowQueryPort = workflowQueryPort ?? throw new ArgumentNullException(nameof(workflowQueryPort));
        _serviceCommandPort = serviceCommandPort ?? throw new ArgumentNullException(nameof(serviceCommandPort));
    }

    public async Task<ScopeWorkflowArchiveAcceptedResult> ArchiveAsync(
        ScopeWorkflowArchiveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var workflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(request.WorkflowId);
        var lookup = await _workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
        if (!lookup.IsRunnable)
            throw FromLookup(workflowId, lookup);

        var workflow = lookup.Workflow!;
        if (!string.Equals(workflow.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(workflow.WorkflowId, workflowId, StringComparison.Ordinal))
        {
            throw IdentityUnavailable(workflowId, "The workflow read model resolved another scope or workflow identity.");
        }

        if (!string.Equals(
                workflow.DeploymentStatus,
                ServiceDeploymentStatus.Active.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ScopeWorkflowArchiveRejectedException(
                ScopeWorkflowArchiveRejectionKind.Conflict,
                "WORKFLOW_NOT_ACTIVE",
                $"Workflow '{workflowId}' does not have an active deployment.");
        }

        if (string.IsNullOrWhiteSpace(workflow.ServiceAppId) ||
            string.IsNullOrWhiteSpace(workflow.ServiceNamespace) ||
            string.IsNullOrWhiteSpace(workflow.PublishedServiceId) ||
            string.IsNullOrWhiteSpace(workflow.DeploymentId))
        {
            throw IdentityUnavailable(workflowId, "Published service or deployment identity is unavailable.");
        }

        var identity = new ServiceIdentity
        {
            TenantId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.ScopeId, nameof(workflow.ScopeId)),
            AppId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.ServiceAppId, nameof(workflow.ServiceAppId)),
            Namespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.ServiceNamespace, nameof(workflow.ServiceNamespace)),
            ServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(workflow.PublishedServiceId, nameof(workflow.PublishedServiceId)),
        };
        var deploymentId = ScopeWorkflowCapabilityOptions.NormalizeRequired(
            workflow.DeploymentId,
            nameof(workflow.DeploymentId));
        var receipt = await _serviceCommandPort.DeactivateServiceDeploymentAsync(
            new DeactivateServiceDeploymentCommand
            {
                Identity = identity,
                DeploymentId = deploymentId,
            },
            ct);

        var readModelUrl = $"/api/scopes/{Uri.EscapeDataString(scopeId)}/workflows/{Uri.EscapeDataString(workflowId)}";
        return new ScopeWorkflowArchiveAcceptedResult(
            scopeId,
            workflowId,
            deploymentId,
            ScopeWorkflowCommandAcceptedHandle.FromReceipt("deactivate_deployment", receipt),
            readModelUrl);
    }

    private static ScopeWorkflowArchiveRejectedException FromLookup(
        string workflowId,
        ScopeWorkflowLookupResult lookup) => lookup.Status switch
        {
            ScopeWorkflowLookupStatus.NotFound => new ScopeWorkflowArchiveRejectedException(
                ScopeWorkflowArchiveRejectionKind.NotFound,
                "SCOPE_WORKFLOW_NOT_FOUND",
                $"Workflow '{workflowId}' was not found: {lookup.Reason}."),
            ScopeWorkflowLookupStatus.NotReady => new ScopeWorkflowArchiveRejectedException(
                ScopeWorkflowArchiveRejectionKind.Conflict,
                "WORKFLOW_ARCHIVE_NOT_READY",
                $"Workflow '{workflowId}' is not ready for archive: {lookup.Reason}."),
            _ => new ScopeWorkflowArchiveRejectedException(
                ScopeWorkflowArchiveRejectionKind.Conflict,
                "WORKFLOW_ARCHIVE_STALE",
                $"Workflow '{workflowId}' cannot be archived from stale identity facts: {lookup.Reason}."),
        };

    private static ScopeWorkflowArchiveRejectedException IdentityUnavailable(
        string workflowId,
        string reason) => new(
            ScopeWorkflowArchiveRejectionKind.Conflict,
            "WORKFLOW_ARCHIVE_IDENTITY_UNAVAILABLE",
            $"Workflow '{workflowId}' cannot be archived. {reason}");
}
