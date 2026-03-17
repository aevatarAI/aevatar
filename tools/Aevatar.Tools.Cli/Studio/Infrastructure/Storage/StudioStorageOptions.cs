namespace Aevatar.Tools.Cli.Studio.Infrastructure.Storage;

public sealed class StudioStorageOptions
{
    public string RootDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "App_Data", "bundles");

    public string DefaultRuntimeBaseUrl { get; set; } = "http://127.0.0.1:5100";

    public bool ForceLocalRuntime { get; set; }
}
