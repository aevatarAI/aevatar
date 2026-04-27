namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Tries each registered <see cref="ICallerScopeResolver"/> in order; returns the first
/// non-null scope. Throws <see cref="CallerScopeUnavailableException"/> when all
/// resolvers return null (no surface matched the request) — fail-closed rather than
/// fall through to permissive scope.
///
/// Order matters: the channel resolver should run BEFORE the native resolver. A
/// channel-surface request still carries a NyxID access token (the bot's relay session),
/// so the native resolver would happily resolve that token without honoring the
/// per-sender / per-bot scope. Putting the channel resolver first ensures that when
/// channel metadata is present, the result is the channel-scoped tuple, not the looser
/// nyxid-scoped tuple.
/// </summary>
public sealed class CompositeCallerScopeResolver : ICallerScopeResolver
{
    private readonly IReadOnlyList<ICallerScopeResolver> _resolvers;

    public CompositeCallerScopeResolver(IEnumerable<ICallerScopeResolver> resolvers)
    {
        _resolvers = resolvers?.ToArray() ?? throw new ArgumentNullException(nameof(resolvers));
    }

    public async Task<OwnerScope?> TryResolveAsync(CancellationToken ct = default)
    {
        foreach (var resolver in _resolvers)
        {
            // Skip self to avoid infinite recursion if both this and inner resolvers are
            // registered as ICallerScopeResolver (shouldn't happen with TryAddSingleton +
            // the per-resolver concrete-type registration, but defense-in-depth costs nothing).
            if (ReferenceEquals(resolver, this)) continue;

            var scope = await resolver.TryResolveAsync(ct);
            if (scope is not null)
                return scope;
        }

        return null;
    }

    /// <summary>
    /// Resolves the caller's scope and throws when no resolver matches. Use this from the
    /// tool layer's per-request entry point so a missing scope becomes a structured error
    /// rather than silently downgrading to "no filter".
    /// </summary>
    public async Task<OwnerScope> RequireAsync(CancellationToken ct = default)
    {
        var scope = await TryResolveAsync(ct);
        if (scope is null)
        {
            throw new CallerScopeUnavailableException(
                "No caller scope resolver matched the current request context. The request must come from either a NyxID-authenticated native client (cli/web) or a channel surface with platform/sender_id metadata.");
        }

        if (!scope.TryValidate(out var error))
        {
            throw new CallerScopeUnavailableException($"Resolved caller scope is invalid: {error}");
        }

        return scope;
    }
}
