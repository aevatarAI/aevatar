using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptRuntimeGAgentReplayContractTests
{
    [Fact]
    public async Task HandleRunRequested_ShouldPersistDomainEvent_AndMutateViaTransitionOnly()
    {
        var definition = new ScriptDefinitionGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
                new InMemoryEventStore()),
        };
        await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            SourceText = BuildStatefulRuntimeSource(),
            SourceHash = "hash-1",
        });

        var agent = new ScriptRuntimeGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
        };
        agent.Services = BuildServices(definition);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            InputJson = "{}",
            ScriptRevision = "rev-1",
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        agent.State.LastRunId.Should().Be("run-1");
        agent.State.Revision.Should().Be("rev-1");
        agent.State.DefinitionActorId.Should().Be("definition-1");
        agent.State.StatePayloadJson.Should().Be("{\"step\":1,\"last_event\":\"RuntimeContractEvent\"}");
        agent.State.ReadModelPayloadJson.Should().Be("{\"status\":\"RuntimeContractEvent\"}");
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleRunRequested_ShouldCarryStateAndReadModelPayloadBetweenRuns_ByScriptApplyAndReduce()
    {
        var definition = new ScriptDefinitionGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
                new InMemoryEventStore()),
        };
        await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-stateful-1",
            ScriptRevision = "rev-stateful-1",
            SourceText = BuildStatefulRuntimeSource(),
            SourceHash = "hash-stateful-1",
        });

        var agent = new ScriptRuntimeGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
        };
        agent.Services = BuildServices(definition);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-state-1",
            InputJson = "{}",
            ScriptRevision = "rev-stateful-1",
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        agent.State.StatePayloadJson.Should().Be("{\"step\":1,\"last_event\":\"RuntimeContractEvent\"}");
        agent.State.ReadModelPayloadJson.Should().Be("{\"status\":\"RuntimeContractEvent\"}");

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-state-2",
            InputJson = "{}",
            ScriptRevision = "rev-stateful-1",
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        agent.State.StatePayloadJson.Should().Be("{\"step\":2,\"last_event\":\"RuntimeContractEvent\"}");
        agent.State.ReadModelPayloadJson.Should().Be("{\"status\":\"RuntimeContractEvent\"}");
    }

    private static IServiceProvider BuildServices(ScriptDefinitionGAgent definition)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScriptExecutionEngine, RoslynScriptExecutionEngine>();
        services.AddSingleton<IScriptPackageCompiler>(new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()));
        services.AddSingleton<IScriptCapabilityFactory, DefaultScriptCapabilityFactory>();
        services.AddSingleton<IScriptRuntimeExecutionOrchestrator, ScriptRuntimeExecutionOrchestrator>();
        services.AddSingleton<IActorRuntime>(new DefinitionOnlyRuntime(definition));
        services.AddSingleton<Aevatar.Scripting.Core.Ports.IGAgentEventRoutingPort, NullEventRoutingPort>();
        services.AddSingleton<Aevatar.Scripting.Core.Ports.IGAgentInvocationPort, NullInvocationPort>();
        services.AddSingleton<Aevatar.Scripting.Core.Ports.IGAgentFactoryPort, NullFactoryPort>();
        return services.BuildServiceProvider();
    }

    private static string BuildStatefulRuntimeSource()
    {
        return """
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class StatefulRuntimeScript : IScriptPackageRuntime
{
    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "RuntimeContractEvent" } }));
    }

    public ValueTask<string> ApplyDomainEventAsync(
        string currentStateJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var step = 0;
        if (!string.IsNullOrWhiteSpace(currentStateJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(currentStateJson);
                if (doc.RootElement.TryGetProperty("step", out var stepElement) && stepElement.ValueKind == JsonValueKind.Number)
                    step = stepElement.GetInt32();
            }
            catch
            {
                step = 0;
            }
        }

        step += 1;
        return ValueTask.FromResult("{\"step\":" + step + ",\"last_event\":\"" + domainEvent.EventType + "\"}");
    }

    public ValueTask<string> ReduceReadModelAsync(
        string currentReadModelJson,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult("{\"status\":\"" + domainEvent.EventType + "\"}");
    }
}
""";
    }

    private sealed class DefinitionOnlyRuntime(ScriptDefinitionGAgent definition) : IActorRuntime
    {
        private readonly IActor _actor = new DefinitionActor(definition);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(string.Equals(id, "definition-1", StringComparison.Ordinal) ? _actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(string.Equals(id, "definition-1", StringComparison.Ordinal));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DefinitionActor(ScriptDefinitionGAgent definition) : IActor
    {
        public string Id => "definition-1";
        public IAgent Agent => definition;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NullEventRoutingPort : Aevatar.Scripting.Core.Ports.IGAgentEventRoutingPort
    {
        public Task PublishAsync(string sourceActorId, IMessage eventPayload, EventDirection direction, string correlationId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task SendToAsync(string sourceActorId, string targetActorId, IMessage eventPayload, string correlationId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NullInvocationPort : Aevatar.Scripting.Core.Ports.IGAgentInvocationPort
    {
        public Task InvokeAsync(string targetAgentId, IMessage eventPayload, string correlationId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NullFactoryPort : Aevatar.Scripting.Core.Ports.IGAgentFactoryPort
    {
        public Task<string> CreateAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
            Task.FromResult(actorId ?? "agent-created");

        public Task DestroyAsync(string actorId, CancellationToken ct) => Task.CompletedTask;

        public Task LinkAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;

        public Task UnlinkAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
    }
}
