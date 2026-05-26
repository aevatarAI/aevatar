using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StatusDashboard.Executors;

/// <summary>
/// Inspects a registered <see cref="IReadmodelFreshnessSource"/> and converts
/// the freshness signal into a health outcome. Concrete sources are registered
/// per readmodel (channel bot runtime, scheduled agent catalog, etc.) so this
/// executor stays generic and consumers can add new freshness checks without
/// touching the executor.
///
/// Parameters:
///   Source             required — name matched against IReadmodelFreshnessSource.Name
///   MinCount           optional — minimum entry count for OK (default 0)
///   StaleAfterSeconds  optional — degrade if last_committed_at is older than this
/// </summary>
public sealed class ReadmodelFreshnessProbeExecutor : IHealthProbeExecutor
{
    private readonly Dictionary<string, IReadmodelFreshnessSource> _sources;
    private readonly TimeProvider _timeProvider;

    public ReadmodelFreshnessProbeExecutor(
        IEnumerable<IReadmodelFreshnessSource> sources,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string Kind => "readmodel_freshness";

    public async Task<HealthProbeOutcome> ProbeAsync(HealthProbeTargetDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var sourceName = ReadParam(descriptor, "Source");
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "missing_parameter",
                ErrorMessage = "Parameter 'Source' is required.",
                ObservedAt = NowTimestamp(),
            };
        }

        if (!_sources.TryGetValue(sourceName, out var source))
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "unknown_source",
                ErrorMessage = $"No IReadmodelFreshnessSource named '{sourceName}'. Known: {string.Join(",", _sources.Keys)}.",
                ObservedAt = NowTimestamp(),
            };
        }

        var minCount = TryReadInt(descriptor, "MinCount") ?? 0;
        var staleAfter = TryReadInt(descriptor, "StaleAfterSeconds");

        ReadmodelFreshnessSnapshot snapshot;
        try
        {
            snapshot = await source.GetFreshnessAsync(ct);
        }
        catch (Exception ex)
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "freshness_source_threw",
                ErrorMessage = ex.Message,
                ObservedAt = NowTimestamp(),
            };
        }

        if (snapshot.Count < minCount)
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Degraded,
                Detail = $"count_{snapshot.Count}",
                ErrorMessage = $"Read model has {snapshot.Count} entries; expected at least {minCount}.",
                ObservedAt = NowTimestamp(),
            };
        }

        if (staleAfter.HasValue && snapshot.LastUpdatedAt.HasValue)
        {
            // Refactor (iter89/cluster-089-status-dashboard-probe-clock):
            // Old: freshness age compared the readmodel timestamp against DateTimeOffset.UtcNow.
            // New: freshness age compares against the injected TimeProvider clock.
            var age = _timeProvider.GetUtcNow() - snapshot.LastUpdatedAt.Value;
            if (age.TotalSeconds > staleAfter.Value)
            {
                return new HealthProbeOutcome
                {
                    Status = HealthOutcomeStatus.Degraded,
                    Detail = $"stale_{(int)age.TotalSeconds}s",
                    ErrorMessage = $"Latest entry is {(int)age.TotalSeconds}s old; threshold {staleAfter}s.",
                    ObservedAt = NowTimestamp(),
                };
            }
        }

        return new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Ok,
            Detail = $"count_{snapshot.Count}",
        };
    }

    private Timestamp NowTimestamp() =>
        Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());

    private static string? ReadParam(HealthProbeTargetDescriptor descriptor, string key)
    {
        foreach (var (k, v) in descriptor.Parameters)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    private static int? TryReadInt(HealthProbeTargetDescriptor descriptor, string key)
    {
        var raw = ReadParam(descriptor, key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw, out var v) ? v : null;
    }
}

/// <summary>
/// Pluggable freshness source — one per read model that wants to surface a
/// status indicator. Implementations live alongside the read model they expose
/// so the executor stays decoupled from any specific projection.
/// </summary>
public interface IReadmodelFreshnessSource
{
    /// <summary>Stable identifier referenced from the <c>Source</c> probe parameter.</summary>
    string Name { get; }

    Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct);
}

public sealed record ReadmodelFreshnessSnapshot(
    int Count,
    DateTimeOffset? LastUpdatedAt);
