using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class AgentToolCredentialPolicy
{
    public static bool IsMutation(IAgentTool tool, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var callSafety = tool.GetCallSafety(argumentsJson);

        return !callSafety.IsReadOnly ||
               callSafety.IsDestructive ||
               !string.IsNullOrWhiteSpace(tool.SideEffectKind) ||
               callSafety.RequiresApproval == true;
    }
}
