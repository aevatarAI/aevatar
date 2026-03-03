using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class ChatCommand
{
    public static Command Create()
    {
        var command = new Command("chat", "Open app UI and send a chat prompt through /api/chat.");
        var messageArgument = new Argument<string?>("message", "Prompt text to send in the app UI.");
        var portOption = new Option<int>("--port", () => 6688, "App port for local UI and health check.");
        var urlOption = new Option<string?>("--url", "Override workflow API base URL for this invocation.");

        command.AddArgument(messageArgument);
        command.AddOption(portOption);
        command.AddOption(urlOption);
        command.AddCommand(CreateConfigCommand());

        command.SetHandler(
            (string? message, int port, string? url) =>
                ChatCommandHandler.RunAsync(message, port, url, CancellationToken.None),
            messageArgument,
            portOption,
            urlOption);

        return command;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Manage persisted chat remote URL.");
        command.AddCommand(CreateSetUrlCommand());
        command.AddCommand(CreateGetUrlCommand());
        command.AddCommand(CreateClearUrlCommand());
        return command;
    }

    private static Command CreateSetUrlCommand()
    {
        var command = new Command("set-url", "Persist chat API base URL.");
        var urlArgument = new Argument<string>("url", "Absolute API base URL, e.g. http://localhost:5000");
        command.AddArgument(urlArgument);
        command.SetHandler((string url) => ChatCommandHandler.SetApiBaseUrl(url), urlArgument);
        return command;
    }

    private static Command CreateGetUrlCommand()
    {
        var command = new Command("get-url", "Print persisted chat API base URL.");
        command.SetHandler(ChatCommandHandler.GetApiBaseUrl);
        return command;
    }

    private static Command CreateClearUrlCommand()
    {
        var command = new Command("clear-url", "Clear persisted chat API base URL.");
        command.SetHandler(ChatCommandHandler.ClearApiBaseUrl);
        return command;
    }
}
