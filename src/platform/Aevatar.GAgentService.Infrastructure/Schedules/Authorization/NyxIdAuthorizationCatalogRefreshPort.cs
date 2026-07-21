using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
{
    public static readonly TimeSpan CatalogFreshnessLifetime = TimeSpan.FromMinutes(15);

    private const string OrganizationOwnerNotSupportedFailureCode =
        "nyxid_catalog_organization_owner_not_supported";
    private const string CatalogMismatchFailureCode = "nyxid_scope_plan_catalog_mismatch";

    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshPort> _logger;

    public NyxIdAuthorizationCatalogRefreshPort(
        INyxIdAuthorizationCatalogCommandPort commandPort,
        INyxIdApiClientFactory nyxClientFactory,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        return RefreshAsync(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = verifiedOwnerSubject.Trim(),
        }, bearerToken, ct);
    }

    public async Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (!string.Equals(
                owner.Authority?.Trim(),
                NyxIdAuthorizationAuthorities.NyxId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NyxID authorization catalog owner authority is not supported.");
        }
        if (owner.OwnerKind != AuthorizationOwnerKind.Personal)
        {
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported,
                OrganizationOwnerNotSupportedFailureCode);
        }
        if (string.IsNullOrWhiteSpace(owner.OwnerSubject))
            throw new InvalidOperationException("NyxID authorization catalog owner subject is required.");

        var normalizedOwner = owner.Clone();
        normalizedOwner.Authority = NyxIdAuthorizationAuthorities.NyxId;
        normalizedOwner.OwnerSubject = owner.OwnerSubject.Trim();
        var refreshId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow();
        await _commandPort.ActivateAsync(normalizedOwner, startedAt, ct);
        await _commandPort.BeginRefreshAsync(normalizedOwner, refreshId, startedAt, ct);

        var client = _nyxClientFactory.CreateClient();
        var inventoryResponse = await client.ListUserServicesAsync(bearerToken, ct);
        var inventoryResult = NyxIdApiAccessResponseParser.ParseUserServices(inventoryResponse);
        if (!inventoryResult.Succeeded)
        {
            return await HandleFailureAsync(
                normalizedOwner,
                refreshId,
                inventoryResult.Failure,
                ct);
        }

        var eligibleServices = inventoryResult.Value!.Services
            .Where(IsEligible)
            .OrderBy(static service => service.Id, StringComparer.Ordinal)
            .ToArray();
        var selectedServiceIds = eligibleServices.Select(static service => service.Id).ToArray();
        var scopePlanResponse = await client.PlanApiKeyScopeAsync(
            bearerToken,
            selectedServiceIds,
            targetOrganizationId: null,
            ct);
        var scopePlanResult = NyxIdApiAccessResponseParser.ParseScopePlan(scopePlanResponse);
        if (!scopePlanResult.Succeeded)
        {
            return await HandleFailureAsync(
                normalizedOwner,
                refreshId,
                scopePlanResult.Failure,
                ct);
        }

        var scopePlan = scopePlanResult.Value!;
        if (!MatchesPersonalCatalog(scopePlan, normalizedOwner, selectedServiceIds))
        {
            return await InvalidateUnstableAsync(
                normalizedOwner,
                refreshId,
                CatalogMismatchFailureCode,
                ct);
        }

        var inventoryById = eligibleServices.ToDictionary(static service => service.Id, StringComparer.Ordinal);
        var services = scopePlan.Services
            .Select(grant => MapServiceEvidence(inventoryById[grant.UserServiceId], grant))
            .ToArray();
        var observedAt = _timeProvider.GetUtcNow();
        var contentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(normalizedOwner, services);
        await _commandPort.ObserveAsync(new NyxIdAuthorizationCatalogObservation(
            normalizedOwner,
            refreshId,
            observedAt,
            observedAt.Add(CatalogFreshnessLifetime),
            scopePlan.ContractVersion,
            scopePlan.PolicyVersion,
            scopePlan.EvaluatedAtUtc,
            contentDigest,
            services), ct);

        return NyxIdAuthorizationCatalogRefreshResult.Observed;
    }

    private async Task<NyxIdAuthorizationCatalogRefreshResult> HandleFailureAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        NyxIdApiAccessFailure? failure,
        CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(failure?.Code)
            ? "nyxid_catalog_refresh_failed"
            : failure.Code;
        var now = _timeProvider.GetUtcNow();
        if (failure?.Kind is NyxIdApiAccessFailureKind.Unauthorized or NyxIdApiAccessFailureKind.Forbidden)
        {
            await _commandPort.InvalidateRefreshAsync(owner, refreshId, now, code, ct);
            _logger.LogWarning(
                "NyxID authorization catalog access was denied. ownerKind={OwnerKind} failureCode={FailureCode}",
                owner.OwnerKind,
                code);
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.AccessDenied,
                code);
        }

        if (failure?.Kind is NyxIdApiAccessFailureKind.RateLimited or
            NyxIdApiAccessFailureKind.Transport or
            NyxIdApiAccessFailureKind.Transient)
        {
            await _commandPort.RecordRefreshFailureAsync(owner, refreshId, now, code, ct);
            _logger.LogWarning(
                "NyxID authorization catalog refresh failed transiently. ownerKind={OwnerKind} failureCode={FailureCode}",
                owner.OwnerKind,
                code);
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Failed,
                code);
        }

        return await InvalidateUnstableAsync(owner, refreshId, code, ct);
    }

    private async Task<NyxIdAuthorizationCatalogRefreshResult> InvalidateUnstableAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        string code,
        CancellationToken ct)
    {
        await _commandPort.InvalidateRefreshAsync(
            owner,
            refreshId,
            _timeProvider.GetUtcNow(),
            code,
            ct);
        _logger.LogWarning(
            "NyxID authorization catalog response was unstable. ownerKind={OwnerKind} failureCode={FailureCode}",
            owner.OwnerKind,
            code);
        return new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
            code);
    }

    private static bool IsEligible(NyxIdUserService service) =>
        service.IsActive &&
        (service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal ||
         service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Organization &&
         service.CredentialSource.Allowed);

    private static bool MatchesPersonalCatalog(
        NyxIdApiKeyScopePlan scopePlan,
        AuthorizationOwnerIdentity owner,
        IReadOnlyList<string> selectedServiceIds) =>
        string.Equals(scopePlan.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) &&
        scopePlan.AuthenticatedActor == new NyxIdScopePlanPrincipal(
            owner.OwnerSubject,
            NyxIdScopePlanPrincipalKind.Personal) &&
        scopePlan.IntendedKeyOwner == new NyxIdScopePlanPrincipal(
            owner.OwnerSubject,
            NyxIdScopePlanPrincipalKind.Personal) &&
        scopePlan.AllowedServiceIds.SequenceEqual(selectedServiceIds, StringComparer.Ordinal) &&
        scopePlan.Services.Select(static service => service.UserServiceId)
            .SequenceEqual(selectedServiceIds, StringComparer.Ordinal);

    private static NyxIdAuthorizationServiceEvidence MapServiceEvidence(
        NyxIdUserService inventory,
        NyxIdScopePlanServiceGrant grant)
    {
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = inventory.Id,
            ServiceSlug = inventory.Slug,
            DisplayName = ResolveDisplayName(inventory),
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = grant.NodeGrant.Kind switch
            {
                NyxIdScopePlanNodeGrantKind.NotRequired => AuthorizationGrantRequirement.NotRequired,
                NyxIdScopePlanNodeGrantKind.Required => AuthorizationGrantRequirement.Required,
                _ => AuthorizationGrantRequirement.Unspecified,
            },
            ResourceOwner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = grant.ResourceOwner.Kind switch
                {
                    NyxIdScopePlanPrincipalKind.Personal => AuthorizationOwnerKind.Personal,
                    NyxIdScopePlanPrincipalKind.Organization => AuthorizationOwnerKind.Organization,
                    _ => AuthorizationOwnerKind.Unspecified,
                },
                OwnerSubject = grant.ResourceOwner.Id,
            },
        };
        service.NodeIds.Add(grant.NodeGrant.NodeIds);
        return service;
    }

    private static string ResolveDisplayName(NyxIdUserService service) =>
        Normalize(service.Label) ?? Normalize(service.CatalogServiceName) ?? service.Slug;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
