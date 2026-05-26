namespace Aevatar.GAgents.StreamingProxy;

public enum StreamingProxyProjectionCompletionStatus
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
}

internal sealed class StreamingProxyChatDurableCompletionResolver
{
    private readonly IStreamingProxyChatSessionTerminalQueryPort _terminalQueryPort;

    public StreamingProxyChatDurableCompletionResolver(
        IStreamingProxyChatSessionTerminalQueryPort terminalQueryPort)
    {
        _terminalQueryPort = terminalQueryPort ?? throw new ArgumentNullException(nameof(terminalQueryPort));
    }

    public async Task<StreamingProxyProjectionCompletionStatus> ResolveAsync(
        string rootActorId,
        string sessionId,
        CancellationToken ct = default)
    {
        var snapshot = await _terminalQueryPort.GetAsync(rootActorId, sessionId, ct);
        return snapshot?.Status switch
        {
            StreamingProxyChatSessionTerminalStatus.Completed => StreamingProxyProjectionCompletionStatus.Completed,
            StreamingProxyChatSessionTerminalStatus.Failed => StreamingProxyProjectionCompletionStatus.Failed,
            _ => StreamingProxyProjectionCompletionStatus.Unknown,
        };
    }
}
