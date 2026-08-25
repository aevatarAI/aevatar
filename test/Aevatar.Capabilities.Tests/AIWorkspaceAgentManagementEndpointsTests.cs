using Aevatar.Mainnet.Host.Api.AI;
using Aevatar.Audit.Hosting.EndpointAudit;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Capabilities.Tests;

public sealed class AIWorkspaceAgentManagementEndpointsTests
{
    [Fact]
    public void Mapping_ShouldExposeCallerScopedManagementRoutesAndRequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapAIWorkspaceAgentManagementEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => new
            {
                Pattern = endpoint.RoutePattern.RawText,
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
                IsAuthorized = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null,
                AuditOperation = endpoint.Metadata.GetMetadata<EndpointAuditMetadata>()?.OperationName,
            })
            .ToArray();

        routes.Should().OnlyContain(static route => route.IsAuthorized);
        routes.Should().OnlyContain(static route => route.AuditOperation != null);
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents" && route.Methods.Contains("POST"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/editor-options" && route.Methods.Contains("GET"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/{profileSlug}" && route.Methods.Contains("GET"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/{profileSlug}/draft" && route.Methods.Contains("PUT"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/{profileSlug}:validate" && route.Methods.Contains("POST"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/{profileSlug}:publish" && route.Methods.Contains("POST"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/default/{agentKind}" && route.Methods.Contains("GET"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/default/{agentKind}" && route.Methods.Contains("PUT"));
        routes.Should().Contain(route => route.Pattern == "/api/ai/agents/default/{agentKind}" && route.Methods.Contains("DELETE"));
        routes.Select(static route => route.AuditOperation).Should().BeEquivalentTo([
            "ai-workspace.agents.create",
            "ai-workspace.agents.editor-options",
            "ai-workspace.agents.get",
            "ai-workspace.agents.update-draft",
            "ai-workspace.agents.validate",
            "ai-workspace.agents.publish",
            "ai-workspace.agents.get-default",
            "ai-workspace.agents.set-default",
            "ai-workspace.agents.clear-default",
        ]);
    }

    [Fact]
    public void Mapping_WithAIWorkspaceQueries_ShouldKeepEveryMethodAndPathUnique()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapAIWorkspaceEndpoints();
        app.MapAIWorkspaceAgentManagementEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith(
                    "/api/ai/agents",
                    StringComparison.Ordinal) == true)
            .SelectMany(static endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .ToArray();

        routes.Should().OnlyHaveUniqueItems();
        routes.Should().Contain("GET /api/ai/agents");
        routes.Should().Contain("POST /api/ai/agents");
        routes.Should().Contain("GET /api/ai/agents/editor-options");
    }
}
