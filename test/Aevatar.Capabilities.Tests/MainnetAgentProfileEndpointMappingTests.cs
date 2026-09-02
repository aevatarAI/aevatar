using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetAgentProfileEndpointMappingTests
{
    [Fact]
    public void MapAgentProfileEndpoints_ShouldExposeCanonicalResourceRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapAgentProfileEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => new
            {
                Pattern = endpoint.RoutePattern.RawText,
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                IsAuthorized = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null,
            })
            .ToArray();

        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profiles", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profiles", HttpMethods.Post));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profiles/{profileSlug}", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft", HttpMethods.Put));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profiles/{profileSlug}:validate", HttpMethods.Post));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profiles/{profileSlug}:publish", HttpMethods.Post));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profile-bindings/{agentKind}", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profile-bindings/{agentKind}", HttpMethods.Put));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/scopes/{scopeId}/agent-profile-bindings/{agentKind}", HttpMethods.Delete));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/agent-profiles/system", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/agent-profiles/system/{profileSlug}", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profiles", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profiles", HttpMethods.Post));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profiles/{profileSlug}", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profiles/{profileSlug}/draft", HttpMethods.Put));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profiles/{profileSlug}:validate", HttpMethods.Post));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profiles/{profileSlug}:publish", HttpMethods.Post));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profile-bindings/{agentKind}", HttpMethods.Get));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profile-bindings/{agentKind}", HttpMethods.Put));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/admin/agent-profile-bindings/{agentKind}", HttpMethods.Delete));
        routes.Should().Contain(route => Is(route.Pattern, route.Methods, "/api/agent-profiles/editor-options", HttpMethods.Get));

        routes.Where(static route => route.Methods.Any(method =>
                string.Equals(method, HttpMethods.Post, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, HttpMethods.Put, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, HttpMethods.Delete, StringComparison.OrdinalIgnoreCase)))
            .Should()
            .OnlyContain(static route => route.IsAuthorized);
    }

    private static bool Is(
        string? pattern,
        IReadOnlyList<string> methods,
        string expectedPattern,
        string expectedMethod) =>
        string.Equals(pattern, expectedPattern, StringComparison.Ordinal) &&
        methods.Contains(expectedMethod, StringComparer.OrdinalIgnoreCase);
}
