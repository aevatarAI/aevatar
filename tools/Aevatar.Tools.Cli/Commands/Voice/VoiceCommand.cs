using System.CommandLine;
using Aevatar.Tools.Cli.Hosting;

namespace Aevatar.Tools.Cli.Commands;

internal static class VoiceCommand
{
    public static Command Create()
    {
        var command = new Command("voice", "Open the browser-based voice UI for a voice-enabled GAgent.");
        var agentOption = new Option<string>("--agent", "Voice-enabled actor ID.") { IsRequired = true };
        var portOption = new Option<int>("--port", () => 6688, "App port for local UI and health check.");
        var urlOption = new Option<string?>("--url", "Override workflow API base URL for this invocation.");
        var providerOption = new Option<string?>("--provider", "Preferred voice provider alias (openai|minicpm).");
        var voiceOption = new Option<string?>("--voice", "Preferred voice label shown in the browser UI.");

        command.AddOption(agentOption);
        command.AddOption(portOption);
        command.AddOption(urlOption);
        command.AddOption(providerOption);
        command.AddOption(voiceOption);

        command.SetHandler(
            (string agent, int port, string? url, string? provider, string? voice) =>
                VoiceCommandHandler.RunAsync(agent, port, url, provider, voice, CancellationToken.None),
            agentOption,
            portOption,
            urlOption,
            providerOption,
            voiceOption);

        return command;
    }
}
