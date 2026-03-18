using System.Collections.Immutable;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflow.Core.Connectors;

/// <summary>
/// Startup-initialized connector registry.
/// <para>
/// Connectors are registered during application bootstrap (before the host
/// starts processing requests) and are read-only thereafter. The registry
/// uses <see cref="ImmutableDictionary{TKey,TValue}"/> for lock-free,
/// thread-safe reads after initialization.
/// </para>
/// <para>
/// <b>Multi-node deployments</b>: each node must be configured with the
/// same set of connectors (typically via identical <c>connectors.json</c>
/// configuration). The registry does not provide cross-node consistency;
/// it relies on consistent configuration across all nodes.
/// </para>
/// </summary>
public sealed class ConfiguredConnectorRegistry : IConnectorRegistry
{
    private ImmutableDictionary<string, IConnector> _connectors =
        ImmutableDictionary.Create<string, IConnector>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(IConnector connector)
    {
        _connectors = _connectors.SetItem(connector.Name, connector);
    }

    /// <inheritdoc />
    public bool TryGet(string name, out IConnector? connector)
    {
        var ok = _connectors.TryGetValue(name, out var found);
        connector = found;
        return ok;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListNames()
    {
        return _connectors.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
