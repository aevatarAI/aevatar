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
            SkillRecovery,
            ExternalMetadata)
    {
    }

    public static AgentToolExecutionContext Empty { get; } = new(
        AgentToolRequestIdentity.Empty,
        AgentToolCredentials.Empty,
        AgentToolCallerContext.Empty,
        AgentToolChannelContext.Empty,
        AgentToolSenderBindingContext.Empty,
        LLMRequestRoutingContext.Empty,
        AgentToolConnectedServicesContext.Empty,
        AgentWorkflowRuntimeContext.Empty,
        AgentSkillRecoveryContext.Empty,
        new Dictionary<string, string>(StringComparer.Ordinal));

    public AgentToolExecutionContext WithCallId(string? callId) =>
        this with { Request = Request with { CallId = Normalize(callId) } };

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AgentToolRequestIdentity(string? RequestId, string? CallId)
{
    public static AgentToolRequestIdentity Empty { get; } = new(null, null);
}

public sealed record AgentToolCredentials(
    string? NyxIdAccessToken,
    string? NyxIdOrgToken,
    string? SenderNyxIdAccessToken)
{
    public static AgentToolCredentials Empty { get; } = new(null, null, null);
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
    string? PlatformMessageId)
{
    public static AgentToolChannelContext Empty { get; } = new(null, null, null, null, null);
}

public sealed record AgentToolSenderBindingContext(string? BindingId)
{
    public static AgentToolSenderBindingContext Empty { get; } = new((string?)null);
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
