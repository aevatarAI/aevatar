using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Scripting.Core.Compilation;

public static class ScriptSourcePackageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static ScriptSourcePackage DeserializeOrWrapCSharp(string sourceText)
    {
        if (TryDeserialize(sourceText, out var package))
            return package;

        return ScriptSourcePackage.SingleSource(sourceText);
    }

    public static bool TryDeserialize(string? sourceText, out ScriptSourcePackage package)
    {
        package = ScriptSourcePackage.Empty;
        if (string.IsNullOrWhiteSpace(sourceText))
            return false;

        var trimmed = sourceText.TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            return false;

        try
        {
            var candidate = JsonSerializer.Deserialize<ScriptSourcePackage>(sourceText, JsonOptions);
            if (candidate == null)
                return false;
            if (!string.Equals(candidate.Format, ScriptSourcePackage.CurrentFormat, StringComparison.Ordinal))
                return false;

            package = candidate.Normalize();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Serialize(ScriptSourcePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return JsonSerializer.Serialize(package.Normalize(), JsonOptions);
    }

    public static string ComputeHash(ScriptSourcePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var bytes = Encoding.UTF8.GetBytes(Serialize(package));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
