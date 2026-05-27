namespace Aevatar.Workflow.Core.Primitives;

internal static class WorkflowRoleConfigurationNormalizer
{
    public static RoleDefinition Normalize(
        string? id,
        string? name,
        string? agentKind,
        string? systemPrompt,
        string? provider,
        string? model,
        double? temperature,
        int? maxTokens,
        int? maxToolRounds,
        int? maxHistoryMessages,
        string? eventModules,
        string? eventRoutes,
        IEnumerable<string>? connectors)
    {
        return new RoleDefinition
        {
            Id = Required(id, "role.id"),
            Name = NormalizeText(name) ?? Required(id, "role.name"),
            AgentKind = NormalizeText(agentKind),
            SystemPrompt = NormalizeText(systemPrompt) ?? string.Empty,
            Provider = NormalizeText(provider),
            Model = NormalizeText(model),
            Temperature = temperature,
            MaxTokens = maxTokens,
            MaxToolRounds = maxToolRounds,
            MaxHistoryMessages = maxHistoryMessages,
            EventModules = NormalizeText(eventModules),
            EventRoutes = NormalizeText(eventRoutes),
            Connectors = connectors?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? [],
        };
    }

    private static string Required(string? value, string field)
    {
        var normalized = NormalizeText(value);
        return normalized ?? throw new InvalidOperationException($"Missing {field}");
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
