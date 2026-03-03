namespace Aevatar.Tools.Cli.Hosting;

internal static class AppCommandHandler
{
    public static Task RunAsync(int port, bool noBrowser, string? apiBase, CancellationToken cancellationToken) =>
        AppToolHost.RunAsync(new AppToolHostOptions
        {
            Port = port,
            NoBrowser = noBrowser,
            ApiBaseUrl = apiBase,
        }, cancellationToken);
}
