using System.Globalization;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Maps live NyxID user-service and OpenAPI responses into credential-free workflow capability
/// contracts. Results are never cached; exact UserService ids remain the NyxID authority key.
/// </summary>
public sealed class NyxIdExternalWorkflowCapabilitySource : IExternalWorkflowCapabilitySource
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    private readonly NyxIdApiClient _client;
    private readonly NyxIdToolOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly INyxIdAuthorizationCatalogQueryPort? _catalogQueryPort;
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
        _catalogQueryPort = catalogQueryPort;
        _logger = logger ?? NullLogger<NyxIdExternalWorkflowCapabilitySource>.Instance;
    }

    public ExternalWorkflowCapabilityRef.CapabilityOneofCase CapabilityKind =>
        ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService;

    public async Task<IReadOnlyList<ExternalWorkflowCapabilityDescriptor>> ListAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        var snapshot = await ReadServicesAsync(access, cancellationToken);
        if (snapshot.Services.Count == 0 && (snapshot.AccessDenied || snapshot.SourceUnavailable))
            return [];

        var descriptors = new List<ExternalWorkflowCapabilityDescriptor>();
        foreach (var service in snapshot.Services
                     .OrderBy(static item => item.UserServiceId, StringComparer.Ordinal))
        {
            var openApi = await ReadOpenApiAsync(service, cancellationToken);
            if (!openApi.Available)
                continue;

            foreach (var operation in openApi.Spec.AdmittedOperations()
                         .OrderBy(static item => item.OperationId, StringComparer.Ordinal)
                         .ThenBy(static item => item.Method, StringComparer.Ordinal)
                         .ThenBy(static item => item.PathTemplate, StringComparer.Ordinal))
            {
                descriptors.Add(BuildDescriptor(service, operation, openApi.Source));
            }
        }

        return descriptors;
    }

    public async Task<ExternalCapabilityReadiness> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(capability);

        if (capability.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService)
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SelectionRequired,
                "NYXID_CAPABILITY_REQUIRED",
                "Select an exact NyxID UserService operation.",
                ExternalCapabilityRemediationActionKind.SelectCapability,
                "Select capability");
        }

        var snapshot = await ReadServicesAsync(access, cancellationToken);
        var selected = capability.NyxIdUserService;
        _logger.LogInformation(
            "NyxID workflow capability readiness inspection started. executionMode={ExecutionMode}, callerIdPresent={CallerIdPresent}, selectedUserServiceIdPresent={SelectedUserServiceIdPresent}, selectedServiceSlug={SelectedServiceSlug}, selectedOperationId={SelectedOperationId}, selectedHttpMethod={SelectedHttpMethod}, serviceCount={ServiceCount}, accessDenied={AccessDenied}, sourceUnavailable={SourceUnavailable}",
            executionMode,
            !string.IsNullOrWhiteSpace(access.CallerId),
            !string.IsNullOrWhiteSpace(selected.UserServiceId),
            selected.ServiceSlugSnapshot,
            selected.OperationId,
            selected.HttpMethod,
            snapshot.Services.Count,
            snapshot.AccessDenied,
            snapshot.SourceUnavailable);
        if (string.IsNullOrWhiteSpace(selected.UserServiceId))
        {
            var matches = snapshot.Services.Count(service =>
                string.Equals(
                    service.ServiceSlug,
                    selected.ServiceSlugSnapshot,
                    StringComparison.OrdinalIgnoreCase));
            _logger.LogInformation(
                "NyxID workflow capability exact service selection required. executionMode={ExecutionMode}, selectedServiceSlug={SelectedServiceSlug}, selectedOperationId={SelectedOperationId}, matchingServiceCount={MatchingServiceCount}, visibleServiceCount={VisibleServiceCount}",
                executionMode,
                selected.ServiceSlugSnapshot,
                selected.OperationId,
                matches,
                snapshot.Services.Count);
            if (matches == 0)
            {
                var sourceFailure = BuildSnapshotFailure(capability, executionMode, snapshot);
                if (sourceFailure is not null)
                    return sourceFailure;

                return Failure(
                    capability,
                    executionMode,
                    ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                    "USER_SERVICE_NOT_VISIBLE",
                    "No caller-visible NyxID UserService matches the selected capability.",
                    ExternalCapabilityRemediationActionKind.RegisterService,
                    "Register service",
                    snapshot.Sources);
            }

            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SelectionRequired,
                "EXACT_USER_SERVICE_SELECTION_REQUIRED",
                "Select one exact NyxID UserService instance.",
                ExternalCapabilityRemediationActionKind.SelectCapability,
                "Select service instance",
                snapshot.Sources);
        }

        var service = snapshot.Services.FirstOrDefault(item =>
            string.Equals(item.UserServiceId, selected.UserServiceId, StringComparison.Ordinal));
        if (service is null)
        {
            var sourceFailure = BuildSnapshotFailure(capability, executionMode, snapshot);
            if (sourceFailure is not null)
                return sourceFailure;

            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                "USER_SERVICE_NOT_VISIBLE",
                "The selected NyxID UserService is not visible to the current caller.",
                ExternalCapabilityRemediationActionKind.RegisterService,
                "Register service",
                snapshot.Sources);
        }

        if (!string.Equals(service.ServiceSlug, selected.ServiceSlugSnapshot, StringComparison.Ordinal))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "SERVICE_ROUTE_SNAPSHOT_DRIFT",
                "The selected NyxID service routing snapshot has changed.",
                ExternalCapabilityRemediationActionKind.SelectCapability,
                "Select current service instance",
                [service.Source]);
        }

        if (IsStale(service.Source))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_STALE",
                "The NyxID service capability source is stale.",
                ExternalCapabilityRemediationActionKind.RefreshSource,
                "Refresh NyxID capabilities",
                [service.Source]);
        }

        var serviceFailure = EvaluateServiceReadiness(capability, executionMode, service);
        if (serviceFailure is not null)
            return serviceFailure;

        var openApi = await ReadOpenApiAsync(service, cancellationToken);
        if (!openApi.Available)
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.EndpointContractRequired,
                "OPENAPI_CONTRACT_REQUIRED",
                "The selected NyxID UserService has no usable published endpoint contract.",
                ExternalCapabilityRemediationActionKind.PublishEndpointContract,
                "Publish endpoint contract",
                [service.Source, openApi.Source]);
        }

        var admitted = openApi.Spec.AdmittedOperations().FirstOrDefault(operation =>
            string.Equals(operation.OperationId, selected.OperationId, StringComparison.Ordinal));
        if (admitted is null)
        {
            var declared = openApi.Spec.Operations.Any(operation =>
                string.Equals(operation.OperationId, selected.OperationId, StringComparison.Ordinal));
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                declared ? "OPERATION_NOT_ALLOWLISTED" : "OPERATION_NOT_FOUND",
                "Select an operation published through the x-aevatar-tool allowlist.",
                ExternalCapabilityRemediationActionKind.SelectOperation,
                "Select operation",
                [service.Source, openApi.Source]);
        }

        if (!string.Equals(admitted.Method, selected.HttpMethod, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(admitted.PathTemplate, selected.PathTemplate, StringComparison.Ordinal))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "NYXID_OPERATION_ROUTE_DRIFT",
                "The selected NyxID operation route has changed.",
                ExternalCapabilityRemediationActionKind.SelectOperation,
                "Select current operation",
                [service.Source, openApi.Source]);
        }

        var currentDigest = BuildOperationContractDigest(admitted);
        if (!string.Equals(currentDigest, selected.ContractDigest, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "NYXID_OPERATION_CONTRACT_DRIFT",
                "The selected NyxID operation contract has changed.",
                ExternalCapabilityRemediationActionKind.SelectOperation,
                "Select current operation",
                [service.Source, openApi.Source]);
        }

        ExternalCapabilityReadiness? durableReadiness = null;
        if (executionMode == ExternalCapabilityExecutionMode.Durable)
        {
            durableReadiness = await InspectDurableAuthorizationAsync(
                access,
                capability,
                cancellationToken);
            if (durableReadiness.Status != ExternalCapabilityReadinessStatus.Ready)
            {
                _logger.LogInformation(
                    "NyxID workflow capability durable authorization inspection blocked. status={Status}, blockerCodes={BlockerCodes}, selectedUserServiceId={SelectedUserServiceId}, selectedOperationId={SelectedOperationId}",
                    durableReadiness.Status,
                    FormatBlockerCodes(durableReadiness.Blockers),
                    selected.UserServiceId,
                    selected.OperationId);
                durableReadiness.Sources.Add(service.Source);
                durableReadiness.Sources.Add(openApi.Source);
                return durableReadiness;
            }
        }

        var ready = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedCapability = capability.Clone(),
        };
        ready.Sources.Add(service.Source);
        ready.Sources.Add(openApi.Source);
        if (durableReadiness is not null)
            ready.Sources.Add(durableReadiness.Sources);
        _logger.LogInformation(
            "NyxID workflow capability readiness inspection completed. executionMode={ExecutionMode}, selectedUserServiceId={SelectedUserServiceId}, selectedOperationId={SelectedOperationId}, status={Status}",
            executionMode,
            selected.UserServiceId,
            selected.OperationId,
            ready.Status);
        return ready;
    }

    private async Task<ExternalCapabilityReadiness> InspectDurableAuthorizationAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilityRef capability,
        CancellationToken cancellationToken)
    {
        if (_catalogQueryPort is null || string.IsNullOrWhiteSpace(access.CallerId))
            return DurableAuthorizationUnavailable(capability);

        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = access.CallerId,
        };
        NyxIdAuthorizationCatalogSnapshot? snapshot;
        try
        {
            snapshot = await _catalogQueryPort.GetAsync(owner, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DurableAuthorizationUnavailable(capability);
        }

        if (!IsUsableDurableCatalog(snapshot, owner))
            return DurableAuthorizationUnavailable(capability);

        var serviceId = capability.NyxIdUserService.UserServiceId;
        var matches = snapshot!.Services
            .Where(service => string.Equals(service.UserServiceId, serviceId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1 ||
            !IsUsableDurableGrant(matches[0], capability.NyxIdUserService))
        {
            return DurableAuthorizationUnavailable(capability);
        }

        var ready = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedCapability = capability.Clone(),
        };
        ready.Sources.Add(new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
            SourceId = NyxIdAuthorizationCatalogActorIds.Build(owner),
            SourceVersion = snapshot.StateVersion,
            ObservedAt = Timestamp.FromDateTimeOffset(snapshot.ObservedAtUtc),
            FreshUntil = Timestamp.FromDateTimeOffset(snapshot.FreshUntilUtc),
            ContentDigest = snapshot.ContentDigest,
        });
        return ready;
    }

    private bool IsUsableDurableCatalog(
        NyxIdAuthorizationCatalogSnapshot? snapshot,
        AuthorizationOwnerIdentity expectedOwner)
    {
        if (snapshot is null ||
            snapshot.StateVersion <= 0 ||
            !snapshot.Activated ||
            snapshot.Invalidated ||
            snapshot.Cleaned ||
            !OwnerEquals(snapshot.Owner, expectedOwner) ||
            snapshot.ObservedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContractVersion) ||
            string.IsNullOrWhiteSpace(snapshot.PolicyVersion) ||
            snapshot.EvaluatedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.ContentDigest) ||
            !string.Equals(
                snapshot.ContentDigest,
                NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(snapshot.Owner, snapshot.Services),
                StringComparison.Ordinal))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        return snapshot.ObservedAtUtc <= now && snapshot.FreshUntilUtc > now;
    }

    private static bool IsUsableDurableGrant(
        NyxIdAuthorizationServiceEvidence service,
        NyxIdUserServiceCapabilityRef selected)
    {
        if (string.IsNullOrWhiteSpace(service.UserServiceId) ||
            !string.Equals(service.UserServiceId, service.UserServiceId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(service.UserServiceId, selected.UserServiceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(service.ServiceSlug) ||
            !string.Equals(service.ServiceSlug, service.ServiceSlug.Trim(), StringComparison.Ordinal) ||
            !string.Equals(service.ServiceSlug, selected.ServiceSlugSnapshot, StringComparison.Ordinal) ||
            service.Access != NyxIdAuthorizationAccess.Permitted ||
            service.NodeGrantRequirement is not (
                AuthorizationGrantRequirement.Required or AuthorizationGrantRequirement.NotRequired) ||
            !IsNormalizedOwner(service.ResourceOwner))
        {
            return false;
        }

        string? previousNodeId = null;
        foreach (var nodeId in service.NodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId) ||
                !string.Equals(nodeId, nodeId.Trim(), StringComparison.Ordinal) ||
                previousNodeId is not null && string.CompareOrdinal(previousNodeId, nodeId) >= 0)
            {
                return false;
            }
            previousNodeId = nodeId;
        }

        return service.NodeGrantRequirement == AuthorizationGrantRequirement.Required
            ? service.NodeIds.Count > 0
            : service.NodeIds.Count == 0;
    }

    private static bool IsNormalizedOwner(AuthorizationOwnerIdentity? owner) =>
        owner is not null &&
        string.Equals(owner.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) &&
        owner.OwnerKind != AuthorizationOwnerKind.Unspecified &&
        System.Enum.IsDefined(owner.OwnerKind) &&
        !string.IsNullOrWhiteSpace(owner.OwnerSubject) &&
        string.Equals(owner.OwnerSubject, owner.OwnerSubject.Trim(), StringComparison.Ordinal);

    private static bool OwnerEquals(
        AuthorizationOwnerIdentity? left,
        AuthorizationOwnerIdentity right) =>
        left is not null &&
        string.Equals(left.Authority, right.Authority, StringComparison.Ordinal) &&
        left.OwnerKind == right.OwnerKind &&
        string.Equals(left.OwnerSubject, right.OwnerSubject, StringComparison.Ordinal);

    private ExternalCapabilityReadiness DurableAuthorizationUnavailable(
        ExternalWorkflowCapabilityRef capability) =>
        Failure(
            capability,
            ExternalCapabilityExecutionMode.Durable,
            ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
            "DURABLE_AUTHORIZATION_UNAVAILABLE",
            "The current NyxID authorization catalog does not prove the complete durable grant.",
            ExternalCapabilityRemediationActionKind.UseInteractiveExecution,
            "Use interactive execution");

    private ExternalCapabilityReadiness? BuildSnapshotFailure(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode,
        NyxIdServicesSnapshot snapshot)
    {
        if (snapshot.AccessDenied)
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "NYXID_CALLER_ACCESS_REQUIRED",
                "The current caller cannot read all NyxID service capability sources.",
                ExternalCapabilityRemediationActionKind.RequestAccess,
                "Open NyxID",
                snapshot.Sources);
        }

        return snapshot.SourceUnavailable
            ? Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_UNAVAILABLE",
                "NyxID service capability facts are currently unavailable.",
                ExternalCapabilityRemediationActionKind.RefreshSource,
                "Refresh NyxID capabilities",
                snapshot.Sources)
            : null;
    }

    private ExternalCapabilityReadiness? EvaluateServiceReadiness(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode,
        NyxIdUserServiceSnapshot service)
    {
        if (service.Allowed == false)
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "USER_SERVICE_ACCESS_DENIED",
                "The current caller is not allowed to use the selected NyxID UserService.",
                ExternalCapabilityRemediationActionKind.RequestAccess,
                "Request service access",
                [service.Source]);
        }

        if (!HasCompletePublishedReadinessFacts(service))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SERVICE_FACTS_INCOMPLETE",
                "NyxID did not publish all facts required to admit the selected UserService.",
                ExternalCapabilityRemediationActionKind.RefreshSource,
                "Refresh NyxID capabilities",
                [service.Source]);
        }

        if (!IsKnownServiceStatus(service.Status))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SERVICE_STATUS_UNKNOWN",
                "NyxID published an unrecognized status for the selected UserService.",
                ExternalCapabilityRemediationActionKind.RefreshSource,
                "Refresh NyxID capabilities",
                [service.Source]);
        }

        if (service.IsActive != true || !IsActiveServiceStatus(service.Status))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "USER_SERVICE_NOT_ACTIVE",
                "The selected NyxID UserService is not active.",
                ExternalCapabilityRemediationActionKind.ConnectCredential,
                "Connect service credential",
                [service.Source]);
        }

        if (service.Connected == false && service.RequiresConnection == false)
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SERVICE_FACTS_INCONSISTENT",
                "NyxID published contradictory connection facts for the selected UserService.",
                ExternalCapabilityRemediationActionKind.RefreshSource,
                "Refresh NyxID capabilities",
                [service.Source]);
        }

        if (service.RequiresConnection == true && service.Connected != true)
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "CREDENTIAL_CONNECTION_REQUIRED",
                "The selected NyxID service credential or connection is not ready.",
                ExternalCapabilityRemediationActionKind.ConnectCredential,
                "Connect service credential",
                [service.Source]);
        }

        if (service.RequiresNode == true &&
            (service.HasNodeBinding != true || string.IsNullOrWhiteSpace(service.NodeId)))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.NodeBindingRequired,
                "NODE_BINDING_REQUIRED",
                "The selected NyxID UserService requires a Node binding.",
                ExternalCapabilityRemediationActionKind.BindNode,
                "Bind Node",
                [service.Source]);
        }

        if (!string.IsNullOrWhiteSpace(service.NodeId) && !IsOnlineNodeStatus(service.NodeStatus))
        {
            return Failure(
                capability,
                executionMode,
                ExternalCapabilityReadinessStatus.NodeUnavailable,
                "NODE_UNAVAILABLE",
                "The Node bound to the selected NyxID UserService is unavailable.",
                ExternalCapabilityRemediationActionKind.RestoreNode,
                "Restore Node",
                [service.Source]);
        }

        return null;
    }

    private async Task<NyxIdServicesSnapshot> ReadServicesAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(access.NyxIdCallerBearerToken) ||
            string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return NyxIdServicesSnapshot.Denied;
        }

        var credentials = new List<(string Token, string SourceSuffix)>
        {
            (access.NyxIdCallerBearerToken, "caller"),
        };
        if (!string.IsNullOrWhiteSpace(access.NyxIdOrganizationBearerToken) &&
            !string.Equals(
                access.NyxIdOrganizationBearerToken,
                access.NyxIdCallerBearerToken,
                StringComparison.Ordinal))
        {
            credentials.Add((access.NyxIdOrganizationBearerToken, "organization"));
        }

        var services = new Dictionary<string, NyxIdUserServiceSnapshot>(StringComparer.Ordinal);
        var sources = new List<ExternalCapabilitySourceStamp>();
        var accessDenied = false;
        var sourceUnavailable = false;
        foreach (var credential in credentials)
        {
            var response = await _client.ListServicesAsync(credential.Token, cancellationToken);
            var parsed = ParseServicesResponse(response, credential.Token, credential.SourceSuffix);
            accessDenied |= parsed.AccessDenied;
            sourceUnavailable |= parsed.SourceUnavailable;
            if (parsed.Source is not null)
                sources.Add(parsed.Source);
            foreach (var service in parsed.Services)
                services.TryAdd(service.UserServiceId, service);
        }

        return new NyxIdServicesSnapshot(
            services.Values.ToArray(),
            sources,
            accessDenied,
            sourceUnavailable);
    }

    private ParsedServicesResponse ParseServicesResponse(
        string response,
        string bearerToken,
        string sourceSuffix)
    {
        var now = _timeProvider.GetUtcNow();
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var source = BuildUserServicesSourceStamp(root, response, sourceSuffix, now);
            if (IsErrorEnvelope(root, out var status))
            {
                return new ParsedServicesResponse(
                    [],
                    source,
                    AccessDenied: status is 401 or 403,
                    SourceUnavailable: status is not (401 or 403));
            }

            if (!HasServiceCollection(root))
                return new ParsedServicesResponse([], source, false, true);

            var services = new List<NyxIdUserServiceSnapshot>();
            foreach (var entry in EnumerateServiceEntries(root))
            {
                var service = ParseService(entry, bearerToken, source);
                if (service is null)
                    return new ParsedServicesResponse([], source, false, true);
                services.Add(service);
            }

            return new ParsedServicesResponse(services, source, false, false);
        }
        catch (JsonException)
        {
            return new ParsedServicesResponse([], null, false, true);
        }
    }

    private ExternalCapabilitySourceStamp BuildUserServicesSourceStamp(
        JsonElement root,
        string response,
        string sourceSuffix,
        DateTimeOffset now)
    {
        var observedAt = ReadTimestamp(root, "observed_at", "observedAt") ?? now;
        var freshUntil = ReadTimestamp(root, "fresh_until", "freshUntil") ?? now.Add(FreshnessWindow);
        return new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = $"nyxid-user-services:{sourceSuffix}",
            SourceVersion = ReadLong(root, "source_version", "sourceVersion", "version") ?? 0,
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(freshUntil),
            ContentDigest = ExternalWorkflowCapabilityContractDigest.Compute(response),
        };
    }

    private async Task<NyxIdOpenApiSnapshot> ReadOpenApiAsync(
        NyxIdUserServiceSnapshot service,
        CancellationToken cancellationToken)
    {
        var response = await _client.GetProxyServiceOpenApiAsync(
            service.BearerToken,
            service.UserServiceId,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var source = new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdOpenApi,
            SourceId = service.UserServiceId,
            ObservedAt = Timestamp.FromDateTimeOffset(now),
            FreshUntil = Timestamp.FromDateTimeOffset(now.Add(FreshnessWindow)),
            ContentDigest = ExternalWorkflowCapabilityContractDigest.Compute(response),
        };

        try
        {
            using var document = JsonDocument.Parse(response);
            if (IsErrorEnvelope(document.RootElement, out _))
                return new NyxIdOpenApiSnapshot(false, ConnectedServiceSpecParseResult.Empty, source);
        }
        catch (JsonException)
        {
            return new NyxIdOpenApiSnapshot(false, ConnectedServiceSpecParseResult.Empty, source);
        }

        var spec = OpenApiToolSpecParser.Parse(response);
        var available = spec.Operations.Count > 0 || spec.ServiceMarker is not null;
        return new NyxIdOpenApiSnapshot(available, spec, source);
    }

    private NyxIdUserServiceSnapshot? ParseService(
        JsonElement element,
        string bearerToken,
        ExternalCapabilitySourceStamp source)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var serviceId = ReadString(element, "id", "user_service_id", "userServiceId", "service_id", "serviceId");
        var slug = ReadString(
            element,
            "slug",
            "catalog_service_slug",
            "catalogServiceSlug",
            "service_slug",
            "serviceSlug");
        if (string.IsNullOrWhiteSpace(serviceId) || string.IsNullOrWhiteSpace(slug))
            return null;

        var credential = ReadObject(element, "credential_source", "credentialSource");
        var node = ReadObject(element, "node", "node_binding", "nodeBinding");
        return new NyxIdUserServiceSnapshot
        {
            UserServiceId = serviceId,
            ServiceSlug = slug,
            DisplayName = ReadString(
                              element,
                              "label",
                              "display_name",
                              "displayName",
                              "catalog_service_name",
                              "catalogServiceName")
                          ?? slug,
            Status = ReadString(element, "status") ?? string.Empty,
            IsActive = ReadBool(element, "is_active", "isActive"),
            CredentialSourceType = ReadString(credential, "type") ?? string.Empty,
            Allowed = ReadBool(element, "allowed") ?? ReadBool(credential, "allowed"),
            RequiresConnection = ReadBool(element, "requires_connection", "requiresConnection"),
            Connected = ReadBool(element, "connected") ?? ReadBool(credential, "connected"),
            RequiresNode = ReadBool(element, "requires_node", "requiresNode"),
            HasNodeBinding = ReadBool(element, "has_node_binding", "hasNodeBinding"),
            NodeId = ReadString(element, "node_id", "nodeId") ?? ReadString(node, "id", "node_id", "nodeId") ?? string.Empty,
            NodeStatus = ReadString(element, "node_status", "nodeStatus") ?? ReadString(node, "status") ?? string.Empty,
            BearerToken = bearerToken,
            Source = source.Clone(),
        };
    }

    private static ExternalWorkflowCapabilityDescriptor BuildDescriptor(
        NyxIdUserServiceSnapshot service,
        ConnectedServiceToolOperation operation,
        ExternalCapabilitySourceStamp source)
    {
        var readOnly = operation.Marker?.ReadOnly ?? IsSafeMethod(operation.Method);
        var destructive = IsSafeMethod(operation.Method)
            ? false
            : operation.Marker?.Destructive ?? true;
        return new ExternalWorkflowCapabilityDescriptor
        {
            Capability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = service.UserServiceId,
                    ServiceSlugSnapshot = service.ServiceSlug,
                    OperationId = operation.OperationId,
                    HttpMethod = operation.Method,
                    PathTemplate = operation.PathTemplate,
                    ContractDigest = BuildOperationContractDigest(operation),
                },
            },
            DisplayName = $"{service.DisplayName} / {operation.OperationId}",
            ReadOnly = readOnly,
            Destructive = destructive,
            Source = source.Clone(),
        };
    }

    private ExternalCapabilityReadiness Failure(
        ExternalWorkflowCapabilityRef capability,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string safeMessage,
        ExternalCapabilityRemediationActionKind actionKind,
        string actionLabel,
        IEnumerable<ExternalCapabilitySourceStamp>? sources = null)
    {
        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = status,
            SelectedCapability = capability.Clone(),
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
        var selected = capability.CapabilityCase == ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService
            ? capability.NyxIdUserService
            : null;
        _logger.LogInformation(
            "NyxID workflow capability readiness inspection blocked. executionMode={ExecutionMode}, status={Status}, code={Code}, selectedUserServiceId={SelectedUserServiceId}, selectedServiceSlug={SelectedServiceSlug}, selectedOperationId={SelectedOperationId}, remediation={Remediation}",
            executionMode,
            status,
            code,
            selected?.UserServiceId ?? string.Empty,
            selected?.ServiceSlugSnapshot ?? string.Empty,
            selected?.OperationId ?? string.Empty,
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

    private bool IsStale(ExternalCapabilitySourceStamp source) =>
        source.FreshUntil is null || source.FreshUntil.ToDateTimeOffset() < _timeProvider.GetUtcNow();

    private static string BuildOperationContractDigest(ConnectedServiceToolOperation operation)
    {
        var parameterContracts = operation.Parameters
            .OrderBy(static parameter => parameter.In)
            .ThenBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .Select(parameter => string.Join(
                "\n",
                parameter.In.ToString(),
                parameter.Name,
                parameter.Required.ToString(CultureInfo.InvariantCulture),
                parameter.Schema?.ToJsonString() ?? string.Empty,
                parameter.Description ?? string.Empty));
        return ExternalWorkflowCapabilityContractDigest.Compute(
            new string?[]
            {
                "nyxid-openapi-operation.v1",
                operation.OperationId,
                operation.Method.ToUpperInvariant(),
                operation.PathTemplate,
                operation.Marker?.Name,
                operation.Marker?.ReadOnly?.ToString(CultureInfo.InvariantCulture),
                operation.Marker?.Destructive?.ToString(CultureInfo.InvariantCulture),
                operation.RequestBodyRequired.ToString(CultureInfo.InvariantCulture),
                operation.RequestBodySchema?.ToJsonString(),
            }.Concat(parameterContracts));
    }

    private static bool IsSafeMethod(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownServiceStatus(string? value) =>
        value?.Trim().ToLowerInvariant() is
            "active" or "pending_auth" or "expired" or "revoked" or "failed" or "refresh_failed";

    private static bool IsActiveServiceStatus(string? value) =>
        value?.Trim().Equals("active", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsOnlineNodeStatus(string? value) =>
        value?.Trim().Equals("online", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasCompletePublishedReadinessFacts(NyxIdUserServiceSnapshot service)
    {
        var sourceType = service.CredentialSourceType.Trim().ToLowerInvariant();
        if (sourceType is not ("personal" or "org") ||
            (sourceType == "org" && service.Allowed is null) ||
            string.IsNullOrWhiteSpace(service.Status) ||
            service.IsActive is null ||
            service.Connected is null ||
            service.RequiresConnection is null ||
            service.HasNodeBinding is null)
        {
            return false;
        }

        var hasNodeId = !string.IsNullOrWhiteSpace(service.NodeId);
        if (service.HasNodeBinding != hasNodeId)
            return false;
        return !hasNodeId || !string.IsNullOrWhiteSpace(service.NodeStatus);
    }

    private static bool IsErrorEnvelope(JsonElement root, out int status)
    {
        status = 0;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind is not (JsonValueKind.True or JsonValueKind.String))
        {
            return false;
        }

        if (root.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var parsed))
            status = parsed;
        return true;
    }

    private static IEnumerable<JsonElement> EnumerateServiceEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var name in new[] { "keys", "services", "items", "data" })
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in array.EnumerateArray())
                yield return item;
        }
    }

    private static bool HasServiceCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return true;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        return new[] { "keys", "services", "items", "data" }
            .Any(name => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array);
    }

    private static JsonElement ReadObject(JsonElement owner, params string[] names)
    {
        foreach (var name in names)
        {
            if (owner.ValueKind == JsonValueKind.Object &&
                owner.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        return default;
    }

    private static string? ReadString(JsonElement owner, params string[] names)
    {
        foreach (var name in names)
        {
            if (owner.ValueKind != JsonValueKind.Object ||
                !owner.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static bool? ReadBool(JsonElement owner, params string[] names)
    {
        foreach (var name in names)
        {
            if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static long? ReadLong(JsonElement owner, params string[] names)
    {
        foreach (var name in names)
        {
            if (owner.ValueKind == JsonValueKind.Object &&
                owner.TryGetProperty(name, out var value) &&
                value.TryGetInt64(out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement owner, params string[] names)
    {
        var text = ReadString(owner, names);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed class NyxIdUserServiceSnapshot
    {
        public required string UserServiceId { get; init; }
        public required string ServiceSlug { get; init; }
        public required string DisplayName { get; init; }
        public required string Status { get; init; }
        public bool? IsActive { get; init; }
        public required string CredentialSourceType { get; init; }
        public bool? Allowed { get; init; }
        public bool? RequiresConnection { get; init; }
        public bool? Connected { get; init; }
        public bool? RequiresNode { get; init; }
        public bool? HasNodeBinding { get; init; }
        public required string NodeId { get; init; }
        public required string NodeStatus { get; init; }
        public required string BearerToken { get; init; }
        public required ExternalCapabilitySourceStamp Source { get; init; }
    }

    private sealed record ParsedServicesResponse(
        IReadOnlyList<NyxIdUserServiceSnapshot> Services,
        ExternalCapabilitySourceStamp? Source,
        bool AccessDenied,
        bool SourceUnavailable);

    private sealed record NyxIdServicesSnapshot(
        IReadOnlyList<NyxIdUserServiceSnapshot> Services,
        IReadOnlyList<ExternalCapabilitySourceStamp> Sources,
        bool AccessDenied,
        bool SourceUnavailable)
    {
        public static NyxIdServicesSnapshot Denied { get; } = new([], [], true, false);
    }

    private sealed record NyxIdOpenApiSnapshot(
        bool Available,
        ConnectedServiceSpecParseResult Spec,
        ExternalCapabilitySourceStamp Source);
}
