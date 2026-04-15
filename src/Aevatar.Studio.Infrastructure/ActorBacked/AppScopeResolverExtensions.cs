using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Extension methods for <see cref="IAppScopeResolver"/> used by ActorBacked stores.
/// </summary>
internal static class AppScopeResolverExtensions
{
    /// <summary>
    /// Resolves the current scope ID, falling back to <c>"default"</c> if no scope is available.
    /// </summary>
    public static string ResolveScopeIdOrDefault(this IAppScopeResolver resolver)
        => resolver.Resolve()?.ScopeId ?? "default";
}
