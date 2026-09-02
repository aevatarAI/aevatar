using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Maps NyxID's live MCP operation catalog into credential-free workflow capability contracts.
/// Results are never cached; exact UserService and endpoint ids remain NyxID authority keys.
/// </summary>
public sealed class NyxIdExternalWorkflowCapabilitySource : IExternalWorkflowCapabilitySource
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    private readonly NyxIdApiClient _client;
    private readonly NyxIdToolOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly NyxIdDurableAuthorizationCatalogInspector _durableAuthorizationCatalog;
    private readonly ILogger<NyxIdExternalWorkflowCapabilitySource> _logger;

    public NyxIdExternalWorkflowCapabilitySource(
        NyxIdApiClient client,
        NyxIdToolOptions options,
        TimeProvider? timeProvider = null,
        INyxIdAuthorizationCatalogQueryPort? catalogQueryPort = null,
        ILogger<NyxIdExternalWorkflowCapabilitySource>? logger = null)
    {
        _client = client;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<NyxIdExternalWorkflowCapabilitySource>.Instance;
        _durableAuthorizationCatalog = new NyxIdDurableAuthorizationCatalogInspector(
            catalogQueryPort,
            _timeProvider,
            _logger);
    }

    public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
        ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation;

    public async Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        return (await ReadCatalogAsync(access, cancellationToken)).Discovery.Clone();
    }

    public async Task<ExternalCapabilityReadiness> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(selector);

        if (selector.SelectorCase !=
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.SelectionRequired,
                "NYXID_OPERATION_SELECTION_REQUIRED",
                "Select an exact NyxID UserService operation.",
                ExternalCapabilityRemediationActionKind.SelectOperation,
                "Select operation");
        }

        var selected = selector.NyxIdOperation;
        if (string.IsNullOrWhiteSpace(selected.UserServiceId) ||
            string.IsNullOrWhiteSpace(selected.EndpointId))
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "NYXID_OPERATION_SELECTION_REQUIRED",
                "Select one exact connected service and operation.",
                ExternalCapabilityRemediationActionKind.SelectOperation,
                "Select operation");
        }

        var snapshot = await ReadCatalogAsync(access, cancellationToken);
        _logger.LogInformation(
            "NyxID workflow capability readiness inspection started. executionMode={ExecutionMode}, callerIdPresent={CallerIdPresent}, selectedUserServiceIdPresent={SelectedUserServiceIdPresent}, selectedEndpointId={SelectedEndpointId}, serviceCount={ServiceCount}, accessDenied={AccessDenied}, sourceUnavailable={SourceUnavailable}",
            executionMode,
            !string.IsNullOrWhiteSpace(access.CallerId),
            !string.IsNullOrWhiteSpace(selected.UserServiceId),
            selected.EndpointId,
            snapshot.Services.Count,
            snapshot.AccessDenied,
            snapshot.SourceUnavailable);

        var service = snapshot.Services.FirstOrDefault(item =>
            string.Equals(item.UserServiceId, selected.UserServiceId, StringComparison.Ordinal));
        if (service is null)
        {
            var issueFailure = BuildIssueFailure(selector, executionMode, snapshot, endpointRequired: false);
            if (issueFailure is not null)
                return issueFailure;
            var sourceFailure = BuildSnapshotFailure(selector, executionMode, snapshot);
            if (sourceFailure is not null)
                return sourceFailure;

            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                "USER_SERVICE_NOT_VISIBLE",
                "The selected NyxID UserService is not visible to the current caller.",
                ExternalCapabilityRemediationActionKind.RegisterService,
                "Register service",
                snapshot.Sources);
        }

        var endpoint = service.Endpoints.SingleOrDefault(candidate =>
            string.Equals(candidate.EndpointId, selected.EndpointId, StringComparison.Ordinal));
        if (endpoint is null)
        {
            var issueFailure = BuildIssueFailure(selector, executionMode, snapshot, endpointRequired: true);
            if (issueFailure is not null)
                return issueFailure;
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "NYXID_ENDPOINT_NOT_FOUND",
                "The selected endpoint is not present in the current NyxID MCP catalog.",
                ExternalCapabilityRemediationActionKind.SelectOperation,
                "Select operation",
                [service.Source]);
        }

        ExternalWorkflowCapabilityRef capability;
        try
        {
            capability = NyxIdOperationAdmissionProofBuilder.Build(
                service.UserServiceId,
                service.ServiceSlug,
                endpoint,
                endpoint.ContractDigest);
        }
        catch (NyxIdOperationSchemaUnsupportedException)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.EndpointContractRequired,
                "OPERATION_SCHEMA_UNSUPPORTED",
                "The selected operation schema is outside the supported workflow contract subset.",
                ExternalCapabilityRemediationActionKind.PublishEndpointContract,
                "Publish a supported endpoint contract",
                [service.Source]);
        }

        ExternalCapabilityReadiness? durableReadiness = null;
        if (executionMode == ExternalCapabilityExecutionMode.Durable)
        {
            if (!capability.NyxIdUserService.ExecutionPolicy.AllowedExecutionModes.Contains(
                    ExternalCapabilityExecutionMode.Durable))
            {
                return Failure(
                    selector,
                    executionMode,
                    ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                    "NYXID_OPERATION_DURABLE_EXECUTION_NOT_ALLOWED",
                    "The selected operation requires interactive execution.",
                    ExternalCapabilityRemediationActionKind.UseInteractiveExecution,
                    "Use interactive execution",
                    [service.Source],
                    capability);
            }

            var durableAuthorizationSource = await _durableAuthorizationCatalog.InspectAsync(
                access,
                capability.NyxIdUserService.UserServiceId,
                capability.NyxIdUserService.ServiceSlugSnapshot,
                cancellationToken);
            if (durableAuthorizationSource is null)
            {
                durableReadiness = DurableAuthorizationUnavailable(selector, capability);
                _logger.LogInformation(
                    "NyxID workflow capability durable authorization inspection blocked. status={Status}, blockerCodes={BlockerCodes}, selectedUserServiceId={SelectedUserServiceId}, selectedEndpointId={SelectedEndpointId}",
                    durableReadiness.Status,
                    FormatBlockerCodes(durableReadiness.Blockers),
                    selected.UserServiceId,
                    selected.EndpointId);
                durableReadiness.Sources.Add(service.Source);
                return durableReadiness;
            }

            durableReadiness = new ExternalCapabilityReadiness
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
                Status = ExternalCapabilityReadinessStatus.Ready,
                SelectedSelector = selector.Clone(),
                SelectedCapability = capability.Clone(),
            };
            durableReadiness.Sources.Add(durableAuthorizationSource);
        }

        var ready = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = selector.Clone(),
            SelectedCapability = capability.Clone(),
        };
        ready.Sources.Add(service.Source);
        if (durableReadiness is not null)
            ready.Sources.Add(durableReadiness.Sources);
        _logger.LogInformation(
            "NyxID workflow capability readiness inspection completed. executionMode={ExecutionMode}, selectedUserServiceId={SelectedUserServiceId}, selectedEndpointId={SelectedEndpointId}, status={Status}",
            executionMode,
            selected.UserServiceId,
            selected.EndpointId,
            ready.Status);
        return ready;
    }

    private ExternalCapabilityReadiness DurableAuthorizationUnavailable(
        ExternalWorkflowCapabilitySelector selector,
        ExternalWorkflowCapabilityRef capability) =>
        Failure(
            selector,
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
            "DURABLE_AUTHORIZATION_UNAVAILABLE",
            "The current NyxID authorization catalog does not prove the complete durable grant.",
            ExternalCapabilityRemediationActionKind.UseInteractiveExecution,
            "Use interactive execution",
            capability: capability);

    private ExternalCapabilityReadiness? BuildSnapshotFailure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        NyxIdMcpCatalogSnapshot snapshot)
    {
        if (snapshot.AccessDenied)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "NYXID_CALLER_ACCESS_REQUIRED",
                "The current caller cannot read all NyxID MCP capability sources.",
                ExternalCapabilityRemediationActionKind.RequestAccess,
                "Open NyxID",
                snapshot.Sources);
        }

        return snapshot.SourceUnavailable
            ? Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_UNAVAILABLE",
                "NyxID MCP capability facts are currently unavailable.",
                ExternalCapabilityRemediationActionKind.RefreshSource,
                "Refresh NyxID capabilities",
                snapshot.Sources)
            : null;
    }

    private async Task<NyxIdMcpCatalogSnapshot> ReadCatalogAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken)
    {
        var sourceReadableBearerToken = access.NyxIdCallerCredential?.SourceReadableUserBearerToken;
        if (string.IsNullOrWhiteSpace(sourceReadableBearerToken) ||
            string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return ToSnapshot(NyxIdMcpOperationCatalog.Parse(
                "{\"error\":true,\"status\":403}",
                "caller",
                _timeProvider.GetUtcNow(),
                FreshnessWindow));
        }

        var response = await _client.GetMcpConfigAsync(
            sourceReadableBearerToken, cancellationToken);
        return ToSnapshot(NyxIdMcpOperationCatalog.Parse(
            response,
            "caller",
            _timeProvider.GetUtcNow(),
            FreshnessWindow));
    }

    private static NyxIdMcpCatalogSnapshot ToSnapshot(NyxIdMcpCatalogRead catalog) =>
        new(
            catalog.Services,
            [catalog.Source.Clone()],
            catalog.Issues,
            catalog.Discovery.Clone(),
            catalog.AccessDenied,
            catalog.SourceUnavailable);

    private ExternalCapabilityReadiness? BuildIssueFailure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        NyxIdMcpCatalogSnapshot snapshot,
        bool endpointRequired)
    {
        var selected = selector.NyxIdOperation;
        var issue = snapshot.Issues.FirstOrDefault(candidate =>
            candidate.Code != ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable &&
            string.Equals(candidate.UserServiceId, selected.UserServiceId, StringComparison.Ordinal) &&
            (!endpointRequired ||
             candidate.EndpointId is null ||
             string.Equals(candidate.EndpointId, selected.EndpointId, StringComparison.Ordinal)));
        if (issue is null)
            return null;

        return Failure(
            selector,
            executionMode,
            ExternalCapabilityReadinessStatus.EndpointContractRequired,
            DiscoveryBlockerCode(issue.Code),
            issue.SafeMessage,
            ExternalCapabilityRemediationActionKind.PublishEndpointContract,
            "Publish a supported endpoint contract",
            snapshot.Sources);
    }

    private static string DiscoveryBlockerCode(ExternalCapabilityDiscoveryDiagnosticCode code) => code switch
    {
        ExternalCapabilityDiscoveryDiagnosticCode.NoExactUserService => "NYXID_EXACT_USER_SERVICE_REQUIRED",
        ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected => "NYXID_GENERIC_PROXY_REJECTED",
        ExternalCapabilityDiscoveryDiagnosticCode.InvalidServiceIdentity => "NYXID_SERVICE_IDENTITY_INVALID",
        ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousServiceIdentity => "NYXID_SERVICE_IDENTITY_AMBIGUOUS",
        ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity => "NYXID_ENDPOINT_IDENTITY_INVALID",
        ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousEndpointIdentity => "NYXID_ENDPOINT_IDENTITY_AMBIGUOUS",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter => "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody => "NYXID_ENDPOINT_BODY_UNSUPPORTED",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema => "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedResponse => "NYXID_ENDPOINT_RESPONSE_UNSUPPORTED",
        _ => "NYXID_ENDPOINT_CONTRACT_UNAVAILABLE",
    };

    private ExternalCapabilityReadiness Failure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage,
        ExternalCapabilityRemediationActionKind actionKind,
        string actionLabel,
        IEnumerable<ExternalCapabilitySourceStamp>? sources = null,
        ExternalWorkflowCapabilityRef? capability = null)
    {
        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = status,
            SelectedSelector = selector.Clone(),
            SelectedCapability = capability?.Clone(),
        };
        result.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = status,
            Code = code,
            SafeMessage = safeMessage,
        });
        result.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = actionKind,
            Label = actionLabel,
            TrustedLocator = TrustedLocator(),
        });
        if (sources is not null)
            result.Sources.Add(sources.Select(static source => source.Clone()));
        var selected = selector.SelectorCase == ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation
            ? selector.NyxIdOperation
            : null;
        _logger.LogInformation(
            "NyxID workflow capability readiness inspection blocked. executionMode={ExecutionMode}, status={Status}, code={Code}, selectedUserServiceId={SelectedUserServiceId}, selectedEndpointId={SelectedEndpointId}, remediation={Remediation}",
            executionMode,
            status,
            code,
            selected?.UserServiceId ?? string.Empty,
            selected?.EndpointId ?? string.Empty,
            actionKind);
        return result;
    }

    private static string FormatBlockerCodes(IEnumerable<ExternalCapabilityBlocker> blockers)
    {
        var codes = blockers
            .Select(static blocker => blocker.Code?.Trim())
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();
        return codes.Length == 0 ? "<none>" : string.Join(",", codes);
    }

    private string TrustedLocator() =>
        string.IsNullOrWhiteSpace(_options.BaseUrl) ? string.Empty : _options.BaseUrl.TrimEnd('/');

    private sealed record NyxIdMcpCatalogSnapshot(
        IReadOnlyList<NyxIdMcpService> Services,
        IReadOnlyList<ExternalCapabilitySourceStamp> Sources,
        IReadOnlyList<NyxIdMcpCatalogIssue> Issues,
        ExternalWorkflowCapabilityDiscoveryResult Discovery,
        bool AccessDenied,
        bool SourceUnavailable);
}
