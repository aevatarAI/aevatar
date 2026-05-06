using System.CommandLine;
using System.CommandLine.Invocation;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class ModelCommand
{
    public static Command Create()
    {
        var command = new Command("model", "List and select the current user's NyxID-backed LLM service/model.");
        var urlOption = new Option<string?>(
            "--url",
            "API base URL. Reads from ~/.aevatar/config.json if not specified.");

        command.AddOption(urlOption);
        command.AddCommand(CreateListCommand(urlOption));
        command.AddCommand(CreateUseCommand(urlOption));
        command.AddCommand(CreatePresetCommand(urlOption));
        command.AddCommand(CreateResetCommand(urlOption));
        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await ModelCommandHandler.ListAsync(
                context.ParseResult.GetValueForOption(urlOption),
                context.GetCancellationToken());
        });
        return command;
    }

    private static Command CreateListCommand(Option<string?> urlOption)
    {
        var command = new Command("list", "List routable LLM services for the logged-in NyxID user.");
        command.AddOption(urlOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await ModelCommandHandler.ListAsync(
                context.ParseResult.GetValueForOption(urlOption),
                context.GetCancellationToken());
        });
        return command;
    }

    private static Command CreateUseCommand(Option<string?> urlOption)
    {
        var command = new Command("use", "Select a service by number/name, or set a model override.");
        var valueArgument = new Argument<string>("value", "Service number/name/id, or raw model name.");
        command.AddArgument(valueArgument);
        command.AddOption(urlOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await ModelCommandHandler.UseAsync(
                context.ParseResult.GetValueForArgument(valueArgument),
                context.ParseResult.GetValueForOption(urlOption),
                context.GetCancellationToken());
        });
        return command;
    }

    private static Command CreatePresetCommand(Option<string?> urlOption)
    {
        var command = new Command("preset", "Apply a NyxID-provided LLM setup preset.");
        var presetArgument = new Argument<string>("preset-id", "Preset id from `aevatar model list`.");
        command.AddArgument(presetArgument);
        command.AddOption(urlOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await ModelCommandHandler.PresetAsync(
                context.ParseResult.GetValueForArgument(presetArgument),
                context.ParseResult.GetValueForOption(urlOption),
                context.GetCancellationToken());
        });
        return command;
    }

    private static Command CreateResetCommand(Option<string?> urlOption)
    {
        var command = new Command("reset", "Clear the current user's LLM service/model preference.");
        command.AddOption(urlOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await ModelCommandHandler.ResetAsync(
                context.ParseResult.GetValueForOption(urlOption),
                context.GetCancellationToken());
        });
        return command;
    }
}
