using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public enum GAgentDraftRunInputPartKind
{
    Unspecified = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Video = 4,
}

public sealed record GAgentDraftRunInputPart
{
    public required GAgentDraftRunInputPartKind Kind { get; init; }
    public string? Text { get; init; }
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public Aevatar.AI.Abstractions.ChatFileRef? FileRef { get; init; }
}

// Refactor (iter1353/cluster-001): Old pattern: draft-run commands rebuilt trusted caller/control facts from headers and legacy scalars.
// New principle: commands carry typed ToolContext and LlmControl as the authoritative internal control fields.
public sealed record GAgentDraftRunCommand(
    string ScopeId,
    string AgentKind,
    string Prompt,
    string? PreferredActorId = null,
    string? SessionId = null,
    string? NyxIdAccessToken = null,
    string? ModelOverride = null,
    string? PreferredLlmRoute = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<GAgentDraftRunInputPart>? InputParts = null,
    bool UseCorrelationIdAsFallbackSessionId = true,
    AgentToolExecutionContext? ToolContext = null,
    LLMControlContext? LlmControl = null,
    string? CommandIdSeed = null,
    string? CorrelationIdSeed = null) : ICommandContextSeed
{
    public string? CommandId => CommandIdSeed;

    public string? CorrelationId => CorrelationIdSeed;
}

public enum GAgentDraftRunStartError
{
    None = 0,
    UnknownAgentKind = 1,
    ActorKindMismatch = 2,
    ProjectionUnavailable = 3,
}

public enum GAgentDraftRunCompletionStatus
{
    Unknown = 0,
    TextMessageCompleted = 1,
    RunFinished = 2,
    Failed = 3,
    OutcomeUncertain = 4,
}

public sealed record GAgentDraftRunAcceptedReceipt(
    string ActorId,
    string DiagnosticClrTypeName,
    string CommandId,
    string CorrelationId,
    string SessionId = "");

public sealed record GAgentApprovalCommand(
    string ActorId,
    string RequestId,
    bool Approved = true,
    string? Reason = null,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? Headers = null) : ICommandContextSeed
{
    public string? CommandId => null;

    public string? CorrelationId => null;
}

public enum GAgentApprovalStartError
{
    None = 0,
    ActorNotFound = 1,
    ProjectionUnavailable = 2,
}

public enum GAgentApprovalCompletionStatus
{
    Unknown = 0,
    TextMessageCompleted = 1,
    RunFinished = 2,
    Failed = 3,
    OutcomeUncertain = 4,
}

public sealed record GAgentApprovalAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string SessionId);
