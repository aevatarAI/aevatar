namespace Aevatar.GAgents.ChannelRuntime;

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
