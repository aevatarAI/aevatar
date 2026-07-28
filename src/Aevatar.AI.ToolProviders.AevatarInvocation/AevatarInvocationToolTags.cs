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
            return CreateSuccessReceipt(callId, toolName, resultJson);

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
