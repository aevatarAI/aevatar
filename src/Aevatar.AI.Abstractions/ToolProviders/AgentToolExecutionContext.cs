using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: tool control facts lived in AsyncLocal string Metadata.
//   New principle: tool control semantics are typed context fields; Metadata is not the internal control plane.
public sealed record AgentToolExecutionContext(
    AgentToolRequestIdentity Request,
    AgentToolCredentials Credentials,
    AgentToolCallerContext Caller,
    AgentToolChannelContext Channel,
    AgentToolSenderBindingContext SenderBinding,
    LLMRequestRoutingContext Routing,
    AgentToolConnectedServicesContext ConnectedServices,
    AgentWorkflowRuntimeContext WorkflowRuntime,
    AgentToolScheduleContext Schedule,
    AgentToolCredentialSource CredentialSource,
    AgentSkillRecoveryContext SkillRecovery,
    IReadOnlyDictionary<string, string> ExternalMetadata)
{
    public AgentToolExecutionContext(
        AgentToolRequestIdentity Request,
        AgentToolCredentials Credentials,
        AgentToolCallerContext Caller,
        AgentToolChannelContext Channel,
        AgentToolSenderBindingContext SenderBinding,
        LLMRequestRoutingContext Routing,
        AgentToolConnectedServicesContext ConnectedServices,
        AgentSkillRecoveryContext SkillRecovery,
        IReadOnlyDictionary<string, string> ExternalMetadata)
        : this(
            Request,
            Credentials,
            Caller,
            Channel,
            SenderBinding,
            Routing,
            ConnectedServices,
            AgentWorkflowRuntimeContext.Empty,
            AgentToolScheduleContext.Empty,
            AgentToolCredentialSource.Unspecified,
            SkillRecovery,
            ExternalMetadata)
    {
    }

    public AgentToolExecutionContext(
        AgentToolRequestIdentity Request,
        AgentToolCredentials Credentials,
        AgentToolCallerContext Caller,
        AgentToolChannelContext Channel,
        AgentToolSenderBindingContext SenderBinding,
        LLMRequestRoutingContext Routing,
        AgentToolConnectedServicesContext ConnectedServices,
        AgentWorkflowRuntimeContext WorkflowRuntime,
        AgentSkillRecoveryContext SkillRecovery,
        IReadOnlyDictionary<string, string> ExternalMetadata)
        : this(
            Request,
            Credentials,
            Caller,
            Channel,
            SenderBinding,
            Routing,
            ConnectedServices,
            WorkflowRuntime,
            AgentToolScheduleContext.Empty,
            AgentToolCredentialSource.Unspecified,
            SkillRecovery,
            ExternalMetadata)
    {
    }

    public AgentToolVisibilityScope ToolVisibility { get; init; } = AgentToolVisibilityScope.Unrestricted;

    public AgentToolNyxIdAuthorityContext NyxIdAuthority { get; init; } =
        AgentToolNyxIdAuthorityContext.Empty;

    /// <summary>
    /// Committed proof for the exact operation this call site was admitted to invoke. Null for
    /// call sites that are not under external-capability admission (ordinary human sessions).
    /// </summary>
    public AgentToolOperationAdmission? OperationAdmission { get; init; }

    public AgentToolInvocationSurface InvocationSurface { get; init; } =
        AgentToolInvocationSurface.Unspecified;

    public AgentChatInvocationContext Chat { get; init; } = AgentChatInvocationContext.Empty;

    public IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef> InputFileRefs { get; init; } = [];

    public AgentToolExecutionOwner ExecutionOwner { get; init; } = new();

    public DurableCallerCredentialRef? DurableNyxIdCredential { get; init; }

    public static AgentToolExecutionContext Empty { get; } = new(
        AgentToolRequestIdentity.Empty,
        AgentToolCredentials.Empty,
        AgentToolCallerContext.Empty,
        AgentToolChannelContext.Empty,
        AgentToolSenderBindingContext.Empty,
        LLMRequestRoutingContext.Empty,
        AgentToolConnectedServicesContext.Empty,
        AgentWorkflowRuntimeContext.Empty,
        AgentToolScheduleContext.Empty,
        AgentToolCredentialSource.Unspecified,
        AgentSkillRecoveryContext.Empty,
        new Dictionary<string, string>(StringComparer.Ordinal));

    public AgentToolExecutionContext WithCallId(string? callId) =>
        this with { Request = Request with { CallId = Normalize(callId) } };

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum AgentToolInvocationSurface
{
    Unspecified = 0,
    HumanSession = 1,
    WorkflowToolCall = 2,
    WorkflowLlmToolLoop = 3,
}

public enum AgentChatInvocationSurface
{
    Unspecified = 0,
    NyxIdAssistant = 1,
    WorkflowChat = 2,
}

public sealed record AgentChatInvocationContext(
    AgentChatInvocationSurface Surface,
    string? ConversationId,
    string? TurnId,
    string? TaskId,
    string? StepId,
    string? ActionRequestId)
{
    public static AgentChatInvocationContext Empty { get; } =
        new(AgentChatInvocationSurface.Unspecified, null, null, null, null, null);
}

public sealed record AgentToolVisibilityScope(IReadOnlySet<string>? AllowedToolNames)
{
    public static AgentToolVisibilityScope Unrestricted { get; } = new((IReadOnlySet<string>?)null);

    public static AgentToolVisibilityScope Empty { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool IsRestricted => AllowedToolNames is not null;

    public bool Allows(string? toolName)
    {
        if (AllowedToolNames is null)
            return true;

        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return AllowedToolNames.Contains(toolName.Trim());
    }

    public static AgentToolVisibilityScope FromAllowedToolNames(IEnumerable<string>? toolNames)
    {
        if (toolNames is null)
            return Unrestricted;

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in toolNames)
        {
            var normalized = AgentToolExecutionContext.Normalize(toolName);
            if (normalized is not null)
                allowed.Add(normalized);
        }

        return new AgentToolVisibilityScope(allowed);
    }
}

public sealed record AgentToolRequestIdentity(
    string? RequestId,
    string? CallId,
    string? IdempotencyKey,
    long IssuedAtUnixMs,
    string? OperationId = null,
    long OperationGeneration = 0)
{
    public AgentToolRequestIdentity(
        string? requestId,
        string? callId,
        string? idempotencyKey = null)
        : this(
            requestId,
            callId,
            idempotencyKey,
            TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds())
    {
    }

    public static AgentToolRequestIdentity Empty { get; } = new(null, null, null, 0);
}

public enum AgentToolNyxIdCredentialKind
{
    Unspecified = 0,
    SourceReadableUserBearer = 1,
    ProxyDelegation = 2,
    AgentKey = 3,
}

public enum AgentToolNyxIdCredentialAuthority
{
    Unspecified = 0,
    ToolExecutionContext = 1,
}

public sealed record AgentToolCredentials(
    string? NyxIdAccessToken,
    string? NyxIdOrgToken,
    string? SenderNyxIdAccessToken,
    AgentToolNyxIdCredentialKind NyxIdCredentialKind = AgentToolNyxIdCredentialKind.Unspecified,
    string? SourceReadableNyxIdAccessToken = null,
    AgentToolNyxIdCredentialAuthority NyxIdCredentialAuthority =
        AgentToolNyxIdCredentialAuthority.Unspecified)
{
    public static AgentToolCredentials Empty { get; } = new(null, null, null);
}

public enum AgentToolCredentialSource
{
    Unspecified = 0,
    NyxIdAssertion = 1,
    BearerToken = 2,
    ChannelRegistration = 3,
    ScheduledRun = 4,
    System = 5,
    ServiceAccount = 6,
}

public sealed record AgentToolCallerContext(string? ScopeId, string? OwnerSubject, string? ResponseId, string? OwnerScopeId = null)
{
    public static AgentToolCallerContext Empty { get; } = new(null, null, null);
}

public sealed record AgentToolNyxIdAuthorityContext(
    string? Platform,
    string? Tenant,
    string? ExternalUserId,
    string? Scope = null)
{
    public static AgentToolNyxIdAuthorityContext Empty { get; } = new(null, null, null);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Platform) &&
        !string.IsNullOrWhiteSpace(ExternalUserId);
}

public sealed record AgentToolChannelContext(
    string? Platform,
    string? SenderId,
    string? RegistrationScopeId,
    string? MessageId,
    string? PlatformMessageId,
    string? DeliveryTargetId = null,
    ChannelWorkflowResultDeliveryCredential? WorkflowResultDeliveryCredential = null,
    string? BotRegistrationId = null,
    IReadOnlyList<AgentToolChannelIdentityHint>? IdentityHints = null)
{
    public static AgentToolChannelContext Empty { get; } = new(null, null, null, null, null, null, null, null, []);
}

public sealed record AgentToolChannelIdentityHint(string Subject, string Kind, string Value);

public sealed record AgentToolSenderBindingContext(string? BindingId, string? NyxUserId = null, string? SenderTenant = null)
{
    public static AgentToolSenderBindingContext Empty { get; } = new((string?)null, null, null);
}

public sealed record AgentToolConnectedServicesContext(string? ContextJson)
{
    public static AgentToolConnectedServicesContext Empty { get; } = new((string?)null);
}

public sealed record AgentWorkflowRuntimeContext(
    string? ParentActorId,
    string? ParentRunId,
    string? ParentStepId,
    string? RootRunId,
    int Depth)
{
    public static AgentWorkflowRuntimeContext Empty { get; } = new(null, null, null, null, 0);

    public bool HasManagedParent =>
        !string.IsNullOrWhiteSpace(ParentActorId) &&
        !string.IsNullOrWhiteSpace(ParentRunId) &&
        !string.IsNullOrWhiteSpace(ParentStepId);
}

public sealed record AgentToolScheduleContext(string? ScheduleId)
{
    public static AgentToolScheduleContext Empty { get; } = new((string?)null);
}

public sealed record AgentSkillRecoveryContext(
    bool RequireInitialOrnnSearch,
    bool RequireOrnnSearchOnBlocker,
    string? CommandName,
    string? OriginalCommand,
    string? PrimarySkillName,
    int MaxOrnnSearchAttempts,
    string? CommandArguments = null,
    bool DiscoveryRequested = false,
    bool IsolatePriorConversationHistory = false,
    bool MountWorkflowsRequested = false)
{
    public static AgentSkillRecoveryContext Empty { get; } = new(
        RequireInitialOrnnSearch: false,
        RequireOrnnSearchOnBlocker: false,
        CommandName: null,
        OriginalCommand: null,
        PrimarySkillName: null,
        MaxOrnnSearchAttempts: 0,
        CommandArguments: null,
        DiscoveryRequested: false,
        IsolatePriorConversationHistory: false,
        MountWorkflowsRequested: false);
}
