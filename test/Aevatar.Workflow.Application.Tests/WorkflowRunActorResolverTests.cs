using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunActorResolverTests
{
    [Fact]
    public async Task ResolveOrCreateAsync_ShouldCreateIsolatedInlineRun_WhenAgentIdAndWorkflowYamlsAreProvided()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        const string helperWorkflowYaml =
            """
            name: helper
            roles: []
            steps: []
            """;
        var bindingReader = new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "agent-1",
                "shared-definition-1",
                "source-run-1",
                "inline_entry",
                "name: inline_entry\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["inline_entry"] = "name: inline_entry\nroles: []\nsteps: []\n",
                }));
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        actorPort.ParseResults[helperWorkflowYaml] = WorkflowYamlParseResult.Success("helper");
        var resolver = new WorkflowRunActorResolver(
            bindingReader,
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1", WorkflowYamls: [entryWorkflowYaml, helperWorkflowYaml]),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("inline_entry");
        result.Actor.Should().NotBeNull();
        result.Actor!.Id.Should().Be("run-1");
        result.CreatedActorIds.Should().Equal("definition-isolated-1", "run-1");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().BeEmpty();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("inline_entry");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(entryWorkflowYaml);
        actorPort.CreateRunBindings[0].InlineWorkflowYamls.Should().Contain(
            new KeyValuePair<string, string>("inline_entry", entryWorkflowYaml));
        actorPort.CreateRunBindings[0].InlineWorkflowYamls.Should().Contain(
            new KeyValuePair<string, string>("helper", helperWorkflowYaml));
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldRejectInlineRun_WhenAgentWorkflowBindingConflicts()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        var bindingReader = new StaticWorkflowActorBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "agent-1",
                "shared-definition-1",
                "source-run-1",
                "bound_workflow",
                "name: bound_workflow\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        var resolver = new WorkflowRunActorResolver(
            bindingReader,
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1", WorkflowYamls: [entryWorkflowYaml]),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowBindingMismatch);
        result.WorkflowNameForRun.Should().Be("bound_workflow");
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    private sealed class StaticWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly WorkflowActorBinding? _binding;

        public StaticWorkflowActorBindingReader(WorkflowActorBinding? binding)
        {
            _binding = binding;
        }

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_binding);
        }
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResults { get; } = new(StringComparer.Ordinal);
        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRunBindings.Add(definition);
            return Task.FromResult(
                new WorkflowRunCreationResult(
                    new FakeActor("run-1"),
                    "definition-isolated-1",
                    ["definition-isolated-1", "run-1"]));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                ParseResults.TryGetValue(workflowYaml, out var result)
                    ? result
                    : WorkflowYamlParseResult.Invalid($"Unexpected workflow YAML: {workflowYaml}"));
        }
    }

    private sealed class InMemoryWorkflowDefinitionRegistry : IWorkflowDefinitionRegistry
    {
        public void Register(string name, string yaml) =>
            throw new NotSupportedException();

        public WorkflowDefinitionRegistration? GetDefinition(string name) => null;

        public string? GetYaml(string name) => null;

        public IReadOnlyList<string> GetNames() => [];
    }

    private sealed class FakeActor : IActor
    {
        public FakeActor(string id)
        {
            Id = id;
            Agent = new FakeAgent(id + "-agent");
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent : IAgent
    {
        public FakeAgent(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
