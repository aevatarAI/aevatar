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
/// Registry for named connectors.
/// </summary>
public interface IConnectorRegistry
{
    /// <summary>Registers or replaces a connector by name.</summary>
    void Register(IConnector connector);

    /// <summary>Resolves a connector by name.</summary>
    bool TryGet(string name, out IConnector? connector);

    /// <summary>Returns all registered connector names.</summary>
    IReadOnlyList<string> ListNames();
}
