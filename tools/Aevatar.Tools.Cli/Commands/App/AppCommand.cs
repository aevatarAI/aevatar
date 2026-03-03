using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppCommand
{
    public static Command Create()
    {
        var command = new Command("app", "Launch embedded workflow playground app.");
        var portOption = new Option<int>("--port", () => 6688, "Port for playground app.");
        var noBrowserOption = new Option<bool>("--no-browser", "Do not auto-open browser.");
        var apiBaseOption = new Option<string?>(
            "--api-base",
            "Workflow API base URL (defaults: --api-base > chat config url > embedded host URL).");

        command.AddOption(portOption);
        command.AddOption(noBrowserOption);
        command.AddOption(apiBaseOption);

        command.SetHandler(
            (int port, bool noBrowser, string? apiBase) =>
                AppCommandHandler.RunAsync(port, noBrowser, apiBase, CancellationToken.None),
            portOption,
            noBrowserOption,
            apiBaseOption);

        return command;
    }
}
