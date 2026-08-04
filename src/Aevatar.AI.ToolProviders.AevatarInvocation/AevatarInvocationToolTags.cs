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
                   CreateReadOnlySuccessReceipt(this, callId, toolName, resultJson) ??
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

    private static AgentToolReceipt? CreateReadOnlySuccessReceipt(
        IAevatarInvocationTool tool,
        string callId,
        string toolName,
        string resultJson) =>
        tool is IAevatarInvocationReadOnlyTool readOnlyTool
            ? AevatarInvocationReceiptJson.CreateReadOnlyInvocationReceipt(
                tool.Name,
                callId,
                toolName,
                resultJson,
                readOnlyTool.ReadOnlySubjectIdPropertyName,
                readOnlyTool.ReadOnlyResultRequirements)
            : null;
}

internal interface IAevatarInvocationReadOnlyTool : IAevatarInvocationTool
{
    string? ReadOnlySubjectIdPropertyName { get; }

    IReadOnlyList<AevatarInvocationReceiptJson.ResultPropertyRequirement> ReadOnlyResultRequirements { get; }
}

internal static class AevatarInvocationReceiptJson
{
    public const string InvocationRunSubjectKind = "aevatar.invocation_run";

    public static ResultPropertyRequirement StringProperty(params string[] path) =>
        new(path, JsonValueKind.String);

    public static AgentToolReceipt? CreateReadOnlyInvocationReceipt(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson,
        string? subjectIdPropertyName,
        IReadOnlyList<ResultPropertyRequirement> resultRequirements)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
                return null;

            foreach (var requirement in resultRequirements)
            {
                if (!TryGetProperty(root, requirement.Path, out var value) || value.ValueKind != requirement.ValueKind)
                    return null;
            }

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? defaultToolName : toolName,
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                SubjectId = string.IsNullOrWhiteSpace(subjectIdPropertyName)
                    ? string.Empty
                    : ReadString(root, subjectIdPropertyName),
                ResultJson = resultJson ?? string.Empty,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static bool TryGetProperty(
        JsonElement root,
        IReadOnlyList<string> path,
        out JsonElement value)
    {
        value = root;
        foreach (var propertyName in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(propertyName, out value))
            {
                return false;
            }
        }

        return true;
    }

    public sealed record ResultPropertyRequirement(
        IReadOnlyList<string> Path,
        JsonValueKind ValueKind);
}
