namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberWorkflowDraftProvisioningPort
{
    Task<StudioMemberWorkflowDraftProvisioningResult> SaveAsync(
        StudioMemberWorkflowDraftProvisioningRequest request,
        CancellationToken ct = default);
}

public sealed record StudioMemberWorkflowDraftProvisioningRequest(
    string ScopeId,
    string TeamId,
    string DisplayName,
    string WorkflowYaml)
{
    public string? MemberId { get; init; }
    public string? WorkflowId { get; init; }
}

public sealed record StudioMemberWorkflowDraftProvisioningResult(
    string Status,
    bool Runnable,
    string BindingStatus,
    string ScopeId,
    string TeamId,
    string MemberId,
    string WorkflowId,
    string StudioUrl,
    string CommandId,
    string AckStage,
    string ActorId,
    string WorkspaceId,
    long? ExpectedVersion,
    DateTimeOffset AckedAtUtc,
    StudioMemberWorkflowDraftReadiness Readiness,
    IReadOnlyList<StudioMemberWorkflowDraftBlocker> Blockers);

public sealed record StudioMemberWorkflowDraftReadiness(
    bool Readable,
    string Stage,
    string Message);

public sealed record StudioMemberWorkflowDraftBlocker(
    string Code,
    string Message);

public static class StudioMemberWorkflowDraftStatusNames
{
    public const string SaveAccepted = "draft_save_accepted";
    public const string NotBound = "not_bound";
}

public static class StudioMemberWorkflowDraftErrorCodes
{
    public const string MemberNotFound = "member_not_found";
    public const string MemberTeamMismatch = "member_team_mismatch";
    public const string MemberKindMismatch = "member_kind_mismatch";
    public const string DraftSaveFailed = "workflow_draft_save_failed";
}

public static class StudioMemberWorkflowDraftBlockerCodes
{
    public const string NyxIdOperationSelectionRequired = "NYXID_OPERATION_SELECTION_REQUIRED";
    public const string WorkflowBindRequired = "WORKFLOW_BIND_REQUIRED";
}

public sealed class StudioMemberWorkflowDraftProvisioningException : InvalidOperationException
{
    public StudioMemberWorkflowDraftProvisioningException(
        string code,
        string message,
        string? memberId = null)
        : base(message)
    {
        Code = code;
        MemberId = memberId;
    }

    public string Code { get; }

    public string? MemberId { get; }
}
