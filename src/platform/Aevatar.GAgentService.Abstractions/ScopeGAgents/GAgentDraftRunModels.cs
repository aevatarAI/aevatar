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
}

public sealed record GAgentDraftRunCommand(
    string ScopeId,
    string ActorTypeName,
    string Prompt,
    string? PreferredActorId = null,
    string? SessionId = null,
    string? NyxIdAccessToken = null,
    string? ModelOverride = null,
    string? PreferredLlmRoute = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<GAgentDraftRunInputPart>? InputParts = null,
    bool UseCorrelationIdAsFallbackSessionId = true) : ICommandContextSeed
{
    public string? CommandId => null;

    public string? CorrelationId => null;
}

public enum GAgentDraftRunStartError
{
    None = 0,
    UnknownActorType = 1,
    ActorTypeMismatch = 2,
}

public enum GAgentDraftRunCompletionStatus
{
    Unknown = 0,
    TextMessageCompleted = 1,
    RunFinished = 2,
    Failed = 3,
}

public sealed record GAgentDraftRunAcceptedReceipt(
    string ActorId,
    string ActorTypeName,
    string CommandId,
    string CorrelationId);

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
}

public enum GAgentApprovalCompletionStatus
{
    Unknown = 0,
    TextMessageCompleted = 1,
    RunFinished = 2,
    Failed = 3,
}

public sealed record GAgentApprovalAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);
