using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.AI.ToolProviders.Workflow.Tools;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Workflow.Tests;

public class WorkflowDefinitionToolsTests
{
    #region WorkflowListDefsTool

    [Fact]
    public async Task WorkflowListDefsTool_ReturnsDefinitions()
    {
        var adapter = new StubDefinitionAdapter(
            listResult:
            [
                new WorkflowDefinitionSummary("wf-alpha", "Alpha workflow", 3, 2, "rev-aaa"),
                new WorkflowDefinitionSummary("wf-beta", "Beta workflow", 5, 1, "rev-bbb"),
            ]);

        var tool = new WorkflowListDefsTool(adapter);
        var result = await tool.ExecuteAsync("{}");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("count").GetInt32().Should().Be(2);

        var defs = root.GetProperty("definitions");
        defs.GetArrayLength().Should().Be(2);
        defs[0].GetProperty("name").GetString().Should().Be("wf-alpha");
        defs[0].GetProperty("step_count").GetInt32().Should().Be(3);
        defs[1].GetProperty("name").GetString().Should().Be("wf-beta");
        defs[1].GetProperty("revision_id").GetString().Should().Be("rev-bbb");
    }

    #endregion

    #region WorkflowReadDefTool

    [Fact]
    public async Task WorkflowReadDefTool_ReturnsYaml()
    {
        var snapshot = new WorkflowDefinitionSnapshot(
            "my-workflow",
            "name: my-workflow\nsteps:\n  - id: s1\n    action: echo",
            "rev-123",
            new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));

        var adapter = new StubDefinitionAdapter(getResult: snapshot);
        var tool = new WorkflowReadDefTool(adapter);
        var result = await tool.ExecuteAsync("""{"workflow_name":"my-workflow"}""");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("my-workflow");
        root.GetProperty("yaml").GetString().Should().Contain("steps:");
        root.GetProperty("revision_id").GetString().Should().Be("rev-123");
    }

    [Fact]
    public async Task WorkflowReadDefTool_ReturnsError_WhenNotFound()
    {
        var adapter = new StubDefinitionAdapter(getResult: null);
        var tool = new WorkflowReadDefTool(adapter);
        var result = await tool.ExecuteAsync("""{"workflow_name":"nonexistent"}""");

        result.Should().Contain("error");
        result.Should().Contain("not found");
    }

    #endregion

    #region WorkflowCreateDefTool

    [Fact]
    public async Task WorkflowCreateDefTool_ReturnsSuccess()
    {
        var adapter = new StubDefinitionAdapter(
            createResult: new WorkflowDefinitionCommandResult(
                true, "new-wf", "rev-new", "name: new-wf", []));

        var options = new WorkflowToolOptions();
        var tool = new WorkflowCreateDefTool(adapter, options);
        var result = await tool.ExecuteAsync("""{"workflow_name":"new-wf","yaml":"name: new-wf\nsteps:\n  - id: s1"}""");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("name").GetString().Should().Be("new-wf");
        doc.RootElement.GetProperty("revision_id").GetString().Should().Be("rev-new");
    }

    [Fact]
    public async Task WorkflowCreateDefTool_RejectsOversizedYaml()
    {
        var adapter = new StubDefinitionAdapter();
        var options = new WorkflowToolOptions { MaxYamlSizeChars = 50 };
        var tool = new WorkflowCreateDefTool(adapter, options);

        var oversizedYaml = new string('x', 100);
        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { workflow_name = "big-wf", yaml = oversizedYaml }));

        result.Should().Contain("error");
        result.Should().Contain("exceeds maximum size");
    }

    #endregion

    #region WorkflowUpdateDefTool

    [Fact]
    public async Task WorkflowUpdateDefTool_RequiresExpectedRevision()
    {
        var adapter = new StubDefinitionAdapter();
        var options = new WorkflowToolOptions();
        var tool = new WorkflowUpdateDefTool(adapter, options);

        var result = await tool.ExecuteAsync("""{"workflow_name":"wf","yaml":"name: wf"}""");

        result.Should().Contain("error");
        result.Should().Contain("expected_revision");
        result.Should().Contain("required");
    }

    [Fact]
    public async Task WorkflowUpdateDefTool_ReturnsSuccess_WhenValid()
    {
        var adapter = new StubDefinitionAdapter(
            updateResult: new WorkflowDefinitionCommandResult(true, "my-wf", "rev-2", "name: my-wf\nsteps: []", []));
        var options = new WorkflowToolOptions();
        var tool = new WorkflowUpdateDefTool(adapter, options);

        var result = await tool.ExecuteAsync(
            """{"workflow_name":"my-wf","yaml":"name: my-wf\nsteps: []","expected_revision":"rev-1"}""");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("revision_id").GetString().Should().Be("rev-2");
        root.GetProperty("name").GetString().Should().Be("my-wf");
    }

    #endregion

    #region WorkflowAgentToolSource

    [Fact]
    public async Task WorkflowAgentToolSource_RegistersDefinitionTools_WhenAdapterProvided()
    {
        var adapter = new StubDefinitionAdapter();
        var options = new WorkflowToolOptions();

        // null! for queryService — tests only check tool registration, not invocation
        var source = new WorkflowAgentToolSource(null!, options, definitionCommand: adapter);
        var tools = await source.DiscoverToolsAsync();

        // 3 base tools (status, actor_inspect, event_query) + 4 def tools (list, read, create, update) = 7
        tools.Should().HaveCount(7);
        tools.Should().Contain(t => t is WorkflowListDefsTool);
        tools.Should().Contain(t => t is WorkflowReadDefTool);
        tools.Should().Contain(t => t is WorkflowCreateDefTool);
        tools.Should().Contain(t => t is WorkflowUpdateDefTool);
    }

    [Fact]
    public async Task WorkflowAgentToolSource_SkipsDefinitionTools_WhenNoAdapter()
    {
        var options = new WorkflowToolOptions();

        var source = new WorkflowAgentToolSource(null!, options);
        var tools = await source.DiscoverToolsAsync();

        // Only the 3 base tools
        tools.Should().HaveCount(3);
        tools.Should().NotContain(t => t is WorkflowListDefsTool);
        tools.Should().NotContain(t => t is WorkflowCreateDefTool);
    }

    #endregion

    #region Stubs

    private sealed class StubDefinitionAdapter : IWorkflowDefinitionCommandAdapter
    {
        private readonly IReadOnlyList<WorkflowDefinitionSummary>? _listResult;
        private readonly WorkflowDefinitionSnapshot? _getResult;
        private readonly WorkflowDefinitionCommandResult? _createResult;
        private readonly WorkflowDefinitionCommandResult? _updateResult;

        public StubDefinitionAdapter(
            IReadOnlyList<WorkflowDefinitionSummary>? listResult = null,
            WorkflowDefinitionSnapshot? getResult = null,
            WorkflowDefinitionCommandResult? createResult = null,
            WorkflowDefinitionCommandResult? updateResult = null)
        {
            _listResult = listResult;
            _getResult = getResult;
            _createResult = createResult;
            _updateResult = updateResult;
        }

        public Task<IReadOnlyList<WorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken ct = default) =>
            Task.FromResult(_listResult ?? (IReadOnlyList<WorkflowDefinitionSummary>)[]);

        public Task<WorkflowDefinitionSnapshot?> GetDefinitionAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult(_getResult);

        public Task<WorkflowDefinitionCommandResult> CreateAsync(string workflowName, string yaml, CancellationToken ct = default) =>
            Task.FromResult(_createResult ?? new WorkflowDefinitionCommandResult(false, workflowName, null, null, []));

        public Task<WorkflowDefinitionCommandResult> UpdateAsync(string workflowName, string yaml, string expectedRevisionId, CancellationToken ct = default) =>
            Task.FromResult(_updateResult ?? new WorkflowDefinitionCommandResult(false, workflowName, null, null, []));
    }

    #endregion
}
