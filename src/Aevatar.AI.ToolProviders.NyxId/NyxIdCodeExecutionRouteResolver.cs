using Aevatar.AI.Abstractions.CodeExecution;

namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdCodeExecutionRouteResolutionKind
{
    Unspecified = 0,
    Ready = 1,
    Missing = 2,
    PolicyMismatch = 3,
    Ambiguous = 4,
    AccessDenied = 5,
    SourceUnavailable = 6,
    Inactive = 7,
    ExecutionNotReady = 8,
}

public sealed record NyxIdCodeExecutionRouteResolution(
    NyxIdCodeExecutionRouteResolutionKind Kind,
    NyxIdUserService? Service,
    int CanonicalCandidateCount,
    int AccessibleCandidateCount,
    int ActiveCandidateCount,
    int EligibleCandidateCount,
    NyxIdApiAccessFailure? SourceFailure = null,
    IReadOnlyList<NyxIdCodeExecutionRoutePolicyObservation>? PolicyObservations = null)
{
    public bool IsReady => Kind == NyxIdCodeExecutionRouteResolutionKind.Ready && Service is not null;
}

public sealed record NyxIdCodeExecutionRoutePolicyObservation(
    bool? ForwardAccessToken,
    bool? InjectDelegationToken,
    IReadOnlyList<string> MissingDelegationScopes);

/// <summary>
/// Resolves the platform code runtime from caller-visible NyxID facts. Catalog identity prevents
/// an arbitrary same-slug UserService from shadowing the platform route.
/// </summary>
public static class NyxIdCodeExecutionRouteResolver
{
    private const string CodeDelegationScope = "sandbox:execute";
    private const string ProxyDelegationScope = "proxy:*";

    public static async Task<NyxIdCodeExecutionRouteResolution> ResolveAsync(
        INyxIdApiClientFactory clientFactory,
        string bearerToken,
        string? exactUserServiceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var snapshot = await new NyxIdUserServiceAuthorityReader(clientFactory)
            .ReadAsync(bearerToken.Trim(), cancellationToken)
            .ConfigureAwait(false);
        return Resolve(snapshot, exactUserServiceId);
    }

    public static NyxIdCodeExecutionRouteResolution Resolve(
        NyxIdUserServiceAuthoritySnapshot snapshot,
        string? exactUserServiceId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Resolve(snapshot.Routes, snapshot.ExecutionInventory, exactUserServiceId);
    }

    public static NyxIdCodeExecutionRouteResolution Resolve(
        NyxIdApiAccessResult<NyxIdUserServices> inventory,
        string? exactUserServiceId = null) =>
        Resolve(inventory, executionInventory: null, exactUserServiceId);

    private static NyxIdCodeExecutionRouteResolution Resolve(
        NyxIdApiAccessResult<NyxIdUserServices> inventory,
        NyxIdApiAccessResult<NyxIdUserServiceKeys>? executionInventory,
        string? exactUserServiceId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (!inventory.Succeeded)
        {
            var denied = inventory.Failure?.Kind is
                NyxIdApiAccessFailureKind.Unauthorized or NyxIdApiAccessFailureKind.Forbidden;
            return new NyxIdCodeExecutionRouteResolution(
                denied
                    ? NyxIdCodeExecutionRouteResolutionKind.AccessDenied
                    : NyxIdCodeExecutionRouteResolutionKind.SourceUnavailable,
                null,
                0,
                0,
                0,
                0,
                inventory.Failure);
        }

        if (executionInventory is not null && !executionInventory.Succeeded)
        {
            var denied = executionInventory.Failure?.Kind is
                NyxIdApiAccessFailureKind.Unauthorized or NyxIdApiAccessFailureKind.Forbidden;
            return new NyxIdCodeExecutionRouteResolution(
                denied
                    ? NyxIdCodeExecutionRouteResolutionKind.AccessDenied
                    : NyxIdCodeExecutionRouteResolutionKind.SourceUnavailable,
                null,
                0,
                0,
                0,
                0,
                executionInventory.Failure);
        }

        var requestedId = NormalizeOptional(exactUserServiceId);
        var canonical = inventory.Value!.Services
            .Where(service =>
                !string.IsNullOrWhiteSpace(service.CatalogServiceId) &&
                (requestedId is null || string.Equals(service.Id, requestedId, StringComparison.Ordinal)) &&
                IsCanonicalCodeExecutionRoute(inventory, executionInventory, service))
            .ToArray();
        if (canonical.Length == 0)
            return Result(NyxIdCodeExecutionRouteResolutionKind.Missing, canonical, [], [], []);

        var accessible = canonical.Where(IsAccessible).ToArray();
        if (accessible.Length == 0)
            return Result(
                NyxIdCodeExecutionRouteResolutionKind.AccessDenied,
                canonical,
                accessible,
                [],
                []);

        var active = accessible.Where(static service => service.IsActive).ToArray();
        if (active.Length == 0)
            return Result(
                NyxIdCodeExecutionRouteResolutionKind.Inactive,
                canonical,
                accessible,
                active,
                []);

        var executionReady = executionInventory is null
            ? active
            : active.Where(service =>
                    TryResolveExecutionAuthority(inventory, executionInventory, service.Id, out var authority) &&
                    authority!.IsExecutionReady &&
                    string.Equals(
                        authority.Execution.CatalogServiceSlug,
                        CodeExecutionContract.ServiceSlug,
                        StringComparison.Ordinal))
                .ToArray();
        if (executionReady.Length == 0)
            return Result(
                NyxIdCodeExecutionRouteResolutionKind.ExecutionNotReady,
                canonical,
                accessible,
                active,
                executionReady);

        var eligible = executionReady.Where(HasUsableExecutionCredential).ToArray();
        if (eligible.Length == 0)
            return new NyxIdCodeExecutionRouteResolution(
                NyxIdCodeExecutionRouteResolutionKind.PolicyMismatch,
                null,
                canonical.Length,
                accessible.Length,
                active.Length,
                eligible.Length,
                PolicyObservations: executionReady
                    .Select(ObservePolicy)
                    .ToArray());

        var selected = SelectPreferredEligibleRoute(eligible, requestedId);
        if (selected is null)
            return Result(
                NyxIdCodeExecutionRouteResolutionKind.Ambiguous,
                canonical,
                accessible,
                active,
                eligible);

        return new NyxIdCodeExecutionRouteResolution(
            NyxIdCodeExecutionRouteResolutionKind.Ready,
            selected,
            canonical.Length,
            accessible.Length,
            active.Length,
            eligible.Length);
    }

    /// <summary>
    /// Whether the shared route delivers both credentials required by platform code execution.
    /// The caller's Agent Key is forwarded for sandbox outbound calls, while NyxID's short-lived
    /// delegation authenticates the Chrono execution boundary and preserves managed Codex's
    /// <c>proxy:*</c> capability on the same UserService.
    /// </summary>
    public static bool HasUsableExecutionCredential(NyxIdUserService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.ForwardAccessToken == true &&
               service.InjectDelegationToken == true &&
               GrantsRequiredDelegationScopes(service.DelegationTokenScope);
    }

    public static string FormatPolicyMismatch(
        IReadOnlyList<NyxIdCodeExecutionRoutePolicyObservation>? observations)
    {
        const string prefix = "The canonical platform code execution route policy differs: ";
        if (observations is not { Count: > 0 })
        {
            return prefix +
                   "forward_access_token must be true; inject_delegation_token must be true; " +
                   "delegation_token_scope must contain proxy:* and sandbox:execute.";
        }

        var differences = new List<string>();
        var forwardValues = observations
            .Where(static observation => observation.ForwardAccessToken != true)
            .Select(static observation => FormatObservedBoolean(observation.ForwardAccessToken))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (forwardValues.Length > 0)
        {
            differences.Add(
                $"forward_access_token: {FormatObservedValues(forwardValues)} -> true");
        }

        var injectionValues = observations
            .Where(static observation => observation.InjectDelegationToken != true)
            .Select(static observation => FormatObservedBoolean(observation.InjectDelegationToken))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (injectionValues.Length > 0)
        {
            differences.Add(
                $"inject_delegation_token: {FormatObservedValues(injectionValues)} -> true");
        }

        var missingScopes = observations
            .SelectMany(static observation => observation.MissingDelegationScopes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingScopes.Length > 0)
        {
            differences.Add(
                $"delegation_token_scope: missing [{string.Join(", ", missingScopes)}] -> " +
                "contains [proxy:*, sandbox:execute]");
        }

        return prefix + string.Join("; ", differences) + ".";
    }

    public static string AddRequiredDelegationScopes(string? delegationTokenScope)
    {
        var scopes = string.IsNullOrWhiteSpace(delegationTokenScope)
            ? []
            : delegationTokenScope
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        if (!scopes.Contains(ProxyDelegationScope, StringComparer.Ordinal))
            scopes.Add(ProxyDelegationScope);
        if (!scopes.Contains(CodeDelegationScope, StringComparer.Ordinal))
            scopes.Add(CodeDelegationScope);
        return string.Join(' ', scopes);
    }

    private static bool GrantsRequiredDelegationScopes(string? delegationTokenScope)
    {
        if (string.IsNullOrWhiteSpace(delegationTokenScope))
            return false;

        var scopes = delegationTokenScope.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return scopes.Contains(CodeDelegationScope, StringComparer.Ordinal) &&
               scopes.Contains(ProxyDelegationScope, StringComparer.Ordinal);
    }

    private static NyxIdCodeExecutionRoutePolicyObservation ObservePolicy(
        NyxIdUserService service)
    {
        var scopes = string.IsNullOrWhiteSpace(service.DelegationTokenScope)
            ? []
            : service.DelegationTokenScope.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missingScopes = new[] { ProxyDelegationScope, CodeDelegationScope }
            .Where(required => !scopes.Contains(required, StringComparer.Ordinal))
            .ToArray();
        return new NyxIdCodeExecutionRoutePolicyObservation(
            service.ForwardAccessToken,
            service.InjectDelegationToken,
            missingScopes);
    }

    private static NyxIdUserService? SelectPreferredEligibleRoute(
        IReadOnlyList<NyxIdUserService> eligible,
        string? requestedId)
    {
        if (eligible.Count == 1)
            return eligible[0];
        if (requestedId is not null)
            return null;

        var shared = eligible
            .Where(static service => string.Equals(
                service.Slug,
                CodeExecutionContract.ServiceSlug,
                StringComparison.Ordinal))
            .ToArray();
        return shared.Length == 1 ? shared[0] : null;
    }

    private static string FormatObservedBoolean(bool? value) =>
        value switch
        {
            true => "true",
            false => "false",
            null => "absent",
        };

    private static string FormatObservedValues(IReadOnlyList<string> values) =>
        values.Count == 1 ? values[0] : $"[{string.Join(", ", values)}]";

    private static bool IsAccessible(NyxIdUserService service) =>
        service.CredentialSource.Kind switch
        {
            NyxIdUserServiceCredentialSourceKind.Personal => true,
            NyxIdUserServiceCredentialSourceKind.Organization =>
                service.CredentialSource.Allowed,
            _ => false,
        };

    private static bool TryResolveExecutionAuthority(
        NyxIdApiAccessResult<NyxIdUserServices> routes,
        NyxIdApiAccessResult<NyxIdUserServiceKeys> executionInventory,
        string userServiceId,
        out NyxIdUserServiceAuthority? authority) =>
        new NyxIdUserServiceAuthoritySnapshot(routes, executionInventory)
            .TryGetExact(userServiceId, out authority);

    private static bool IsCanonicalCodeExecutionRoute(
        NyxIdApiAccessResult<NyxIdUserServices> routes,
        NyxIdApiAccessResult<NyxIdUserServiceKeys>? executionInventory,
        NyxIdUserService service)
    {
        if (executionInventory is null)
        {
            return string.Equals(
                service.Slug,
                CodeExecutionContract.ServiceSlug,
                StringComparison.Ordinal);
        }

        return executionInventory.Succeeded &&
               CodeExecutionContract.IsSupportedServiceSlug(service.Slug) &&
               TryResolveExecutionAuthority(
                   routes,
                   executionInventory,
                   service.Id,
                   out var authority) &&
               authority is not null &&
               string.Equals(
                   authority.Execution.CatalogServiceSlug,
                   CodeExecutionContract.ServiceSlug,
                   StringComparison.Ordinal);
    }

    private static NyxIdCodeExecutionRouteResolution Result(
        NyxIdCodeExecutionRouteResolutionKind kind,
        IReadOnlyCollection<NyxIdUserService> canonical,
        IReadOnlyCollection<NyxIdUserService> accessible,
        IReadOnlyCollection<NyxIdUserService> active,
        IReadOnlyCollection<NyxIdUserService> eligible) =>
        new(kind, null, canonical.Count, accessible.Count, active.Count, eligible.Count);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
