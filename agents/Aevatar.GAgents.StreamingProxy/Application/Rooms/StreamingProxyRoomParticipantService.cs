namespace Aevatar.GAgents.StreamingProxy.Application.Rooms;

internal sealed class StreamingProxyRoomParticipantService : IStreamingProxyRoomParticipantService
{
    private readonly IStreamingProxyRoomParticipantsQueryPort _participantsQueryPort;
    private readonly StreamingProxyNyxParticipantCoordinator _nyxParticipantCoordinator;

    public StreamingProxyRoomParticipantService(
        IStreamingProxyRoomParticipantsQueryPort participantsQueryPort,
        StreamingProxyNyxParticipantCoordinator nyxParticipantCoordinator)
    {
        _participantsQueryPort = participantsQueryPort ?? throw new ArgumentNullException(nameof(participantsQueryPort));
        _nyxParticipantCoordinator = nyxParticipantCoordinator ?? throw new ArgumentNullException(nameof(nyxParticipantCoordinator));
    }

    public async Task<StreamingProxyRoomParticipantListResult> ListAsync(
        StreamingProxyRoomParticipantListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var roomId = NormalizeRequiredValue(query.RoomId, nameof(query.RoomId));
        var snapshot = await _participantsQueryPort.GetAsync(roomId, cancellationToken);
        if (snapshot == null)
        {
            return new StreamingProxyRoomParticipantListResult(
                roomId,
                0,
                DateTimeOffset.MinValue,
                []);
        }

        return new StreamingProxyRoomParticipantListResult(
            roomId,
            snapshot.StateVersion,
            snapshot.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            snapshot.Participants
                .Select(participant => new StreamingProxyRoomParticipantEntry(
                    participant.AgentId,
                    participant.DisplayName,
                    participant.JoinedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue))
                .ToList());
    }

    public async Task<IReadOnlyList<StreamingProxyNyxParticipantDefinition>> EnsureNyxParticipantsJoinedAsync(
        StreamingProxyRoomNyxParticipantJoinCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var roomId = NormalizeRequiredValue(command.RoomId, nameof(command.RoomId));
        return await _nyxParticipantCoordinator.EnsureParticipantsJoinedAsync(
            command.ScopeId,
            roomId,
            NormalizeRequiredValue(command.AccessToken, nameof(command.AccessToken)),
            cancellationToken,
            command.PreferredRoute,
            command.DefaultModel);
    }

    public async Task<int> GenerateNyxRepliesAsync(
        StreamingProxyRoomNyxReplyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var roomId = NormalizeRequiredValue(command.RoomId, nameof(command.RoomId));
        return await _nyxParticipantCoordinator.GenerateRepliesAsync(
            command.Participants,
            roomId,
            NormalizeRequiredValue(command.Prompt, nameof(command.Prompt)),
            NormalizeRequiredValue(command.SessionId, nameof(command.SessionId)),
            NormalizeRequiredValue(command.AccessToken, nameof(command.AccessToken)),
            cancellationToken);
    }

    private static string NormalizeRequiredValue(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return normalized;
    }
}
