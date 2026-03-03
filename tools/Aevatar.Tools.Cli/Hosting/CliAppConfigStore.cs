using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Configuration;

namespace Aevatar.Tools.Cli.Hosting;

internal static class CliAppConfigStore
{
    private const string CliSection = "Cli";
    private const string AppSection = "App";
    private const string ApiBaseUrlKey = "ApiBaseUrl";

    public static string ResolveApiBaseUrl(
        string? overrideApiBaseUrl,
        string localFallbackUrl,
        out string? warning)
    {
        warning = null;
        if (!TryNormalizeApiBaseUrl(localFallbackUrl, out var normalizedLocal, out var localError))
            throw new ArgumentException($"Invalid local fallback URL: {localError}", nameof(localFallbackUrl));

        if (!string.IsNullOrWhiteSpace(overrideApiBaseUrl))
        {
            if (!TryNormalizeApiBaseUrl(overrideApiBaseUrl, out var normalizedOverride, out var overrideError))
                throw new ArgumentException($"Invalid API base URL override: {overrideError}", nameof(overrideApiBaseUrl));

            return normalizedOverride;
        }

        var configured = GetApiBaseUrl(out warning);
        return configured ?? normalizedLocal;
    }

    public static string? GetApiBaseUrl(out string? warning)
    {
        warning = null;
        JsonObject root;
        try
        {
            root = LoadRootObject();
        }
        catch (Exception ex)
        {
            warning = $"Failed to read {AevatarPaths.ConfigJson}: {ex.Message}";
            return null;
        }

        if (root[CliSection] is not JsonObject cli ||
            cli[AppSection] is not JsonObject app ||
            app[ApiBaseUrlKey] is not JsonValue value)
        {
            return null;
        }

        if (!value.TryGetValue<string>(out var raw))
        {
            warning = $"Ignoring non-string {CliSection}:{AppSection}:{ApiBaseUrlKey} value.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!TryNormalizeApiBaseUrl(raw, out var normalized, out var error))
        {
            warning = $"Ignoring invalid {CliSection}:{AppSection}:{ApiBaseUrlKey}: {error}";
            return null;
        }

        return normalized;
    }

    public static void SetApiBaseUrl(string apiBaseUrl)
    {
        if (!TryNormalizeApiBaseUrl(apiBaseUrl, out var normalized, out var error))
            throw new ArgumentException($"Invalid API base URL: {error}", nameof(apiBaseUrl));

        var root = LoadRootObject();
        var cli = EnsureObject(root, CliSection);
        var app = EnsureObject(cli, AppSection);
        app[ApiBaseUrlKey] = normalized;
        SaveRootObject(root);
    }

    public static bool ClearApiBaseUrl()
    {
        var root = LoadRootObject();
        if (root[CliSection] is not JsonObject cli ||
            cli[AppSection] is not JsonObject app)
        {
            return false;
        }

        var removed = app.Remove(ApiBaseUrlKey);
        if (!removed)
            return false;

        if (app.Count == 0)
            cli.Remove(AppSection);
        if (cli.Count == 0)
            root.Remove(CliSection);

        SaveRootObject(root);
        return true;
    }

    public static bool TryNormalizeApiBaseUrl(
        string? rawUrl,
        out string normalizedUrl,
        out string error)
    {
        normalizedUrl = string.Empty;
        error = string.Empty;

        var trimmed = (rawUrl ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "URL is empty.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "URL must be absolute.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Only http/https schemes are supported.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Query string and fragment are not supported in API base URL.";
            return false;
        }

        normalizedUrl = uri.ToString().TrimEnd('/');
        return true;
    }

    private static JsonObject LoadRootObject()
    {
        var path = AevatarPaths.ConfigJson;
        if (!File.Exists(path))
            return [];

        var raw = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var parsed = JsonNode.Parse(raw);
        if (parsed is JsonObject obj)
            return obj;

        throw new InvalidOperationException("Config root must be a JSON object.");
    }

    private static void SaveRootObject(JsonObject root)
    {
        var path = AevatarPaths.ConfigJson;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
            return child;

        child = [];
        parent[key] = child;
        return child;
    }
}
