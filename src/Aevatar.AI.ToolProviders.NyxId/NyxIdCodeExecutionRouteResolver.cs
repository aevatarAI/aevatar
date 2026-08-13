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

    public static async Task<NyxIdCodeExecutionRouteResolution> ResolveAsync(
        INyxIdApiClientFactory clientFactory,
        string bearerToken,
        string? exactUserServiceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);

        var response = await clientFactory.CreateClient()
            .ListUserServicesAsync(bearerToken.Trim(), cancellationToken)
            .ConfigureAwait(false);
        return Resolve(
            NyxIdApiAccessResponseParser.ParseCodeExecutionUserServices(response),
            exactUserServiceId);
    }

    public static NyxIdCodeExecutionRouteResolution Resolve(
        NyxIdApiAccessResult<NyxIdUserServices> inventory,
        string? exactUserServiceId = null)
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

        var requestedId = NormalizeOptional(exactUserServiceId);
        var canonical = inventory.Value!.Services
            .Where(service =>
                string.Equals(service.Slug, CodeExecutionContract.ServiceSlug, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(service.CatalogServiceId) &&
                (requestedId is null || string.Equals(service.Id, requestedId, StringComparison.Ordinal)))
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

        var eligible = active.Where(HasUsableExecutionCredential).ToArray();
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
    /// Whether the route delivers a credential the deployed platform runtime accepts. Existing
    /// forwarding routes remain valid; otherwise NyxID must inject a short-lived token whose
    /// whitespace scope membership includes <c>sandbox:execute</c>. Command-side reconciliation
    /// targets the non-forwarding delegated shape without invalidating already-admitted routes.
    /// </summary>
    public static bool HasUsableExecutionCredential(NyxIdUserService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.ForwardAccessToken == true ||
               (service.InjectDelegationToken == true &&
                GrantsCodeExecutionDelegation(service.DelegationTokenScope));
    }

    public static string AddCodeExecutionDelegationScope(string? delegationTokenScope)
    {
        var scopes = string.IsNullOrWhiteSpace(delegationTokenScope)
            ? []
            : delegationTokenScope
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        if (!scopes.Contains(CodeDelegationScope, StringComparer.Ordinal))
            scopes.Add(CodeDelegationScope);
        return string.Join(' ', scopes);
    }

    private static bool GrantsCodeExecutionDelegation(string? delegationTokenScope) =>
        !string.IsNullOrWhiteSpace(delegationTokenScope) &&
        delegationTokenScope
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(CodeDelegationScope, StringComparer.Ordinal);

    private static bool IsAccessible(NyxIdUserService service) =>
        service.CredentialSource.Kind switch
        {
            NyxIdUserServiceCredentialSourceKind.Personal => true,
            NyxIdUserServiceCredentialSourceKind.Organization =>
                service.CredentialSource.Allowed,
            _ => false,
        };

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
