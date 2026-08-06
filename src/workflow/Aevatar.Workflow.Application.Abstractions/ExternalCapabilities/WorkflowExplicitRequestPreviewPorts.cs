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

/// <summary>
/// 声明 <see cref="WorkflowExplicitRequestPreviewItem.ApprovalRequired"/> 在哪个阶段被兑现。
/// authored explicit request 在 bind 时确认精确请求与风险，并在每次运行时继续要求
/// typed tool approval；两个关口不能互相替代。
/// </summary>
public enum WorkflowExplicitRequestApprovalEnforcement
{
    None = 0,
    BindTimeConfirmationAndRunTimeToolApproval = 1,
}

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
    WorkflowExplicitRequestApprovalEnforcement ApprovalEnforcement,
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
