using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppCommand
{
    public static Command Create()
    {
        var command = new Command("app", "Launch embedded workflow playground app.");
        var portOption = CreatePortOption();
        var noBrowserOption = CreateNoBrowserOption();
        var apiBaseOption = CreateApiBaseOption();

        command.AddOption(portOption);
        command.AddOption(noBrowserOption);
        command.AddOption(apiBaseOption);
        command.AddCommand(CreateRestartCommand());

        command.SetHandler(
            (int port, bool noBrowser, string? apiBase) =>
                AppCommandHandler.RunAsync(port, noBrowser, apiBase, CancellationToken.None),
            portOption,
            noBrowserOption,
            apiBaseOption);

        return command;
    }

    private static Command CreateRestartCommand()
    {
        var command = new Command("restart", "Force restart app by killing process on the target port.");
        var portOption = CreatePortOption();
        var noBrowserOption = CreateNoBrowserOption();
        var apiBaseOption = CreateApiBaseOption();

        command.AddOption(portOption);
        command.AddOption(noBrowserOption);
        command.AddOption(apiBaseOption);

        command.SetHandler(
            (int port, bool noBrowser, string? apiBase) =>
                AppCommandHandler.RestartAsync(port, noBrowser, apiBase, CancellationToken.None),
            portOption,
            noBrowserOption,
            apiBaseOption);

        return command;
    }

    private static Option<int> CreatePortOption() =>
        new("--port", () => 6688, "Port for playground app.");

    private static Option<bool> CreateNoBrowserOption() =>
        new("--no-browser", "Do not auto-open browser.");

    private static Option<string?> CreateApiBaseOption() =>
        new(
            "--api-base",
            "Workflow API base URL (defaults: --api-base > chat config url > embedded host URL).");
}
