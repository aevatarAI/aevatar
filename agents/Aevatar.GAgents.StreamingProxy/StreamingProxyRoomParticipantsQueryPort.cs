using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public interface IStreamingProxyRoomParticipantsQueryPort
{
    Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
        string rootActorId,
        CancellationToken ct = default);
}

public sealed class StreamingProxyRoomParticipantsQueryPort
    : IStreamingProxyRoomParticipantsQueryPort
{
    private readonly IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string> _documentReader;

    public StreamingProxyRoomParticipantsQueryPort(
        IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
        string rootActorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return Task.FromResult<StreamingProxyRoomParticipantsSnapshot?>(null);

        return _documentReader.GetAsync(rootActorId.Trim(), ct);
    }
}
