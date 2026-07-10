namespace Aevatar.Foundation.Abstractions.Credentials;

public static class CredentialSecretPurposes
{
    public const string ScheduledNyxApiKey = "scheduled.nyx-api-key";
    public const string ScheduledInvocationAgentKey = "scheduled.invocation-agent-key";
    public const string WorkflowCallerBearerToken = "workflow.caller-bearer-token";
    public const string WorkflowSecureInputValue = "workflow.secure-input-value";
    public const string DeviceHmacSigningKey = "device.hmac-signing-key";
    public const string OAuthStateTokenHmacKey = "identity.oauth-state-token-hmac-key";
}
