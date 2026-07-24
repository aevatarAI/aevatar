namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Shared scope-resolution policy for ambient, scope-sensitive operations.
/// </summary>
public static class AppScopeResolverExtensions
{
    /// <summary>
    /// Resolves the current scope ID, falling back to <c>"default"</c> only when no HTTP request
    /// context exists.
    /// </summary>
    public static string ResolveScopeIdOrDefault(this IAppScopeResolver resolver)
    {
        var scope = resolver.Resolve()?.ScopeId;
        if (!string.IsNullOrWhiteSpace(scope))
            return scope;

        if (resolver.HasHttpRequestContext())
        {
            throw new InvalidOperationException(
                "HTTP request has no resolvable scope; refusing to use the default scope.");
        }

        return "default";
    }
}
