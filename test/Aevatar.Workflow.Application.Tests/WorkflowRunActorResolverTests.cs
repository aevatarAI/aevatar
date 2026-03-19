using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunActorResolverTests
{
    [Fact]
    public async Task ResolveOrCreateAsync_ShouldCreateRunFromRequestedRegistryWorkflow()
    {
        var bindingReader = new StaticWorkflowActorBindingReader(null);
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("direct", "name: direct\nroles: []\nsteps: []\n");
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", " direct ", null),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("direct");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be("name: direct\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldForwardScopeIdFromMetadata()
    {
        var bindingReader = new StaticWorkflowActorBindingReader(null);
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("direct", "name: direct\nroles: []\nsteps: []\n");
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest(
                "hello",
                "direct",
                null,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "scope-user-1",
                }),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].ScopeId.Should().Be("scope-user-1");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldUseAutoWorkflow_WhenConfiguredAsDefault()
    {
        var bindingReader = new StaticWorkflowActorBindingReader(null);
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("auto", "name: auto\nroles: []\nsteps: []\n");
        var resolver = new WorkflowRunActorResolver(
            bindingReader,
            actorPort,
            registry,
            new WorkflowRunBehaviorOptions
            {
                UseAutoAsDefaultWhenWorkflowUnspecified = true,
            });

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, null),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("auto");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("auto");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldUseConfiguredDefaultWorkflowName_WhenWorkflowUnspecified()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("review", "name: review\nroles: []\nsteps: []\n");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            registry,
            new WorkflowRunBehaviorOptions
            {
                DefaultWorkflowName = "review",
            });

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, null),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        result.WorkflowNameForRun.Should().Be("review");
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("review");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnWorkflowNotFound_WhenRegistryDoesNotContainWorkflow()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "missing", null),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNotFound);
        result.Actor.Should().BeNull();
        result.WorkflowNameForRun.Should().Be("missing");
    }

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

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldRejectInlineRun_WhenRequestedWorkflowNameDiffersFromEntryWorkflow()
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
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        actorPort.ParseResults[helperWorkflowYaml] = WorkflowYamlParseResult.Success("helper");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "auto", null, WorkflowYamls: [entryWorkflowYaml, helperWorkflowYaml]),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowNameMismatch);
        result.WorkflowNameForRun.Should().Be("inline_entry");
        result.Actor.Should().BeNull();
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnInvalidWorkflowYaml_WhenInlineYamlBundleIsInvalid()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults["bad"] = WorkflowYamlParseResult.Invalid("bad yaml");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, null, WorkflowYamls: ["bad"]),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnInvalidWorkflowYaml_WhenInlineWorkflowNamesDuplicate()
    {
        const string firstYaml =
            """
            name: duplicate
            roles: []
            steps: []
            """;
        const string secondYaml =
            """
            name: duplicate
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort();
        actorPort.ParseResults[firstYaml] = WorkflowYamlParseResult.Success("duplicate");
        actorPort.ParseResults[secondYaml] = WorkflowYamlParseResult.Success("duplicate");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, null, WorkflowYamls: [firstYaml, secondYaml]),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.InvalidWorkflowYaml);
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentNotFound_WhenSourceActorBindingMissing()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-404"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentNotFound);
        result.Actor.Should().BeNull();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentTypeNotSupported_WhenSourceActorIsUnsupported()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(WorkflowActorBinding.Unsupported("agent-1")),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentTypeNotSupported);
        result.Actor.Should().BeNull();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentWorkflowNotConfigured_WhenBoundWorkflowNameMissing()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    string.Empty,
                    "run-1",
                    string.Empty,
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentWorkflowNotConfigured);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnWorkflowBindingMismatch_WhenRequestedWorkflowDiffersFromBoundWorkflow()
    {
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    "definition-1",
                    "run-1",
                    "bound",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            new RecordingWorkflowRunActorPort(),
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "requested", "agent-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.WorkflowBindingMismatch);
        result.WorkflowNameForRun.Should().Be("bound");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldUseRegistryYaml_WhenSourceBindingHasWorkflowNameOnly()
    {
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("direct", "name: direct\nroles: []\nsteps: []\n");
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    string.Empty,
                    "run-1",
                    "direct",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            actorPort,
            registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be("name: direct\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldPreferSourceBindingYamlAndDefinitionActorId_WhenPresent()
    {
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("direct", "name: direct\nroles: []\nsteps: []\n");
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    "definition-bound",
                    "run-1",
                    "direct",
                    "name: source\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            actorPort,
            registry);

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-bound");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be("name: source\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldKeepExistingBindingForOpaqueActorId_WhileNewRunUsesLatestRegistryDefinition()
    {
        const string opaqueActorId = "script-runtime:legacy-worker-42";
        const string legacyYaml =
            """
            name: direct
            description: legacy implementation
            roles: []
            steps: []
            """;
        const string latestYaml =
            """
            name: direct
            description: latest implementation
            roles: []
            steps: []
            """;
        var bindingReader = new RecordingWorkflowActorBindingReader();
        bindingReader.Register(
            opaqueActorId,
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                opaqueActorId,
                "definition-direct-legacy",
                "run-legacy",
                "direct",
                legacyYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        var actorPort = new RecordingWorkflowRunActorPort();
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("direct", latestYaml);
        var resolver = new WorkflowRunActorResolver(bindingReader, actorPort, registry);

        var boundResult = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, opaqueActorId),
            CancellationToken.None);

        boundResult.Error.Should().Be(WorkflowChatRunStartError.None);
        bindingReader.LastActorId.Should().Be(opaqueActorId);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct-legacy");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(legacyYaml);

        actorPort.CreateRunBindings.Clear();

        var freshResult = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            CancellationToken.None);

        freshResult.Error.Should().Be(WorkflowChatRunStartError.None);
        actorPort.CreateRunBindings.Should().ContainSingle();
        actorPort.CreateRunBindings[0].DefinitionActorId.Should().Be("definition-direct");
        actorPort.CreateRunBindings[0].WorkflowName.Should().Be("direct");
        actorPort.CreateRunBindings[0].WorkflowYaml.Should().Be(latestYaml);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldReturnAgentWorkflowNotConfigured_WhenBoundWorkflowYamlMissingEverywhere()
    {
        var actorPort = new RecordingWorkflowRunActorPort();
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(
                new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    "agent-1",
                    string.Empty,
                    "run-1",
                    "direct",
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var result = await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, "agent-1"),
            CancellationToken.None);

        result.Error.Should().Be(WorkflowChatRunStartError.AgentWorkflowNotConfigured);
        actorPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldWrapFallbackEligibleCreateFailures()
    {
        var actorPort = new RecordingWorkflowRunActorPort
        {
            CreateRunException = new InvalidOperationException("boom"),
        };
        var registry = new InMemoryWorkflowDefinitionRegistry();
        registry.Register("direct", "name: direct\nroles: []\nsteps: []\n");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            registry);

        var act = async () => await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", "direct", null),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkflowDirectFallbackTriggerException>();
        ex.Which.InnerException.Should().Be(actorPort.CreateRunException);
    }

    [Fact]
    public async Task ResolveOrCreateAsync_ShouldNotWrapCreateFailure_ForInlineWorkflowRun()
    {
        const string entryWorkflowYaml =
            """
            name: inline_entry
            roles: []
            steps: []
            """;
        var actorPort = new RecordingWorkflowRunActorPort
        {
            CreateRunException = new InvalidOperationException("inline failed"),
        };
        actorPort.ParseResults[entryWorkflowYaml] = WorkflowYamlParseResult.Success("inline_entry");
        var resolver = new WorkflowRunActorResolver(
            new StaticWorkflowActorBindingReader(null),
            actorPort,
            new InMemoryWorkflowDefinitionRegistry());

        var act = async () => await resolver.ResolveOrCreateAsync(
            new WorkflowChatRunRequest("hello", null, null, WorkflowYamls: [entryWorkflowYaml]),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Be("inline failed");
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

    private sealed class RecordingWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        private readonly Dictionary<string, WorkflowActorBinding> _bindings = new(StringComparer.Ordinal);

        public string? LastActorId { get; private set; }

        public void Register(string actorId, WorkflowActorBinding binding) =>
            _bindings[actorId] = binding;

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastActorId = actorId;
            return Task.FromResult(_bindings.GetValueOrDefault(actorId));
        }
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public Dictionary<string, WorkflowYamlParseResult> ParseResults { get; } = new(StringComparer.Ordinal);
        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];
        public Exception? CreateRunException { get; set; }

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (CreateRunException != null)
                throw CreateRunException;
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
            string? scopeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

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
        private readonly Dictionary<string, WorkflowDefinitionRegistration> _definitions = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string name, string yaml)
        {
            var normalizedName = name.Trim();
            _definitions[normalizedName] = new WorkflowDefinitionRegistration(
                normalizedName,
                yaml,
                $"definition-{normalizedName}");
        }

        public WorkflowDefinitionRegistration? GetDefinition(string name) =>
            _definitions.TryGetValue(name, out var registration)
                ? registration
                : null;

        public string? GetYaml(string name) =>
            _definitions.TryGetValue(name, out var registration)
                ? registration.WorkflowYaml
                : null;

        public IReadOnlyList<string> GetNames() => _definitions.Keys.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
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
