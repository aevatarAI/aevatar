// ─────────────────────────────────────────────────────────────
// Connector contracts
// Provides a unified external invocation abstraction used by
// workflow modules (MCP / HTTP / CLI / custom adapters).
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Connectors;

/// <summary>
/// A named connector that executes one external operation and returns
/// structured output + metadata.
/// </summary>
public interface IConnector
{
    /// <summary>Connector name used by workflow YAML (parameters.connector).</summary>
    string Name { get; }

    /// <summary>Connector type identifier, e.g. mcp/http/cli.</summary>
    string Type { get; }

    /// <summary>Executes a connector request.</summary>
    Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request model passed to connectors.
/// </summary>
public sealed class ConnectorRequest
{
    /// <summary>Workflow run id.</summary>
    public string RunId { get; init; } = "";

    /// <summary>Workflow step id.</summary>
    public string StepId { get; init; } = "";

    /// <summary>Connector name selected by workflow.</summary>
    public string Connector { get; init; } = "";

    /// <summary>Operation name selected by workflow.</summary>
    public string Operation { get; init; } = "";

    /// <summary>Raw input payload from StepRequestEvent.Input.</summary>
    public string Payload { get; init; } = "";

    /// <summary>Original step parameters for connector-specific options.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Connector execution result.
/// </summary>
public sealed class ConnectorResponse
{
    /// <summary>Whether connector execution succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Connector output body.</summary>
    public string Output { get; init; } = "";

    /// <summary>Error text when success=false.</summary>
    public string Error { get; init; } = "";

    /// <summary>Structured metadata returned by connector.</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// Read-only catalog for named connectors.
/// </summary>
public interface IConnectorCatalog
{
    /// <summary>Resolves a connector by name.</summary>
    bool TryGet(string name, out IConnector? connector);

    /// <summary>Returns all registered connector names.</summary>
    IReadOnlyList<string> ListNames();
}

/// <summary>
/// Immutable connector catalog used by host/bootstrap and tests.
/// </summary>
public sealed class StaticConnectorCatalog : IConnectorCatalog
{
    private readonly IReadOnlyDictionary<string, IConnector> _connectors;
    private readonly IReadOnlyList<string> _names;

    public StaticConnectorCatalog(IEnumerable<IConnector> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);

        var items = new Dictionary<string, IConnector>(StringComparer.OrdinalIgnoreCase);
        foreach (var connector in connectors)
        {
            ArgumentNullException.ThrowIfNull(connector);
            if (string.IsNullOrWhiteSpace(connector.Name))
                throw new ArgumentException("Connector name is required.", nameof(connectors));

            items[connector.Name.Trim()] = connector;
        }

        _connectors = items;
        _names = items.Keys
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static StaticConnectorCatalog Empty { get; } = new(Array.Empty<IConnector>());

    public bool TryGet(string name, out IConnector? connector)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            connector = null;
            return false;
        }

        var found = _connectors.TryGetValue(name.Trim(), out var value);
        connector = value;
        return found;
    }

    public IReadOnlyList<string> ListNames() => _names;
}
