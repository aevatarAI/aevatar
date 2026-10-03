using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>Public catalog facts and an API contract already normalized by the document adapter.</summary>
public sealed record CatalogSkillContentInput(
    string CatalogServiceId,
    string CatalogServiceSlug,
    string CatalogName,
    string CatalogDescription,
    CatalogApiContract ApiContract);

public sealed record CatalogApiContract(IReadOnlyList<CatalogApiOperation> Operations);

public sealed record CatalogApiOperation(
    string OperationId,
    string Name,
    string Method,
    string RelativePath,
    NyxIdOperationRisk Risk,
    IReadOnlyList<CatalogApiParameter> Parameters,
    CatalogApiRequestBody? RequestBody,
    IReadOnlyList<CatalogApiResponse> Responses);

public sealed record CatalogApiParameter(
    string Name,
    ParameterLocation In,
    bool Required,
    Value? Schema,
    string? Description);

public sealed record CatalogApiRequestBody(bool Required, IReadOnlyList<CatalogApiMediaType> Content);

public sealed record CatalogApiResponse(
    string StatusCode,
    string Description,
    IReadOnlyList<CatalogApiMediaType> Content);

/// <summary>A normalized external OpenAPI media contract; the schema retains its business fields.</summary>
public sealed record CatalogApiMediaType(string MediaType, Value? Schema);

public sealed record GeneratedCatalogSkillContent(
    string Name,
    string Description,
    string Category,
    string InstructionsMarkdown,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ToolList,
    string DisplayName,
    string RecommendationName,
    string Revision);
