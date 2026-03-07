using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflow.Infrastructure.Connectors;

/// <summary>
/// Development/test in-memory connector registry.
/// Connector names are case-insensitive and later registrations replace earlier ones.
/// </summary>
public sealed class InMemoryConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public void Register(IConnector connector)
    {
        lock (_sync)
            _connectors[connector.Name] = connector;
    }

    public bool TryGet(string name, out IConnector? connector)
    {
        lock (_sync)
        {
            var ok = _connectors.TryGetValue(name, out var found);
            connector = found;
            return ok;
        }
    }

    public IReadOnlyList<string> ListNames()
    {
        lock (_sync)
            return _connectors.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
