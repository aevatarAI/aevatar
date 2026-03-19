using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class DefaultScriptingActorAddressResolver : IScriptingActorAddressResolver
{
    private const string EvolutionManagerActorId = "script-evolution-manager";
    private const string CatalogActorId = "script-catalog";
    private const string ScopedCatalogActorIdPrefix = "user-script-catalog";
    private const string ScopedDefinitionActorIdPrefix = "user-script-definition";

    public string GetEvolutionManagerActorId() => EvolutionManagerActorId;

    public string GetEvolutionManagerActorId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId)
            ? EvolutionManagerActorId
            : $"{EvolutionManagerActorId}:{BuildOpaqueToken(scopeId)}";

    public string GetEvolutionSessionActorId(string proposalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        return $"script-evolution-session:{proposalId}";
    }

    public string GetEvolutionSessionActorId(string proposalId, string? scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        return string.IsNullOrWhiteSpace(scopeId)
            ? GetEvolutionSessionActorId(proposalId)
            : $"script-evolution-session:{BuildOpaqueToken(scopeId)}:{proposalId}";
    }

    public string GetCatalogActorId() => CatalogActorId;

    public string GetCatalogActorId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId)
            ? CatalogActorId
            : $"{ScopedCatalogActorIdPrefix}:{BuildOpaqueToken(scopeId)}";

    public string GetDefinitionActorId(string scriptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        return $"script-definition:{scriptId}";
    }

    public string GetDefinitionActorId(string scriptId, string? scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        return string.IsNullOrWhiteSpace(scopeId)
            ? GetDefinitionActorId(scriptId)
            : $"{ScopedDefinitionActorIdPrefix}:{BuildOpaqueToken(scopeId)}:{BuildOpaqueToken(scriptId)}";
    }

    private static string BuildOpaqueToken(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new ArgumentException("Value is required.", nameof(value));

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..10];
        var slug = string.Concat(normalized.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-'))
            .Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(slug) ? hash : $"{slug}-{hash}";
    }
}
