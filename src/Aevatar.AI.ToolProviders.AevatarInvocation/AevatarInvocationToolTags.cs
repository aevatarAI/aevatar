using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public static class AevatarInvocationToolTags
{
    public const string ToolSet = "aevatar.invocation";
}

public interface IAevatarInvocationTool : IAgentTool
{
    string ToolSetTag => AevatarInvocationToolTags.ToolSet;

    AgentToolReceipt? IAgentTool.CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        if (!AevatarInvocationJson.TryReadError(resultJson, out var error) || error is null)
            return CreateSuccessReceipt(callId, toolName, resultJson) ??
                   AevatarInvocationReceiptJson.CreateAcceptedInvocationReceipt(
                       Name,
                       SideEffectKind,
                       callId,
                       toolName,
                       resultJson);

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            SideEffectKind = SideEffectKind,
            ErrorCode = error.Code,
            ErrorMessage = error.Message,
            ResultJson = resultJson ?? string.Empty,
        };
    }
}

internal static class AevatarInvocationReceiptJson
{
    public const string InvocationRunSubjectKind = "aevatar.invocation_run";

    public static AgentToolReceipt? CreateAcceptedInvocationReceipt(
        string defaultToolName,
        string sideEffectKind,
        string callId,
        string toolName,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                return null;

            var runId = ReadString(root, "run_id");
            var status = ReadString(root, "status");
            var actorId = ReadString(root, "actor_id");
            var commandId = ReadString(root, "command_id");
            if (string.IsNullOrWhiteSpace(runId) ||
                string.IsNullOrWhiteSpace(actorId) ||
                string.IsNullOrWhiteSpace(commandId) ||
                !IsAcceptedStatus(status))
            {
                return null;
            }

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? defaultToolName : toolName,
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                SideEffectKind = sideEffectKind ?? string.Empty,
                SubjectKind = InvocationRunSubjectKind,
                SubjectId = runId.Trim(),
                ResultJson = resultJson ?? string.Empty,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAcceptedStatus(string status) =>
        string.Equals(status, "accepted", StringComparison.Ordinal) ||
        string.Equals(status, "streaming", StringComparison.Ordinal);

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
