using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Pure helpers used by <see cref="RuntimeActorGrain"/> for kind identity
/// resolution. Extracted so the activation logic is exercisable without an
/// Orleans test cluster.
/// </summary>
internal static class RuntimeActorIdentityResolution
{
    /// <summary>
    /// Returns true when <paramref name="requestedKind"/> resolves to the same
    /// canonical kind that the grain is currently bound to.
    /// Used as the idempotency check for repeat calls to
    /// <c>InitializeAgentByKindAsync</c>.
    /// </summary>
    internal static bool ResolvesToSameImplementation(
        IAgentKindRegistry? registry,
        string? activeKind,
        string requestedKind)
    {
        if (string.IsNullOrWhiteSpace(activeKind))
            return false;

        if (string.Equals(activeKind, requestedKind, StringComparison.Ordinal))
            return true;

        if (registry == null)
            return false;

        try
        {
            return string.Equals(
                registry.Resolve(requestedKind).Metadata.Kind,
                activeKind,
                StringComparison.Ordinal);
        }
        catch (UnknownAgentKindException)
        {
            return false;
        }
    }
}
