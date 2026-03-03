namespace Aevatar.AI.Abstractions.Agents;

/// <summary>
/// Raw role extension input from YAML.
/// </summary>
public sealed class RoleExtensionsInput
{
    public string? EventModules { get; init; }
    public string? EventRoutes { get; init; }
}

/// <summary>
/// Cross-entry role configuration input.
/// Used by both workflow roles and standalone role yaml.
/// </summary>
public sealed class RoleConfigurationInput
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? SystemPrompt { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }

    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxToolRounds { get; init; }
    public int? MaxHistoryMessages { get; init; }
    public int? StreamBufferCapacity { get; init; }

    public string? EventModules { get; init; }
    public string? EventRoutes { get; init; }
    public RoleExtensionsInput? Extensions { get; init; }
    public IReadOnlyList<string>? Connectors { get; init; }
}

/// <summary>
/// Normalized role configuration.
/// </summary>
public sealed class RoleConfigurationNormalized
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string SystemPrompt { get; init; } = "";
    public string? Provider { get; init; }
    public string? Model { get; init; }

    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxToolRounds { get; init; }
    public int? MaxHistoryMessages { get; init; }
    public int? StreamBufferCapacity { get; init; }

    public string? EventModules { get; init; }
    public string? EventRoutes { get; init; }
    public IReadOnlyList<string> Connectors { get; init; } = [];
}

/// <summary>
/// Shared role normalization rules.
/// </summary>
public static class RoleConfigurationNormalizer
{
    public static RoleConfigurationNormalized Normalize(RoleConfigurationInput input)
    {
        var effectiveId = input.Id ?? input.Name ?? string.Empty;
        var effectiveName = input.Name ?? input.Id ?? string.Empty;

        // Top-level fields win over extensions.* to keep explicit override semantics.
        var eventModules = NormalizeText(input.EventModules)
                           ?? NormalizeText(input.Extensions?.EventModules);
        var eventRoutes = NormalizeText(input.EventRoutes)
                          ?? NormalizeText(input.Extensions?.EventRoutes);

        var connectors = input.Connectors?.ToList() ?? [];
        return new RoleConfigurationNormalized
        {
            Id = effectiveId,
            Name = effectiveName,
            SystemPrompt = input.SystemPrompt ?? string.Empty,
            Provider = NormalizeText(input.Provider),
            Model = NormalizeText(input.Model),
            Temperature = input.Temperature,
            MaxTokens = input.MaxTokens,
            MaxToolRounds = input.MaxToolRounds,
            MaxHistoryMessages = input.MaxHistoryMessages,
            StreamBufferCapacity = input.StreamBufferCapacity,
            EventModules = eventModules,
            EventRoutes = eventRoutes,
            Connectors = connectors,
        };
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

}
