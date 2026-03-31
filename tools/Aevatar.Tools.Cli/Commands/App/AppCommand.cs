using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppCommand
{
    public static Command Create()
    {
        var command = new Command("app", "Start the app UI or run runtime subcommands (draft-run, services, bindings, invoke, logs).");

        var urlOption = new Option<string?>(
            "--url",
            "API base URL. Reads from ~/.aevatar/config.json if not specified.");
        var portOption = new Option<int>(
            "--port",
            () => 6688,
            "Port for the local app web server.");
        var noBrowserOption = new Option<bool>(
            "--no-browser",
            "Do not auto-open browser.");

        command.AddOption(urlOption);
        command.AddOption(portOption);
        command.AddOption(noBrowserOption);

        // Default handler: start web server with playground UI.
        command.SetHandler(async (string? url, int port, bool noBrowser) =>
        {
            var apiBaseUrl = CliAppConfigStore.ResolveApiBaseUrl(url, "http://localhost:5080", out _);
            await AppPlaygroundHost.RunAsync(port, apiBaseUrl, noBrowser, CancellationToken.None);
        }, urlOption, portOption, noBrowserOption);

        // Subcommands.
        command.AddCommand(AppDraftRunCommand.Create(urlOption));
        command.AddCommand(AppServicesCommand.Create(urlOption));
        command.AddCommand(AppBindingsCommand.Create(urlOption));
        command.AddCommand(AppInvokeCommand.Create(urlOption));
        command.AddCommand(AppLogsCommand.Create(urlOption));
        command.AddCommand(OrnnSkillsCommand.Create());

        return command;
    }
}
