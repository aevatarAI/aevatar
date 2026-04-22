using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppStudioAuthEndpointTests
{
    [Fact]
    public async Task AuthMeEndpoint_ShouldExposeBindAuthContractFields()
    {
        await using var host = await StudioAuthTestHost.StartAsync(
            authenticated: true,
            resolvedScopeId: "scope-a");

        var response = await host.Client.GetAsync("/api/auth/me");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var payload = await response.Content.ReadFromJsonAsync<AppAuthMeResponse>();
        payload.Should().NotBeNull();
        payload!.Enabled.Should().BeTrue();
        payload.Authenticated.Should().BeTrue();
        payload.ProviderDisplayName.Should().Be("NyxID");
        payload.LoginUrl.Should().Be("/auth/login?returnUrl=%2F");
        payload.LogoutUrl.Should().Be("/auth/logout?returnUrl=%2F");
        payload.InvokeAuthMode.Should().Be("studio-session");
        payload.ExternalCallerHint.Should().Contain("Authorization: Bearer <token>");
        payload.ScopeId.Should().Be("scope-a");
        payload.ScopeSource.Should().Be("claim:scope_id");
    }

    [Fact]
    public async Task AuthMeEndpoint_ShouldReturnBearerTokenMode_WhenUnauthenticated()
    {
        await using var host = await StudioAuthTestHost.StartAsync(
            authenticated: false,
            resolvedScopeId: null);

        var payload = await host.Client.GetFromJsonAsync<AppAuthMeResponse>("/api/auth/me");

        payload.Should().NotBeNull();
        payload!.Enabled.Should().BeTrue();
        payload.Authenticated.Should().BeFalse();
        payload.InvokeAuthMode.Should().Be("bearer-token");
        payload.ExternalCallerHint.Should().Contain("Sign in with");
        payload.LoginUrl.Should().Be("/auth/login?returnUrl=%2F");
        payload.LogoutUrl.Should().Be("/auth/logout?returnUrl=%2F");
    }

    [Fact]
    public async Task AuthMeEndpoint_ShouldReturnAnonymousMode_WhenAuthIsDisabled()
    {
        await using var host = await StudioAuthTestHost.StartAsync(
            authenticated: false,
            resolvedScopeId: null,
            enableAuth: false);

        var payload = await host.Client.GetFromJsonAsync<AppAuthMeResponse>("/api/auth/me");

        payload.Should().NotBeNull();
        payload!.Enabled.Should().BeFalse();
        payload.Authenticated.Should().BeFalse();
        payload.InvokeAuthMode.Should().Be("anonymous");
        payload.ExternalCallerHint.Should().Contain("without authentication");
        payload.LoginUrl.Should().BeNull();
        payload.LogoutUrl.Should().BeNull();
    }

    private sealed class StudioAuthTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StudioAuthTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<StudioAuthTestHost> StartAsync(
            bool authenticated,
            string? resolvedScopeId,
            bool enableAuth = true)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            if (enableAuth)
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                    ["Aevatar:Authentication:Authority"] = "https://nyx.example.com",
                    ["Cli:App:NyxId:Authority"] = "https://nyx.example.com",
                });
                builder.Services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            }

            builder.Services.AddSingleton<IAppScopeResolver>(new StubAppScopeResolver(resolvedScopeId));
            builder.Services.AddSingleton(new AevatarHostMetadata
            {
                ServiceName = "test-studio-auth",
            });
            builder.Services.AddSingleton<AevatarHostHealthService>();

            var app = builder.Build();
            if (enableAuth)
                app.UseAuthentication();
            app.Use(async (http, next) =>
            {
                if (authenticated)
                {
                    http.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("scope_id", resolvedScopeId ?? string.Empty),
                        new Claim("email", "studio@example.com"),
                        new Claim(ClaimTypes.Name, "Studio User"),
                    ], "Test"));
                }

                await next();
            });

            StudioEndpoints.Map(app, embeddedWorkflowMode: false);
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new StudioAuthTestHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubAppScopeResolver : IAppScopeResolver
    {
        private readonly string? _scopeId;

        public StubAppScopeResolver(string? scopeId)
        {
            _scopeId = scopeId;
        }

        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            string.IsNullOrWhiteSpace(_scopeId)
                ? null
                : new AppScopeContext(_scopeId, "claim:scope_id");
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}
