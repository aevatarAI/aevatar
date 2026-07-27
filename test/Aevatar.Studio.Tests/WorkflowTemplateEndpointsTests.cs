using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowTemplateEndpointsTests
{
    [Fact]
    public void Map_ShouldExposeOnlyTheTwoReadRoutesWithoutAnonymousOverrides()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        var app = builder.Build();

        WorkflowTemplateEndpoints.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Should().Equal(
            "/api/studio/workflow-templates",
            "/api/studio/workflow-templates/{templateId}/revisions/{revision}");
        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() == null);
    }

    [Fact]
    public async Task HandleListAsync_ShouldMapDefaultsAndSupportConditionalETag()
    {
        var response = new WorkflowTemplateCatalogPage([], null, "\"catalog-etag\"");
        var port = new StubCatalogQueryPort { Page = response };
        var http = new DefaultHttpContext();

        var result = await WorkflowTemplateEndpoints.HandleListAsync(
            http, port, null, null, null, null, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<Ok<WorkflowTemplateCatalogPage>>().Which.Value.Should().BeSameAs(response);
        port.LastQuery.Should().Be(new WorkflowTemplateCatalogQuery(PageSize: 20));
        http.Response.Headers.ETag.Should().ContainSingle().Which.Should().Be("\"catalog-etag\"");
        http.Response.Headers.CacheControl.Should().ContainSingle().Which.Should().Contain("max-age=60");

        http.Request.Headers.IfNoneMatch = "\"catalog-etag\"";
        var notModified = await WorkflowTemplateEndpoints.HandleListAsync(
            http, port, null, null, null, 20, NullLoggerFactory.Instance, CancellationToken.None);
        notModified.Should().BeOfType<StatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    [Theory]
    [InlineData(WorkflowTemplateLookupStatus.NotFound, StatusCodes.Status404NotFound, "WORKFLOW_TEMPLATE_NOT_FOUND")]
    [InlineData(WorkflowTemplateLookupStatus.Disabled, StatusCodes.Status404NotFound, "WORKFLOW_TEMPLATE_DISABLED")]
    [InlineData(WorkflowTemplateLookupStatus.Incompatible, StatusCodes.Status409Conflict, "WORKFLOW_TEMPLATE_INCOMPATIBLE")]
    public async Task HandleGetAsync_ShouldMapLookupOutcomesWithoutChangingRequestedRevision(
        WorkflowTemplateLookupStatus lookupStatus,
        int expectedStatus,
        string expectedCode)
    {
        var detail = lookupStatus == WorkflowTemplateLookupStatus.Incompatible
            ? Detail(WorkflowTemplateCompatibilityStatus.Incompatible)
            : null;
        var port = new StubCatalogQueryPort
        {
            Lookup = new WorkflowTemplateLookupResult(lookupStatus, detail),
        };
        var http = new DefaultHttpContext();

        var result = await WorkflowTemplateEndpoints.HandleGetAsync(
            http, "alpha", "rev-1", port, NullLoggerFactory.Instance, CancellationToken.None);

        var error = result.Should().BeOfType<JsonHttpResult<WorkflowTemplateErrorResponse>>().Subject;
        error.StatusCode.Should().Be(expectedStatus);
        error.Value!.Code.Should().Be(expectedCode);
        port.LastTemplateId.Should().Be("alpha");
        port.LastRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task HandleGetAsync_ShouldReturnImmutableDetailCaching()
    {
        var detail = Detail(WorkflowTemplateCompatibilityStatus.Compatible);
        var port = new StubCatalogQueryPort
        {
            Lookup = new WorkflowTemplateLookupResult(WorkflowTemplateLookupStatus.Found, detail),
        };
        var http = new DefaultHttpContext();

        var result = await WorkflowTemplateEndpoints.HandleGetAsync(
            http, "alpha", "rev-1", port, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<Ok<WorkflowTemplateDetail>>().Which.Value.Should().BeSameAs(detail);
        http.Response.Headers.CacheControl.Should().ContainSingle().Which.Should().Be("public, max-age=31536000, immutable");
    }

    private static WorkflowTemplateDetail Detail(WorkflowTemplateCompatibilityStatus status) =>
        new(
            "alpha",
            "rev-1",
            Text("Alpha"),
            Text("Summary"),
            Text("Description"),
            "Reports",
            ["report"],
            new WorkflowTemplateExpectedIO(Text("input"), Text("output")),
            new WorkflowTemplateRequirements(["transform"], "1.0"),
            status == WorkflowTemplateCompatibilityStatus.Compatible
                ? WorkflowTemplateCompatibility.Compatible
                : new WorkflowTemplateCompatibility(
                    status,
                    WorkflowTemplateCompatibilityReason.RequiredPrimitiveUnavailable),
            "name: alpha\nsteps:\n  - id: start\n    type: transform\n");

    private static WorkflowTemplateLocalizedText Text(string value) =>
        new(value, value);

    private sealed class StubCatalogQueryPort : IWorkflowTemplateCatalogQueryPort
    {
        public WorkflowTemplateCatalogPage Page { get; init; } = new([], null, "\"empty\"");
        public WorkflowTemplateLookupResult Lookup { get; init; } =
            new(WorkflowTemplateLookupStatus.NotFound, null);
        public WorkflowTemplateCatalogQuery? LastQuery { get; private set; }
        public string? LastTemplateId { get; private set; }
        public string? LastRevision { get; private set; }

        public Task<WorkflowTemplateCatalogPage> ListAsync(
            WorkflowTemplateCatalogQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Page);
        }

        public Task<WorkflowTemplateLookupResult> GetAsync(
            string templateId,
            string revision,
            CancellationToken ct = default)
        {
            LastTemplateId = templateId;
            LastRevision = revision;
            return Task.FromResult(Lookup);
        }
    }
}
