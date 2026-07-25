using System.Security.Cryptography;
using System.Text;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>
/// Builds stable, provider-safe operation tool names without embedding service-instance identity.
/// </summary>
public static class ConnectedServiceToolNaming
{
    public const string Prefix = "nyxid_service_operation__";
    private const int MaxLength = 64;

    public static string Build(string operation)
    {
        var raw = $"{Prefix}{Sanitize(operation)}";
        if (raw.Length <= MaxLength)
            return raw;

        var hash = ShortHash(raw);
        var keep = MaxLength - hash.Length - 1;
        return $"{raw[..keep]}_{hash}";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "x";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');

        var sanitized = sb.ToString().Trim('_', '-');
        return string.IsNullOrEmpty(sanitized) ? "x" : sanitized;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes)[..8];
    }
}
