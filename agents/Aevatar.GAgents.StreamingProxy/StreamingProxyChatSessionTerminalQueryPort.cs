using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.StreamingProxy;

public interface IStreamingProxyChatSessionTerminalQueryPort
{
    Task<StreamingProxyChatSessionTerminalSnapshot?> GetAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default);
}

public sealed class StreamingProxyChatSessionTerminalQueryPort
    : IStreamingProxyChatSessionTerminalQueryPort
{
    private readonly IProjectionDocumentReader<StreamingProxyChatSessionTerminalSnapshot, string> _documentReader;

    public StreamingProxyChatSessionTerminalQueryPort(
        IProjectionDocumentReader<StreamingProxyChatSessionTerminalSnapshot, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public Task<StreamingProxyChatSessionTerminalSnapshot?> GetAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId) || string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult<StreamingProxyChatSessionTerminalSnapshot?>(null);

        return _documentReader.GetAsync(ComposeSnapshotId(rootActorId, sessionId), ct);
    }

    public static string ComposeSnapshotId(string rootActorId, string sessionId) =>
        $"{rootActorId.Trim()}:{sessionId.Trim()}";
}
