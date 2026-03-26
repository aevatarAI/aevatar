using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GroupChat.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GroupChat.Tests.TestSupport;

internal static class GroupChatTestKit
{
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    public static CreateGroupThreadCommand CreateThreadCommand(
        string groupId = "group-a",
        string threadId = "general",
        string displayName = "General",
        params string[] participantAgentIds)
    {
        var command = new CreateGroupThreadCommand
        {
            GroupId = groupId,
            ThreadId = threadId,
            DisplayName = displayName,
        };
        command.ParticipantAgentIds.Add((participantAgentIds.Length == 0
            ? ["agent-alpha", "agent-beta"]
            : participantAgentIds));
        return command;
    }

    public static PostUserMessageCommand CreateUserMessageCommand(
        string groupId = "group-a",
        string threadId = "general",
        string messageId = "msg-user-1",
        string senderUserId = "user-1",
        string text = "hello",
        params string[] directHintAgentIds)
    {
        var command = new PostUserMessageCommand
        {
            GroupId = groupId,
            ThreadId = threadId,
            MessageId = messageId,
            SenderUserId = senderUserId,
            Text = text,
        };
        command.DirectHintAgentIds.Add(directHintAgentIds);
        return command;
    }

    public static AppendAgentMessageCommand CreateAgentMessageCommand(
        string groupId = "group-a",
        string threadId = "general",
        string messageId = "msg-agent-1",
        string participantAgentId = "agent-alpha",
        string text = "working on it",
        string replyToMessageId = "msg-user-1")
    {
        return new AppendAgentMessageCommand
        {
            GroupId = groupId,
            ThreadId = threadId,
            MessageId = messageId,
            ParticipantAgentId = participantAgentId,
            Text = text,
            ReplyToMessageId = replyToMessageId,
        };
    }

    public static TAgent CreateStatefulAgent<TAgent, TState>(
        InMemoryEventStore eventStore,
        string actorId,
        Func<TAgent> factory)
        where TAgent : GAgentBase<TState>
        where TState : class, IMessage<TState>, new()
    {
        var agent = factory();
        AssignActorId(agent, actorId);
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<TState>(eventStore);
        agent.Services = new ServiceCollection()
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>(Array.Empty<IGAgentExecutionHook>())
            .BuildServiceProvider();
        return agent;
    }

    public static void AssignActorId(IAgent agent, string actorId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        SetIdMethod.Invoke(agent, [actorId]);
    }
}
