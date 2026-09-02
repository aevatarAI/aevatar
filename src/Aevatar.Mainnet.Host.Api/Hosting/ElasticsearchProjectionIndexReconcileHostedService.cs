using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Hosting;

/// <summary>
/// At host startup, reconciles every Elasticsearch projection index lifecycle — healing schema
/// drift data-safely (reindex old→new + atomic alias swap, old physical retained for rollback)
/// BEFORE the read path is exercised. Without this, a deploy that bumps a read-model schema leaves
/// the ES alias pointing at the stale physical, and the next read throws ProjectionIndexSchemaDrift
/// (e.g. /ws/voice reading the voice-presence-capabilities projection returns 500). Per-target
/// failures are logged and skipped so a transient ES error or one drifted index never crash-loops
/// the co-hosted silo boot.
/// </summary>
internal sealed class ElasticsearchProjectionIndexReconcileHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ElasticsearchProjectionIndexReconcileHostedService> _logger;

    public ElasticsearchProjectionIndexReconcileHostedService(
        IServiceProvider services,
        ILogger<ElasticsearchProjectionIndexReconcileHostedService> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var targets = _services.GetServices<IProjectionIndexReconcileTarget>().ToList();
        if (targets.Count == 0)
            return;

        var reconciled = 0;
        var failed = 0;
        foreach (var target in targets)
        {
            try
            {
                await target.ReconcileIndexAsync(cancellationToken);
                reconciled++;
            }
            catch (Exception ex)
            {
                // Boundary fallback (CLAUDE.md observability rule): one drifted index or a transient
                // ES error must never crash host boot. Log loud and continue; that alias stays
                // drifted until the next deploy/restart or a manual reindex+repoint.
                failed++;
                _logger.LogError(
                    "Projection index startup reconcile failed. alias={Alias} errorType={ErrorType}; continuing.",
                    target.IndexAlias,
                    ex.GetType().Name);
            }
        }

        _logger.LogInformation(
            "Projection index startup reconcile complete: targets={Targets} reconciled={Reconciled} failed={Failed}",
            targets.Count,
            reconciled,
            failed);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
