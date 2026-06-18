using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class AgentToolCredentialPolicy
{
    public static bool IsMutation(IAgentTool tool, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return !tool.IsReadOnly ||
               tool.IsDestructive ||
               !string.IsNullOrWhiteSpace(tool.SideEffectKind) ||
               tool.RequiresApproval(argumentsJson) == true;
    }
}
