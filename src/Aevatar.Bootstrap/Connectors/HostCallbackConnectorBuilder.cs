using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Connectors;

public sealed class HostCallbackConnectorBuilder : IConnectorBuilder
{
    private readonly IReadOnlyDictionary<string, IHostCallbackConnectorHandler> _handlersByName;

    public HostCallbackConnectorBuilder(IEnumerable<IHostCallbackConnectorHandler>? handlers = null)
    {
        _handlersByName = (handlers ?? [])
            .Where(handler => !string.IsNullOrWhiteSpace(handler.Name))
            .GroupBy(handler => handler.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Type => "host_callback";

    public bool TryBuild(ConnectorConfigEntry entry, ILogger logger, out IConnector? connector)
    {
        connector = null;
        var handlerName = string.IsNullOrWhiteSpace(entry.HostCallback.Handler)
            ? entry.Name
            : entry.HostCallback.Handler.Trim();

        if (string.IsNullOrWhiteSpace(handlerName))
        {
            logger.LogWarning("Skip connector {Name}: hostCallback.handler is required", entry.Name);
            return false;
        }

        if (!_handlersByName.TryGetValue(handlerName, out var handler))
        {
            logger.LogWarning(
                "Skip connector {Name}: host callback handler {HandlerName} is not registered",
                entry.Name,
                handlerName);
            return false;
        }

        // Implement (issue #3526):
        //   Behavior: A deterministic connector is buildable only when its non-empty allowlist exactly matches signed algorithms.
        //   Why this shape: Deployment configuration and the handler descriptor become one fail-closed runtime contract.
        if (handler is IDeterministicComputeHandler deterministicHandler &&
            !HasExactDeterministicOperationContract(
                entry.HostCallback.AllowedOperations,
                deterministicHandler.Algorithms))
        {
            logger.LogWarning(
                "Skip connector {Name}: deterministic host callback operations do not match handler {HandlerName}",
                entry.Name,
                handlerName);
            return false;
        }

        connector = new HostCallbackConnector(
            entry.Name,
            handlerName,
            handler,
            entry.HostCallback.AllowedOperations,
            entry.HostCallback.AllowedInputKeys);
        return true;
    }

    private static bool HasExactDeterministicOperationContract(
        IEnumerable<string> allowedOperations,
        IReadOnlyList<DeterministicAlgorithmDescriptor> algorithms)
    {
        var allowed = allowedOperations
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0 || algorithms.Count == 0)
            return false;

        var algorithmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var algorithm in algorithms)
        {
            if (!IsValidAlgorithmDescriptor(algorithm) || !algorithmIds.Add(algorithm.AlgorithmId.Trim()))
                return false;
        }

        return allowed.SetEquals(algorithmIds);
    }

    private static bool IsValidAlgorithmDescriptor(DeterministicAlgorithmDescriptor? algorithm) =>
        algorithm is { AlgorithmVersion: > 0 } &&
        !string.IsNullOrWhiteSpace(algorithm.AlgorithmId) &&
        IsSHA256Digest(algorithm.InputSchemaDigest) &&
        IsSHA256Digest(algorithm.OutputSchemaDigest);

    private static bool IsSHA256Digest(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;

        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}
