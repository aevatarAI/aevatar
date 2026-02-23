using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class WorkflowGAgentCoverageTests
{
    [Fact]
    public async Task ConfigureWorkflow_WhenSwitchingWorkflowName_ShouldThrow()
    {
        var agent = CreateAgent();
        await agent.ConfigureWorkflowAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_a");

        Func<Task> act = () => agent.ConfigureWorkflowAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_b");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot switch*");
    }

    [Fact]
    public async Task ConfigureWorkflow_WithEmptyYaml_ShouldMarkInvalidAndDescribe()
    {
        var agent = CreateAgent();

        await agent.ConfigureWorkflowAsync("", "wf_empty");
        var description = await agent.GetDescriptionAsync();

        agent.State.Compiled.Should().BeFalse();
        agent.State.CompilationError.Should().Be("workflow yaml is empty");
        description.Should().Contain("invalid");
        description.Should().Contain("wf_empty");
    }

    [Fact]
    public async Task HandleChatRequest_WhenNotCompiled_ShouldPublishFailureResponse()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.ConfigureWorkflowAsync("", "wf_invalid");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "s1",
        });

        runtime.CreateCalls.Should().Be(0);
        publisher.Published.Should().ContainSingle();
        var response = publisher.Published[0].evt.Should().BeOfType<ChatResponseEvent>().Subject;
        response.Content.Should().Contain("未编译");
        response.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task HandleChatRequest_WhenCompiled_ShouldCreateRoleActorsOnlyOnceAndStartWorkflow()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        agent.EventPublisher = publisher;
        await agent.ConfigureWorkflowAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_ok");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "second", SessionId = "s2" });

        runtime.CreateCalls.Should().Be(1);
        runtime.Linked.Should().ContainSingle();
        runtime.Linked[0].child.Should().EndWith(":role_a");

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.RoleName.Should().Be("RoleA");
        roleAgent.LastConfig.Should().NotBeNull();
        roleAgent.LastConfig!.ProviderName.Should().Be("deepseek");
        roleAgent.LastConfig.SystemPrompt.Should().Be("helpful role");

        var starts = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().ToList();
        starts.Should().HaveCount(2);
        starts.Should().OnlyContain(x => x.WorkflowName == "wf_valid");
    }

    [Fact]
    public async Task HandleChatRequest_WhenResolvedAgentNotIRoleAgent_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeNonRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        await agent.ConfigureWorkflowAsync(BuildValidWorkflowYaml("role_x", "RoleX"), "wf_error");

        var act = async () => await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not implement IRoleAgent*");
    }

    [Fact]
    public async Task HandleChatRequest_WhenRoleIdMissing_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        await agent.ConfigureWorkflowAsync(BuildValidWorkflowYaml("", "RoleNoId"), "wf_missing_role");

        var act = async () => await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role id is required*");
    }

    [Fact]
    public async Task HandleWorkflowCompleted_ShouldUpdateCountersAndPublishFinalText()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf",
            Success = true,
            Output = "done",
        });

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf",
            Success = false,
            Error = "boom",
        });

        agent.State.TotalExecutions.Should().Be(2);
        agent.State.SuccessfulExecutions.Should().Be(1);
        agent.State.FailedExecutions.Should().Be(1);

        var outputs = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Select(x => x.Content).ToList();
        outputs.Should().Contain("done");
        outputs.Should().Contain(x => x.Contains("失败") && x.Contains("boom"));
    }

    [Fact]
    public async Task ConfigureWorkflow_ShouldInstallAndConfigureModules()
    {
        var factory = new RecordingEventModuleFactory();
        var expander = new StaticDependencyExpander(10, "module_a", "module_b");
        var configurator = new RecordingModuleConfigurator();
        var pack = new TestModulePack([expander], [configurator]);
        var agent = CreateAgent(eventModuleFactory: factory, packs: [pack]);

        await agent.ConfigureWorkflowAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_modules");

        agent.GetModules().Select(x => x.Name).Should().BeEquivalentTo(["module_a", "module_b"]);
        configurator.Configured.Should().BeEquivalentTo(["module_a:wf_valid", "module_b:wf_valid"]);
        factory.CreatedNames.Should().BeEquivalentTo(["module_a", "module_b"]);
    }

    [Fact]
    public async Task ActivateAsync_WhenStateContainsWorkflowYaml_ShouldCompileAndInstallModules()
    {
        var factory = new RecordingEventModuleFactory();
        var expander = new StaticDependencyExpander(0, "module_on_activate");
        var configurator = new RecordingModuleConfigurator();
        var pack = new TestModulePack([expander], [configurator]);
        var sharedEventStore = new InMemoryEventStore();

        var agent1 = CreateAgent(eventModuleFactory: factory, packs: [pack], eventStore: sharedEventStore);
        await agent1.ActivateAsync();
        await agent1.ConfigureWorkflowAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_activate");
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(eventModuleFactory: factory, packs: [pack], eventStore: sharedEventStore);
        await agent2.ActivateAsync();

        agent2.State.Compiled.Should().BeTrue();
        agent2.GetModules().Should().ContainSingle(x => x.Name == "module_on_activate");
        configurator.Configured.Should().Contain(x => x == "module_on_activate:wf_valid");
    }

    private static WorkflowGAgent CreateAgent(
        RecordingActorRuntime? runtime = null,
        IRoleAgentTypeResolver? roleResolver = null,
        IEventModuleFactory? eventModuleFactory = null,
        IEnumerable<IWorkflowModulePack>? packs = null,
        IEventStore? eventStore = null)
    {
        runtime ??= new RecordingActorRuntime();
        roleResolver ??= new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        eventModuleFactory ??= new RecordingEventModuleFactory();
        packs ??= [];
        eventStore ??= new InMemoryEventStore();
        var agent = new WorkflowGAgent(runtime, roleResolver, eventModuleFactory, packs)
        {
            Services = new ServiceCollection()
                .AddSingleton(eventStore)
                .BuildServiceProvider(),
        };
        return agent;
    }

    private static string BuildValidWorkflowYaml(string roleId, string roleName)
    {
        return $$"""
                 name: wf_valid
                 roles:
                   - id: "{{roleId}}"
                     name: "{{roleName}}"
                     system_prompt: "helpful role"
                 steps:
                   - id: step_1
                     type: transform
                 """;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            Published.Add((evt, EventDirection.Self));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public int CreateCalls { get; private set; }
        public List<IActor> CreatedActors { get; } = [];
        public List<(string parent, string child)> Linked { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            return CreateAsync(typeof(TAgent), id, ct);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreateCalls++;
            var actorId = id ?? $"actor-{CreateCalls}";
            IAgent agent = agentType == typeof(FakeRoleAgent)
                ? new FakeRoleAgent(actorId)
                : new FakeNonRoleAgent(actorId);

            var actor = new FakeActor(actorId, agent);
            CreatedActors.Add(actor);
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(CreatedActors.FirstOrDefault(x => x.Id == id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(CreatedActors.Any(x => x.Id == id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Agent.HandleEventAsync(envelope, ct);
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeRoleAgent(string id) : IRoleAgent
    {
        public string Id { get; } = id;
        public string RoleName { get; private set; } = "";
        public RoleAgentConfig? LastConfig { get; private set; }

        public void SetRoleName(string name) => RoleName = name;
        public Task ConfigureAsync(RoleAgentConfig config, CancellationToken ct = default)
        {
            LastConfig = config;
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope.Payload?.Is(ConfigureRoleAgentEvent.Descriptor) == true)
            {
                var evt = envelope.Payload.Unpack<ConfigureRoleAgentEvent>();
                SetRoleName(evt.RoleName);
                LastConfig = new RoleAgentConfig
                {
                    ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? "deepseek" : evt.ProviderName,
                    Model = string.IsNullOrWhiteSpace(evt.Model) ? null : evt.Model,
                    SystemPrompt = evt.SystemPrompt ?? string.Empty,
                };
            }

            return Task.CompletedTask;
        }
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-role");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeNonRoleAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-non-role");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticRoleAgentTypeResolver(Type roleAgentType) : IRoleAgentTypeResolver
    {
        public Type ResolveRoleAgentType() => roleAgentType;
    }

    private sealed class RecordingEventModuleFactory : IEventModuleFactory
    {
        public List<string> CreatedNames { get; } = [];

        public bool TryCreate(string name, out IEventModule? module)
        {
            CreatedNames.Add(name);
            module = new RecordingEventModule(name);
            return true;
        }
    }

    private sealed class RecordingEventModule(string name) : IEventModule
    {
        public string Name { get; } = name;
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => false;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StaticDependencyExpander(int order, params string[] moduleNames) : IWorkflowModuleDependencyExpander
    {
        public int Order { get; } = order;

        public void Expand(WorkflowDefinition? workflow, ISet<string> names)
        {
            _ = workflow;
            foreach (var moduleName in moduleNames)
                names.Add(moduleName);
        }
    }

    private sealed class RecordingModuleConfigurator : IWorkflowModuleConfigurator
    {
        public int Order => 0;
        public List<string> Configured { get; } = [];

        public void Configure(IEventModule module, WorkflowDefinition workflow)
        {
            Configured.Add($"{module.Name}:{workflow.Name}");
        }
    }

    private sealed class TestModulePack(
        IReadOnlyList<IWorkflowModuleDependencyExpander> expanders,
        IReadOnlyList<IWorkflowModuleConfigurator> configurators) : IWorkflowModulePack
    {
        public string Name => "test-pack";
        public IReadOnlyList<WorkflowModuleRegistration> Modules => [];
        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = expanders;
        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = configurators;
    }
}
