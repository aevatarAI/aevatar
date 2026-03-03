using Aevatar.Tools.Config;

namespace Aevatar.Tools.Cli.Hosting;

internal static class ConfigCommandHandler
{
    public static Task RunUiAsync(int port, bool noBrowser, CancellationToken cancellationToken)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "wwwroot", "config"),
            Path.Combine(baseDirectory, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDirectory, "../../../../tools/Aevatar.Tools.Config/wwwroot")),
            Path.Combine(Environment.CurrentDirectory, "tools", "Aevatar.Tools.Config", "wwwroot"),
        };

        return ConfigToolHost.RunAsync(
            new ConfigToolHostOptions
            {
                Port = port,
                NoBrowser = noBrowser,
                BannerTitle = "aevatar config",
                WebRootCandidates = candidates,
            },
            cancellationToken);
    }
}
