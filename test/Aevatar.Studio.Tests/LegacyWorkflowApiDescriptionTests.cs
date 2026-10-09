using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Capabilities;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Application.Abstractions.Queries;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Aevatar.Studio.Tests;

public sealed class LegacyWorkflowApiDescriptionTests
{
    [Fact]
    public async Task LegacyEndpoints_RemainRoutableButExcluded_WithoutHidingGenericServices()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();
        builder.Services.AddSingleton<AevatarHostHealthService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IWorkflowExecutionQueryApplicationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IWorkflowExecutionScopeQueryApplicationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAppScopedWorkflowCatalogueService, UnusedCatalogueService>();
        await using var app = builder.Build();

        StudioTeamEndpoints.Map(app);
        StudioMemberEndpoints.Map(app);
        StudioMemberAutomationEndpoints.Map(app);
        StudioProvisioningEndpoints.Map(app);
        WorkflowBoardSnapshotEndpoints.Map(app);
        WorkflowDeliveryEndpoints.Map(app);
        StudioEndpoints.Map(app, embeddedWorkflowMode: true);
        app.MapScopeWorkflowCapabilityEndpoints();
        app.MapScopeServiceEndpoints();
        app.MapWorkflowRunObservatory();
        ChatQueryEndpoints.Map(app.MapGroup("/api"));
        app.MapOpenApi();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var legacy = endpoints.Where(endpoint => IsLegacyRoute(endpoint.RoutePattern.RawText!)).ToArray();
        legacy.Length.Should().BeGreaterThan(70);
        legacy.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() != null &&
            endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription,
            "legacy routes must remain mapped while tool discovery excludes their descriptions");

        var serviceInvoke = endpoints.Single(endpoint => endpoint.RoutePattern.RawText ==
            "/api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}");
        serviceInvoke.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription
            .Should().NotBe(true);
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/scripts/generator")
            .Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription.Should().NotBe(true);
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/workflow/observatory/runs/{runId}")
            .Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull();
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/delivery/session")
            .Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull();

        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToArray();
        paths.Should().NotContain(path => IsLegacyRoute(path));
        paths.Should().Contain("/api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}");
        paths.Should().Contain("/api/scripts/generator");
    }

    [Fact]
    public async Task MvcApiExplorer_ExcludesLegacyWorkflowActions_WithoutHidingWorkspace()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers().AddApplicationPart(typeof(WorkspaceController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();

        var descriptions = app.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items).ToArray();
        descriptions.Should().NotContain(description =>
            description.RelativePath!.Contains("workflow-drafts", StringComparison.Ordinal) ||
            description.RelativePath.StartsWith("api/editor", StringComparison.Ordinal) ||
            description.RelativePath.StartsWith("api/executions", StringComparison.Ordinal) ||
            description.RelativePath.Contains("workflow-templates", StringComparison.Ordinal));
        descriptions.Should().Contain(description => description.RelativePath == "api/workspace/settings");
        descriptions.Should().Contain(description => description.RelativePath == "api/workspace/directories");

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
        routes.Should().Contain("api/workspace/workflow-drafts/{workflowId}");
        routes.Should().Contain("api/editor/parse-yaml");
        routes.Should().Contain("api/workflow-templates");
        routes.Should().Contain("api/executions/{executionId}/resume");
    }

    private static bool IsLegacyRoute(string route) =>
        route.Contains("/teams", StringComparison.Ordinal) ||
        route.Contains("/members", StringComparison.Ordinal) ||
        route.Contains("/workflows", StringComparison.Ordinal) ||
        route.Contains("/workflow-catalogue", StringComparison.Ordinal) ||
        route == "/api/workflow-catalog" ||
        route.StartsWith("/api/workflow-actors/", StringComparison.Ordinal) ||
        route.StartsWith("/api/workflow-runs/", StringComparison.Ordinal) ||
        route.Contains("/workflow/draft-run", StringComparison.Ordinal) ||
        route.StartsWith("/api/scopes/{scopeId}/runs", StringComparison.Ordinal) ||
        route.Contains("/provision-workflow", StringComparison.Ordinal) ||
        route.Contains("/workflow-board", StringComparison.Ordinal) ||
        route.Contains("/delivery", StringComparison.Ordinal) ||
        route.Contains("/installations", StringComparison.Ordinal) ||
        route == "/api/app/workflow-generator" ||
        route.Contains("/workflow/observatory/runs", StringComparison.Ordinal) ||
        route.Contains("/workflow/observatory/activity-runs", StringComparison.Ordinal) ||
        route.Contains("/workflow/observatory/admin/runs", StringComparison.Ordinal);

    private sealed class UnusedCatalogueService : IAppScopedWorkflowCatalogueService
    {
        public Task<ScopeWorkflowCatalogueResponse> QueryAsync(
            ScopeWorkflowCatalogueQuery query,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
