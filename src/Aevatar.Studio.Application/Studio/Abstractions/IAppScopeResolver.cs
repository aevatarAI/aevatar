using Microsoft.AspNetCore.Http;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public sealed record AppScopeContext(string ScopeId, string Source);

public interface IAppScopeResolver
{
    AppScopeContext? Resolve(HttpContext? httpContext = null);

    /// <summary>
    /// True when the current HTTP request has an authenticated caller but no scope can be
    /// resolved from their claims. Typically means the JWT reached the endpoint without a
    /// <c>scope_id</c> claim — the auth provider's claims transformer is misconfigured or
    /// the caller is carrying a stale token. Scope-sensitive services MUST fail closed in
    /// this state rather than fall through to workspace-global behaviour.
    /// </summary>
    bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null);
}
