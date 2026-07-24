namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Shared scope-resolution policy for ambient, scope-sensitive operations.
/// </summary>
public static class AppScopeResolverExtensions
{
    /// <summary>
    /// Resolves the current scope ID, falling back to <c>"default"</c> only outside an
    /// authenticated request context.
    /// </summary>
    public static string ResolveScopeIdOrDefault(this IAppScopeResolver resolver)
    {
        var scope = resolver.Resolve()?.ScopeId;
        if (!string.IsNullOrWhiteSpace(scope))
            return scope;

        if (resolver.HasAuthenticatedRequestWithoutScope())
        {
            throw new InvalidOperationException(
                "Authenticated caller has no resolvable scope; refusing to route to the shared default catalog. " +
                "Check that the auth provider's claims transformer emits a scope_id claim.");
        }

        return "default";
    }
}
