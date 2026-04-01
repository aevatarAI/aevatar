namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IUserConfigStore
{
    Task<UserConfig> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default);
}

public static class UserConfigLlmRouteDefaults
{
    public const string Auto = "auto";
    public const string Gateway = "gateway";
}

public static class UserConfigLlmRoute
{
    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return UserConfigLlmRouteDefaults.Auto;

        return normalized;
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

    public static string NormalizeBaseUrl(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

        return normalized.TrimEnd('/');
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
}

public sealed record UserConfig(
    string DefaultModel,
    string PreferredLlmRoute = UserConfigLlmRouteDefaults.Auto,
    string RuntimeMode = UserConfigRuntimeDefaults.LocalMode,
    string LocalRuntimeBaseUrl = UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
    string RemoteRuntimeBaseUrl = UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);
