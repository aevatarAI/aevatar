using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.Internal;
using Aevatar.GroupChat.Core.GAgents;
using Google.Protobuf;

namespace Aevatar.GroupChat.Application.Services;

public sealed class GroupThreadCommandApplicationService : IGroupThreadCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IGroupTimelineProjectionPort _projectionPort;

    public GroupThreadCommandApplicationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IGroupTimelineProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public Task<GroupCommandAcceptedReceipt> CreateThreadAsync(
        CreateGroupThreadCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.Thread(command.GroupId, command.ThreadId),
            command,
            CorrelationForThread(command.GroupId, command.ThreadId),
            ct);

    public Task<GroupCommandAcceptedReceipt> PostUserMessageAsync(
        PostUserMessageCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.Thread(command.GroupId, command.ThreadId),
            command,
            CorrelationForMessage(command.GroupId, command.ThreadId, command.MessageId),
            ct);

    public Task<GroupCommandAcceptedReceipt> AppendAgentMessageAsync(
        AppendAgentMessageCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.Thread(command.GroupId, command.ThreadId),
            command,
            CorrelationForMessage(command.GroupId, command.ThreadId, command.MessageId),
            ct);

    private async Task<GroupCommandAcceptedReceipt> DispatchAsync(
        string actorId,
        IMessage command,
        string correlationId,
        CancellationToken ct)
    {
        await EnsureProjectionAsync(actorId, ct);
        await EnsureThreadActorAsync(actorId, ct);
        var envelope = GroupChatCommandEnvelopeFactory.Create(actorId, command, correlationId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct);
        return new GroupCommandAcceptedReceipt(actorId, envelope.Id, correlationId);
    }

    private async Task EnsureProjectionAsync(string actorId, CancellationToken ct)
    {
        try
        {
            await _projectionPort.EnsureProjectionAsync(actorId, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            // Another concurrent command or query created the durable projection scope first.
        }
    }

    private async Task EnsureThreadActorAsync(string actorId, CancellationToken ct)
    {
        if (await _runtime.GetAsync(actorId) != null)
            return;

        try
        {
            _ = await _runtime.CreateAsync<GroupThreadGAgent>(actorId, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            // Another concurrent command created the actor first.
        }
    }

    private static string CorrelationForThread(string groupId, string threadId) => $"{groupId}:{threadId}";

    private static string CorrelationForMessage(string groupId, string threadId, string messageId) =>
        $"{CorrelationForThread(groupId, threadId)}:{messageId}";
}
