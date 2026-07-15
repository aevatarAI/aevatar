using Aevatar.AI.Abstractions.LLMProviders;

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

public sealed record AgentToolRequestIdentity(string? RequestId, string? CallId, string? IdempotencyKey = null)
{
    public static AgentToolRequestIdentity Empty { get; } = new(null, null, null);
}

public sealed record AgentToolCredentials(
    string? AccessToken,
    string? OrganizationToken,
    string? SenderAccessToken)
{
    public static AgentToolCredentials Empty { get; } = new(null, null, null);
}

public enum AgentToolCredentialSource
{
    Unspecified = 0,
    IdentityAssertion = 1,
    BearerToken = 2,
    ChannelRegistration = 3,
    ScheduledRun = 4,
    System = 5,
    ServiceAccount = 6,
}

public sealed record AgentToolCallerContext(string? ScopeId, string? OwnerSubject, string? ResponseId)
{
    public static AgentToolCallerContext Empty { get; } = new(null, null, null);
}

public sealed record AgentToolChannelContext(
    string? Platform,
    string? SenderId,
    string? RegistrationScopeId,
    string? MessageId,
    string? PlatformMessageId,
    string? DeliveryTargetId = null,
    ChannelWorkflowResultDeliveryCredential? WorkflowResultDeliveryCredential = null,
    string? BotRegistrationId = null)
{
    public static AgentToolChannelContext Empty { get; } = new(null, null, null, null, null, null, null, null);
}

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
    bool DiscoveryRequested = false)
{
    public static AgentSkillRecoveryContext Empty { get; } = new(
        RequireInitialOrnnSearch: false,
        RequireOrnnSearchOnBlocker: false,
        CommandName: null,
        OriginalCommand: null,
        PrimarySkillName: null,
        MaxOrnnSearchAttempts: 0,
        CommandArguments: null,
        DiscoveryRequested: false);
}
