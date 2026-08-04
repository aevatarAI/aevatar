using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed class AgentToolReceiptRenderer : IAgentToolReceiptRenderer
{
    private readonly record struct ToolCallIdentity(string CallId, string ToolName);

    public string Render(IReadOnlyList<AgentToolReceipt> receipts, IReadOnlyList<AgentRunToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (receipts.Count == 0)
            return string.Empty;

        var toolCallByIdentity = toolCalls
            .Where(static call => Normalize(call.Id) is not null)
            .GroupBy(static call => new ToolCallIdentity(
                Normalize(call.Id)!,
                Normalize(call.Name) ?? string.Empty))
            .ToDictionary(static group => group.Key, static group => group.Last());
        var builder = new StringBuilder();
        foreach (var receipt in receipts)
        {
            var line = RenderLine(receipt, toolCallByIdentity);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (builder.Length == 0)
                builder.AppendLine();
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    private static string RenderLine(
        AgentToolReceipt receipt,
        IReadOnlyDictionary<ToolCallIdentity, AgentRunToolCall> toolCallByIdentity)
    {
        // Success receipts stay typed-only (persisted state, audit trail, model reply
        // constraint); the user-visible line is reserved for statuses the model must
        // not narrate around: pending approval, authorization, denial, failure, or unknown outcome.
        var status = receipt.Status switch
        {
            AgentToolReceiptStatus.ApprovalRequired => "Approval pending",
            AgentToolReceiptStatus.Denied => "Denied",
            AgentToolReceiptStatus.Error => "Failed",
            AgentToolReceiptStatus.AuthorizationRequired => "Authorization required",
            AgentToolReceiptStatus.Unspecified => "Outcome unverified",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(status))
            return string.Empty;

        var subject = ResolveSubject(receipt, toolCallByIdentity);
        var evidence = ResolveEvidence(receipt);
        var builder = new StringBuilder();
        builder.Append("[tool receipt] ");
        builder.Append(status);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            builder.Append(": ");
            builder.Append(subject);
        }

        if (!string.IsNullOrWhiteSpace(evidence))
        {
            builder.Append(" (");
            builder.Append(evidence);
            builder.Append(')');
        }

        var safeMessage = ResolveSafeMessage(receipt);
        if (!string.IsNullOrWhiteSpace(safeMessage))
        {
            builder.Append(" - ");
            builder.Append(safeMessage);
        }

        return builder.ToString();
    }

    private static string ResolveSubject(
        AgentToolReceipt receipt,
        IReadOnlyDictionary<ToolCallIdentity, AgentRunToolCall> toolCallByIdentity)
    {
        var kind = Normalize(receipt.SubjectKind);
        var id = Normalize(receipt.SubjectId);
        if (kind is not null || id is not null)
            return string.Join(" ", new[] { kind, id }.Where(static value => !string.IsNullOrWhiteSpace(value)));

        var identity = new ToolCallIdentity(
            Normalize(receipt.CallId) ?? string.Empty,
            Normalize(receipt.ToolName) ?? string.Empty);
        if (!toolCallByIdentity.TryGetValue(identity, out var toolCall))
            return Normalize(receipt.ToolName) ?? string.Empty;

        return TryGetDisplayNameFromArguments(toolCall.ArgumentsJson) ??
               Normalize(receipt.ToolName) ??
               string.Empty;
    }

    private static string ResolveEvidence(AgentToolReceipt receipt)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(receipt.SubjectVersion))
            parts.Add($"version={receipt.SubjectVersion.Trim()}");
        if (!string.IsNullOrWhiteSpace(receipt.SubjectHash))
            parts.Add($"hash={receipt.SubjectHash.Trim()}");
        if (!string.IsNullOrWhiteSpace(receipt.ApprovalRequestId) &&
            receipt.Status == AgentToolReceiptStatus.ApprovalRequired)
        {
            parts.Add($"local_request={receipt.ApprovalRequestId.Trim()}");
        }

        return string.Join(", ", parts);
    }

    private static string? ResolveSafeMessage(AgentToolReceipt receipt)
    {
        if (receipt.Status == AgentToolReceiptStatus.AuthorizationRequired &&
            !string.IsNullOrWhiteSpace(receipt.AuthorizationRequired?.SafeMessage))
        {
            return receipt.AuthorizationRequired.SafeMessage.Trim();
        }

        return receipt.Status is AgentToolReceiptStatus.Error or
            AgentToolReceiptStatus.Denied or
            AgentToolReceiptStatus.AuthorizationRequired or
            AgentToolReceiptStatus.Unspecified
            ? Normalize(receipt.ErrorMessage)
            : null;
    }

    private static string? TryGetDisplayNameFromArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return TryGetString(doc.RootElement, "name", "skill_name", "skillName", "title");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return Normalize(value.GetString());
        }

        return null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
