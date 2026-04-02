namespace Aevatar.Studio.Domain.Studio.Models;

public sealed record RoleModel
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

    public int? StreamBufferCapacity { get; init; }

    public string? EventModules { get; init; }

    public string? EventRoutes { get; init; }

    public List<string> Connectors { get; init; } = [];
}
