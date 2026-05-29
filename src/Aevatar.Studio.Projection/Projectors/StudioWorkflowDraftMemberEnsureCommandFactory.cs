using Aevatar.GAgents.StudioMember;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

internal sealed record StudioWorkflowDraftMemberEnsureCommandPlan(
    string ScopeId,
    string MemberId,
    string ActorId,
    EnsureStudioMember Command,
    string PublisherId,
    string CommandId,
    string DeduplicationOperationId);

internal sealed class StudioWorkflowDraftMemberEnsureCommandFactory
{
    internal const string PublisherId = "aevatar.studio.projection.workflow-draft-member-ensure";

    public StudioWorkflowDraftMemberEnsureCommandPlan? TryCreate(
        string scopeId,
        string workflowId,
        string? displayName,
        Timestamp? requestedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            return null;

        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var memberId = StudioMemberConventions.NormalizeMemberId(workflowId);
        var actorId = StudioMemberConventions.BuildActorId(normalizedScopeId, memberId);
        var commandId = BuildCommandId(normalizedScopeId, memberId);
        var command = new EnsureStudioMember
        {
            MemberId = memberId,
            ScopeId = normalizedScopeId,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? memberId
                : displayName.Trim(),
            Description = string.Empty,
            RequestedAtUtc = requestedAtUtc,
        };

        return new StudioWorkflowDraftMemberEnsureCommandPlan(
            normalizedScopeId,
            memberId,
            actorId,
            command,
            PublisherId,
            commandId,
            commandId);
    }

    private static string BuildCommandId(string scopeId, string memberId) =>
        $"{PublisherId}:{scopeId}:{memberId}";
}
