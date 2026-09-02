using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgentService.Application.Workflows;

public sealed class ScopeWorkflowDefinitionBindingResolveService : IScopeWorkflowDefinitionBindingResolvePort
{
    private readonly IScopeWorkflowQueryPort _workflowQueryPort;
    private readonly IWorkflowActorBindingReader _workflowActorBindingReader;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogReader;

    public ScopeWorkflowDefinitionBindingResolveService(
        IScopeWorkflowQueryPort workflowQueryPort,
        IWorkflowActorBindingReader workflowActorBindingReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader)
    {
        _workflowQueryPort = workflowQueryPort ?? throw new ArgumentNullException(nameof(workflowQueryPort));
        _workflowActorBindingReader = workflowActorBindingReader ?? throw new ArgumentNullException(nameof(workflowActorBindingReader));
        _revisionCatalogReader = revisionCatalogReader ?? throw new ArgumentNullException(nameof(revisionCatalogReader));
    }

    public async Task<ScopeWorkflowDefinitionBindingResolveResult> ResolveAsync(
        ScopeWorkflowDefinitionBindingResolveRequest request,
        CancellationToken ct = default)
    {
        var scopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var workflowId = ScopeWorkflowCapabilityConventions.NormalizeWorkflowId(request.WorkflowId);
        var lookup = await _workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct).ConfigureAwait(false);
        if (!lookup.IsRunnable)
            return ScopeWorkflowDefinitionBindingResolveResult.NotRunnable(scopeId, workflowId, lookup.Reason);

        var binding = await ResolveBindingAsync(lookup.Workflow!, ct).ConfigureAwait(false);
        return binding is null
            ? ScopeWorkflowDefinitionBindingResolveResult.NotRunnable(scopeId, workflowId, "workflow_definition_binding_unavailable")
            : ScopeWorkflowDefinitionBindingResolveResult.Resolved(scopeId, workflowId, binding);
    }

    private async Task<WorkflowDefinitionBinding?> ResolveBindingAsync(
        ScopeWorkflowSummary workflow,
        CancellationToken ct)
    {
        WorkflowActorBinding? actorBinding = null;
        if (!string.IsNullOrWhiteSpace(workflow.ActorId))
            actorBinding = await _workflowActorBindingReader.GetAsync(workflow.ActorId, ct).ConfigureAwait(false);

        if (actorBinding?.HasDefinitionPayload == true)
            return FromActorBinding(workflow, actorBinding);

        if (string.IsNullOrWhiteSpace(workflow.ServiceAppId) ||
            string.IsNullOrWhiteSpace(workflow.ServiceNamespace) ||
            string.IsNullOrWhiteSpace(workflow.PublishedServiceId) ||
            string.IsNullOrWhiteSpace(workflow.ActiveRevisionId))
        {
            return null;
        }

        var revisionCatalog = await _revisionCatalogReader.GetAsync(BuildWorkflowServiceIdentity(workflow), ct)
            .ConfigureAwait(false);
        var artifact = revisionCatalog?.Revisions
            .FirstOrDefault(revision => string.Equals(revision.RevisionId, workflow.ActiveRevisionId, StringComparison.Ordinal))
            ?.PreparedArtifact
            ?.Clone();
        var workflowPlan = artifact?.DeploymentPlan?.WorkflowPlan;
        if (workflowPlan is null)
            return null;

        var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            artifact!,
            workflow.ActiveRevisionId);
        return new WorkflowDefinitionBinding(
            workflowPlan.DefinitionActorId,
            workflowPlan.WorkflowName,
            workflowPlan.WorkflowYaml,
            workflowPlan.InlineWorkflowYamls,
            workflowPlan.ExecutionMode,
            workflow.ScopeId,
            WorkflowRunOrigins.AdHocChat,
            SourceKind: "service_revision",
            CapabilityAdmissionPlan: workflowPlan.CapabilityAdmissionPlan?.Clone(),
            WorkflowId: string.IsNullOrWhiteSpace(bindingIdentity.WorkflowId) ? workflow.WorkflowId : bindingIdentity.WorkflowId,
            RevisionId: bindingIdentity.RevisionId,
            ToolCatalogPolicyVersion: workflowPlan.ToolCatalogPolicyVersion);
    }

    private static WorkflowDefinitionBinding FromActorBinding(
        ScopeWorkflowSummary workflow,
        WorkflowActorBinding binding) =>
        new(
            binding.EffectiveDefinitionActorId,
            binding.WorkflowName,
            binding.WorkflowYaml,
            binding.InlineWorkflowYamls,
            binding.ExpectedExecutionMode,
            binding.ScopeId,
            WorkflowRunOrigins.AdHocChat,
            SourceKind: binding.SourceKind,
            CapabilityAdmissionPlan: binding.CapabilityAdmissionPlan?.Clone(),
            WorkflowId: workflow.WorkflowId,
            RevisionId: binding.RevisionId,
            DefinitionVersion: ResolveDefinitionVersionForExecution(binding),
            ToolCatalogPolicyVersion: binding.ToolCatalogPolicyVersion);

    private static long ResolveDefinitionVersionForExecution(WorkflowActorBinding binding) =>
        binding.ActorKind == WorkflowActorKind.Definition
            ? Math.Max(0, binding.SourceVersion)
            : 0;

    private static ServiceIdentity BuildWorkflowServiceIdentity(ScopeWorkflowSummary workflow) =>
        new()
        {
            TenantId = workflow.ScopeId.Trim(),
            AppId = workflow.ServiceAppId.Trim(),
            Namespace = workflow.ServiceNamespace.Trim(),
            ServiceId = workflow.PublishedServiceId.Trim(),
        };
}
