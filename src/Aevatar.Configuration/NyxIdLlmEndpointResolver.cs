using Microsoft.Extensions.Configuration;

namespace Aevatar.Configuration;

public enum NyxIdLlmEndpointKind
{
    Gateway = 0,
    RelativePath = 1,
}

public sealed record NyxIdLlmEndpointSpec(
    NyxIdLlmEndpointKind Kind,
    string? RelativePath = null);

public static class NyxIdLlmEndpointResolver
{
    private const string GatewayPath = "/api/v1/llm/gateway/v1";
    private const string CliAuthorityKey = "Cli:App:NyxId:Authority";
    private const string AppAuthorityKey = "Aevatar:NyxId:Authority";
    private const string AuthAuthorityKey = "Aevatar:Authentication:Authority";
    private const string CliEndpointKindKey = "Cli:App:NyxId:LlmEndpoint:Kind";
    private const string AppEndpointKindKey = "Aevatar:NyxId:LlmEndpoint:Kind";
    private const string CliRelativePathKey = "Cli:App:NyxId:LlmEndpoint:RelativePath";
    private const string AppRelativePathKey = "Aevatar:NyxId:LlmEndpoint:RelativePath";

    public static string? ResolveEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var authority = configuration[CliAuthorityKey]
            ?? configuration[AppAuthorityKey]
            ?? configuration[AuthAuthorityKey];
        return ResolveEndpoint(authority, ResolveSpec(configuration));
    }

    public static string? ResolveEndpoint(string? authority, NyxIdLlmEndpointSpec? spec)
    {
        var normalizedAuthority = NormalizeAuthorityBase(authority);
        if (string.IsNullOrWhiteSpace(normalizedAuthority))
            return null;

        var effectiveSpec = spec ?? new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.Gateway);
        return effectiveSpec.Kind switch
        {
            NyxIdLlmEndpointKind.Gateway => normalizedAuthority + GatewayPath,
            NyxIdLlmEndpointKind.RelativePath => normalizedAuthority + NormalizeRelativePath(effectiveSpec.RelativePath),
            _ => throw new InvalidOperationException($"Unsupported NyxID LLM endpoint kind '{effectiveSpec.Kind}'."),
        };
    }

    public static NyxIdLlmEndpointSpec ResolveSpec(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawKind = configuration[CliEndpointKindKey] ?? configuration[AppEndpointKindKey];
        if (string.IsNullOrWhiteSpace(rawKind))
            return new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.Gateway);

        if (string.Equals(rawKind, nameof(NyxIdLlmEndpointKind.Gateway), StringComparison.OrdinalIgnoreCase))
            return new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.Gateway);

        if (string.Equals(rawKind, nameof(NyxIdLlmEndpointKind.RelativePath), StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = configuration[CliRelativePathKey] ?? configuration[AppRelativePathKey];
            return new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.RelativePath, relativePath);
        }

        throw new InvalidOperationException(
            $"Unsupported NyxID LLM endpoint kind '{rawKind}'. Supported values: Gateway, RelativePath.");
    }

    private static string? NormalizeAuthorityBase(string? authority)
    {
        var trimmed = authority?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var absolute = uri.ToString().TrimEnd('/');
        if (absolute.EndsWith(GatewayPath, StringComparison.OrdinalIgnoreCase))
            return absolute[..^GatewayPath.Length];

        return absolute;
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        var trimmed = relativePath?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(
                "NyxID LLM endpoint kind 'RelativePath' requires a non-empty relative path.");
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.Contains("://", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"NyxID LLM relative path '{trimmed}' must not be an absolute URL.");
        }

        return trimmed.StartsWith("/", StringComparison.Ordinal)
            ? trimmed
            : "/" + trimmed;
    }
}
