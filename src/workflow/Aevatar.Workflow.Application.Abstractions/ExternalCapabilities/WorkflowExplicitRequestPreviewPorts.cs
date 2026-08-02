using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

public sealed record WorkflowExplicitRequestPreviewRequest(
    ExternalWorkflowCapabilityAccessContext Access,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string>? InlineWorkflowYamls,
    ExternalCapabilityExecutionMode ExecutionMode,
    string? WorkflowId = null,
    string? RevisionId = null,
    IReadOnlyList<string>? WorkflowYamls = null);

public sealed record WorkflowExplicitRequestPreviewItem(
    string CallSiteId,
    string RequestContractDigest,
    string UserServiceId,
    NyxIdRequestMethod Method,
    string PathTemplate,
    NyxIdRequestBodyMode BodyMode,
    bool BodyRequired,
    NyxIdRequestResponseMode ResponseMode,
    NyxIdOperationRisk EffectiveRisk,
    bool ApprovalRequired,
    IReadOnlyList<ExternalCapabilityExecutionMode> AllowedExecutionModes);

public sealed record WorkflowExplicitRequestPreviewResult(
    string WorkflowId,
    string RevisionId,
    IReadOnlyList<WorkflowExplicitRequestPreviewItem> Items);

public interface IWorkflowExplicitRequestPreviewService
{
    Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
        WorkflowExplicitRequestPreviewRequest request,
        CancellationToken cancellationToken = default);
}
