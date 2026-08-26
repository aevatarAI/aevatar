using System.Text.Json.Nodes;

namespace Aevatar.Foundation.Abstractions.Connectors;

/// <summary>
/// Host-owned callback request dispatched through the host_callback connector type.
/// </summary>
public sealed class HostCallbackConnectorRequest
{
    public string ConnectorName { get; init; } = string.Empty;

    public string HandlerName { get; init; } = string.Empty;

    public string Operation { get; init; } = string.Empty;

    public string RunId { get; init; } = string.Empty;

    public string StepId { get; init; } = string.Empty;

    public string IdempotencyKey { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Structured host callback result returned to workflow connector execution.
/// </summary>
public sealed class HostCallbackConnectorResponse
{
    public bool Success { get; init; }

    public string Error { get; init; } = string.Empty;

    public JsonNode? Result { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// Host-owned handler bound to a named host_callback connector.
/// </summary>
public interface IHostCallbackConnectorHandler
{
    /// <summary>Stable handler name resolved from connector configuration.</summary>
    string Name { get; }

    /// <summary>Executes a structured host callback for a workflow connector step.</summary>
    Task<HostCallbackConnectorResponse> HandleAsync(
        HostCallbackConnectorRequest request,
        CancellationToken ct = default);
}

// Implement (issue #3526):
//   Behavior: Give deterministic host operations a typed, versioned signature for admission and drift detection.
//   Why this shape: The signature extends the existing host callback contract instead of creating another workflow primitive.
/// <summary>
/// Versioned signature for one pure deterministic algorithm exposed as a host callback operation.
/// Schema digests are SHA-256 fingerprints of canonical JSON Schema documents.
/// </summary>
public sealed record DeterministicAlgorithmDescriptor(
    string AlgorithmId,
    int AlgorithmVersion,
    string InputSchemaDigest,
    string OutputSchemaDigest);

/// <summary>
/// Host callback restricted to pure deterministic computation. Implementations must not read the
/// clock, randomness, environment, network, file system, or any other external state.
/// </summary>
public interface IDeterministicComputeHandler : IHostCallbackConnectorHandler
{
    /// <summary>Exact versioned algorithm signatures owned by this handler.</summary>
    IReadOnlyList<DeterministicAlgorithmDescriptor> Algorithms { get; }
}
