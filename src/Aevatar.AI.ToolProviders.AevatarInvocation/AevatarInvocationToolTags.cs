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

// Refactor (iter290/cluster001): Old pattern: invocation tools exposed only boundary JSON to chat-run orchestration. New principle: chat-run invocation tools implement the typed completion-control contract.
public interface IAevatarInvocationChatRunTool : IAevatarInvocationTool, IChatRunToolCompletionControlExecutor;
