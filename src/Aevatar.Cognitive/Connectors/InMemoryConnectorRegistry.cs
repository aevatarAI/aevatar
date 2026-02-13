using Aevatar.Connectors;

namespace Aevatar.Cognitive.Connectors;

/// <summary>
/// Default in-memory connector registry.
/// Connector names are case-insensitive and later registrations replace earlier ones.
/// </summary>
public sealed class InMemoryConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <inheritdoc />
    public void Register(IConnector connector)
    {
        lock (_sync)
            _connectors[connector.Name] = connector;
    }

    /// <inheritdoc />
    public bool TryGet(string name, out IConnector? connector)
    {
        lock (_sync)
        {
            var ok = _connectors.TryGetValue(name, out var found);
            connector = found;
            return ok;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListNames()
    {
        lock (_sync)
            return _connectors.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
