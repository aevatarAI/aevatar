using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Application.Abstractions.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.AI.ToolProviders.Workflow.Tests;

public class WorkflowCatalogToolsTests
{
    [Fact]
    public async Task AddWorkflowTools_ShouldResolveWorkflowCatalogSource()
    {
        var port = new RecordingWorkflowCatalogPort();
        var services = new ServiceCollection();
        services.AddWorkflowTools();

        var descriptor = services.Should().ContainSingle(item =>
                item.ServiceType == typeof(IAgentToolSource)
                && item.ImplementationType == typeof(WorkflowCatalogAgentToolSource))
            .Subject;
        var source = ActivatorUtilities.CreateInstance(
                new SingleServiceProvider(port),
                descriptor.ImplementationType!)
            .Should().BeOfType<WorkflowCatalogAgentToolSource>().Subject;

        var tools = await source.DiscoverToolsAsync();
        tools.Select(tool => tool.Name).Should().Equal(
            "aevatar_list_workflow_templates",
            "aevatar_get_workflow_template");
    }

    [Fact]
    public async Task Source_WithCatalogPort_ShouldDiscoverOnlyAevatarCatalogTools()
    {
        var source = new WorkflowCatalogAgentToolSource(new RecordingWorkflowCatalogPort());

        var tools = await source.DiscoverToolsAsync();

        tools.Select(tool => tool.Name).Should().Equal(
            "aevatar_list_workflow_templates",
            "aevatar_get_workflow_template");
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
    public async Task ListWorkflowTemplates_ShouldReturnCatalogFreshness()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflow_templates");

        var output = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        var template = document.RootElement.GetProperty("templates")[0];
        template.GetProperty("name").GetString().Should().Be("daily_digest");
        template.GetProperty("authority_state_version").GetInt64().Should().Be(7);
        template.GetProperty("projection_watermark").GetDateTimeOffset().Should().Be(ProjectionWatermark);
        template.GetProperty("last_event_id").GetString().Should().Be("event-7");
        port.Calls.Should().Equal("ListPublicWorkflowCatalog");
    }

    [Fact]
    public async Task ListWorkflowTemplates_ShouldEnumerateOnlyPublicLibraryTemplates()
    {
        var port = new RecordingWorkflowCatalogPort
        {
            Catalog =
            [
                new()
                {
                    Name = "public_template",
                    ShowInLibrary = true,
                    AuthorityStateVersion = 3,
                    ProjectionWatermark = ProjectionWatermark,
                    LastEventId = "event-3",
                },
                new()
                {
                    Name = "hidden_primitive_example",
                    ShowInLibrary = false,
                    AuthorityStateVersion = 4,
                    ProjectionWatermark = ProjectionWatermark,
                    LastEventId = "event-4",
                },
            ],
        };
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflow_templates");

        var output = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        var names = document.RootElement.GetProperty("templates")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();
        names.Should().Equal("public_template");
        names.Should().NotContain("hidden_primitive_example");
    }

    [Fact]
    public async Task WorkflowCatalogTools_ShouldForwardCallerCancellationToken()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tools = await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync();
        using var callerCancellation = new CancellationTokenSource();
        var callerToken = callerCancellation.Token;

        await tools.Single(item => item.Name == "aevatar_list_workflow_templates")
            .ExecuteAsync("{}", callerToken);
        await tools.Single(item => item.Name == "aevatar_get_workflow_template")
            .ExecuteAsync("""{"template_name":"daily_digest"}""", callerToken);

        port.CancellationTokens.Should().Equal(callerToken, callerToken);
        port.CancellationTokens.Should().OnlyContain(token =>
            token.CanBeCanceled && !token.IsCancellationRequested);
    }

    [Fact]
    public async Task ListWorkflowTemplates_ShouldExposeExactPropertySets()
    {
        AssertWireTypeExists("WorkflowTemplateCatalogListJson");
        AssertWireTypeExists("WorkflowTemplateCatalogItemJson");
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflow_templates");

        var output = await tool.ExecuteAsync("{}");

        using var document = JsonDocument.Parse(output);
        PropertyNames(document.RootElement).Should().Equal("templates", "count");
        PropertyNames(document.RootElement.GetProperty("templates")[0]).Should().Equal(
            "name",
            "description",
            "category",
            "group",
            "group_label",
            "sort_order",
            "source",
            "source_label",
            "show_in_library",
            "is_primitive_example",
            "requires_llm_provider",
            "primitives",
            "authority_state_version",
            "projection_watermark",
            "last_event_id");
    }

    [Fact]
    public async Task ListWorkflowTemplates_WhenArgumentsContainUnknownProperty_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflow_templates");

        var output = await tool.ExecuteAsync("""{"member_id":"m-alpha"}""");

        AssertError(output, "invalid_arguments", "member_id");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ListWorkflowTemplates_WhenJsonIsMalformed_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_list_workflow_templates");

        var output = await tool.ExecuteAsync("{");

        AssertError(output, "invalid_arguments");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkflowTemplate_ShouldReturnYamlDefinitionAndEdges()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow_template");

        var output = await tool.ExecuteAsync("""{"template_name":"  daily_digest  "}""");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("template").GetProperty("name").GetString().Should().Be("daily_digest");
        root.GetProperty("yaml").GetString().Should().Contain("name: daily_digest");
        root.GetProperty("definition").GetProperty("closed_world_mode").GetBoolean().Should().BeTrue();
        root.GetProperty("definition").GetProperty("roles")[0]
            .GetProperty("system_prompt").GetString().Should().Be("Summarize the day.");
        root.GetProperty("definition").GetProperty("steps")[1]
            .GetProperty("target_role").GetString().Should().Be("summarizer");
        root.GetProperty("edges")[0].GetProperty("from").GetString().Should().Be("collect");
        root.GetProperty("edges")[0].GetProperty("to").GetString().Should().Be("summarize");
        port.Calls.Should().Equal("GetPublicWorkflowDetail:daily_digest");
    }

    [Fact]
    public async Task GetWorkflowTemplate_ShouldExposeExactNestedPropertySets()
    {
        AssertWireTypeExists("WorkflowTemplateCatalogDetailJson");
        AssertWireTypeExists("WorkflowTemplateCatalogDefinitionJson");
        AssertWireTypeExists("WorkflowTemplateCatalogRoleJson");
        AssertWireTypeExists("WorkflowTemplateCatalogStepJson");
        AssertWireTypeExists("WorkflowTemplateCatalogChildStepJson");
        AssertWireTypeExists("WorkflowTemplateCatalogEdgeJson");
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow_template");

        var output = await tool.ExecuteAsync("""{"template_name":"daily_digest"}""");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        PropertyNames(root).Should().Equal("template", "yaml", "definition", "edges");
        PropertyNames(root.GetProperty("template")).Should().Equal(
            "name",
            "description",
            "category",
            "group",
            "group_label",
            "sort_order",
            "source",
            "source_label",
            "show_in_library",
            "is_primitive_example",
            "requires_llm_provider",
            "primitives",
            "authority_state_version",
            "projection_watermark",
            "last_event_id");

        var definition = root.GetProperty("definition");
        PropertyNames(definition).Should().Equal(
            "name",
            "description",
            "closed_world_mode",
            "roles",
            "steps");
        PropertyNames(definition.GetProperty("roles")[0]).Should().Equal(
            "id",
            "name",
            "system_prompt",
            "provider",
            "model",
            "temperature",
            "max_tokens",
            "max_tool_rounds",
            "max_history_messages",
            "event_modules",
            "event_routes",
            "connectors");
        PropertyNames(definition.GetProperty("steps")[0]).Should().Equal(
            "id",
            "type",
            "target_role",
            "parameters",
            "next",
            "branches",
            "children");
        PropertyNames(definition.GetProperty("steps")[0].GetProperty("children")[0]).Should().Equal(
            "id",
            "type",
            "target_role");
        PropertyNames(root.GetProperty("edges")[0]).Should().Equal("from", "to", "label");
    }

    [Fact]
    public async Task GetWorkflowTemplate_WhenNameMissing_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow_template");

        var output = await tool.ExecuteAsync("""{"template_name":"   "}""");

        AssertError(output, "invalid_arguments", "template_name");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkflowTemplate_WhenArgumentsUseLegacyWorkflowName_ShouldReturnInvalidArguments()
    {
        var port = new RecordingWorkflowCatalogPort();
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow_template");

        var output = await tool.ExecuteAsync("""{"workflow_name":"daily_digest"}""");

        AssertError(output, "invalid_arguments", "workflow_name");
        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkflowTemplate_WhenMissing_ShouldReturnWorkflowTemplateNotFound()
    {
        var port = new RecordingWorkflowCatalogPort { Detail = null };
        var tool = (await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync())
            .Single(item => item.Name == "aevatar_get_workflow_template");

        var output = await tool.ExecuteAsync("""{"template_name":"missing"}""");

        AssertError(output, "workflow_template_not_found", "missing");
        port.Calls.Should().Equal("GetPublicWorkflowDetail:missing");
    }

    [Fact]
    public async Task WorkflowCatalogTools_WhenCanceled_ShouldRethrowCancellation()
    {
        var port = new RecordingWorkflowCatalogPort
        {
            Failure = new OperationCanceledException("catalog query canceled"),
        };
        var tools = await new WorkflowCatalogAgentToolSource(port).DiscoverToolsAsync();

        var listAct = () => tools.Single(item => item.Name == "aevatar_list_workflow_templates")
            .ExecuteAsync("{}");
        var getAct = () => tools.Single(item => item.Name == "aevatar_get_workflow_template")
            .ExecuteAsync("""{"template_name":"daily_digest"}""");

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

        var listOutput = await tools.Single(item => item.Name == "aevatar_list_workflow_templates")
            .ExecuteAsync("{}");
        var getOutput = await tools.Single(item => item.Name == "aevatar_get_workflow_template")
            .ExecuteAsync("""{"template_name":"daily_digest"}""");

        AssertError(listOutput, "workflow_template_query_failed", nameof(InvalidOperationException));
        AssertError(getOutput, "workflow_template_query_failed", nameof(InvalidOperationException));
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

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static void AssertWireTypeExists(string typeName) =>
        typeof(WorkflowCatalogAgentToolSource).Assembly.GetType(
                $"Aevatar.AI.ToolProviders.Workflow.Tools.{typeName}")
            .Should().NotBeNull();

    private sealed class RecordingWorkflowCatalogPort : IWorkflowCatalogPort
    {
        public List<string> Calls { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

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
                ShowInLibrary = true,
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
                        Children =
                        [
                            new WorkflowCatalogChildStep
                            {
                                Id = "collect-child",
                                Type = "connector",
                                TargetRole = "collector",
                            },
                        ],
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
            CancellationTokens.Add(ct);
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<WorkflowCatalogItem>>(Failure);

            return Task.FromResult(Catalog);
        }

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
            string workflowName,
            CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowDetail:{workflowName}");
            CancellationTokens.Add(ct);
            if (Failure is not null)
                return Task.FromException<WorkflowCatalogItemDetail?>(Failure);

            return Task.FromResult(Detail);
        }

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListPublicWorkflowCatalogAsync(
            CancellationToken ct = default)
        {
            Calls.Add("ListPublicWorkflowCatalog");
            CancellationTokens.Add(ct);
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<WorkflowCatalogItem>>(Failure);

            IReadOnlyList<WorkflowCatalogItem> publicCatalog = Catalog
                .Where(static item => item.ShowInLibrary)
                .ToList();
            return Task.FromResult(publicCatalog);
        }

        public Task<WorkflowCatalogItemDetail?> GetPublicWorkflowDetailAsync(
            string templateId,
            CancellationToken ct = default)
        {
            Calls.Add($"GetPublicWorkflowDetail:{templateId}");
            CancellationTokens.Add(ct);
            if (Failure is not null)
                return Task.FromException<WorkflowCatalogItemDetail?>(Failure);

            return Task.FromResult(Detail?.Catalog.ShowInLibrary == true ? Detail : null);
        }
    }

    private sealed class SingleServiceProvider(IWorkflowCatalogPort catalog) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWorkflowCatalogPort) ? catalog : null;
    }
}
