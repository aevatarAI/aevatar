using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Aevatar.Workflow.Infrastructure.Workflows;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowDefinitionCatalogTests
{
    [Fact]
    public void Register_And_GetYaml()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("test", "name: test\nsteps: []");

        registry.GetYaml("test").Should().Contain("name: test");
        registry.GetYaml("TEST").Should().NotBeNull(); // 不区分大小写
        registry.GetYaml("nonexistent").Should().BeNull();
        registry.GetDefinition("test")!.DefinitionActorId.Should().Be(WorkflowDefinitionActorId.Format("test"));
    }

    [Fact]
    public void GetNames_ReturnsAll()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("alpha", "a");
        registry.Register("beta", "b");

        registry.GetNames().Should().HaveCount(2);
    }

    [Fact]
    public void FileLoader_NonExistentDirectory_ReturnsZero()
    {
        var registry = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        var loaded = loader.LoadInto(
            registry,
            ["/nonexistent/path/12345"],
            NullLogger.Instance);

        loaded.Should().Be(0);
    }

    [Fact]
    public void FileLoader_LoadsYamlFiles()
    {
        // 创建临时目录
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "chat.yml"), "name: chat");
            File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "not a workflow");

            var registry = new WorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();
            var count = loader.LoadInto(registry, [tmpDir], NullLogger.Instance);

            count.Should().Be(2);
            registry.GetYaml("review").Should().Contain("review");
            registry.GetYaml("chat").Should().Contain("chat");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_LoadsLarkApprovalInstanceWaitTemplate()
    {
        var registry = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();
        var repoRoot = FindRepoRoot();

        var count = loader.LoadInto(
            registry,
            [Path.Combine(repoRoot, "workflows")],
            NullLogger.Instance);

        count.Should().BeGreaterThan(0);
        var yaml = registry.GetYaml("lark_approval_instance_wait");
        yaml.Should().NotBeNull();
        yaml.Should().Contain("lark_approvals_get");
        yaml.Should().Contain("workflow_call");
        yaml.Should().Contain("duration_ms");

        var workflow = new WorkflowParser().Parse(yaml!);
        workflow.Name.Should().Be("lark_approval_instance_wait");
        workflow.Steps.Should().Contain(step => step.Type == "assign" && step.Parameters["target"] == "lark_approval_wait_request");
        workflow.Steps.Should().Contain(step => step.Type == "tool_call" && step.Parameters["tool"] == "lark_approvals_get");
        workflow.Steps.Should().Contain(step => step.Id == "extract_terminal_kind" && step.Parameters["path"] == "terminal_kind");
        workflow.Steps.Should().Contain(step => step.Type == "switch" && step.Parameters["on"] == "${input}");
        workflow.Steps.Should().Contain(step => step.Id == "route_status" && step.Branches!["approved"] == "finish_terminal");
        workflow.Steps.Should().Contain(step => step.Id == "route_status" && step.Branches!["_default"] == "restore_request_for_retry");
        workflow.Steps.Should().Contain(step => step.Id == "restore_request_for_retry" && step.Parameters["value"] == "${lark_approval_wait_request}");
        workflow.Steps.Should().Contain(step => step.Type == "workflow_call" && step.Parameters["workflow"] == "lark_approval_instance_wait");
        workflow.Steps.Should().Contain(step => step.Type == "delay");
        workflow.Steps.Should().Contain(step => step.Id == "finish_terminal" && step.Parameters["value"] == "${steps.fetch_instance.output}");

        var availableWorkflows = registry.GetNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        WorkflowValidator.Validate(
                workflow,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = true,
                    KnownStepTypes = WorkflowPrimitiveCatalog.BuiltInCanonicalTypes.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    RequireResolvableWorkflowCallTargets = true,
                },
                availableWorkflows)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void FileLoader_DuplicateWorkflowName_ShouldThrow()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "review.yaml"), "name: review");
            File.WriteAllText(Path.Combine(tmpDir, "review.yml"), "name: review_2");

            var registry = new WorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();

            Action act = () => loader.LoadInto(registry, [tmpDir], NullLogger.Instance);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Duplicate workflow definition name*");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateWorkflowName_WithOverridePolicy_ShouldUseFileVersion()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "direct.yaml"), "name: direct\nsteps:\n  - id: from_file\n");

            var registry = new WorkflowDefinitionCatalog();
            registry.Register("direct", "name: direct\nsteps:\n  - id: built_in\n");
            var loader = new WorkflowDefinitionFileLoader();

            var count = loader.LoadInto(
                registry,
                [tmpDir],
                NullLogger.Instance,
                WorkflowDefinitionDuplicatePolicy.Override);

            count.Should().Be(1);
            registry.GetYaml("direct").Should().Contain("from_file");
            registry.GetYaml("direct").Should().NotContain("built_in");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void FileLoader_DuplicateDirectoryEntries_ShouldLoadOnlyOnce()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"wf_test_dup_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            File.WriteAllText(Path.Combine(tmpDir, "brainstorm.yaml"), "name: brainstorm");
            var equivalentPath = Path.Combine(tmpDir, ".");

            var registry = new WorkflowDefinitionCatalog();
            var loader = new WorkflowDefinitionFileLoader();

            var count = loader.LoadInto(registry, [tmpDir, equivalentPath], NullLogger.Instance);

            count.Should().Be(1);
            registry.GetNames().Should().ContainSingle().Which.Should().Be("brainstorm");
        }
        finally
        {
            Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldContainFewShotWorkflowExamples()
    {
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("Example valid workflow YAML (simple deterministic flow):");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("name: normalize_text");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("name: review_summary");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("Do not invent extra fields.");
    }

    [Fact]
    public void BuiltInAutoYaml_ShouldUseRouteTokenSwitchWithoutSubstringHeuristics()
    {
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("id: classify_route");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("id: route_intent");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("type: switch");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("\"workflow\": prepare_workflow_request");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().Contain("\"direct\": prepare_direct_response");
        WorkflowDefinitionCatalog.BuiltInAutoYaml.Should().NotContain("condition: \"```y\"");
    }

    [Fact]
    public void CreateBuiltInAutoYaml_ShouldKeepRouteTokenFlow()
    {
        var autoYaml = WorkflowDefinitionCatalog.CreateBuiltInAutoYaml();

        autoYaml.Should().Contain("id: classify_route");
        autoYaml.Should().Contain("next: route_intent");
        autoYaml.Should().NotContain("condition: \"```y\"");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "workflows")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root for workflow catalog tests.");
    }

    [Fact]
    public void FileLoader_ShouldLoadLarkApprovalWaitTemplates()
    {
        var registry = new WorkflowDefinitionCatalog();
        var loader = new WorkflowDefinitionFileLoader();

        var count = loader.LoadInto(
            registry,
            [Path.Combine(FindRepositoryRoot(), "workflows")],
            NullLogger.Instance);

        count.Should().BeGreaterThanOrEqualTo(2);
        registry.GetYaml("lark_approval_wait").Should().Contain("workflow_call");
        registry.GetYaml("lark_approval_wait").Should().Contain("lark_approval_wait_poll");
        registry.GetYaml("lark_approval_wait").Should().Contain("max_iterations: \"60\"");
        registry.GetYaml("lark_approval_wait").Should().Contain("on: \"${input}\"");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("lark_approvals_get");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("arguments: '{\"instance_code\":\"${json(input)}\"}'");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("duration_ms: \"5000\"");
        registry.GetYaml("lark_approval_wait_poll").Should().Contain("value: \"${steps.get_instance.json.instance_code}\"");
    }

    [Fact]
    public void LarkApprovalWaitTemplates_ShouldUseExpectedWorkflowPrimitives()
    {
        var root = FindRepositoryRoot();
        var waitYaml = File.ReadAllText(Path.Combine(root, "workflows", "lark_approval_wait.yaml"));
        var pollYaml = File.ReadAllText(Path.Combine(root, "workflows", "lark_approval_wait_poll.yaml"));

        waitYaml.Should().Contain("type: while");
        waitYaml.Should().Contain("step: workflow_call");
        waitYaml.Should().Contain("condition: \"${and(not(eq(output, 'approved'))");
        waitYaml.Should().Contain("type: switch");
        waitYaml.Should().Contain("on: \"${input}\"");
        pollYaml.Should().Contain("type: tool_call");
        pollYaml.Should().Contain("tool: lark_approvals_get");
        pollYaml.Should().Contain("arguments: '{\"instance_code\":\"${json(input)}\"}'");
        pollYaml.Should().Contain("type: delay");
        pollYaml.Should().Contain("value: \"${steps.get_instance.json.instance_code}\"");
        waitYaml.Should().NotContain("nyxid_proxy");
        pollYaml.Should().NotContain("nyxid_proxy");
    }

    [Fact]
    public async Task WorkflowExecutionKernel_ShouldMirrorJsonControlFieldsForTemplateBranching()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "get_instance",
                    Type = "tool_call",
                },
                new StepDefinition
                {
                    Id = "route_status",
                    Type = "switch",
                    Parameters = new Dictionary<string, string>
                    {
                        ["on"] = "${steps.get_instance.json.status}",
                    },
                },
            ],
        };
        var ctx = new CapturingContext();
        var kernel = new WorkflowExecutionKernel(workflow, (IWorkflowExecutionStateHost)ctx.Agent);
        const string runId = "run-json-fields";

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = runId,
            Input = "inst_1",
        }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(Wrap(new StepCompletedEvent
        {
            StepId = "get_instance",
            RunId = runId,
            Success = true,
            Output = """{"success":true,"status":"approved","should_continue_waiting":false,"task_count":1}""",
        }), ctx, CancellationToken.None);

        var request = ctx.Published.Single(x => x.Event is StepRequestEvent).Event.Should().BeOfType<StepRequestEvent>().Subject;
        request.StepId.Should().Be("route_status");
        request.Parameters["on"].Should().Be("approved");

        var state = ((IWorkflowExecutionStateHost)ctx.Agent)
            .GetExecutionState("workflow_execution_kernel")!
            .Unpack<WorkflowExecutionKernelState>();
        state.Variables["steps.get_instance.json.success"].Should().Be("true");
        state.Variables["steps.get_instance.json.status"].Should().Be("approved");
        state.Variables["steps.get_instance.json.should_continue_waiting"].Should().Be("false");
        state.Variables["steps.get_instance.json.task_count"].Should().Be("1");
    }

    private static string FindRepositoryRoot() => FindRepoRoot();

    private static EventEnvelope Wrap(IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
    };

    private sealed class CapturingContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";
        public IAgent Agent { get; } = new StubWorkflowRunAgent("agent-1", "run-1");
        public IServiceProvider Services { get; } = new NullServiceProvider();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException("This test context does not support scheduling.");
    }

    private sealed class StubWorkflowRunAgent(string id, string runId) : IAgent, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);

        public string Id => id;
        public string RunId { get; } = runId;
        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();
        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();
        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            _ = delta;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _executionStates.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
