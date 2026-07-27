using System.Text.Json.Serialization;

namespace Aevatar.Studio.Application.Studio.Contracts;

public enum WorkflowTemplateCompatibilityStatus
{
    Compatible = 0,
    Incompatible = 1,
}

public enum WorkflowTemplateCompatibilityReason
{
    None = 0,
    WorkflowSchemaUnsupported = 1,
    RequiredPrimitiveUnavailable = 2,
}

public sealed record WorkflowTemplateRequirements(
    IReadOnlyList<string> RequiredPrimitives,
    string WorkflowSchemaVersion);

public sealed record WorkflowTemplateCompatibility(
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    WorkflowTemplateCompatibilityStatus Status,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    WorkflowTemplateCompatibilityReason Reason)
{
    public static WorkflowTemplateCompatibility Compatible { get; } =
        new(WorkflowTemplateCompatibilityStatus.Compatible, WorkflowTemplateCompatibilityReason.None);
}

public sealed record WorkflowTemplateSummary(
    string TemplateId,
    string Revision,
    string Title,
    string Description,
    string Category,
    WorkflowTemplateRequirements Requirements,
    WorkflowTemplateCompatibility Compatibility);

public sealed record WorkflowTemplateDetail(
    string TemplateId,
    string Revision,
    string Title,
    string Description,
    string Category,
    WorkflowTemplateRequirements Requirements,
    WorkflowTemplateCompatibility Compatibility,
    string WorkflowYaml);

public sealed record WorkflowTemplateCatalogQuery(
    string? Query = null,
    string? Category = null,
    string? Cursor = null,
    int PageSize = 20);

public sealed record WorkflowTemplateCatalogPage(
    IReadOnlyList<WorkflowTemplateSummary> Items,
    string? NextCursor,
    string ETag);

public enum WorkflowTemplateLookupStatus
{
    Found = 0,
    NotFound = 1,
    Disabled = 2,
    Incompatible = 3,
}

public sealed record WorkflowTemplateLookupResult(
    WorkflowTemplateLookupStatus Status,
    WorkflowTemplateDetail? Detail);
