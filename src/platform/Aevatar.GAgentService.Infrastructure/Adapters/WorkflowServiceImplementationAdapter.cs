using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class WorkflowServiceImplementationAdapter : IServiceImplementationAdapter
{
    private readonly IWorkflowDefinitionParser _workflowDefinitionParser;
    private readonly IWorkflowExternalCapabilityAdmissionService _capabilityAdmissionService;

    public WorkflowServiceImplementationAdapter(
        IWorkflowDefinitionParser workflowDefinitionParser,
        IWorkflowExternalCapabilityAdmissionService capabilityAdmissionService)
    {
        _workflowDefinitionParser = workflowDefinitionParser ?? throw new ArgumentNullException(nameof(workflowDefinitionParser));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
    }

    public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Workflow;

    public async Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
        PrepareServiceRevisionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = request.Spec?.WorkflowSpec
            ?? throw new InvalidOperationException("workflow implementation_spec is required.");
        if (string.IsNullOrWhiteSpace(spec.WorkflowYaml))
            throw new InvalidOperationException("workflow_yaml is required.");

        var expectedExecutionMode = spec.CapabilityAdmissionPlan is null &&
                                    spec.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified
            ? ExternalCapabilityExecutionMode.Interactive
            : spec.ExpectedExecutionMode;
        var capabilityAdmissionPlan = spec.CapabilityAdmissionPlan is { } persistedPlan
            ? await _capabilityAdmissionService.RevalidatePersistedAsync(
                new PersistedWorkflowCapabilityAdmissionRequest(
                    persistedPlan,
                    spec.WorkflowYaml,
                    spec.InlineWorkflowYamls,
                    "service_revision_prepare",
                    expectedExecutionMode,
                    spec.WorkflowId,
                    request.Spec.RevisionId),
                ct)
            : await _capabilityAdmissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                    new ExternalWorkflowCapabilityAccessContext(
                        request.Spec.Identity?.TenantId ?? string.Empty,
                        string.Empty),
                    spec.WorkflowYaml,
                    spec.InlineWorkflowYamls,
                    "service_revision_prepare",
                    expectedExecutionMode),
                ct);

        var parse = await _workflowDefinitionParser.ParseWorkflowYamlAsync(spec.WorkflowYaml, ct);
        if (!parse.Succeeded)
            throw new InvalidOperationException(parse.Error);

        var resolvedWorkflowName = spec.WorkflowName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedWorkflowName))
        {
            resolvedWorkflowName = parse.WorkflowName;
        }
        else if (!string.Equals(resolvedWorkflowName, parse.WorkflowName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("workflow_name must match workflow_yaml name.");
        }

        var authorizationDependencies = parse.AuthorizationDependencies
            ?? throw new InvalidOperationException("workflow authorization dependencies are required.");

        return WorkflowServiceRevisionArtifactBuilder.Build(
            request.Spec,
            resolvedWorkflowName,
            authorizationDependencies,
            capabilityAdmissionPlan);
    }
}
