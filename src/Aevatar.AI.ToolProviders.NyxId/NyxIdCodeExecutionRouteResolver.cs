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
    NyxIdApiAccessFailure? SourceFailure = null)
{
    public bool IsReady => Kind == NyxIdCodeExecutionRouteResolutionKind.Ready && Service is not null;
}

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
            return Result(
                NyxIdCodeExecutionRouteResolutionKind.PolicyMismatch,
                canonical,
                accessible,
                active,
                eligible);
        if (eligible.Length != 1)
            return Result(
                NyxIdCodeExecutionRouteResolutionKind.Ambiguous,
                canonical,
                accessible,
                active,
                eligible);

        return new NyxIdCodeExecutionRouteResolution(
            NyxIdCodeExecutionRouteResolutionKind.Ready,
            eligible[0],
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
