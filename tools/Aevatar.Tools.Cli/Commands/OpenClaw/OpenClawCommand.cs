using System.CommandLine;
using System.CommandLine.Invocation;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class OpenClawCommand
{
    public static Command Create()
    {
        var command = new Command("openclaw", "OpenClaw integration commands.");
        command.AddCommand(CreateSyncCommand());
        return command;
    }

    private static Command CreateSyncCommand()
    {
        var command = new Command("sync", "Synchronize LLM provider config between Aevatar and OpenClaw.");
        command.AddCommand(CreatePlanCommand());
        command.AddCommand(CreateApplyCommand());
        return command;
    }

    private static Command CreatePlanCommand()
    {
        var command = new Command("plan", "Show sync plan (dry-run).");
        var modeOption = new Option<string>("--mode", () => "bidirectional", "Sync mode. PoC supports: bidirectional.");
        var precedenceOption = new Option<string>("--precedence", () => "aevatar", "Conflict precedence. PoC supports: aevatar.");
        var dryRunOption = new Option<bool>("--dry-run", () => true, "Dry-run mode.");
        var openClawConfigOption = new Option<string?>("--openclaw-config", "OpenClaw config path (default: ~/.openclaw/openclaw.json).");
        var secretsPathOption = new Option<string?>("--aevatar-secrets", "Aevatar secrets path (default: ~/.aevatar/secrets.json).");

        command.AddOption(modeOption);
        command.AddOption(precedenceOption);
        command.AddOption(dryRunOption);
        command.AddOption(openClawConfigOption);
        command.AddOption(secretsPathOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await OpenClawSyncCommandHandler.PlanAsync(
                context.ParseResult.GetValueForOption(modeOption),
                context.ParseResult.GetValueForOption(precedenceOption),
                context.ParseResult.GetValueForOption(dryRunOption),
                context.ParseResult.GetValueForOption(openClawConfigOption),
                context.ParseResult.GetValueForOption(secretsPathOption),
                CancellationToken.None);
        });

        return command;
    }

    private static Command CreateApplyCommand()
    {
        var command = new Command("apply", "Apply sync plan to both Aevatar and OpenClaw.");
        var modeOption = new Option<string>("--mode", () => "bidirectional", "Sync mode. PoC supports: bidirectional.");
        var precedenceOption = new Option<string>("--precedence", () => "aevatar", "Conflict precedence. PoC supports: aevatar.");
        var backupOption = new Option<bool>("--backup", () => true, "Create backup before writing OpenClaw config.");
        var openClawConfigOption = new Option<string?>("--openclaw-config", "OpenClaw config path (default: ~/.openclaw/openclaw.json).");
        var secretsPathOption = new Option<string?>("--aevatar-secrets", "Aevatar secrets path (default: ~/.aevatar/secrets.json).");

        command.AddOption(modeOption);
        command.AddOption(precedenceOption);
        command.AddOption(backupOption);
        command.AddOption(openClawConfigOption);
        command.AddOption(secretsPathOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            context.ExitCode = await OpenClawSyncCommandHandler.ApplyAsync(
                context.ParseResult.GetValueForOption(modeOption),
                context.ParseResult.GetValueForOption(precedenceOption),
                context.ParseResult.GetValueForOption(backupOption),
                context.ParseResult.GetValueForOption(openClawConfigOption),
                context.ParseResult.GetValueForOption(secretsPathOption),
                CancellationToken.None);
        });

        return command;
    }
}
