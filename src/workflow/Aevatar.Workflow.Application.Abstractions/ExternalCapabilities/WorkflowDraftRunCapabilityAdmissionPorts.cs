using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

public sealed record WorkflowDraftRunCapabilityAdmissionRequest(
    string ScopeId,
    string CommandId,
    Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential? CallerCredential,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls);

public sealed record WorkflowDraftRunCapabilityAdmissionResult(
    string SourceKind,
    string WorkflowId,
    string RevisionId,
    WorkflowCapabilityAdmissionPlan CapabilityAdmissionPlan);

public interface IWorkflowDraftRunCapabilityAdmissionService
{
    Task<WorkflowDraftRunCapabilityAdmissionResult> PrepareAsync(
        WorkflowDraftRunCapabilityAdmissionRequest request,
        CancellationToken cancellationToken = default);
}
