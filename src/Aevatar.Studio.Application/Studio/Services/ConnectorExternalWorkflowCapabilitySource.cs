using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class ConnectorExternalWorkflowCapabilitySource : IExternalWorkflowCapabilitySource
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    private readonly IConnectorCatalogQueryPort _catalogQueryPort;
    private readonly IReadOnlyDictionary<string, IDeterministicComputeHandler> _deterministicHandlersByName;
    private readonly TimeProvider _timeProvider;

    public ConnectorExternalWorkflowCapabilitySource(
        IConnectorCatalogQueryPort catalogQueryPort,
        IEnumerable<IHostCallbackConnectorHandler>? hostCallbackHandlers = null,
        TimeProvider? timeProvider = null)
    {
        _catalogQueryPort = catalogQueryPort;
        _deterministicHandlersByName = (hostCallbackHandlers ?? [])
            .OfType<IDeterministicComputeHandler>()
            .Where(static handler => !string.IsNullOrWhiteSpace(handler.Name))
            .GroupBy(static handler => handler.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
        ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector;

    public async Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var catalog = await _catalogQueryPort.GetConnectorCatalogAsync(cancellationToken);
        var source = BuildSourceStamp(catalog);

        var capabilities = catalog.Connectors
            .Where(static connector => connector.Enabled)
            .OrderBy(static connector => connector.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(connector => EnumerateOperations(connector)
                .Select(operation => BuildDescriptor(connector, operation, source)))
            .ToArray();
        var result = new ExternalWorkflowCapabilityDiscoveryResult
        {
            CandidateCount = capabilities.Length,
        };
        result.Capabilities.Add(capabilities);
        return result;
    }

    public async Task<ExternalCapabilityReadiness> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(selector);

        var catalog = await _catalogQueryPort.GetConnectorCatalogAsync(cancellationToken);
        var source = BuildSourceStamp(catalog);
        if (selector.SelectorCase !=
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ConnectorNotFound,
                "CONNECTOR_CAPABILITY_REQUIRED",
                "Select an exact Host Connector capability.",
                source);
        }

        var selected = selector.HostConnector;
        var connector = catalog.Connectors.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Name,
                selected.ConnectorCapabilityRef,
                StringComparison.OrdinalIgnoreCase));
        if (connector is null)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ConnectorNotFound,
                "CONNECTOR_NOT_FOUND",
                "The selected Host Connector is not present in the current catalog.",
                source);
        }

        if (!connector.Enabled)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ConnectorNotFound,
                "CONNECTOR_DISABLED",
                "The selected Host Connector is disabled.",
                source);
        }

        var operation = EnumerateOperations(connector).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, selected.OperationId, StringComparison.OrdinalIgnoreCase));
        if (operation is null)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "CONNECTOR_OPERATION_NOT_FOUND",
                "The selected Host Connector operation is no longer allowlisted.",
                source);
        }

        var currentDigest = BuildContractDigest(connector, operation);
        if (!string.Equals(currentDigest, selected.ContractDigest, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "CONNECTOR_CONTRACT_DRIFT",
                "The selected Host Connector operation contract has changed.",
                source);
        }

        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = selector.Clone(),
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                HostConnector = selected.Clone(),
            },
        };
        result.Sources.Add(source);
        return result;
    }

    private ExternalCapabilitySourceStamp BuildSourceStamp(StoredConnectorCatalog catalog)
    {
        var now = _timeProvider.GetUtcNow();
        var safeContractDigests = catalog.Connectors
            .OrderBy(static connector => connector.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(connector => EnumerateOperations(connector)
                .Select(operation => BuildContractDigest(connector, operation)));
        return new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.ConnectorCatalog,
            SourceId = "host-connector-catalog",
            SourceVersion = catalog.Version,
            ObservedAt = Timestamp.FromDateTimeOffset(now),
            FreshUntil = Timestamp.FromDateTimeOffset(now.Add(FreshnessWindow)),
            ContentDigest = ExternalWorkflowCapabilityContractDigest.Compute(safeContractDigests),
        };
    }

    private ExternalWorkflowCapabilityDescriptor BuildDescriptor(
        StoredConnectorDefinition connector,
        ConnectorOperation operation,
        ExternalCapabilitySourceStamp source)
    {
        var readOnly = IsReadOnly(connector.Type, operation.OperationId);
        return new ExternalWorkflowCapabilityDescriptor
        {
            Selector = new ExternalWorkflowCapabilitySelector
            {
                HostConnector = new HostConnectorCapabilityRef
                {
                    ConnectorCapabilityRef = connector.Name,
                    OperationId = operation.OperationId,
                    ContractDigest = BuildContractDigest(connector, operation),
                },
            },
            DisplayName = $"{connector.Name} / {operation.OperationId}",
            ReadOnly = readOnly,
            Destructive = !readOnly,
            Source = source.Clone(),
        };
    }

    private static ExternalCapabilityReadiness Failure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage,
        ExternalCapabilitySourceStamp source)
    {
        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = status,
            SelectedSelector = selector.Clone(),
        };
        result.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = status,
            Code = code,
            SafeMessage = safeMessage,
        });
        result.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.ConfigureConnector,
            Label = "Configure Host Connector",
            TrustedLocator = "studio:connectors",
        });
        result.Sources.Add(source);
        return result;
    }

    // Implement (issue #3526):
    //   Behavior: Publish deterministic host callbacks only when deployment allowlists and registered signatures exactly align.
    //   Why this shape: Catalog listing and readiness share the existing connector admission path without publishing a weak contract.
    private IReadOnlyList<ConnectorOperation> EnumerateOperations(StoredConnectorDefinition connector)
    {
        var type = connector.Type?.Trim().ToLowerInvariant();
        if (string.Equals(type, "host_callback", StringComparison.Ordinal))
            return EnumerateHostCallbackOperations(connector);

        var operationIds = type switch
        {
            "http" => connector.Http.AllowedMethods,
            "cli" => connector.Cli.AllowedOperations,
            "mcp" => connector.Mcp.AllowedTools.Concat(
                string.IsNullOrWhiteSpace(connector.Mcp.DefaultTool) ? [] : [connector.Mcp.DefaultTool]),
            _ => [],
        };

        return operationIds
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .Select(static value => new ConnectorOperation(value))
            .ToArray();
    }

    private IReadOnlyList<ConnectorOperation> EnumerateHostCallbackOperations(
        StoredConnectorDefinition connector)
    {
        var handlerName = connector.HostCallback.Handler?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(handlerName))
            return [];

        if (!_deterministicHandlersByName.TryGetValue(handlerName, out var handler))
            return [];

        var allowedOperations = connector.HostCallback.AllowedOperations
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allowedOperations.Length == 0 || handler.Algorithms.Count == 0)
            return [];

        var algorithms = new Dictionary<string, DeterministicAlgorithmDescriptor>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var algorithm in handler.Algorithms)
        {
            if (!IsValidAlgorithmDescriptor(algorithm) ||
                !algorithms.TryAdd(algorithm.AlgorithmId.Trim(), algorithm))
            {
                return [];
            }
        }

        if (!algorithms.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(allowedOperations))
        {
            return [];
        }

        return algorithms.Values
            .OrderBy(static algorithm => algorithm.AlgorithmId, StringComparer.OrdinalIgnoreCase)
            .Select(static algorithm => new ConnectorOperation(algorithm.AlgorithmId.Trim(), algorithm))
            .ToArray();
    }

    private string BuildContractDigest(
        StoredConnectorDefinition connector,
        ConnectorOperation operation)
    {
        var type = connector.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        var components = new List<string?>
        {
            "host-connector-operation.v1",
            connector.Name.Trim(),
            type,
            operation.OperationId,
            connector.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            connector.Retry.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        switch (type)
        {
            case "http":
                components.AddRange([
                    connector.Http.BaseUrl?.Trim(),
                    Join(connector.Http.AllowedMethods),
                    Join(connector.Http.AllowedPaths),
                    Join(connector.Http.AllowedInputKeys),
                    Join(connector.Http.DefaultHeaders.Keys),
                    connector.Http.Auth.Type?.Trim().ToLowerInvariant(),
                    connector.Http.Auth.TokenUrl?.Trim(),
                    connector.Http.Auth.ClientId?.Trim(),
                    connector.Http.Auth.Scope?.Trim(),
                    connector.Http.Auth.SecretRef?.Trim(),
                    connector.Http.Auth.HeaderName?.Trim(),
                    connector.Http.Auth.HeaderValuePrefix?.Trim(),
                ]);
                break;
            case "cli":
                components.AddRange([
                    connector.Cli.Command?.Trim(),
                    Join(connector.Cli.AllowedOperations),
                    Join(connector.Cli.AllowedInputKeys),
                    Join(connector.Cli.Environment.Keys),
                ]);
                break;
            case "mcp":
                components.AddRange([
                    connector.Mcp.ServerName?.Trim(),
                    connector.Mcp.Command?.Trim(),
                    connector.Mcp.Url?.Trim(),
                    Join(connector.Mcp.AllowedTools),
                    Join(connector.Mcp.AllowedInputKeys),
                    Join(connector.Mcp.Environment.Keys),
                    Join(connector.Mcp.AdditionalHeaders.Keys),
                    connector.Mcp.Auth.Type?.Trim().ToLowerInvariant(),
                    connector.Mcp.Auth.SecretRef?.Trim(),
                    connector.Mcp.Auth.HeaderName?.Trim(),
                ]);
                break;
            case "host_callback":
                var algorithm = operation.Algorithm ?? throw new InvalidOperationException(
                    "A host callback capability requires a deterministic algorithm signature.");
                components.AddRange([
                    "host-callback-operation.v1",
                    connector.HostCallback.Handler?.Trim(),
                    algorithm.AlgorithmId.Trim(),
                    algorithm.AlgorithmVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    algorithm.InputSchemaDigest,
                    algorithm.OutputSchemaDigest,
                    Join(connector.HostCallback.AllowedInputKeys),
                ]);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported connector type '{connector.Type}' cannot publish a capability descriptor.");
        }

        return ExternalWorkflowCapabilityContractDigest.Compute(components);
    }

    private static bool IsReadOnly(string? connectorType, string operationId) =>
        string.Equals(connectorType?.Trim(), "host_callback", StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(connectorType?.Trim(), "http", StringComparison.OrdinalIgnoreCase) &&
         (operationId.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
          operationId.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
          operationId.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)));

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

    private static string Join(IEnumerable<string> values) =>
        string.Join(
            "\n",
            values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

    private sealed record ConnectorOperation(
        string OperationId,
        DeterministicAlgorithmDescriptor? Algorithm = null);
}
