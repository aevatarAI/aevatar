using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Implementations.Local.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunActorPortRuntimeIntegrationTests
{
    [Fact]
    public async Task CreateRunAsync_ShouldPersistDefinitionAndRunBindings_WithLocalRuntime()
    {
        var harness = CreateHarness();
        const string initialYaml = "name: direct\nroles: []\nsteps:\n  - id: old\n    type: delay\n";
        const string updatedYaml = "name: direct\nroles: []\nsteps: []\n";
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["child"] = "name: child\nroles: []\nsteps: []\n",
        };

        var definitionActor = await harness.Port.CreateDefinitionAsync("definition-runtime", CancellationToken.None);
        await harness.Port.BindWorkflowDefinitionAsync(
            definitionActor,
            initialYaml,
            "direct",
            ct: CancellationToken.None);

        var result = await harness.Port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                definitionActor.Id,
                "direct",
                updatedYaml,
                inlineWorkflowYamls),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be(definitionActor.Id);
        result.CreatedActorIds.Should().Equal(result.Actor.Id);

        var definitionBinding = await harness.BindingReader.GetAsync(definitionActor.Id, CancellationToken.None);
        definitionBinding.Should().NotBeNull();
        definitionBinding!.ActorKind.Should().Be(WorkflowActorKind.Definition);
        definitionBinding.ActorId.Should().Be(definitionActor.Id);
        definitionBinding.WorkflowName.Should().Be("direct");
        definitionBinding.WorkflowYaml.Should().Be(updatedYaml);
        definitionBinding.InlineWorkflowYamls.Should().ContainKey("child");

        var runBinding = await harness.BindingReader.GetAsync(result.Actor.Id, CancellationToken.None);
        runBinding.Should().NotBeNull();
        runBinding!.ActorKind.Should().Be(WorkflowActorKind.Run);
        runBinding.ActorId.Should().Be(result.Actor.Id);
        runBinding.DefinitionActorId.Should().Be(definitionActor.Id);
        runBinding.RunId.Should().Be(result.Actor.Id);
        runBinding.WorkflowName.Should().Be("direct");
        runBinding.WorkflowYaml.Should().Be(updatedYaml);
        runBinding.InlineWorkflowYamls.Should().ContainKey("child");

        var runtimeDefinitionActor = await harness.Runtime.GetAsync(definitionActor.Id);
        var runtimeRunActor = await harness.Runtime.GetAsync(result.Actor.Id);
        runtimeDefinitionActor.Should().NotBeNull();
        runtimeRunActor.Should().NotBeNull();
        (await runtimeDefinitionActor!.GetChildrenIdsAsync()).Should().Contain(result.Actor.Id);
        (await runtimeRunActor!.GetParentIdAsync()).Should().Be(definitionActor.Id);

        var definitionAgent = runtimeDefinitionActor.Agent.Should().BeOfType<WorkflowGAgent>().Subject;
        definitionAgent.State.WorkflowYaml.Should().Be(updatedYaml);
        definitionAgent.State.InlineWorkflowYamls.Should().ContainKey("child");

        var runAgent = runtimeRunActor.Agent.Should().BeOfType<WorkflowRunGAgent>().Subject;
        runAgent.State.DefinitionActorId.Should().Be(definitionActor.Id);
        runAgent.State.RunId.Should().Be(result.Actor.Id);
        runAgent.State.WorkflowYaml.Should().Be(updatedYaml);
        runAgent.State.Compiled.Should().BeTrue();
        runAgent.State.InlineWorkflowYamls.Should().ContainKey("child");
    }

    private static RuntimeHarness CreateHarness()
    {
        var forwardingRegistry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            forwardingRegistry);

        LocalActorRuntime? runtime = null;
        var services = new ServiceCollection();
        services.AddSingleton<IStreamProvider>(streams);
        services.AddSingleton<IActorRuntime>(_ => runtime ?? throw new InvalidOperationException("Runtime not initialized."));
        services.AddSingleton<IActorDispatchPort>(_ => new LocalActorDispatchPort(runtime ?? throw new InvalidOperationException("Runtime not initialized.")));
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddSingleton<InMemoryActorRuntimeCallbackScheduler>();
        services.AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
            sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>());
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IRoleAgentTypeResolver, StaticRoleAgentTypeResolver>();
        services.AddSingleton<IEventModuleFactory<IWorkflowExecutionContext>, EmptyWorkflowModuleFactory>();
        services.AddSingleton<IWorkflowModulePack, WorkflowCoreModulePack>();

        var serviceProvider = services.BuildServiceProvider();
        runtime = new LocalActorRuntime(
            streams,
            serviceProvider,
            streams);

        var dispatchPort = serviceProvider.GetRequiredService<IActorDispatchPort>();
        var bindingReader = new RuntimeWorkflowActorBindingReader(
            new RuntimeWorkflowQueryClient(
                streams,
                new RuntimeStreamRequestReplyClient(),
                dispatchPort),
            new DefaultAgentTypeVerifier(new LocalActorTypeProbe(runtime)));

        var port = new WorkflowRunActorPort(
            runtime,
            dispatchPort,
            bindingReader,
            [new WorkflowCoreModulePack()]);

        return new RuntimeHarness(runtime, bindingReader, port);
    }

    private sealed record RuntimeHarness(
        LocalActorRuntime Runtime,
        RuntimeWorkflowActorBindingReader BindingReader,
        WorkflowRunActorPort Port);

    private sealed class EmptyWorkflowModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }

    private sealed class StaticRoleAgentTypeResolver : IRoleAgentTypeResolver
    {
        public Type ResolveRoleAgentType() => typeof(FakeRoleAgent);
    }

    private sealed class FakeRoleAgent(string id) : IRoleAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-role");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
