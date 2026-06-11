namespace Aevatar.GAgents.StatusDashboard.Executors;

public sealed class HealthProbeExecutorRegistry : IHealthProbeExecutorRegistry
{
    private readonly Dictionary<string, IHealthProbeExecutor> _byKind;

    public HealthProbeExecutorRegistry(IEnumerable<IHealthProbeExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _byKind = executors.ToDictionary(e => e.Kind, e => e, StringComparer.OrdinalIgnoreCase);
    }

    public IHealthProbeExecutor? Resolve(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return _byKind.TryGetValue(kind, out var executor) ? executor : null;
    }

    public IReadOnlyCollection<string> KnownKinds => _byKind.Keys;
}
