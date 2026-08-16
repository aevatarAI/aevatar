using System.Text;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

internal interface IChannelWorkflowDraftRunCommand
{
    bool TryParseWorkflowId(string commandName, string argumentText, out string workflowId);
}

/// <summary>
/// Handles the <c>/workflow</c> forms that are not a run request. A valid
/// <c>/workflow run &lt;workflow-id&gt;</c> never reaches here: the turn runner admits it
/// through <see cref="ChannelWorkflowDraftRunAdmission"/> before slash dispatch. What is
/// left is discovery — <c>/workflow list</c> — and usage help, which matters because the
/// run form only accepts an opaque workflow id that a chat user has no other way to learn.
/// </summary>
public sealed class ChannelWorkflowDraftRunSlashCommandHandler : IChannelSlashCommandHandler, IChannelWorkflowDraftRunCommand
{
    private const int MaxListedWorkflows = 20;
    private static readonly char[] ArgumentSeparators = [' ', '\t', '\r', '\n'];

    private readonly IScopeWorkflowQueryPort? _workflowQueryPort;

    public ChannelWorkflowDraftRunSlashCommandHandler(IScopeWorkflowQueryPort? workflowQueryPort = null)
    {
        _workflowQueryPort = workflowQueryPort;
    }

    public string Name => "workflow";

    public IReadOnlyList<string> Aliases { get; } = ["run-workflow"];

    public bool RequiresBinding => false;

    public ChannelSlashCommandUsage Usage => new(
        Name,
        "list | run <workflow-id>",
        "列出或运行当前 scope 内的 workflow");

    public async Task<MessageContent?> HandleAsync(ChannelSlashCommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var parts = context.ArgumentText.Split(ArgumentSeparators, StringSplitOptions.RemoveEmptyEntries);
        var subCommand = parts.Length == 0 ? string.Empty : parts[0];
        if (parts.Length == 1 && string.Equals(subCommand, "list", StringComparison.OrdinalIgnoreCase))
            return await ListWorkflowsAsync(context, ct).ConfigureAwait(false);

        return new MessageContent { Text = UsageText() };
    }

    private async Task<MessageContent> ListWorkflowsAsync(
        ChannelSlashCommandContext context,
        CancellationToken ct)
    {
        if (_workflowQueryPort is null)
            return new MessageContent { Text = "Workflow 查询服务暂不可用,请稍后重试。" };

        var scopeId = context.RegistrationScopeId.Trim();
        if (scopeId.Length == 0)
            return new MessageContent { Text = "无法确定当前 NyxID scope,暂不能列出 workflow。" };

        var workflows = await _workflowQueryPort.ListAsync(scopeId, ct).ConfigureAwait(false);
        if (workflows.Count == 0)
        {
            return new MessageContent
            {
                Text = "当前 scope 没有已发布的 workflow。通过 Aevatar 交付或 Console 发布后再运行。",
            };
        }

        var builder = new StringBuilder();
        builder.Append("当前 scope 的 workflow（共 ")
            .Append(workflows.Count)
            .Append(" 个）:");
        foreach (var workflow in workflows.Take(MaxListedWorkflows))
        {
            var label = workflow.DisplayName.Trim().Length != 0
                ? workflow.DisplayName.Trim()
                : workflow.WorkflowName.Trim();
            builder.Append('\n')
                .Append("• ")
                .Append(label)
                .Append('\n')
                .Append("  /workflow run ")
                .Append(workflow.WorkflowId);
        }

        if (workflows.Count > MaxListedWorkflows)
        {
            builder.Append('\n')
                .Append("… 仅显示前 ")
                .Append(MaxListedWorkflows)
                .Append(" 个，其余请在 Aevatar Console 查看。");
        }

        return new MessageContent { Text = builder.ToString() };
    }

    private static string UsageText() =>
        "用法:\n/workflow list — 列出当前 scope 的 workflow 及其运行指令\n/workflow run <workflow-id> — 运行指定 workflow";

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
