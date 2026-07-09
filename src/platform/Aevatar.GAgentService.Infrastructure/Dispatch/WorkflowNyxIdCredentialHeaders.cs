namespace Aevatar.GAgentService.Infrastructure.Dispatch;

public static class WorkflowNyxIdCredentialHeaders
{
    public const string SubjectPlatform = "workflow.caller_credential.nyx_id.subject.platform";
    public const string SubjectTenant = "workflow.caller_credential.nyx_id.subject.tenant";
    public const string SubjectExternalUserId = "workflow.caller_credential.nyx_id.subject.external_user_id";
    public const string Scope = "workflow.caller_credential.nyx_id.scope";

    public static bool IsReserved(string key) =>
        string.Equals(key, SubjectPlatform, StringComparison.Ordinal) ||
        string.Equals(key, SubjectTenant, StringComparison.Ordinal) ||
        string.Equals(key, SubjectExternalUserId, StringComparison.Ordinal) ||
        string.Equals(key, Scope, StringComparison.Ordinal);
}
