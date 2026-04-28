using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgents.Scheduled;

public static class ChannelWorkflowTextRouting
{
    private const string ApproveCommand = "/approve";
    private const string RejectCommand = "/reject";
    private const string SubmitCommand = "/submit";

    public static bool TryBuildWorkflowResumeCommand(
        InboundMessage inbound,
        out WorkflowResumeCommand? command)
    {
        command = null;
        ArgumentNullException.ThrowIfNull(inbound);

        var tokens = ChannelTextCommandParser.Tokenize(inbound.Text);
        if (tokens.Count == 0)
            return false;

        var commandToken = tokens[0];
        var approved = commandToken switch
        {
            ApproveCommand => true,
            RejectCommand => false,
            SubmitCommand => true,
            _ => (bool?)null,
        };

        if (approved is null)
            return false;

        var args = ChannelTextCommandParser.ParseNamedArguments(tokens);
        if (!TryGetRequired(args, "actor_id", out var actorId) ||
            !TryGetRequired(args, "run_id", out var runId) ||
            !TryGetRequired(args, "step_id", out var stepId))
        {
            return false;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.platform"] = inbound.Platform,
            ["channel.conversation_id"] = inbound.ConversationId,
        };
        if (!string.IsNullOrWhiteSpace(inbound.MessageId))
            metadata["channel.message_id"] = inbound.MessageId;

        var editedContent = commandToken == SubmitCommand
            ? null
            : ResolveEditedContent(args);
        var userInput = ResolveUserInput(args, approved.Value, editedContent);
        var feedback = ResolveFeedback(args, approved.Value);
        command = new WorkflowResumeCommand(
            actorId,
            runId,
            stepId,
            NormalizeOptional(inbound.MessageId),
            approved.Value,
            userInput,
            metadata,
            editedContent,
            feedback);
        return true;
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var raw))
            return false;

        value = NormalizeOptional(raw) ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ResolveEditedContent(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("edited_content", out var raw))
            return null;

        return NormalizeOptional(raw);
    }

    private static string? ResolveUserInput(
        IReadOnlyDictionary<string, string> values,
        bool approved,
        string? editedContent)
    {
        if (approved && editedContent is not null)
            return editedContent;

        if (values.TryGetValue("user_input", out var directInput))
        {
            var normalizedDirect = NormalizeOptional(directInput);
            if (normalizedDirect is not null)
                return normalizedDirect;
        }

        if (values.TryGetValue("value", out var directValue))
        {
            var normalizedValue = NormalizeOptional(directValue);
            if (normalizedValue is not null)
                return normalizedValue;
        }

        var keys = approved
            ? new[] { "comment", "notes" }
            : new[] { "feedback", "user_input", "comment", "notes" };

        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var raw))
                continue;

            var normalized = NormalizeOptional(raw);
            if (normalized is not null)
                return normalized;
        }

        return approved ? editedContent : null;
    }

    private static string? ResolveFeedback(IReadOnlyDictionary<string, string> values, bool approved)
    {
        var keys = approved
            ? new[] { "comment", "user_input", "notes" }
            : new[] { "feedback", "comment", "user_input", "notes" };

        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var raw))
                continue;

            var normalized = NormalizeOptional(raw);
            if (normalized is not null)
                return normalized;
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
