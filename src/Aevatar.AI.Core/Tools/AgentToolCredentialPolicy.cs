using Aevatar.AI.Abstractions;
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

        return AgentToolReceiptEffectPolicy.FromCallSafety(callSafety, tool.SideEffectKind) ==
               AgentToolReceiptEffect.Mutating;
    }
}
