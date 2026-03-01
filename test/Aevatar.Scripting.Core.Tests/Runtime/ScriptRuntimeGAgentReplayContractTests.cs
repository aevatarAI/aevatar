using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptRuntimeGAgentReplayContractTests
{
    [Fact]
    public async Task HandleRunRequested_ShouldPersistDomainEvent_AndMutateViaTransitionOnly()
    {
        var definition = CreateDefinitionAgent();
        await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            SourceText = BuildStatefulRuntimeSource(),
            SourceHash = "hash-1",
        });

        var agent = CreateRuntimeAgent(definition);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-1",
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        agent.State.LastRunId.Should().Be("run-1");
        agent.State.Revision.Should().Be("rev-1");
        agent.State.DefinitionActorId.Should().Be("definition-1");
        agent.State.StatePayloads.Should().ContainKey("state");
        agent.State.StatePayloads["state"].Unpack<Int32Value>().Value.Should().Be(1);
        agent.State.ReadModelPayloads.Should().ContainKey("view");
        agent.State.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("RuntimeContractEvent");
        agent.State.LastAppliedSchemaVersion.Should().Be("7");
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleRunRequested_ShouldCarryStateAndReadModelPayloadBetweenRuns_ByScriptApplyAndReduce()
    {
        var definition = CreateDefinitionAgent();
        await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-stateful-1",
            ScriptRevision = "rev-stateful-1",
            SourceText = BuildStatefulRuntimeSource(),
            SourceHash = "hash-stateful-1",
        });

        var agent = CreateRuntimeAgent(definition);

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-state-1",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-stateful-1",
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        agent.State.StatePayloads.Should().ContainKey("state");
        agent.State.StatePayloads["state"].Unpack<Int32Value>().Value.Should().Be(1);
        agent.State.ReadModelPayloads.Should().ContainKey("view");
        agent.State.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("RuntimeContractEvent");
        agent.State.LastAppliedSchemaVersion.Should().Be("7");

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-state-2",
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = "rev-stateful-1",
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        agent.State.StatePayloads.Should().ContainKey("state");
        agent.State.StatePayloads["state"].Unpack<Int32Value>().Value.Should().Be(2);
        agent.State.ReadModelPayloads.Should().ContainKey("view");
        agent.State.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("RuntimeContractEvent");
        agent.State.LastAppliedSchemaVersion.Should().Be("7");
    }

    private static ScriptRuntimeGAgent CreateRuntimeAgent(ScriptDefinitionGAgent definition)
    {
        var orchestrator = new ScriptRuntimeExecutionOrchestrator(
            new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()),
            new NullAICapability(),
            new NullEventRoutingPort(),
            new NullInvocationPort(),
            new NullFactoryPort());

        return new ScriptRuntimeGAgent(orchestrator, new StaticSnapshotPort(definition))
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
        };
    }

    private static ScriptDefinitionGAgent CreateDefinitionAgent()
    {
        return new ScriptDefinitionGAgent(
            new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()),
            new DefaultScriptReadModelSchemaActivationPolicy())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
                new InMemoryEventStore()),
        };
    }

    private static string BuildStatefulRuntimeSource()
    {
        return """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class StatefulRuntimeScript : IScriptPackageRuntime, IScriptContractProvider
{
    public ScriptContractManifest ContractManifest => new(
        "runtime_case_v1",
        new[] { "RuntimeContractEvent" },
        "runtime_state_v1",
        "runtime_case_readmodel_v7",
        new ScriptReadModelDefinition(
            "runtime_case",
            "7",
            new[]
            {
                new ScriptReadModelFieldDefinition("status", "keyword", "status", false),
            },
            new[]
            {
                new ScriptReadModelIndexDefinition("idx_status", new[] { "status" }, false, "elasticsearch"),
            },
            new ScriptReadModelRelationDefinition[] { }),
        new[] { "elasticsearch" });

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptHandlerResult(
            new IMessage[] { new StringValue { Value = "RuntimeContractEvent" } }));
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var step = 0;
        if (currentState.TryGetValue("state", out var statePayload) && statePayload != null)
        {
            try
            {
                step = statePayload.Unpack<Int32Value>().Value;
            }
            catch
            {
                step = 0;
            }
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["state"] = Any.Pack(new Int32Value { Value = step + 1 }),
            });
    }

    public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(
            new Dictionary<string, Any>
            {
                ["view"] = Any.Pack(new StringValue { Value = domainEvent.EventType }),
            });
    }
}
""";
    }

    private sealed class StaticSnapshotPort(ScriptDefinitionGAgent definition) : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            definitionActorId.Should().Be("definition-1");
            var snapshot = definition.GetSnapshot();
            if (!string.IsNullOrWhiteSpace(requestedRevision))
                snapshot.Revision.Should().Be(requestedRevision);
            snapshot.SourceText.Should().NotBeNullOrWhiteSpace();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class NullAICapability : IAICapability
    {
        public Task<string> AskAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct) => Task.FromResult("noop");
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
