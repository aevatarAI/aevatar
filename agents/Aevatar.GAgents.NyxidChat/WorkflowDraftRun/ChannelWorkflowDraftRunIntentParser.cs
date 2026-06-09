using Aevatar.GAgents.Channel.Abstractions.Slash;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

internal sealed record ChannelWorkflowDraftRunIntent(string WorkflowId, string Prompt);

public sealed class ChannelWorkflowDraftRunIntentParser
{
    private readonly ChannelSlashCommandRegistry? _slashCommandRegistry;

    public ChannelWorkflowDraftRunIntentParser(ChannelSlashCommandRegistry? slashCommandRegistry = null)
    {
        _slashCommandRegistry = slashCommandRegistry;
    }

    internal bool TryParse(string? text, out ChannelWorkflowDraftRunIntent intent)
    {
        intent = new ChannelWorkflowDraftRunIntent(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        if (!TryParseSlashCommand(normalized, out var commandName, out var argumentText))
            return false;

        var handler = _slashCommandRegistry?.Find(commandName);
        if (handler is not IChannelWorkflowDraftRunCommand workflowCommand)
            return false;

        if (!workflowCommand.TryParseWorkflowId(commandName, argumentText, out var workflowId))
            return false;

        intent = new ChannelWorkflowDraftRunIntent(workflowId, normalized);
        return true;
    }

    private static bool TryParseSlashCommand(string text, out string commandName, out string argumentText)
    {
        commandName = string.Empty;
        argumentText = string.Empty;
        if (text.Length < 2 || text[0] != '/')
            return false;

        var firstSeparator = -1;
        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                firstSeparator = i;
                break;
            }
        }

        if (firstSeparator < 0)
        {
            commandName = text[1..].Trim();
            return !string.IsNullOrWhiteSpace(commandName);
        }

        commandName = text[1..firstSeparator].Trim();
        argumentText = text[firstSeparator..].Trim();
        return !string.IsNullOrWhiteSpace(commandName);
    }
}
