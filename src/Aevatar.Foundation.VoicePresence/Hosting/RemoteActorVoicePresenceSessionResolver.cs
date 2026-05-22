namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Disabled remote resolver shell retained only as a null resolver until raw remote audio transport exists.
/// </summary>
// Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
//   Old pattern: VoicePresenceModule 在 module 内持有 process-local background state(unbounded channels / TaskCompletionSource waiters / 静态字段持 lifecycle),还保留 disabled remote voice fallback shell;违反 Actor 单线程事实源 + 中间层状态约束。
//   New principle: Reuse existing RoleGAgent state for voice runtime facts(typed protobuf sub-state in RoleGAgent state);transport handles 仅作 volatile process-local lease(non-fact source);provider callbacks 走 typed self-signals(self-message 到 actor inbox);**删除** disabled remote voice fallback shell。无新 actor type / 新 envelope kind。
public sealed class RemoteActorVoicePresenceSessionResolver : IVoicePresenceSessionResolver
{
    public RemoteActorVoicePresenceSessionResolver(
        IServiceProvider services,
        IEnumerable<VoicePresenceModuleRegistration>? registrations = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = registrations;
    }

    public Task<VoicePresenceSession?> ResolveAsync(VoicePresenceSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorId);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<VoicePresenceSession?>(null);
    }
}
