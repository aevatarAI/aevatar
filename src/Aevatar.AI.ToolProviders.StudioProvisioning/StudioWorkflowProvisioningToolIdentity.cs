using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal static class StudioWorkflowProvisioningToolIdentity
{
    public const string MissingIdentityErrorCode = "operation_identity_unavailable";

    public const string ConflictErrorCode = "workflow_provisioning_identity_conflict";

    public const string MissingIdentityErrorMessage =
        "A trusted idempotency key is required to provision a workflow without creating duplicate fallback resources.";

    public static string? ResolveTrustedIdempotencyKey()
    {
        var callerIdempotencyKey = Normalize(AgentToolRequestContext.IdempotencyKey);
        return callerIdempotencyKey is null ? null : $"agent-tool-idempotency:{callerIdempotencyKey}";
    }

    public static string BuildMemberId(string scopeId, string idempotencyKey) =>
        WorkflowProvisioningIdentity.BuildMemberId(scopeId, idempotencyKey);

    public static string BuildWorkflowId(string scopeId, string idempotencyKey) =>
        WorkflowProvisioningIdentity.BuildWorkflowId(scopeId, idempotencyKey);

    public static bool IsProvisioningMember(string scopeId, string idempotencyKey, string memberId) =>
        string.Equals(
            BuildMemberId(scopeId, idempotencyKey),
            memberId,
            StringComparison.Ordinal);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
