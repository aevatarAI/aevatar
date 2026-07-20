namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberWorkflowBindingRequest(
    string ScopeId,
    string MemberId,
    string WorkflowYaml)
{
    public string? WorkflowId { get; init; }
}

public static class StudioMemberWorkflowBindingOperationNames
{
    public const string Bind = "bind";
    public const string SaveAndBind = "save_and_bind";
}

public sealed record StudioMemberWorkflowBindingResult(
    bool Success,
    string ScopeId,
    string MemberId,
    string Operation,
    string Status,
    string? BindingRunId = null,
    string? AckStage = null,
    string? BindingRunRole = null,
    string? WorkflowId = null,
    string? RevisionId = null);
