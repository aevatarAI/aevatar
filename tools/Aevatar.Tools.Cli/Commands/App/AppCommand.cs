using System.CommandLine;

namespace Aevatar.Tools.Cli.Commands;

internal static class AppCommand
{
    public static Command Create()
    {
        var command = new Command("app", "Aevatar app commands — draft-run, services, bindings, invoke, logs.");

        var urlOption = new Option<string?>(
            "--url",
            "API base URL. Reads from ~/.aevatar/config.json if not specified.");

        command.AddCommand(AppDraftRunCommand.Create(urlOption));
        command.AddCommand(AppServicesCommand.Create(urlOption));
        command.AddCommand(AppBindingsCommand.Create(urlOption));
        command.AddCommand(AppInvokeCommand.Create(urlOption));
        command.AddCommand(AppLogsCommand.Create(urlOption));

        return command;
    }
}
