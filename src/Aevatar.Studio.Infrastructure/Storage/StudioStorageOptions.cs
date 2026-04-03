using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.Storage;

public sealed class StudioStorageOptions
{
    public string RootDirectory { get; set; } =
        Path.Combine(StudioStoragePathHelpers.ResolveDefaultAevatarHomeDirectory(), "studio");

    // Legacy alias: retained so existing config keeps working.
    public string DefaultRuntimeBaseUrl { get; set; } = UserConfigRuntimeDefaults.LocalRuntimeBaseUrl;

    public string DefaultLocalRuntimeBaseUrl { get; set; } = UserConfigRuntimeDefaults.LocalRuntimeBaseUrl;

    public string DefaultRemoteRuntimeBaseUrl { get; set; } = UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl;

    public bool ForceLocalRuntime { get; set; }
}

internal static class StudioStorageOptionsExtensions
{
    public static StudioStorageOptions ResolveRootDirectory(this StudioStorageOptions options)
    {
        var rootDirectory = Path.IsPathRooted(options.RootDirectory)
            ? options.RootDirectory
            : Path.GetFullPath(options.RootDirectory, AppContext.BaseDirectory);

        return new StudioStorageOptions
        {
            RootDirectory = rootDirectory,
            DefaultRuntimeBaseUrl = options.DefaultRuntimeBaseUrl,
            DefaultLocalRuntimeBaseUrl = options.DefaultLocalRuntimeBaseUrl,
            DefaultRemoteRuntimeBaseUrl = options.DefaultRemoteRuntimeBaseUrl,
            ForceLocalRuntime = options.ForceLocalRuntime,
        };
    }

    public static string ResolveDefaultLocalRuntimeBaseUrl(this StudioStorageOptions options)
    {
        var candidate = string.IsNullOrWhiteSpace(options.DefaultLocalRuntimeBaseUrl)
            ? options.DefaultRuntimeBaseUrl
            : options.DefaultLocalRuntimeBaseUrl;
        return NormalizeRuntimeBaseUrl(candidate, UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
    }

    public static string ResolveDefaultRemoteRuntimeBaseUrl(this StudioStorageOptions options)
    {
        return NormalizeRuntimeBaseUrl(options.DefaultRemoteRuntimeBaseUrl, UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);
    }

    private static string NormalizeRuntimeBaseUrl(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return normalized.TrimEnd('/');
    }
}

internal static class StudioStoragePathHelpers
{
    internal static string ResolveDefaultAevatarHomeDirectory()
    {
        var envPath = Environment.GetEnvironmentVariable("AEVATAR_HOME");
        var rawPath = string.IsNullOrWhiteSpace(envPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aevatar")
            : envPath.Trim();

        return ExpandPath(rawPath);
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
}
