using System.Linq;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// M4 — the NyxID relay diagnostic route (<c>/api/webhooks/nyxid-relay/diag</c>)
/// is a token-relay oracle: it forwards an arbitrary caller-supplied token to the
/// NyxID gateway and echoes the response, letting anyone probe whether a token is
/// a valid credential. It is compiled out of production and only mapped when the
/// host runs in the Development environment, so a mainnet deployment exposes no
/// diag route at all.
/// </summary>
public sealed class NyxIdChatDiagEndpointEnvironmentTests
{
    private const string DiagRoute = "/api/webhooks/nyxid-relay/diag";
    private const string HealthRoute = "/api/webhooks/nyxid-relay/health";

    [Fact]
    public void DiagRoute_IsNotMapped_InProduction()
    {
        var patterns = MapAndCollectRoutePatterns(Environments.Production);

        patterns.Should().NotContain(DiagRoute, "the token-relay oracle must not exist in production");
        // Sanity: the always-on relay routes are still mapped, so we didn't just
        // fail to map anything.
        patterns.Should().Contain(HealthRoute);
    }

    [Fact]
    public void DiagRoute_IsMapped_InDevelopment()
    {
        var patterns = MapAndCollectRoutePatterns(Environments.Development);

        patterns.Should().Contain(DiagRoute, "operators keep the local dev probe");
    }

    private static IReadOnlyList<string> MapAndCollectRoutePatterns(string environmentName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });
        var app = builder.Build();

        // Route registration does not resolve the handlers' [FromServices]
        // dependencies (only invocation would), so mapping on a bare app is safe.
        app.MapNyxIdChatEndpoints();

        var routeBuilder = (IEndpointRouteBuilder)app;
        return routeBuilder.DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();
    }
}
