using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;

namespace Aevatar.Studio.Hosting.Controllers;

public sealed record SerializeYamlHttpRequest(
    EditorWorkflowDocumentDto Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    IReadOnlyCollection<string>? AvailableStepTypes = null)
{
    internal SerializeYamlRequest ToApplicationRequest() =>
        new(Document.ToDomainModel(), AvailableWorkflowNames, AvailableStepTypes);
}

public sealed record ValidateWorkflowHttpRequest(
    EditorWorkflowDocumentDto Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    IReadOnlyCollection<string>? AvailableStepTypes = null)
{
    internal ValidateWorkflowRequest ToApplicationRequest() =>
        new(Document.ToDomainModel(), AvailableWorkflowNames, AvailableStepTypes);
}

public sealed record NormalizeWorkflowHttpRequest(
    EditorWorkflowDocumentDto Document,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    IReadOnlyCollection<string>? AvailableStepTypes = null)
{
    internal NormalizeWorkflowRequest ToApplicationRequest() =>
        new(Document.ToDomainModel(), AvailableWorkflowNames, AvailableStepTypes);
}

public sealed record EditorWorkflowDocumentDto
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public EditorWorkflowConfigurationDto Configuration { get; init; } = new();

    public IReadOnlyList<EditorRoleDto> Roles { get; init; } = [];

    public IReadOnlyList<EditorStepDto> Steps { get; init; } = [];

    internal WorkflowDocument ToDomainModel() => new()
    {
        Name = Name,
        Description = Description,
        Configuration = Configuration.ToDomainModel(),
        Roles = Roles.Select(role => role.ToDomainModel()).ToList(),
        Steps = Steps.Select(step => step.ToDomainModel()).ToList(),
    };
}

public sealed record EditorWorkflowConfigurationDto
{
    public bool ClosedWorldMode { get; init; }

    internal WorkflowConfiguration ToDomainModel() => new()
    {
        ClosedWorldMode = ClosedWorldMode,
    };
}

public sealed record EditorRoleDto
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string SystemPrompt { get; init; } = string.Empty;

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    public int? MaxToolRounds { get; init; }

    public int? MaxHistoryMessages { get; init; }

    public string? EventModules { get; init; }

    public string? EventRoutes { get; init; }

    public IReadOnlyList<string> Connectors { get; init; } = [];

    internal RoleModel ToDomainModel() => new()
    {
        Id = Id,
        Name = Name,
        SystemPrompt = SystemPrompt,
        Provider = Provider,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        MaxToolRounds = MaxToolRounds,
        MaxHistoryMessages = MaxHistoryMessages,
        EventModules = EventModules,
        EventRoutes = EventRoutes,
        Connectors = Connectors.ToList(),
    };
}

public sealed record EditorStepDto
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string? OriginalType { get; init; }

    public string? TargetRole { get; init; }

    [JsonPropertyName("target_role")]
    public string? TargetRoleAlias { get; init; }

    public bool UsedRoleAlias { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Parameters { get; init; }

    public string? Next { get; init; }

    public IReadOnlyDictionary<string, string>? Branches { get; init; }

    public IReadOnlyList<EditorStepDto> Children { get; init; } = [];

    public bool ImportedFromChildren { get; init; }

    public EditorStepRetryPolicyDto? Retry { get; init; }

    public EditorStepErrorPolicyDto? OnError { get; init; }

    public int? TimeoutMs { get; init; }

    internal StepModel ToDomainModel() => new()
    {
        Id = Id,
        Type = Type,
        OriginalType = OriginalType,
        TargetRole = TargetRole ?? TargetRoleAlias,
        UsedRoleAlias = UsedRoleAlias,
        Parameters = ToDomainParameters(Parameters),
        Next = Next,
        Branches = Branches?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ??
            new Dictionary<string, string>(StringComparer.Ordinal),
        Children = Children.Select(child => child.ToDomainModel()).ToList(),
        ImportedFromChildren = ImportedFromChildren,
        Retry = Retry?.ToDomainModel(),
        OnError = OnError?.ToDomainModel(),
        TimeoutMs = TimeoutMs,
    };

    private static StudioStepParameters ToDomainParameters(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return new StudioStepParameters();
        }

        return new StudioStepParameters(parameters.Select(parameter =>
            new KeyValuePair<string, StudioStepParameterValue?>(
                parameter.Key,
                ToParameterValue(parameter.Value))));
    }

    private static StudioStepParameterValue ToParameterValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Null => StudioStepParameterValue.Null,
            JsonValueKind.String => StudioStepParameterValue.FromScalar(value.GetString()),
            JsonValueKind.True => StudioStepParameterValue.FromScalar(bool.TrueString.ToLowerInvariant()),
            JsonValueKind.False => StudioStepParameterValue.FromScalar(bool.FalseString.ToLowerInvariant()),
            JsonValueKind.Number => StudioStepParameterValue.FromScalar(value.GetRawText()),
            JsonValueKind.Array => StudioStepParameterValue.FromList(
                value.EnumerateArray().Select(ToParameterValue)),
            JsonValueKind.Object => StudioStepParameterValue.FromObject(
                value.EnumerateObject().Select(property =>
                    new KeyValuePair<string, StudioStepParameterValue?>(
                        property.Name,
                        ToParameterValue(property.Value)))),
            _ => StudioStepParameterValue.FromScalar(value.GetRawText()),
        };
}

public sealed record EditorStepRetryPolicyDto
{
    public int MaxAttempts { get; init; } = 3;

    public string Backoff { get; init; } = "fixed";

    public int DelayMs { get; init; } = 1000;

    internal StepRetryPolicy ToDomainModel() => new()
    {
        MaxAttempts = MaxAttempts,
        Backoff = Backoff,
        DelayMs = DelayMs,
    };
}

public sealed record EditorStepErrorPolicyDto
{
    public string Strategy { get; init; } = "fail";

    public string? FallbackStep { get; init; }

    public string? DefaultOutput { get; init; }

    internal StepErrorPolicy ToDomainModel() => new()
    {
        Strategy = Strategy,
        FallbackStep = FallbackStep,
        DefaultOutput = DefaultOutput,
    };
}
