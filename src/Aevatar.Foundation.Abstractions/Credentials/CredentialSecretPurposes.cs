namespace Aevatar.Foundation.Abstractions.Credentials;

public static class CredentialSecretPurposes
{
    public const string ScheduledNyxApiKey = "scheduled.nyx-api-key";
    public const string ScheduledInvocationAgentKey = "scheduled.invocation-agent-key";
    public const string WorkflowCallerDurableBearerToken = "workflow.caller-durable-bearer-token";
    public const string WorkflowCallerBearerToken = "workflow.caller-bearer-token";
    public const string WorkflowCallerSourceReadableUserBearerToken =
        "workflow.caller-source-readable-user-bearer-token";
    public const string WorkflowSecureInputValue = "workflow.secure-input-value";
    public const string WorkflowConnectorExternalActionMaterial = "workflow.connector-external-action-material";
    public const string WorkflowConnectorExternalActionCompletion = "workflow.connector-external-action-completion";
    public const string DeviceHmacSigningKey = "device.hmac-signing-key";
    public const string OAuthStateTokenHmacKey = "identity.oauth-state-token-hmac-key";
    public const string ChannelWorkflowResultDeliveryAgentKey = "channel.workflow-result-delivery-agent-key";
    public const string ManagedCodexInvocationAgentKey = "managed.codex-invocation-agent-key";
}
