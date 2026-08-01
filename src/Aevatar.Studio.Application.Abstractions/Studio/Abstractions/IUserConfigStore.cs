using Aevatar.AI.Abstractions;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class UserConfigLlmRouteDefaults
{
    public const string Gateway = "/api/v1/llm/gateway/v1";
}

public sealed record UserConfigUpdate(
    LLMSelection? LlmSelection = null,
    string? RuntimeMode = null,
    string? LocalRuntimeBaseUrl = null,
    string? RemoteRuntimeBaseUrl = null,
    string? GithubUsername = null,
    int? MaxToolRounds = null);

public static class UserConfigLlmRoute
{
    public static string Normalize(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gateway", StringComparison.OrdinalIgnoreCase))
        {
            return UserConfigLlmRouteDefaults.Gateway;
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal) ||
            normalized.Contains("://", StringComparison.Ordinal))
        {
            return UserConfigLlmRouteDefaults.Gateway;
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            return normalized;

        return $"/api/v1/proxy/s/{normalized.Trim('/')}";
    }
}

public readonly record struct UserConfigLlmModelRoute(string RouteSlug, string Model);

public static class UserConfigLlmModel
{
    public static UserConfigLlmModelRoute? TryParseRouteModel(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= trimmed.Length - 1)
            return null;

        var prefix = trimmed[..slashIndex];
        if (!LooksLikeSlug(prefix))
            return null;

        return new UserConfigLlmModelRoute(prefix, trimmed[(slashIndex + 1)..]);
    }

    private static bool LooksLikeSlug(string value)
    {
        if (value.Length is < 2 or > 64) return false;
        if (!char.IsAsciiLetterLower(value[0])) return false;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                return false;
        }

        return true;
    }
}

public static class UserConfigRuntimeDefaults
{
    public const string LocalMode = "local";
    public const string RemoteMode = "remote";
    public const string LocalRuntimeBaseUrl = "http://127.0.0.1:5080";
    public const string RemoteRuntimeBaseUrl = "https://aevatar-console-backend-api.aevatar.ai";
}

public static class UserConfigRuntime
{
    public static string NormalizeMode(string? value) =>
        string.Equals(value?.Trim(), UserConfigRuntimeDefaults.RemoteMode, StringComparison.OrdinalIgnoreCase)
            ? UserConfigRuntimeDefaults.RemoteMode
            : UserConfigRuntimeDefaults.LocalMode;

    public static string NormalizeConfiguredMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Runtime mode must be '{UserConfigRuntimeDefaults.LocalMode}' or '{UserConfigRuntimeDefaults.RemoteMode}'.");

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            UserConfigRuntimeDefaults.LocalMode => UserConfigRuntimeDefaults.LocalMode,
            UserConfigRuntimeDefaults.RemoteMode => UserConfigRuntimeDefaults.RemoteMode,
            _ => throw new InvalidOperationException(
                $"Runtime mode must be '{UserConfigRuntimeDefaults.LocalMode}' or '{UserConfigRuntimeDefaults.RemoteMode}'."),
        };
    }

    public static string NormalizeBaseUrl(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

        return normalized.TrimEnd('/');
    }

    public static string NormalizeConfiguredBaseUrl(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must be an absolute http(s) URL.");

        var normalized = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{name} must be an absolute http(s) URL.");
        }

        return normalized;
    }

    public static string ResolveActiveRuntimeBaseUrl(UserConfig config)
    {
        var runtimeMode = NormalizeMode(config.RuntimeMode);
        return runtimeMode == UserConfigRuntimeDefaults.RemoteMode
            ? NormalizeBaseUrl(config.RemoteRuntimeBaseUrl, UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl)
            : NormalizeBaseUrl(config.LocalRuntimeBaseUrl, UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
    }

    public static bool IsLoopbackRuntime(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var runtimeUri))
        {
            return false;
        }

        return runtimeUri.IsLoopback;
    }

    public static UserConfigRuntimeView BuildView(UserConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new UserConfigRuntimeView(
            RuntimeMode: NormalizeMode(config.RuntimeMode),
            ActiveRuntimeBaseUrl: ResolveActiveRuntimeBaseUrl(config),
            LocalRuntimeBaseUrl: NormalizeBaseUrl(
                config.LocalRuntimeBaseUrl,
                UserConfigRuntimeDefaults.LocalRuntimeBaseUrl),
            RemoteRuntimeBaseUrl: NormalizeBaseUrl(
                config.RemoteRuntimeBaseUrl,
                UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl),
            RuntimeDefaults: new UserConfigRuntimeDefaultsView(
                LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
                RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
                LocalMode: UserConfigRuntimeDefaults.LocalMode,
                RemoteMode: UserConfigRuntimeDefaults.RemoteMode));
    }
}

public sealed record UserConfigRuntimeDefaultsView(
    string LocalRuntimeBaseUrl,
    string RemoteRuntimeBaseUrl,
    string LocalMode,
    string RemoteMode);

public sealed record UserConfigRuntimeView(
    string RuntimeMode,
    string ActiveRuntimeBaseUrl,
    string LocalRuntimeBaseUrl,
    string RemoteRuntimeBaseUrl,
    UserConfigRuntimeDefaultsView RuntimeDefaults);

public sealed record UserConfig(
    string DefaultModel,
    string PreferredLlmRoute = "",
    string RuntimeMode = UserConfigRuntimeDefaults.LocalMode,
    string LocalRuntimeBaseUrl = UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
    string RemoteRuntimeBaseUrl = UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
    string? GithubUsername = null,
    int MaxToolRounds = 0,
    LLMSelection? LlmSelection = null);
