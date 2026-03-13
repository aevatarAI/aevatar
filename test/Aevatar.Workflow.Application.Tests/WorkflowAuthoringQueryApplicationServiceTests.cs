using Aevatar.Workflow.Application.Abstractions.Authoring;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Authoring;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowAuthoringQueryApplicationServiceTests
{
    [Fact]
    public async Task ParseWorkflowAsync_ShouldDelegateToValidationPort()
    {
        var expected = new PlaygroundWorkflowParseResult
        {
            Valid = true,
            Definition = new WorkflowAuthoringDefinition
            {
                Name = "draft_flow",
            },
        };
        var validationPort = new FakeWorkflowDefinitionValidationPort
        {
            ParseResult = expected,
        };
        var service = new WorkflowAuthoringQueryApplicationService(
            validationPort,
            new FakeWorkflowCatalogPort(),
            new FakeWorkflowCapabilitiesPort(),
            new FakeWorkflowRuntimeStatusPort());

        var result = await service.ParseWorkflowAsync(new PlaygroundWorkflowParseRequest
        {
            Yaml = "name: draft_flow",
        });

        result.Should().BeSameAs(expected);
        validationPort.LastRequest!.Yaml.Should().Be("name: draft_flow");
    }

    [Fact]
    public async Task ListPrimitivesAsync_ShouldComposeCapabilitiesAndCatalogExamples()
    {
        var service = new WorkflowAuthoringQueryApplicationService(
            new FakeWorkflowDefinitionValidationPort(),
            new FakeWorkflowCatalogPort
            {
                Catalog =
                [
                    new WorkflowCatalogItem
                    {
                        Name = "assign_example",
                        IsPrimitiveExample = true,
                        Primitives = ["assign"],
                    },
                    new WorkflowCatalogItem
                    {
                        Name = "assign_pipeline",
                        IsPrimitiveExample = false,
                        Primitives = ["assign"],
                    },
                    new WorkflowCatalogItem
                    {
                        Name = "parallel_fanout",
                        IsPrimitiveExample = true,
                        Primitives = ["parallel"],
                    },
                ],
            },
            new FakeWorkflowCapabilitiesPort
            {
                Document = new WorkflowCapabilitiesDocument
                {
                    Primitives =
                    [
                        new WorkflowPrimitiveCapability
                        {
                            Name = "parallel",
                            Category = "composition",
                            Description = "Parallel work.",
                        },
                        new WorkflowPrimitiveCapability
                        {
                            Name = "assign",
                            Category = "data",
                            Description = "Assigns data.",
                            Aliases = ["set"],
                            Parameters =
                            [
                                new WorkflowPrimitiveParameterCapability
                                {
                                    Name = "target",
                                    Type = "string",
                                    Required = true,
                                    Description = "Target field.",
                                    Enum = ["result"],
                                },
                            ],
                        },
                    ],
                },
            },
            new FakeWorkflowRuntimeStatusPort());

        var result = await service.ListPrimitivesAsync();

        result.Select(item => item.Name).Should().ContainInOrder("assign", "parallel");
        result[0].ExampleWorkflows.Should().Equal("assign_example", "assign_pipeline");
        result[0].Parameters.Should().ContainSingle();
        result[0].Parameters[0].Name.Should().Be("target");
        result[0].Parameters[0].Required.Should().BeTrue();
        result[0].Parameters[0].EnumValues.Should().Equal("result");
    }

    [Fact]
    public async Task GetLlmStatusAsync_ShouldDelegateToRuntimeStatusPort()
    {
        var expected = new WorkflowLlmStatus
        {
            Available = true,
            Provider = "tornado",
            Model = "gpt-test",
            Providers = ["tornado"],
        };
        var service = new WorkflowAuthoringQueryApplicationService(
            new FakeWorkflowDefinitionValidationPort(),
            new FakeWorkflowCatalogPort(),
            new FakeWorkflowCapabilitiesPort(),
            new FakeWorkflowRuntimeStatusPort
            {
                Status = expected,
            });

        var result = await service.GetLlmStatusAsync();

        result.Should().BeSameAs(expected);
    }

    private sealed class FakeWorkflowDefinitionValidationPort : IWorkflowDefinitionValidationPort
    {
        public PlaygroundWorkflowParseRequest? LastRequest { get; private set; }

        public PlaygroundWorkflowParseResult ParseResult { get; set; } = new();

        public Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
            PlaygroundWorkflowParseRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(ParseResult);
        }
    }

    private sealed class FakeWorkflowCatalogPort : IWorkflowCatalogPort
    {
        public IReadOnlyList<WorkflowCatalogItem> Catalog { get; set; } = [];

        public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog() => Catalog;

        public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName) => null;
    }

    private sealed class FakeWorkflowCapabilitiesPort : IWorkflowCapabilitiesPort
    {
        public WorkflowCapabilitiesDocument Document { get; set; } = new();

        public WorkflowCapabilitiesDocument GetCapabilities() => Document;
    }

    private sealed class FakeWorkflowRuntimeStatusPort : IWorkflowRuntimeStatusPort
    {
        public WorkflowLlmStatus Status { get; set; } = new();

        public Task<WorkflowLlmStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(Status);
    }
}
