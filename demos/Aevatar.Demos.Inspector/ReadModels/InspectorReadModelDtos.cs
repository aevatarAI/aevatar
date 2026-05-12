using System.Text.Json;

namespace Aevatar.Demos.Inspector.ReadModels;

public sealed record InspectorActorGroupDto(
    string Type,
    IReadOnlyList<string> ActorIds,
    int Count);

public sealed record InspectorActorsResponse(
    string ScopeId,
    long StateVersion,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ObservedAt,
    IReadOnlyList<InspectorActorGroupDto> Groups);

public sealed record InspectorWorkflowRunDto(
    string ActorId,
    string WorkflowName,
    string Status,
    long StateVersion,
    string LastEventId,
    DateTimeOffset LastUpdatedAt,
    int TotalSteps,
    int CompletedSteps);

public sealed record InspectorReadModelSummaryDto(
    string Name,
    string DocumentType,
    long? TotalCount,
    long? LatestStateVersion,
    DateTimeOffset? LatestUpdatedAt);

public sealed record InspectorReadModelDocumentDto(
    string Name,
    string DocumentType,
    JsonElement Document);

public sealed record InspectorReadModelPageDto(
    string Name,
    string DocumentType,
    int Count,
    string? NextCursor,
    IReadOnlyList<JsonElement> Documents);
