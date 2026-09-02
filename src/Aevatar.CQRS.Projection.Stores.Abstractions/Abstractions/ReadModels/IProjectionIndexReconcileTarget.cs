namespace Aevatar.CQRS.Projection.Stores.Abstractions;

/// <summary>
/// Non-generic surface so the host can enumerate every projection store as a single
/// <c>IEnumerable&lt;IProjectionIndexReconcileTarget&gt;</c> (MS DI cannot resolve an
/// open-generic enumerable like <c>IEnumerable&lt;IProjectionIndexConsistencyProbe&lt;T&gt;&gt;</c>)
/// and reconcile its physical-index lifecycle once at startup — healing schema drift
/// data-safely before the read path is exercised.
/// </summary>
public interface IProjectionIndexReconcileTarget
{
    /// <summary>The read/write alias name this target reconciles.</summary>
    string IndexAlias { get; }

    /// <summary>
    /// Reconcile the backing physical index for this store's alias, healing schema drift by
    /// copying data forward and atomically repointing the alias. Provider read-model targets may
    /// opt out when request-path auto-create is disabled; explicitly governed artifact targets
    /// may still provision at startup under their own lifecycle policy.
    /// </summary>
    Task ReconcileIndexAsync(CancellationToken ct = default);
}
