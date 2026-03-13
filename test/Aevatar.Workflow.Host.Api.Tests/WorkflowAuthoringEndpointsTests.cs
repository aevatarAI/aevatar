using System.Text;
using Aevatar.Workflow.Application.Abstractions.Authoring;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowAuthoringEndpointsTests
{
    [Fact]
    public async Task ParseWorkflow_WhenValid_ShouldReturnOk()
    {
        var result = await WorkflowAuthoringEndpoints.ParseWorkflow(
            new PlaygroundWorkflowParseRequest
            {
                Yaml = "name: draft_flow",
            },
            new FakeWorkflowAuthoringQueryApplicationService
            {
                ParseResult = new PlaygroundWorkflowParseResult
                {
                    Valid = true,
                    Definition = new WorkflowAuthoringDefinition
                    {
                        Name = "draft_flow",
                    },
                },
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadBodyAsync(http)).Should().Contain("draft_flow");
    }

    [Fact]
    public async Task ParseWorkflow_WhenInvalid_ShouldReturnBadRequest()
    {
        var result = await WorkflowAuthoringEndpoints.ParseWorkflow(
            new PlaygroundWorkflowParseRequest
            {
                Yaml = "broken",
            },
            new FakeWorkflowAuthoringQueryApplicationService
            {
                ParseResult = new PlaygroundWorkflowParseResult
                {
                    Valid = false,
                    Error = "invalid yaml",
                    Errors = ["invalid yaml"],
                },
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http)).Should().Contain("invalid yaml");
    }

    [Fact]
    public async Task SaveWorkflow_WhenSucceeded_ShouldReturnOk()
    {
        var result = await WorkflowAuthoringEndpoints.SaveWorkflow(
            new PlaygroundWorkflowSaveRequest
            {
                Yaml = "name: saved_flow",
            },
            new FakeWorkflowAuthoringCommandApplicationService
            {
                SaveResult = new PlaygroundWorkflowSaveResult
                {
                    Saved = true,
                    WorkflowName = "saved_flow",
                    Filename = "saved_flow.yaml",
                },
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadBodyAsync(http)).Should().Contain("saved_flow");
    }

    [Fact]
    public async Task SaveWorkflow_WhenValidationFails_ShouldReturnBadRequest()
    {
        var result = await WorkflowAuthoringEndpoints.SaveWorkflow(
            new PlaygroundWorkflowSaveRequest
            {
                Yaml = "broken",
            },
            new FakeWorkflowAuthoringCommandApplicationService
            {
                SaveHandler = (_, _) => throw new WorkflowAuthoringValidationException(
                    "workflow yaml validation failed",
                    ["bad yaml"]),
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        (await ReadBodyAsync(http)).Should().Contain("bad yaml");
    }

    [Fact]
    public async Task SaveWorkflow_WhenConflictOccurs_ShouldReturnConflict()
    {
        var result = await WorkflowAuthoringEndpoints.SaveWorkflow(
            new PlaygroundWorkflowSaveRequest
            {
                Yaml = "name: existing_flow",
            },
            new FakeWorkflowAuthoringCommandApplicationService
            {
                SaveHandler = (_, _) => throw new WorkflowAuthoringConflictException(
                    "Workflow 'existing_flow.yaml' already exists.",
                    "existing_flow.yaml",
                    "/tmp/existing_flow.yaml"),
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        (await ReadBodyAsync(http)).Should().Contain("existing_flow.yaml");
    }

    [Fact]
    public async Task ListPrimitives_ShouldReturnOk()
    {
        var result = await WorkflowAuthoringEndpoints.ListPrimitives(
            new FakeWorkflowAuthoringQueryApplicationService
            {
                Primitives =
                [
                    new WorkflowPrimitiveDescriptor
                    {
                        Name = "assign",
                    },
                ],
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadBodyAsync(http)).Should().Contain("assign");
    }

    [Fact]
    public async Task GetLlmStatus_ShouldReturnOk()
    {
        var result = await WorkflowAuthoringEndpoints.GetLlmStatus(
            new FakeWorkflowAuthoringQueryApplicationService
            {
                Status = new WorkflowLlmStatus
                {
                    Available = true,
                    Provider = "tornado",
                },
            });

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadBodyAsync(http)).Should().Contain("tornado");
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }

    private static async Task<string> ReadBodyAsync(HttpContext http)
    {
        http.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(http.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeWorkflowAuthoringQueryApplicationService : IWorkflowAuthoringQueryApplicationService
    {
        public PlaygroundWorkflowParseResult ParseResult { get; set; } = new();

        public IReadOnlyList<WorkflowPrimitiveDescriptor> Primitives { get; set; } = [];

        public WorkflowLlmStatus Status { get; set; } = new();

        public Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
            PlaygroundWorkflowParseRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(ParseResult);

        public Task<IReadOnlyList<WorkflowPrimitiveDescriptor>> ListPrimitivesAsync(
            CancellationToken ct = default) =>
            Task.FromResult(Primitives);

        public Task<WorkflowLlmStatus> GetLlmStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(Status);
    }

    private sealed class FakeWorkflowAuthoringCommandApplicationService : IWorkflowAuthoringCommandApplicationService
    {
        public Func<PlaygroundWorkflowSaveRequest, CancellationToken, PlaygroundWorkflowSaveResult>? SaveHandler { get; set; }

        public PlaygroundWorkflowSaveResult SaveResult { get; set; } = new();

        public Task<PlaygroundWorkflowSaveResult> SaveWorkflowAsync(
            PlaygroundWorkflowSaveRequest request,
            CancellationToken ct = default)
        {
            if (SaveHandler != null)
                return Task.FromResult(SaveHandler(request, ct));

            return Task.FromResult(SaveResult);
        }
    }
}
