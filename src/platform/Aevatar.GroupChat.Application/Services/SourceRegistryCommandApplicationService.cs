using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.Internal;
using Aevatar.GroupChat.Core.GAgents;
using Google.Protobuf;

namespace Aevatar.GroupChat.Application.Services;

public sealed class SourceRegistryCommandApplicationService : ISourceRegistryCommandPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ISourceCatalogProjectionPort _projectionPort;

    public SourceRegistryCommandApplicationService(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        ISourceCatalogProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public Task<GroupCommandAcceptedReceipt> RegisterSourceAsync(RegisterGroupSourceCommand command, CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.Source(command.SourceId),
            command,
            $"source:{command.SourceId}:register",
            ct);

    public Task<GroupCommandAcceptedReceipt> UpdateSourceTrustAsync(UpdateGroupSourceTrustCommand command, CancellationToken ct = default) =>
        DispatchAsync(
            GroupChatActorIds.Source(command.SourceId),
            command,
            $"source:{command.SourceId}:trust",
            ct);

    private async Task<GroupCommandAcceptedReceipt> DispatchAsync(
        string actorId,
        IMessage command,
        string correlationId,
        CancellationToken ct)
    {
        await EnsureProjectionAsync(actorId, ct);
        await EnsureSourceActorAsync(actorId, ct);
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
        }
    }

    private async Task EnsureSourceActorAsync(string actorId, CancellationToken ct)
    {
        if (await _runtime.GetAsync(actorId) != null)
            return;

        try
        {
            _ = await _runtime.CreateAsync<SourceRegistryGAgent>(actorId, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
        }
    }
}
