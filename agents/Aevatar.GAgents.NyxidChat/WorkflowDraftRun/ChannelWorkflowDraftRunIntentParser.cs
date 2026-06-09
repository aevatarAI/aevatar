using System.Text.RegularExpressions;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

internal sealed record ChannelWorkflowDraftRunIntent(string WorkflowId, string Prompt);

public sealed class ChannelWorkflowDraftRunIntentParser
{
    private static readonly Regex SlashWorkflowRun = new(
        @"^/workflow\s+run\s+(?<id>[A-Za-z0-9_.-]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SlashRunWorkflow = new(
        @"^/run-workflow\s+(?<id>[A-Za-z0-9_.-]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EnglishRunWorkflow = new(
        @"^run\s+(?<id>[A-Za-z0-9_.-]+)\s+workflow\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ChineseRunWorkflow = new(
        @"^跑一下\s+(?<id>[A-Za-z0-9_.-]+)\s+的\s+workflow\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal bool TryParse(string? text, out ChannelWorkflowDraftRunIntent intent)
    {
        intent = new ChannelWorkflowDraftRunIntent(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim();
        var workflowId =
            MatchWorkflowId(SlashWorkflowRun, normalized) ??
            MatchWorkflowId(SlashRunWorkflow, normalized) ??
            MatchWorkflowId(EnglishRunWorkflow, normalized) ??
            MatchWorkflowId(ChineseRunWorkflow, normalized);
        if (workflowId is null)
            return false;

        intent = new ChannelWorkflowDraftRunIntent(workflowId, normalized);
        return true;
    }

    private static string? MatchWorkflowId(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Groups["id"].Value.Trim() : null;
    }
}
