using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Queries;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Workflow.Tests;

public class WorkflowCatalogToolsTests
{
    [Fact]
    public async Task Source_WithCatalogPort_ShouldDiscoverOnlyAevatarCatalogTools()
    {
        var source = new WorkflowCatalogAgentToolSource(new RecordingWorkflowCatalogPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Select(tool => tool.Name).Should().Equal(
            "aevatar_list_workflows",
            "aevatar_get_workflow");
        tools.Should().OnlyContain(tool => tool.IsReadOnly && !tool.IsDestructive);
    }

    [Fact]
    public async Task Source_WithoutCatalogPort_ShouldDiscoverNoTools()
    {
        var source = new WorkflowCatalogAgentToolSource();

        var tools = await source.DiscoverToolsAsync();

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWorkflows_ShouldReturnCatalogFreshness()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflows");

        var output = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        var workflow = document.RootElement.GetProperty("workflows")[0];
        workflow.GetProperty("name").GetString().Should().Be("daily_digest");
        workflow.GetProperty("authority_state_version").GetInt64().Should().Be(7);
        workflow.GetProperty("projection_watermark").GetDateTimeOffset().Should().Be(ProjectionWatermark);
        workflow.GetProperty("last_event_id").GetString().Should().Be("event-7");
        port.Calls.Should().Equal("ListWorkflowCatalog");
    }

    [Fact]
    public async Task ListWorkflows_WhenArgumentsContainUnknownProperty_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflows");

        var output = await tool.ExecuteAsync("""{"member_id":"m-alpha"}""");

        AssertError(output, "invalid_arguments", "member_id");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWorkflows_WhenJsonIsMalformed_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflows");

        var output = await tool.ExecuteAsync("{");

        AssertError(output, "invalid_arguments");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkflow_ShouldReturnYamlDefinitionAndEdges()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow");

        var output = await tool.ExecuteAsync("""{"workflow_name":"  daily_digest  "}""");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("catalog").GetProperty("name").GetString().Should().Be("daily_digest");
        root.GetProperty("yaml").GetString().Should().Contain("name: daily_digest");
        root.GetProperty("definition").GetProperty("closed_world_mode").GetBoolean().Should().BeTrue();
        root.GetProperty("definition").GetProperty("roles")[0]
            .GetProperty("system_prompt").GetString().Should().Be("Summarize the day.");
        root.GetProperty("definition").GetProperty("steps")[1]
            .GetProperty("target_role").GetString().Should().Be("summarizer");
        root.GetProperty("edges")[0].GetProperty("from").GetString().Should().Be("collect");
        root.GetProperty("edges")[0].GetProperty("to").GetString().Should().Be("summarize");
        port.Calls.Should().Equal("GetWorkflowDetail:daily_digest");
    }

    [Fact]
    public async Task GetWorkflow_WhenNameMissing_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow");

        var output = await tool.ExecuteAsync("""{"workflow_name":"   "}""");

        AssertError(output, "invalid_arguments", "workflow_name");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkflow_WhenMissing_ShouldReturnWorkflowNotFound()
    {
        var port = new RecordingWorkflowCatalogPort { Detail = null };
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow");

        var output = await tool.ExecuteAsync("""{"workflow_name":"missing"}""");

        AssertError(output, "workflow_not_found", "missing");
        port.Calls.Should().Equal("GetWorkflowDetail:missing");
    }

    [Fact]
    public async Task WorkflowCatalogTools_WhenCanceled_ShouldRethrowCancellation()
    {
        var port = new RecordingWorkflowCatalogPort
        {
            Failure = new OperationCanceledException("catalog query canceled"),
        };
        var tools = await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync();

        var listAct = () => tools.Single(item => item.Name == "aevatar_list_workflows")
            .ExecuteAsync("{}");
        var getAct = () => tools.Single(item => item.Name == "aevatar_get_workflow")
            .ExecuteAsync("""{"workflow_name":"daily_digest"}""");

        await listAct.Should().ThrowAsync<OperationCanceledException>();
        await getAct.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WorkflowCatalogTools_WhenProviderFails_ShouldReturnSafeStructuredError()
    {
        var port = new RecordingWorkflowCatalogPort
        {
            Failure = new InvalidOperationException("sensitive backend details"),
        };
        var tools = await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync();

        var listOutput = await tools.Single(item => item.Name == "aevatar_list_workflows")
            .ExecuteAsync("{}");
        var getOutput = await tools.Single(item => item.Name == "aevatar_get_workflow")
            .ExecuteAsync("""{"workflow_name":"daily_digest"}""");

        AssertError(listOutput, "workflow_query_failed", nameof(InvalidOperationException));
        AssertError(getOutput, "workflow_query_failed", nameof(InvalidOperationException));
        listOutput.Should().NotContain("sensitive backend details");
        getOutput.Should().NotContain("sensitive backend details");
    }

    private static readonly DateTimeOffset ProjectionWatermark =
        new(2026, 7, 21, 8, 30, 0, TimeSpan.Zero);

    private static void AssertError(string output, string code, string? messageFragment = null)
    {
        using var document = JsonDocument.Parse(output);
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(code);
        if (messageFragment is not null)
            error.GetProperty("message").GetString().Should().Contain(messageFragment);
    }

    private sealed class RecordingWorkflowCatalogPort : IWorkflowCatalogPort
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<WorkflowCatalogItem> Catalog { get; init; } =
        [
            new()
            {
                Name = "daily_digest",
                Description = "Collect and summarize the day.",
                Category = "productivity",
                Group = "daily",
                GroupLabel = "Daily workflows",
                SortOrder = 10,
                Source = "builtin",
                SourceLabel = "Built in",
                ShowInLibrary = true,
                RequiresLlmProvider = true,
                Primitives = ["collect", "llm"],
                AuthorityStateVersion = 7,
                ProjectionWatermark = ProjectionWatermark,
                LastEventId = "event-7",
            },
        ];

        public WorkflowCatalogItemDetail? Detail { get; init; } = new()
        {
            Catalog = new WorkflowCatalogItem
            {
                Name = "daily_digest",
                AuthorityStateVersion = 7,
                ProjectionWatermark = ProjectionWatermark,
                LastEventId = "event-7",
            },
            Yaml = "name: daily_digest\nsteps:\n  - id: collect",
            Definition = new WorkflowCatalogDefinition
            {
                Name = "daily_digest",
                Description = "Collect and summarize the day.",
                ClosedWorldMode = true,
                Roles =
                [
                    new WorkflowCatalogRole
                    {
                        Id = "summarizer",
                        Name = "Summarizer",
                        SystemPrompt = "Summarize the day.",
                    },
                ],
                Steps =
                [
                    new WorkflowCatalogStep
                    {
                        Id = "collect",
                        Type = "connector",
                        Next = "summarize",
                    },
                    new WorkflowCatalogStep
                    {
                        Id = "summarize",
                        Type = "llm",
                        TargetRole = "summarizer",
                    },
                ],
            },
            Edges =
            [
                new WorkflowCatalogEdge
                {
                    From = "collect",
                    To = "summarize",
                    Label = "next",
                },
            ],
        };

        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(
            CancellationToken ct = default)
        {
            Calls.Add("ListWorkflowCatalog");
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<WorkflowCatalogItem>>(Failure);

            return Task.FromResult(Catalog);
        }

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
            string workflowName,
            CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowDetail:{workflowName}");
            if (Failure is not null)
                return Task.FromException<WorkflowCatalogItemDetail?>(Failure);

            return Task.FromResult(Detail);
        }
    }
}
