using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppCommand
{
    public static Command Create()
    {
        var command = new Command("app", "Open the aevatar app UI (requires a running Mainnet host).");
        var urlOption = new Option<string?>(
            "--url",
            "Mainnet API base URL. Reads from ~/.aevatar/config.json if not specified.");
        var noBrowserOption = new Option<bool>(
            "--no-browser",
            "Do not auto-open browser.");

        command.AddOption(urlOption);
        command.AddOption(noBrowserOption);

        command.SetHandler(
            (string? url, bool noBrowser) =>
                AppCommandHandler.RunAsync(url, noBrowser, CancellationToken.None),
            urlOption,
            noBrowserOption);

        return command;
    }
}
