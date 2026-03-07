namespace Aevatar.Workflow.Core;

/// <summary>
/// Local registry for stateless workflow primitive handlers.
/// </summary>
public sealed class WorkflowPrimitiveExecutorRegistry
{
    private readonly IReadOnlyDictionary<string, PrimitiveEntry> _entriesByName;

    public WorkflowPrimitiveExecutorRegistry(IEnumerable<IWorkflowPrimitivePack> primitivePacks)
    {
        _entriesByName = BuildPrimitiveMap(primitivePacks ?? throw new ArgumentNullException(nameof(primitivePacks)));
    }

    public IReadOnlyCollection<string> RegisteredNames => _entriesByName.Keys.ToArray();

    public bool TryCreate(string name, IServiceProvider services, out IWorkflowPrimitiveExecutor? handler)
    {
        ArgumentNullException.ThrowIfNull(services);

        handler = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!_entriesByName.TryGetValue(name, out var entry))
            return false;

        handler = entry.Registration.Create(services);
        return handler != null;
    }

    private static IReadOnlyDictionary<string, PrimitiveEntry> BuildPrimitiveMap(
        IEnumerable<IWorkflowPrimitivePack> primitivePacks)
    {
        var map = new Dictionary<string, PrimitiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var primitivePack in primitivePacks)
        {
            var packName = string.IsNullOrWhiteSpace(primitivePack.Name) ? primitivePack.GetType().Name : primitivePack.Name;
            foreach (var registration in primitivePack.Executors)
            {
                foreach (var name in registration.Names)
                {
                    if (map.TryGetValue(name, out var existing))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate workflow primitive name '{name}' found in packs '{existing.PackName}' and '{packName}'.");
                    }

                    map[name] = new PrimitiveEntry(packName, registration);
                }
            }
        }

        return map;
    }

    private sealed record PrimitiveEntry(
        string PackName,
        WorkflowPrimitiveRegistration Registration);
}
