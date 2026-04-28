namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Resolves the current caller's <see cref="OwnerScope"/> at the surface boundary.
///
/// Each surface has its own implementation:
/// - cli/web → <see cref="NyxIdNativeCallerScopeResolver"/> (queries NyxID `/me`)
/// - lark/telegram → <see cref="ChannelMetadataCallerScopeResolver"/>
///   (reads from <c>AgentToolRequestContext</c> metadata populated by the inbound channel middleware)
///
/// Resolution failure throws <see cref="CallerScopeUnavailableException"/>; never returns
/// a partial / "anonymous" / fallthrough scope. This is the architectural fail-closed
/// guarantee from issue #466 — no caller, no agents.
/// </summary>
public interface ICallerScopeResolver
{
    /// <summary>
    /// Resolve the current caller's owner scope. Returns <c>null</c> when the resolver does
    /// not apply to the current request context (e.g. native resolver with no NyxID token,
    /// channel resolver with no inbound metadata) — the composite resolver tries the next
    /// strategy. Throws <see cref="CallerScopeUnavailableException"/> when the resolver
    /// SHOULD apply but cannot resolve (NyxID `/me` 5xx, malformed metadata, etc.) — that
    /// terminates resolution with a fail-closed error rather than falling through.
    /// </summary>
    Task<OwnerScope?> TryResolveAsync(CancellationToken ct = default);
}

/// <summary>
/// Convenience extensions for <see cref="ICallerScopeResolver"/>. Tools call these from a
/// per-request entry point so a missing or invalid scope becomes a structured exception
/// rather than silently degrading to "no filter".
///
/// (An earlier iteration used a default interface method; that didn't compose with
/// NSubstitute's proxy behavior in tests, which intercepts default methods and returns
/// the default Task — bypassing the body. Extension methods aren't intercepted, so tests
/// can mock <see cref="ICallerScopeResolver.TryResolveAsync"/> and have <see cref="RequireAsync"/>
/// run the validate-or-throw logic against the mocked return.)
/// </summary>
public static class CallerScopeResolverExtensions
{
    /// <summary>
    /// Resolves the caller's scope and throws when no resolver matches or the resolved
    /// scope fails validation. Centralizes the "fail-closed" contract so both
    /// <c>AgentBuilderTool</c> and <c>AgentDeliveryTargetTool</c> use the same error
    /// shape (issue #466 review: avoid duplicating ResolveCallerScopeAsync per tool).
    /// </summary>
    public static async Task<OwnerScope> RequireAsync(this ICallerScopeResolver resolver, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var scope = await resolver.TryResolveAsync(ct);
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

/// <summary>
/// Thrown when caller-scope resolution should have succeeded but the upstream identity
/// source returned an error envelope, malformed payload, or expired credentials. The
/// per-id ops surface this as a specific error rather than falling through to permissive
/// behavior.
/// </summary>
public sealed class CallerScopeUnavailableException : Exception
{
    public CallerScopeUnavailableException(string message) : base(message) { }
    public CallerScopeUnavailableException(string message, Exception inner) : base(message, inner) { }
}
