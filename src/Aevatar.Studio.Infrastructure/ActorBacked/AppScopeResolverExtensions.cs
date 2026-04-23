using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Extension methods for <see cref="IAppScopeResolver"/> used by ActorBacked stores.
/// </summary>
internal static class AppScopeResolverExtensions
{
    /// <summary>
    /// Resolves the current scope ID, falling back to <c>"default"</c> for out-of-request
    /// contexts (CLI, background workers, tests).
    /// <para>
    /// When the caller is authenticated but the resolver cannot produce a scope — i.e. the JWT
    /// reached us without a <c>scope_id</c> claim — we refuse instead of routing the request
    /// into the shared <c>*-default</c> actor. Silently defaulting would let a broken provider
    /// claims pipeline read or overwrite another tenant's role/connector/actor catalog.
    /// </para>
    /// </summary>
    public static string ResolveScopeIdOrDefault(this IAppScopeResolver resolver)
    {
        var scope = resolver.Resolve()?.ScopeId;
        if (!string.IsNullOrWhiteSpace(scope))
            return scope;

        if (resolver.HasAuthenticatedRequestWithoutScope())
            throw new InvalidOperationException(
                "Authenticated caller has no resolvable scope; refusing to route to the shared default catalog. " +
                "Check that the auth provider's claims transformer emits a scope_id claim.");

        return "default";
    }
}
