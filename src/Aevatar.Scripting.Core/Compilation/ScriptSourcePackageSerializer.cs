using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Scripting.Core.Compilation;

public static class ScriptSourcePackageSerializer
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
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
