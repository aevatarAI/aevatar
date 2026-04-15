using Microsoft.AspNetCore.Http;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public sealed record AppScopeContext(string ScopeId, string Source);

public interface IAppScopeResolver
{
    AppScopeContext? Resolve(HttpContext? httpContext = null);
}
