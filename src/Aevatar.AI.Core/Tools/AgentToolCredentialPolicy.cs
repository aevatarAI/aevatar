using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class AgentToolCredentialPolicy
{
    public static bool IsMutation(IAgentTool tool, string argumentsJson) =>
        IsMutation(tool, tool.GetCallSafety(argumentsJson));

    public static bool IsMutation(IAgentTool tool, AgentToolCallSafety callSafety)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(callSafety);

<<<<<<< HEAD
=======
        var callSafety = tool.GetCallSafety(argumentsJson);

>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
        return !callSafety.IsReadOnly ||
               callSafety.IsDestructive ||
               !string.IsNullOrWhiteSpace(tool.SideEffectKind) ||
               callSafety.RequiresApproval == true;
    }
}
