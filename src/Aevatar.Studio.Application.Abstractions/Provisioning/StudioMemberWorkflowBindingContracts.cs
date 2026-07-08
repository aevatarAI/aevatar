namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberWorkflowBindingRequest(
    string ScopeId,
    string MemberId,
    string WorkflowYaml)
{
    public string? WorkflowId { get; init; }
}

public sealed record StudioMemberWorkflowBindingResult(
    bool Success,
    string ScopeId,
    string MemberId,
    string BindingRunId,
    string Status,
    string AckStage,
    string BindingRunRole);
