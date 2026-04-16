namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Default host resolver that prefers direct in-process module attachment and falls back to runtime-neutral remote bridging.
/// </summary>
public sealed class CompositeVoicePresenceSessionResolver : IVoicePresenceSessionResolver
{
    private readonly InProcessActorVoicePresenceSessionResolver _inProcessResolver;
    private readonly RemoteActorVoicePresenceSessionResolver _remoteResolver;

    public CompositeVoicePresenceSessionResolver(
        InProcessActorVoicePresenceSessionResolver inProcessResolver,
        RemoteActorVoicePresenceSessionResolver remoteResolver)
    {
        _inProcessResolver = inProcessResolver ?? throw new ArgumentNullException(nameof(inProcessResolver));
        _remoteResolver = remoteResolver ?? throw new ArgumentNullException(nameof(remoteResolver));
    }

    public async Task<VoicePresenceSession?> ResolveAsync(VoicePresenceSessionRequest request, CancellationToken ct = default)
    {
        var inProcessSession = await _inProcessResolver.ResolveAsync(request, ct);
        if (inProcessSession != null)
            return inProcessSession;

        return await _remoteResolver.ResolveAsync(request, ct);
    }
}
