using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Binding.Models;
using Aevatar.AI.ToolProviders.Binding.Ports;
using Aevatar.AI.ToolProviders.Binding.Tools;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.Binding.Tests;

public class BindingToolsTests
{
    #region BindingListTool

    [Fact]
    public async Task BindingListTool_ReturnsBindings()
    {
        var queryAdapter = new StubQueryAdapter(
        [
            new ScopeBindingEntry("svc-1", "Service One", "workflow", "rev-1", "actor-1", DateTimeOffset.UtcNow),
            new ScopeBindingEntry("svc-2", "Service Two", "scripting", "rev-2", "actor-2", DateTimeOffset.UtcNow),
        ]);

        var options = new BindingToolOptions();
        var tool = new BindingListTool(queryAdapter, options);

        // Set scope_id in request context
        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            ["scope_id"] = "test-scope"
        };

        try
        {
            var result = await tool.ExecuteAsync("{}");

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            root.GetProperty("scope_id").GetString().Should().Be("test-scope");
            root.GetProperty("count").GetInt32().Should().Be(2);
            root.GetProperty("total").GetInt32().Should().Be(2);

            var bindings = root.GetProperty("bindings");
            bindings.GetArrayLength().Should().Be(2);
            bindings[0].GetProperty("service_id").GetString().Should().Be("svc-1");
            bindings[1].GetProperty("implementation_kind").GetString().Should().Be("scripting");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task BindingBindTool_RequiresScope()
    {
        var commandPort = new StubCommandPort();
        var tool = new BindingBindTool(commandPort);

        // Ensure no scope_id in context
        AgentToolRequestContext.CurrentMetadata = null;

        var result = await tool.ExecuteAsync("""{"kind":"workflow","workflow_yamls":["name: wf1"]}""");

        result.Should().Contain("error");
        result.Should().Contain("scope_id");
    }

    #endregion

    #region BindingStatusTool

    [Fact]
    public async Task BindingStatusTool_ReturnsStatus()
    {
        var queryAdapter = new StubQueryAdapter([], new ScopeBindingHealthStatus(
            "svc-1", "Service One", "workflow", "healthy", "actor-1", "actor-1", null, DateTimeOffset.UtcNow));
        var tool = new BindingStatusTool(queryAdapter);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            ["scope_id"] = "test-scope"
        };

        try
        {
            var result = await tool.ExecuteAsync("""{"service_id":"svc-1"}""");

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            root.GetProperty("service_id").GetString().Should().Be("svc-1");
            root.GetProperty("status").GetString().Should().Be("healthy");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    #endregion

    #region BindingBindTool

    [Fact]
    public async Task BindingBindTool_WorkflowKind_CallsUpsert()
    {
        ScopeBindingUpsertRequest? captured = null;
        var commandPort = new StubCommandPort(captureRequest: r => captured = r);
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            ["scope_id"] = "scope-1"
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"kind":"workflow","workflow_yamls":["name: wf1\nsteps:\n  - id: s1"],"display_name":"My WF"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            captured.Should().NotBeNull();
            captured!.ScopeId.Should().Be("scope-1");
            captured.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
            captured.Workflow.Should().NotBeNull();
            captured.Workflow!.WorkflowYamls.Should().HaveCount(1);
            captured.DisplayName.Should().Be("My WF");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task BindingBindTool_ScriptingKind_CallsUpsert()
    {
        ScopeBindingUpsertRequest? captured = null;
        var commandPort = new StubCommandPort(captureRequest: r => captured = r);
        var tool = new BindingBindTool(commandPort);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            ["scope_id"] = "scope-2"
        };

        try
        {
            var result = await tool.ExecuteAsync(
                """{"kind":"scripting","script_id":"script-abc","script_revision":"v2"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            captured.Should().NotBeNull();
            captured!.ScopeId.Should().Be("scope-2");
            captured.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
            captured.Script.Should().NotBeNull();
            captured.Script!.ScriptId.Should().Be("script-abc");
            captured.Script.ScriptRevision.Should().Be("v2");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    #endregion

    #region BindingUnbindTool

    [Fact]
    public async Task BindingUnbindTool_CallsUnbind()
    {
        var unbindAdapter = new StubUnbindAdapter(
            new ScopeBindingUnbindResult(true, "svc-remove"));

        var tool = new BindingUnbindTool(unbindAdapter);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            ["scope_id"] = "scope-unbind"
        };

        try
        {
            var result = await tool.ExecuteAsync("""{"service_id":"svc-remove"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("service_id").GetString().Should().Be("svc-remove");
            doc.RootElement.GetProperty("scope_id").GetString().Should().Be("scope-unbind");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    #endregion

    #region BindingAgentToolSource conditional registration

    [Fact]
    public async Task BindingAgentToolSource_ConditionalRegistration()
    {
        // All ports provided -> all 4 tools
        var sourceAll = new BindingAgentToolSource(
            new BindingToolOptions(),
            commandPort: new StubCommandPort(),
            queryAdapter: new StubQueryAdapter([]),
            unbindAdapter: new StubUnbindAdapter(new ScopeBindingUnbindResult(true, "")));

        var toolsAll = await sourceAll.DiscoverToolsAsync();
        toolsAll.Should().HaveCount(4);
        toolsAll.Should().Contain(t => t is BindingListTool);
        toolsAll.Should().Contain(t => t is BindingStatusTool);
        toolsAll.Should().Contain(t => t is BindingBindTool);
        toolsAll.Should().Contain(t => t is BindingUnbindTool);

        // No ports -> empty
        var sourceNone = new BindingAgentToolSource(new BindingToolOptions());
        var toolsNone = await sourceNone.DiscoverToolsAsync();
        toolsNone.Should().BeEmpty();

        // Only query adapter -> 2 read tools
        var sourceQueryOnly = new BindingAgentToolSource(
            new BindingToolOptions(),
            queryAdapter: new StubQueryAdapter([]));
        var toolsQueryOnly = await sourceQueryOnly.DiscoverToolsAsync();
        toolsQueryOnly.Should().HaveCount(2);
        toolsQueryOnly.Should().Contain(t => t is BindingListTool);
        toolsQueryOnly.Should().Contain(t => t is BindingStatusTool);
    }

    #endregion

    #region Stubs

    private sealed class StubQueryAdapter : IScopeBindingQueryAdapter
    {
        private readonly IReadOnlyList<ScopeBindingEntry> _entries;
        private readonly ScopeBindingHealthStatus? _healthStatus;

        public StubQueryAdapter(
            IReadOnlyList<ScopeBindingEntry> entries,
            ScopeBindingHealthStatus? healthStatus = null)
        {
            _entries = entries;
            _healthStatus = healthStatus;
        }

        public Task<IReadOnlyList<ScopeBindingEntry>> ListAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(_entries);

        public Task<ScopeBindingHealthStatus?> GetStatusAsync(string scopeId, string serviceId, CancellationToken ct = default) =>
            Task.FromResult(_healthStatus);
    }

    private sealed class StubCommandPort : IScopeBindingCommandPort
    {
        private readonly Action<ScopeBindingUpsertRequest>? _captureRequest;

        public StubCommandPort(Action<ScopeBindingUpsertRequest>? captureRequest = null)
        {
            _captureRequest = captureRequest;
        }

        public Task<ScopeBindingUpsertResult> UpsertAsync(ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            _captureRequest?.Invoke(request);

            return Task.FromResult(new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? "auto-generated-id",
                DisplayName: request.DisplayName ?? "Unnamed",
                RevisionId: "rev-stub",
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "actor-stub"));
        }
    }

    private sealed class StubUnbindAdapter : IScopeBindingUnbindAdapter
    {
        private readonly ScopeBindingUnbindResult _result;

        public StubUnbindAdapter(ScopeBindingUnbindResult result)
        {
            _result = result;
        }

        public Task<ScopeBindingUnbindResult> UnbindAsync(string scopeId, string serviceId, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    #endregion
}
