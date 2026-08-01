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
    /// <summary>Execution metadata propagated from workflow/runtime context, excluding connector authorization.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Typed HTTP Authorization header value for connector execution, e.g. "Bearer token".</summary>
    public string HttpAuthorization { get; init; } = "";

    /// <summary>Workflow run id.</summary>
    public string RunId { get; init; } = "";

    /// <summary>Workflow step id.</summary>
    public string StepId { get; init; } = "";

    /// <summary>Advisory idempotency key for the logical workflow side effect.</summary>
    public string IdempotencyKey { get; init; } = "";

    /// <summary>Unix timestamp when the logical connector request was first issued.</summary>
    public long IssuedAtUnixMs { get; init; }

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

    /// <summary>Whether an admitted, start-once terminal was invoked, when the connector can prove it.</summary>
    public bool? TerminalInvoked { get; init; }

    /// <summary>Whether the failed invocation is safe to retry with the same logical identity, when known.</summary>
    public bool? Retryable { get; init; }

    /// <summary>Structured metadata returned by connector.</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// Registry for named connectors.
/// </summary>
public interface IConnectorRegistry : IAsyncDisposable
{
    /// <summary>Registers or replaces a connector by name.</summary>
    ValueTask RegisterAsync(ConnectorRegistration registration, CancellationToken ct = default);

    /// <summary>Resolves a connector by name.</summary>
    bool TryGet(string name, out IConnector? connector);

    /// <summary>Returns all registered connector names.</summary>
    IReadOnlyList<string> ListNames();
}

/// <summary>
/// Connector registration ownership.
/// </summary>
public enum ConnectorOwnership
{
    /// <summary>The registry owns the connector and disposes it on replacement or registry shutdown.</summary>
    RegistryOwned,

    /// <summary>The caller or DI owns the connector lifetime.</summary>
    ExternallyOwned,
}

/// <summary>
/// Connector registration entry with explicit lifecycle ownership.
/// </summary>
public sealed class ConnectorRegistration
{
    private ConnectorRegistration(IConnector connector, ConnectorOwnership ownership)
    {
        Connector = connector ?? throw new ArgumentNullException(nameof(connector));
        Ownership = ownership;
    }

    /// <summary>Connector instance to register by <see cref="IConnector.Name"/>.</summary>
    public IConnector Connector { get; }

    /// <summary>Lifecycle owner for the connector instance.</summary>
    public ConnectorOwnership Ownership { get; }

    /// <summary>Creates a registry-owned connector registration.</summary>
    public static ConnectorRegistration Owned(IConnector connector) =>
        new(connector, ConnectorOwnership.RegistryOwned);

    /// <summary>Creates an externally owned connector registration.</summary>
    public static ConnectorRegistration External(IConnector connector) =>
        new(connector, ConnectorOwnership.ExternallyOwned);
}
