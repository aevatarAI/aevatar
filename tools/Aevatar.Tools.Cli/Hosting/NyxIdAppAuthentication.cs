using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class NyxIdAppAuthOptions
{
    public const string SectionName = "Cli:App:NyxId";

    public bool? Enabled { get; set; }

    public string Authority { get; set; } = "https://nyx-api.chrono-ai.fun";

    public string ClientId { get; set; } = "37a93189-2734-406e-bca1-7dbdf25c5a53";

    public string? ClientSecret { get; set; }

    public string Scope { get; set; } = "openid profile email";

    public string CallbackPath { get; set; } = "/auth/callback";

    public string? TokenEndpoint { get; set; }

    public string ProviderDisplayName { get; set; } = "Chrono workspace account";

    public bool RequireHttpsMetadata { get; set; } = true;
}

internal static class NyxIdAppAuthentication
{
    private static readonly HashSet<string> IgnoredGenericIdClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_id",
        "session_id",
        "sid",
    };

    private static readonly string[] ScopeClaimCandidates =
    [
        "scope_id",
        "uid",
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    internal static bool ResolveIsEnabled(IConfiguration configuration, bool embeddedWorkflowMode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(NyxIdAppAuthOptions.SectionName).Get<NyxIdAppAuthOptions>();
        return options?.Enabled ?? true;
    }

    internal static NyxIdAppAuthOptions BuildOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(NyxIdAppAuthOptions.SectionName).Get<NyxIdAppAuthOptions>()
            ?? new NyxIdAppAuthOptions();
    }

    internal static IServiceCollection AddNyxIdAppAuthentication(
        this IServiceCollection services,
        NyxIdAppAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHttpContextAccessor();
        services.AddSingleton<IOptions<NyxIdAppAuthOptions>>(Options.Create(options));
        services.AddSingleton<NyxIdAppTokenService>();
        services.AddTransient<NyxIdAccessTokenHandler>();

        services
            .AddAuthentication(authentication =>
            {
                authentication.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                authentication.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, cookie =>
            {
                cookie.Cookie.Name = "aevatar.cli.auth";
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, oidc =>
            {
                oidc.Authority = options.Authority.TrimEnd('/');
                oidc.ClientId = options.ClientId;
                if (!string.IsNullOrWhiteSpace(options.ClientSecret))
                    oidc.ClientSecret = options.ClientSecret;

                oidc.CallbackPath = options.CallbackPath;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;
                oidc.SaveTokens = true;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "name",
                    RoleClaimType = "roles",
                };

                oidc.Scope.Clear();
                foreach (var scope in ParseScopes(options.Scope))
                    oidc.Scope.Add(scope);

                oidc.ClaimActions.MapUniqueJsonKey("sub", "sub");
                oidc.ClaimActions.MapUniqueJsonKey("scope_id", "scope_id");
                oidc.ClaimActions.MapUniqueJsonKey("uid", "uid");
                oidc.ClaimActions.MapUniqueJsonKey("name", "name");
                oidc.ClaimActions.MapUniqueJsonKey("email", "email");
                oidc.ClaimActions.MapUniqueJsonKey("picture", "picture");
                oidc.ClaimActions.MapUniqueJsonKey("roles", "roles");
                oidc.ClaimActions.MapUniqueJsonKey("groups", "groups");

                oidc.Events = new OpenIdConnectEvents
                {
                    OnTicketReceived = context =>
                    {
                        EnsureScopeClaim(context.Principal);
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        var message = Uri.EscapeDataString(
                            context.Failure?.Message ?? "NyxID authentication failed.");
                        context.HandleResponse();
                        context.Response.Redirect($"/auth/error?message={message}");
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }

    internal static void UseNyxIdAppProtection(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (!IsProtectedApiPath(context.Request.Path))
            {
                await next();
                return;
            }

            if (context.User.Identity?.IsAuthenticated == true)
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "AUTH_REQUIRED",
                message = "Sign in to use the app APIs.",
                loginUrl = BuildLoginUrl("/"),
            });
        });
    }

    internal static void MapNyxIdAppEndpoints(this IEndpointRouteBuilder app, bool authEnabled)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/auth/me", async (HttpContext context) =>
        {
            var authOptions = context.RequestServices.GetService<IOptions<NyxIdAppAuthOptions>>()?.Value;
            var scopeContext = context.RequestServices.GetService<IAppScopeResolver>()?.Resolve(context);
            if (!authEnabled)
            {
                return Results.Json(new
                {
                    enabled = false,
                    authenticated = false,
                    providerDisplayName = authOptions?.ProviderDisplayName,
                    scopeId = scopeContext?.ScopeId,
                    scopeSource = scopeContext?.Source,
                });
            }

            var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
            if (!isAuthenticated)
            {
                return Results.Json(new
                {
                    enabled = true,
                    authenticated = false,
                    providerDisplayName = authOptions?.ProviderDisplayName,
                    loginUrl = BuildLoginUrl("/"),
                    scopeId = scopeContext?.ScopeId,
                    scopeSource = scopeContext?.Source,
                });
            }

            return Results.Json(new
            {
                enabled = true,
                authenticated = true,
                sub = context.User.FindFirst("sub")?.Value,
                name = context.User.FindFirst("name")?.Value ?? context.User.Identity?.Name,
                email = context.User.FindFirst("email")?.Value,
                picture = context.User.FindFirst("picture")?.Value,
                providerDisplayName = authOptions?.ProviderDisplayName,
                scopeId = scopeContext?.ScopeId,
                scopeSource = scopeContext?.Source,
                expiresAt = await context.GetTokenAsync("expires_at"),
                logoutUrl = "/auth/logout",
            });
        });

        app.MapGet("/auth/login", (HttpContext context, string? returnUrl) =>
        {
            if (!authEnabled)
                return Results.Redirect("/");

            var redirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl.Trim();
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = redirectUri },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        });

        app.MapGet("/auth/logout", async (HttpContext context) =>
        {
            if (authEnabled)
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Redirect("/auth/signed-out");
        });

        app.MapGet("/auth/signed-out", () => Results.Content(
            RenderHtmlPage(
                "Signed out",
                "Local app session has been cleared.",
                "Sign in again",
                BuildLoginUrl("/")),
            "text/html; charset=utf-8"));

        app.MapGet("/auth/error", (string? message) => Results.Content(
            RenderHtmlPage(
                "Authentication failed",
                WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(message)
                    ? "NyxID login did not complete."
                    : message.Trim()),
                "Try again",
                BuildLoginUrl("/")),
            "text/html; charset=utf-8"));
    }

    private static bool IsProtectedApiPath(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        !IsAnonymousApiPath(path);

    private static bool IsAnonymousApiPath(PathString path) =>
        path.Equals("/api/app/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ParseScopes(string rawScope)
    {
        var scopes = (rawScope ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return scopes.Length == 0 ? ["openid", "profile", "email"] : scopes;
    }

    private static string BuildLoginUrl(string? returnUrl)
    {
        var redirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl.Trim();
        return $"/auth/login?returnUrl={Uri.EscapeDataString(redirectUri)}";
    }

    private static void EnsureScopeClaim(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
            return;

        if (identity.FindFirst("scope_id") != null)
            return;

        foreach (var claimType in ScopeClaimCandidates)
        {
            var claimValue = identity.FindFirst(claimType)?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(claimValue))
                continue;

            identity.AddClaim(new Claim("scope_id", claimValue));
            return;
        }

        var genericIdClaim = identity.Claims.FirstOrDefault(claim =>
            claim.Type.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
            !IgnoredGenericIdClaimTypes.Contains(claim.Type) &&
            !string.IsNullOrWhiteSpace(claim.Value));
        if (genericIdClaim == null)
            return;

        identity.AddClaim(new Claim("scope_id", genericIdClaim.Value.Trim()));
    }

    private static string RenderHtmlPage(
        string title,
        string message,
        string actionLabel,
        string actionUrl) =>
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{WebUtility.HtmlEncode(title)}} · Aevatar</title>
          <style>
            body { margin: 0; font-family: Inter, system-ui, sans-serif; background: #f4f1eb; color: #231f1a; }
            .shell { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 24px; }
            .card { width: min(420px, 100%); background: rgba(255,255,255,0.92); border: 1px solid #e8dfd2; border-radius: 28px; padding: 28px; box-shadow: 0 28px 70px rgba(15, 23, 42, 0.12); }
            .eyebrow { font-size: 11px; letter-spacing: 0.18em; text-transform: uppercase; color: #b46c2c; }
            h1 { margin: 14px 0 10px; font-size: 28px; line-height: 1.1; }
            p { margin: 0; font-size: 14px; line-height: 1.7; color: #64584b; }
            a { display: inline-flex; margin-top: 20px; align-items: center; justify-content: center; border-radius: 999px; background: #231f1a; color: white; text-decoration: none; padding: 11px 18px; font-weight: 600; }
          </style>
        </head>
        <body>
          <main class="shell">
            <section class="card">
              <div class="eyebrow">NyxID</div>
              <h1>{{WebUtility.HtmlEncode(title)}}</h1>
              <p>{{message}}</p>
              <a href="{{WebUtility.HtmlEncode(actionUrl)}}">{{WebUtility.HtmlEncode(actionLabel)}}</a>
            </section>
          </main>
        </body>
        </html>
        """;
}
