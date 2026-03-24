namespace Aevatar.Studio.Infrastructure.Storage;

public sealed class StudioStorageOptions
{
    public string RootDirectory { get; set; } =
        Path.Combine(StudioStoragePathHelpers.ResolveDefaultAevatarHomeDirectory(), "studio");

    public string DefaultRuntimeBaseUrl { get; set; } = "http://127.0.0.1:5100";

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
            ForceLocalRuntime = options.ForceLocalRuntime,
        };
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
