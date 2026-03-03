using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionReleaseService
    : IProjectionPortReleaseService<ScriptEvolutionRuntimeLease>
{
    private readonly IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>> _lifecycle;

    public ScriptEvolutionProjectionReleaseService(
        IProjectionLifecycleService<ScriptEvolutionSessionProjectionContext, IReadOnlyList<string>> lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task ReleaseIfIdleAsync(ScriptEvolutionRuntimeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        if (lease.GetLiveSinkSubscriptionCount() > 0)
            return;

        await _lifecycle.StopAsync(lease.Context, ct);
    }
}
