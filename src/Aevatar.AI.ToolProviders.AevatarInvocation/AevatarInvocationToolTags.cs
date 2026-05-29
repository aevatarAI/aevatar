using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public static class AevatarInvocationToolTags
{
    public const string ToolSet = "aevatar.invocation";
}

public interface IAevatarInvocationTool : IAgentTool
{
    string ToolSetTag => AevatarInvocationToolTags.ToolSet;
}

// Refactor (issue1298-first): Old: ResultJson string control parsing. New: typed scalar dispatch fields.
public interface IAevatarInvocationChatRunTool : IAevatarInvocationTool, IChatRunToolCompletionControlExecutor;
