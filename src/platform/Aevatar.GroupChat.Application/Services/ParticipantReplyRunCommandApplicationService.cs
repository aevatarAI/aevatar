using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.Internal;
using Aevatar.GroupChat.Core.GAgents;

namespace Aevatar.GroupChat.Application.Services;

public sealed class ParticipantReplyRunCommandApplicationService : IParticipantReplyRunCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public ParticipantReplyRunCommandApplicationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task<GroupCommandAcceptedReceipt> StartAsync(
        global::Aevatar.GroupChat.Abstractions.StartParticipantReplyRunCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.ParticipantReplyRun(
                command.GroupId,
                command.ThreadId,
                command.ParticipantAgentId,
                command.SourceEventId),
            command,
            $"{command.GroupId}:{command.ThreadId}:{command.ParticipantAgentId}:{command.SignalId}:reply-run:start",
            ct);

    public Task<GroupCommandAcceptedReceipt> CompleteAsync(
        global::Aevatar.GroupChat.Abstractions.CompleteParticipantReplyRunCommand command,
        CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.ParticipantReplyRun(
                command.GroupId,
                command.ThreadId,
                command.ParticipantAgentId,
                command.SourceEventId),
            command,
            $"{command.GroupId}:{command.ThreadId}:{command.ParticipantAgentId}:{command.SourceEventId}:reply-run:complete",
            ct);

    private async Task<GroupCommandAcceptedReceipt> DispatchAsync(
        string actorId,
        Google.Protobuf.IMessage command,
        string correlationId,
        CancellationToken ct)
    {
        await EnsureRunActorAsync(actorId, ct);
        var envelope = GroupChatCommandEnvelopeFactory.Create(actorId, command, correlationId);
        await _dispatchPort.DispatchAsync(actorId, envelope, ct);
        return new GroupCommandAcceptedReceipt(actorId, envelope.Id, correlationId);
    }

    private async Task EnsureRunActorAsync(string actorId, CancellationToken ct)
    {
        if (await _runtime.GetAsync(actorId) != null)
            return;

        try
        {
            _ = await _runtime.CreateAsync<ParticipantReplyRunGAgent>(actorId, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
        }
    }
}
