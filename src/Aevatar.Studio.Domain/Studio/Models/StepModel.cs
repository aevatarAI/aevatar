namespace Aevatar.Studio.Domain.Studio.Models;

public sealed record StepModel
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string? OriginalType { get; init; }

    public string? TargetRole { get; init; }

    public bool UsedRoleAlias { get; init; }

    public List<string>? AllowedTools { get; init; }

    public StepCapability? Capability { get; init; }

    public StudioStepParameters Parameters { get; init; } = new();

    public string? Next { get; init; }

    public Dictionary<string, string> Branches { get; init; } = new(StringComparer.Ordinal);

    public List<StepModel> Children { get; init; } = [];

    public bool ImportedFromChildren { get; init; }

    public StepRetryPolicy? Retry { get; init; }

    public StepErrorPolicy? OnError { get; init; }

    public int? TimeoutMs { get; init; }
}
