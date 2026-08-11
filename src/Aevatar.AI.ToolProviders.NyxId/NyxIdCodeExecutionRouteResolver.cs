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
    private const string SharedDelegationScope = "proxy:*";
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

        var eligible = active.Where(HasTransitionExecutionPolicy).ToArray();
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

    public static bool HasTransitionExecutionPolicy(NyxIdUserService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.InjectDelegationToken != true ||
            service.ForwardAccessToken is null ||
            string.IsNullOrWhiteSpace(service.DelegationTokenScope))
        {
            return false;
        }

        var scopes = service.DelegationTokenScope.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isCodeOnlyScope = scopes.Length == 1 &&
                              string.Equals(scopes[0], CodeDelegationScope, StringComparison.Ordinal);
        var isLegacyScope = scopes.Length == 1 &&
                            string.Equals(scopes[0], SharedDelegationScope, StringComparison.Ordinal);
        var isCombinedScope = scopes.Length == 2 &&
                              scopes.Contains(SharedDelegationScope, StringComparer.Ordinal) &&
                              scopes.Contains(CodeDelegationScope, StringComparer.Ordinal);

        return service.ForwardAccessToken.Value
            ? isLegacyScope || isCombinedScope
            : isCodeOnlyScope || isCombinedScope;
    }

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
