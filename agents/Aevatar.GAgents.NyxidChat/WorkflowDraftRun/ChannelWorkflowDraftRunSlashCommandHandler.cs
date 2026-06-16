using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

internal interface IChannelWorkflowDraftRunCommand
{
    bool TryParseWorkflowId(string commandName, string argumentText, out string workflowId);
}

public sealed class ChannelWorkflowDraftRunSlashCommandHandler : IChannelSlashCommandHandler, IChannelWorkflowDraftRunCommand
{
    private static readonly char[] ArgumentSeparators = [' ', '\t', '\r', '\n'];

    public string Name => "workflow";

    public IReadOnlyList<string> Aliases { get; } = ["run-workflow"];

    public bool RequiresBinding => false;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        "run <workflow-id>",
        "运行当前 scope 内的 workflow");

    public Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<MessageContent?>(new MessageContent
        {
            Text = "Workflow 运行入口暂不可用,请稍后重试。",
        });
    }

    public bool TryParseWorkflowId(string commandName, string argumentText, out string workflowId)
    {
        workflowId = string.Empty;
        var parts = argumentText.Split(ArgumentSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (string.Equals(commandName, Name, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2 &&
                string.Equals(parts[0], "run", StringComparison.OrdinalIgnoreCase))
            {
                workflowId = parts[1];
                return true;
            }

            return false;
        }

        if (Aliases.Any(alias => string.Equals(commandName, alias, StringComparison.OrdinalIgnoreCase)) &&
            parts.Length == 1)
        {
            workflowId = parts[0];
            return true;
        }

        return false;
    }
}
