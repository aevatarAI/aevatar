using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Hosting.NyxId;

internal static class NyxIdAuthorityResolver
{
    private const string GatewaySuffix = "/api/v1/llm/gateway/v1";

    internal static string? ResolveNyxIdAuthorityBase(IConfiguration configuration)
    {
        var authority = configuration["Cli:App:NyxId:Authority"]
            ?? configuration["Aevatar:NyxId:Authority"]
            ?? configuration["Aevatar:Authentication:Authority"];

        if (string.IsNullOrWhiteSpace(authority))
            return null;

        var trimmed = authority.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return null;

        return trimmed.EndsWith(GatewaySuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^GatewaySuffix.Length]
            : trimmed;
    }
}
