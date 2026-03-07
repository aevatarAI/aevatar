using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
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
        var capabilityComposer = new ScriptRuntimeCapabilityComposer(
            new NullAICapability(),
            new NullAgentRuntimePort(),
            new NullLifecyclePort(),
            new NullEvolutionProjectionLifecyclePort(),
            new NullEvolutionQueryPort(),
            new StaticAddressResolver());

        var orchestrator = new ScriptRuntimeExecutionOrchestrator(
            new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()),
            capabilityComposer);

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
        public bool UseEventDrivenDefinitionQuery => false;

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            definitionActorId.Should().Be("definition-1");
            var snapshot = new ScriptDefinitionSnapshot(
                ScriptId: definition.State.ScriptId ?? string.Empty,
                Revision: definition.State.Revision ?? string.Empty,
                SourceText: definition.State.SourceText ?? string.Empty,
                ReadModelSchemaVersion: definition.State.ReadModelSchemaVersion ?? string.Empty,
                ReadModelSchemaHash: definition.State.ReadModelSchemaHash ?? string.Empty);
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

    private sealed class NullAgentRuntimePort : Aevatar.Scripting.Core.Ports.IGAgentRuntimePort
    {
        public Task<string> CreateAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
            Task.FromResult(actorId ?? "agent-created");

        public Task PublishAsync(
            string sourceActorId,
            IMessage eventPayload,
            EventDirection direction,
            string correlationId,
            CancellationToken ct) => Task.CompletedTask;

        public Task SendToAsync(
            string sourceActorId,
            string targetActorId,
            IMessage eventPayload,
            string correlationId,
            CancellationToken ct) => Task.CompletedTask;

        public Task InvokeAsync(
            string targetAgentId,
            IMessage eventPayload,
            string correlationId,
            CancellationToken ct) => Task.CompletedTask;

        public Task DestroyAsync(string actorId, CancellationToken ct) => Task.CompletedTask;

        public Task LinkAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;

        public Task UnlinkAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullLifecyclePort : IScriptLifecyclePort
    {
        public Task<ScriptEvolutionCommandAccepted> ProposeAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptEvolutionCommandAccepted(
                proposal.ProposalId ?? string.Empty,
                proposal.ScriptId ?? string.Empty,
                $"script-evolution-session:{proposal.ProposalId}"));
        }

        public Task<string> UpsertDefinitionAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct)
        {
            _ = scriptId;
            _ = scriptRevision;
            _ = sourceText;
            _ = sourceHash;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(definitionActorId ?? "definition-1");
        }

        public Task<string> SpawnRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = scriptRevision;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(runtimeActorId ?? "runtime-1");
        }

        public Task<ScriptRuntimeRunAccepted> RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct)
        {
            _ = runtimeActorId;
            _ = runId;
            _ = inputPayload;
            _ = scriptRevision;
            _ = definitionActorId;
            _ = requestedEventType;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptRuntimeRunAccepted(runtimeActorId, runId, definitionActorId, scriptRevision));
        }

        public Task PromoteCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string expectedBaseRevision,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            _ = expectedBaseRevision;
            _ = revision;
            _ = definitionActorId;
            _ = sourceHash;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            _ = targetRevision;
            _ = reason;
            _ = proposalId;
            _ = expectedCurrentRevision;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
            string? catalogActorId,
            string scriptId,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = scriptId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptCatalogEntrySnapshot?>(null);
        }
    }

    private sealed class NullEvolutionProjectionLifecyclePort : IScriptEvolutionProjectionLifecyclePort
    {
        public bool ProjectionEnabled => false;

        public Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
            string sessionActorId,
            string proposalId,
            CancellationToken ct)
        {
            _ = sessionActorId;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IScriptEvolutionProjectionLease?>(null);
        }

        public Task AttachLiveSinkAsync(
            IScriptEvolutionProjectionLease lease,
            IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IScriptEvolutionProjectionLease lease,
            IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptEvolutionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NullEvolutionQueryPort : IScriptEvolutionProjectionQueryPort
    {
        public Task<ScriptEvolutionProposalSnapshot?> GetProposalSnapshotAsync(
            string proposalId,
            CancellationToken ct = default)
        {
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptEvolutionProposalSnapshot?>(null);
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
        public string GetRuntimeActorId(string definitionActorId, string revision) =>
            $"script-runtime:{definitionActorId}:{revision}";
        public string GetCatalogActorId() => "script-catalog";
        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";
    }
}
