namespace Aevatar.GAgents.StatusDashboard.Configuration;

/// <summary>
/// Resolved view over <see cref="StatusDashboardOptions"/> — pre-validates the
/// manifest at startup so the GAgent module receives well-formed descriptors
/// instead of fishing for missing fields at runtime.
/// </summary>
public sealed class StatusDashboardManifest
{
    public StatusDashboardManifest(IReadOnlyList<HealthProbeTargetDescriptor> descriptors)
    {
        Descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
    }

    public IReadOnlyList<HealthProbeTargetDescriptor> Descriptors { get; }

    public static StatusDashboardManifest FromOptions(StatusDashboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var defaultInterval = options.DefaultIntervalSeconds > 0
            ? options.DefaultIntervalSeconds
            : 60;
        var defaultTimeout = options.DefaultTimeoutMs > 0
            ? options.DefaultTimeoutMs
            : 5_000;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<HealthProbeTargetDescriptor>();
        foreach (var t in options.Targets ?? new())
        {
            if (string.IsNullOrWhiteSpace(t.Slug)) continue;
            if (string.IsNullOrWhiteSpace(t.Probe)) continue;
            if (!seen.Add(t.Slug.Trim())) continue;

            var descriptor = new HealthProbeTargetDescriptor
            {
                Slug = t.Slug.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(t.Name) ? t.Slug.Trim() : t.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(t.Category) ? "upstream" : t.Category.Trim().ToLowerInvariant(),
                ProbeKind = t.Probe.Trim(),
                IntervalSeconds = (t.IntervalSeconds.HasValue && t.IntervalSeconds.Value > 0)
                    ? t.IntervalSeconds.Value
                    : defaultInterval,
                TimeoutMs = (t.TimeoutMs.HasValue && t.TimeoutMs.Value > 0)
                    ? t.TimeoutMs.Value
                    : defaultTimeout,
                Enabled = t.Enabled,
            };
            foreach (var (k, v) in t.Parameters ?? new())
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                descriptor.Parameters[k.Trim()] = v ?? string.Empty;
            }
            descriptors.Add(descriptor);
        }

        return new StatusDashboardManifest(descriptors);
    }
}
