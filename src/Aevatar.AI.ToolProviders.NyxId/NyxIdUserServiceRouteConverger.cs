using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// A typed boolean requirement for a NyxID UserService route. Unspecified fields are left intact.
/// </summary>
public enum NyxIdUserServiceBooleanRequirement
{
    Unspecified = 0,
    Disabled = 1,
    Enabled = 2,
}

/// <summary>
/// Capability-owned route requirements. Required delegation scopes are merged with the route's
/// existing scopes; they never replace unrelated grants.
/// </summary>
public sealed record NyxIdUserServiceRouteContract(
    NyxIdUserServiceBooleanRequirement ForwardAccessToken,
    NyxIdUserServiceBooleanRequirement InjectDelegationToken,
    IReadOnlyList<string> RequiredDelegationScopes)
{
    public bool IsSatisfiedBy(NyxIdUserService route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return Matches(ForwardAccessToken, route.ForwardAccessToken) &&
               Matches(InjectDelegationToken, route.InjectDelegationToken) &&
               GrantsEveryScope(route.DelegationTokenScope, RequiredDelegationScopes);
    }

    internal NyxIdUserServiceRouteMutationPlan Plan(NyxIdUserService route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var preservedScopes = MergeScopes(route.DelegationTokenScope, RequiredDelegationScopes);
        var before = new NyxIdUserServiceRouteValues(
            route.ForwardAccessToken,
            route.InjectDelegationToken,
            route.DelegationTokenScope);
        return new NyxIdUserServiceRouteMutationPlan(
            new NyxIdUserServiceRoutePatch(
                RequirementValue(ForwardAccessToken),
                RequirementValue(InjectDelegationToken),
                RequiredDelegationScopes.Count == 0
                    ? null
                    : string.Join(' ', preservedScopes)),
            before);
    }

    private static bool Matches(
        NyxIdUserServiceBooleanRequirement requirement,
        bool? actual) => requirement switch
    {
        NyxIdUserServiceBooleanRequirement.Unspecified => true,
        NyxIdUserServiceBooleanRequirement.Disabled => actual == false,
        NyxIdUserServiceBooleanRequirement.Enabled => actual == true,
        _ => false,
    };

    private static bool? RequirementValue(
        NyxIdUserServiceBooleanRequirement requirement) => requirement switch
    {
        NyxIdUserServiceBooleanRequirement.Unspecified => null,
        NyxIdUserServiceBooleanRequirement.Disabled => false,
        NyxIdUserServiceBooleanRequirement.Enabled => true,
        _ => throw new ArgumentOutOfRangeException(nameof(requirement)),
    };

    private static bool GrantsEveryScope(
        string? actual,
        IReadOnlyList<string> required)
    {
        var actualScopes = SplitScopes(actual).ToHashSet(StringComparer.Ordinal);
        return NormalizeRequiredScopes(required).All(actualScopes.Contains);
    }

    private static IReadOnlyList<string> MergeScopes(
        string? current,
        IReadOnlyList<string> required)
    {
        var merged = SplitScopes(current).ToList();
        foreach (var scope in NormalizeRequiredScopes(required))
        {
            if (!merged.Contains(scope, StringComparer.Ordinal))
                merged.Add(scope);
        }

        return merged;
    }

    private static IEnumerable<string> NormalizeRequiredScopes(
        IReadOnlyList<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return scopes.Select(static scope =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(scope);
                var normalized = scope.Trim();
                if (normalized.Any(char.IsWhiteSpace) || normalized.Any(char.IsControl))
                    throw new ArgumentException("A delegation scope must be one token.", nameof(scopes));
                return normalized;
            })
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> SplitScopes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal);
}

public sealed record NyxIdUserServiceRoutePatch(
    bool? ForwardAccessToken,
    bool? InjectDelegationToken,
    string? DelegationTokenScope);

internal sealed record NyxIdUserServiceRouteValues(
    bool? ForwardAccessToken,
    bool? InjectDelegationToken,
    string? DelegationTokenScope);

internal sealed record NyxIdUserServiceRouteMutationPlan(
    NyxIdUserServiceRoutePatch Patch,
    NyxIdUserServiceRouteValues Before)
{
    public bool IsVerifiedBy(NyxIdUserService route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.ForwardAccessToken ==
                   (Patch.ForwardAccessToken ?? Before.ForwardAccessToken) &&
               route.InjectDelegationToken ==
                   (Patch.InjectDelegationToken ?? Before.InjectDelegationToken) &&
               HasSameScopes(
                   route.DelegationTokenScope,
                   Patch.DelegationTokenScope ?? Before.DelegationTokenScope);
    }

    private static bool HasSameScopes(string? actual, string? expected)
    {
        var actualScopes = SplitScopes(actual).ToHashSet(StringComparer.Ordinal);
        return actualScopes.SetEquals(SplitScopes(expected));
    }

    private static IEnumerable<string> SplitScopes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal);
}

/// <summary>
/// Transient direct-human authority for changing an exact NyxID UserService route. It can only be
/// derived from an ingress credential explicitly marked as route-manageable and is never persisted
/// or rendered with its bearer token.
/// </summary>
public sealed class NyxIdUserServiceRouteMutationAuthority
{
    private readonly string _bearerToken;

    private NyxIdUserServiceRouteMutationAuthority(string bearerToken)
    {
        _bearerToken = bearerToken;
    }

    internal string BearerToken => _bearerToken;

    public static bool TryCreate(
        NyxIdCallerCredentialSelection? credential,
        out NyxIdUserServiceRouteMutationAuthority? authority)
    {
        authority = null;
        if (credential?.CanManageUserServices != true ||
            string.IsNullOrWhiteSpace(credential.SourceReadableUserBearerToken))
        {
            return false;
        }

        authority = new NyxIdUserServiceRouteMutationAuthority(
            credential.SourceReadableUserBearerToken.Trim());
        return true;
    }

    public override string ToString() =>
        $"{nameof(NyxIdUserServiceRouteMutationAuthority)} {{ BearerToken = [REDACTED] }}";
}

/// <summary>
/// Exact-ID join of NyxID's execution authority (/keys) and route configuration
/// (/user-services). A route is never inferred from slug equality alone.
/// </summary>
public sealed record NyxIdUserServiceAuthority(
    NyxIdUserService Route,
    NyxIdUserServiceKey Execution)
{
    public bool IsExecutionReady =>
        Route.IsActive &&
        Route.CredentialSource.Allowed &&
        Execution.IsActive &&
        Execution.Connected &&
        Execution.CredentialSource.Allowed &&
        SameAuthority(Route.CredentialSource, Execution.CredentialSource) &&
        string.Equals(Route.Slug, Execution.Slug, StringComparison.Ordinal) &&
        string.Equals(
            Route.CatalogServiceId,
            Execution.CatalogServiceId,
            StringComparison.Ordinal) &&
        (Execution.NodeId is null
            ? Execution.CredentialStatus == NyxIdUserServiceCredentialStatus.Active
            : Execution.NodeStatus == NyxIdUserServiceNodeStatus.Online);

    public bool CanManageRoute =>
        Execution.AutoConnected == false &&
        IsExecutionReady &&
        Route.CredentialSource.Kind switch
        {
            NyxIdUserServiceCredentialSourceKind.Personal => true,
            NyxIdUserServiceCredentialSourceKind.Organization =>
                Route.CredentialSource.OrganizationRole == NyxIdOrganizationRole.Admin &&
                Execution.CredentialSource.OrganizationRole == NyxIdOrganizationRole.Admin,
            _ => false,
        };

    internal bool HasSameIdentity(NyxIdUserServiceAuthority other) =>
        string.Equals(Route.Id, other.Route.Id, StringComparison.Ordinal) &&
        string.Equals(Route.Slug, other.Route.Slug, StringComparison.Ordinal) &&
        string.Equals(Route.CatalogServiceId, other.Route.CatalogServiceId, StringComparison.Ordinal) &&
        SameAuthority(Route.CredentialSource, other.Route.CredentialSource) &&
        string.Equals(Execution.Id, other.Execution.Id, StringComparison.Ordinal) &&
        string.Equals(Execution.Slug, other.Execution.Slug, StringComparison.Ordinal) &&
        string.Equals(
            Execution.CatalogServiceId,
            other.Execution.CatalogServiceId,
            StringComparison.Ordinal) &&
        string.Equals(
            Execution.CatalogServiceSlug,
            other.Execution.CatalogServiceSlug,
            StringComparison.Ordinal) &&
        Execution.AutoConnected == other.Execution.AutoConnected &&
        SameAuthority(Execution.CredentialSource, other.Execution.CredentialSource);

    private static bool SameAuthority(
        NyxIdUserServiceCredentialSource left,
        NyxIdUserServiceCredentialSource right) =>
        left.Kind == right.Kind &&
        string.Equals(left.OrganizationId, right.OrganizationId, StringComparison.Ordinal);
}

public sealed record NyxIdUserServiceAuthoritySnapshot(
    NyxIdApiAccessResult<NyxIdUserServices> Routes,
    NyxIdApiAccessResult<NyxIdUserServiceKeys> ExecutionInventory)
{
    public bool Succeeded => Routes.Succeeded && ExecutionInventory.Succeeded;

    public bool TryGetExact(string userServiceId, out NyxIdUserServiceAuthority? authority)
    {
        authority = null;
        if (!Succeeded || string.IsNullOrWhiteSpace(userServiceId))
            return false;

        var normalizedId = userServiceId.Trim();
        var route = Routes.Value!.Services.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, normalizedId, StringComparison.Ordinal));
        var execution = ExecutionInventory.Value!.Services.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, normalizedId, StringComparison.Ordinal));
        if (route is null || execution is null)
            return false;

        authority = new NyxIdUserServiceAuthority(route, execution);
        return true;
    }
}

public sealed class NyxIdUserServiceAuthorityReader(INyxIdApiClientFactory clientFactory)
{
    public async Task<NyxIdUserServiceAuthoritySnapshot> ReadAsync(
        string bearerToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var client = clientFactory.CreateClient();
        var routeResponse = await client.ListUserServicesAsync(
                bearerToken.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        var executionResponse = await client.ListServicesAsync(
                bearerToken.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        var routes = NyxIdApiAccessResponseParser.ParseUserServiceRoutes(routeResponse);
        var executionInventory = NyxIdApiAccessResponseParser.ParseUserServiceKeys(executionResponse);
        return new NyxIdUserServiceAuthoritySnapshot(
            ApplyExecutionProvenance(routes, executionInventory),
            executionInventory);
    }

    private static NyxIdApiAccessResult<NyxIdUserServices> ApplyExecutionProvenance(
        NyxIdApiAccessResult<NyxIdUserServices> routes,
        NyxIdApiAccessResult<NyxIdUserServiceKeys> executionInventory)
    {
        if (!routes.Succeeded || !executionInventory.Succeeded)
            return routes;

        var executionById = executionInventory.Value!.Services
            .ToDictionary(static service => service.Id, StringComparer.Ordinal);
        var normalizedRoutes = routes.Value!.Services
            .Select(route => executionById.TryGetValue(route.Id, out var execution)
                ? route with { AutoConnected = execution.AutoConnected == true }
                : route)
            .ToArray();
        return NyxIdApiAccessResult<NyxIdUserServices>.Success(
            new NyxIdUserServices(normalizedRoutes));
    }
}

public enum NyxIdUserServiceRouteConvergenceFailureKind
{
    None = 0,
    SourceUnavailable = 1,
    ExactServiceUnavailable = 2,
    ExecutionNotReady = 3,
    RouteNotWritable = 4,
    UpdateException = 5,
    PostconditionMismatch = 6,
    MutationRejected = 7,
}

public sealed record NyxIdUserServiceRouteConvergence(
    NyxIdUserServiceAuthoritySnapshot Snapshot,
    bool Attempted,
    bool Verified,
    NyxIdUserServiceRouteConvergenceFailureKind FailureKind =
        NyxIdUserServiceRouteConvergenceFailureKind.None,
    int HttpStatus = 0);

/// <summary>
/// Converges one exact caller-visible UserService route and verifies the postcondition against
/// fresh execution and route authority. Visibility alone never grants mutation authority.
/// </summary>
public sealed class NyxIdUserServiceRouteConverger(INyxIdApiClientFactory clientFactory)
{
    private readonly NyxIdUserServiceAuthorityReader _reader = new(clientFactory);

    public Task<NyxIdUserServiceAuthoritySnapshot> ReadAsync(
        NyxIdUserServiceRouteMutationAuthority authority,
        CancellationToken cancellationToken = default) =>
        _reader.ReadAsync(
            (authority ?? throw new ArgumentNullException(nameof(authority))).BearerToken,
            cancellationToken);

    public async Task<NyxIdUserServiceRouteConvergence> ConvergeAsync(
        NyxIdUserServiceRouteMutationAuthority authority,
        string exactUserServiceId,
        NyxIdUserServiceRouteContract contract,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var before = await _reader.ReadAsync(authority.BearerToken, cancellationToken)
            .ConfigureAwait(false);
        return await ConvergeAsync(
                authority,
                exactUserServiceId,
                contract,
                before,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<NyxIdUserServiceRouteConvergence> ConvergeAsync(
        NyxIdUserServiceRouteMutationAuthority authority,
        string exactUserServiceId,
        NyxIdUserServiceRouteContract contract,
        NyxIdUserServiceAuthoritySnapshot before,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactUserServiceId);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(before);
        if (!before.Succeeded)
            return Failure(before, NyxIdUserServiceRouteConvergenceFailureKind.SourceUnavailable);
        if (!before.TryGetExact(exactUserServiceId, out var current) || current is null)
            return Failure(before, NyxIdUserServiceRouteConvergenceFailureKind.ExactServiceUnavailable);
        if (!current.IsExecutionReady)
            return Failure(before, NyxIdUserServiceRouteConvergenceFailureKind.ExecutionNotReady);
        if (contract.IsSatisfiedBy(current.Route))
        {
            return new NyxIdUserServiceRouteConvergence(
                before,
                Attempted: false,
                Verified: true);
        }
        if (!current.CanManageRoute)
            return Failure(before, NyxIdUserServiceRouteConvergenceFailureKind.RouteNotWritable);

        var plan = contract.Plan(current.Route);
        var updateFailure = NyxIdUserServiceRouteConvergenceFailureKind.None;
        var updateHttpStatus = 0;
        try
        {
            var body = NyxIdUserServiceRouteUpdateAdapter.Serialize(plan.Patch);
            var response = await clientFactory.CreateClient()
                .UpdateServiceRouteResponseAsync(
                    authority.BearerToken,
                    current.Route.Id,
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.Succeeded)
            {
                updateFailure = NyxIdUserServiceRouteConvergenceFailureKind.MutationRejected;
                updateHttpStatus = response.HttpStatus;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            updateFailure = NyxIdUserServiceRouteConvergenceFailureKind.UpdateException;
        }

        var after = await _reader.ReadAsync(authority.BearerToken, cancellationToken)
            .ConfigureAwait(false);
        var verified = after.TryGetExact(current.Route.Id, out var refreshed) &&
                       refreshed is not null &&
                       refreshed.IsExecutionReady &&
                       current.HasSameIdentity(refreshed) &&
                       plan.IsVerifiedBy(refreshed.Route);
        return new NyxIdUserServiceRouteConvergence(
            after,
            Attempted: true,
            Verified: verified,
            FailureKind: verified
                ? NyxIdUserServiceRouteConvergenceFailureKind.None
                : updateFailure == NyxIdUserServiceRouteConvergenceFailureKind.None
                    ? NyxIdUserServiceRouteConvergenceFailureKind.PostconditionMismatch
                    : updateFailure,
            HttpStatus: verified ? 0 : updateHttpStatus);
    }

    private static NyxIdUserServiceRouteConvergence Failure(
        NyxIdUserServiceAuthoritySnapshot snapshot,
        NyxIdUserServiceRouteConvergenceFailureKind failureKind) =>
        new(snapshot, Attempted: false, Verified: false, failureKind);
}

internal static class NyxIdUserServiceRouteUpdateAdapter
{
    public static string Serialize(NyxIdUserServiceRoutePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return JsonSerializer.Serialize(new UpdateRouteRequest(
            patch.ForwardAccessToken,
            patch.InjectDelegationToken,
            patch.DelegationTokenScope));
    }

    private sealed record UpdateRouteRequest(
        [property: JsonPropertyName("forward_access_token")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? ForwardAccessToken,
        [property: JsonPropertyName("inject_delegation_token")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? InjectDelegationToken,
        [property: JsonPropertyName("delegation_token_scope")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DelegationTokenScope);
}
