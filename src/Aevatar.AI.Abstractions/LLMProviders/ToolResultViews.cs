using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Abstractions.LLMProviders;

public enum ToolResultViewStatus
{
    Unknown = 0,
    Success = 1,
    NoMatch = 2,
    NotFound = 3,
    Error = 4,
}

public sealed record SkillSearchMatchView(
    string SkillName,
    string? Description,
    bool IsPrivate,
    string? Category,
    IReadOnlyList<string> Tags);

public sealed record SkillSearchToolResultView(
    ToolResultViewStatus Status,
    bool HasMatches,
    IReadOnlyList<SkillSearchMatchView> Matches,
    string? Error,
    int? HttpStatus,
    string DisplayText);

public sealed record SkillLoadToolResultView(
    ToolResultViewStatus Status,
    string? SkillName,
    bool Loaded,
    string? Error,
    int? HttpStatus,
    string DisplayText);

public sealed record ToolFailureResultView(
    AgentToolReceiptStatus Status,
    string ErrorCode,
    string SafeMessage);

public sealed record ToolResultView(
    string ToolName,
    SkillSearchToolResultView? SkillSearch,
    SkillLoadToolResultView? SkillLoad,
    ToolFailureResultView? Failure = null);
