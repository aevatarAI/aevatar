using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
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
using Microsoft.Extensions.DependencyInjection;

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

        var harness = CreateRuntimeHarness();
        var agent = harness.Agent;

        await ExecuteRunAsync(harness, definition, "run-1", "rev-1");

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

        var harness = CreateRuntimeHarness();
        var agent = harness.Agent;

        await ExecuteRunAsync(harness, definition, "run-state-1", "rev-stateful-1");

        agent.State.StatePayloads.Should().ContainKey("state");
        agent.State.StatePayloads["state"].Unpack<Int32Value>().Value.Should().Be(1);
        agent.State.ReadModelPayloads.Should().ContainKey("view");
        agent.State.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("RuntimeContractEvent");
        agent.State.LastAppliedSchemaVersion.Should().Be("7");

        await ExecuteRunAsync(harness, definition, "run-state-2", "rev-stateful-1");

        agent.State.StatePayloads.Should().ContainKey("state");
        agent.State.StatePayloads["state"].Unpack<Int32Value>().Value.Should().Be(2);
        agent.State.ReadModelPayloads.Should().ContainKey("view");
        agent.State.ReadModelPayloads["view"].Unpack<StringValue>().Value.Should().Be("RuntimeContractEvent");
        agent.State.LastAppliedSchemaVersion.Should().Be("7");
    }

    private static RuntimeHarness CreateRuntimeHarness()
    {
        var ports = new NullScriptPorts();
        var capabilityComposer = new ScriptRuntimeCapabilityComposer(
            new NullAICapability(),
            new NullActorRuntime(),
            ports,
            ports,
            ports,
            ports,
            ports);

        var orchestrator = new ScriptRuntimeExecutionOrchestrator(
            new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()),
            capabilityComposer);
        var publisher = new RecordingEventPublisher();

        return new RuntimeHarness(new ScriptRuntimeGAgent(orchestrator)
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler>(new NoOpCallbackScheduler())
                .BuildServiceProvider(),
        }, publisher);
    }

    private static async Task ExecuteRunAsync(
        RuntimeHarness harness,
        ScriptDefinitionGAgent definition,
        string runId,
        string revision)
    {
        harness.Publisher.Sent.Clear();

        await harness.Agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = runId,
            InputPayload = Any.Pack(new Struct()),
            ScriptRevision = revision,
            DefinitionActorId = "definition-1",
            RequestedEventType = "claim.submitted",
        });

        var query = harness.Publisher.Sent
            .Select(x => x.Payload)
            .OfType<QueryScriptDefinitionSnapshotRequestedEvent>()
            .Single();

        await harness.Agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = query.RequestId,
            Found = true,
            ScriptId = definition.State.ScriptId ?? string.Empty,
            Revision = definition.State.Revision ?? string.Empty,
            SourceText = definition.State.SourceText ?? string.Empty,
            ReadModelSchemaVersion = definition.State.ReadModelSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = definition.State.ReadModelSchemaHash ?? string.Empty,
        });
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
    private sealed class NullAICapability : IAICapability
    {
        public Task<string> AskAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct) => Task.FromResult("noop");
    }

    private sealed class NullActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            Task.FromResult<IActor>(new NullActor(id ?? "agent-created"));

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new NullActor(id ?? "agent-created"));

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed record RuntimeHarness(
        ScriptRuntimeGAgent Agent,
        RecordingEventPublisher Publisher);

    private sealed record PublishedMessage(
        string TargetActorId,
        IMessage Payload);

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            BroadcastDirection direction = BroadcastDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }

        public Task PublishCommittedAsync<TEvent>(
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: 1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NullScriptPorts :
        IScriptEvolutionProposalPort,
        IScriptDefinitionCommandPort,
        IScriptRuntimeProvisioningPort,
        IScriptRuntimeCommandPort,
        IScriptCatalogCommandPort
    {
        public Task<ScriptPromotionDecision> ProposeAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ScriptPromotionDecision(
                    Accepted: true,
                    ProposalId: proposal.ProposalId,
                    ScriptId: proposal.ScriptId,
                    BaseRevision: proposal.BaseRevision,
                    CandidateRevision: proposal.CandidateRevision,
                    Status: "promoted",
                    FailureReason: string.Empty,
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "script-catalog",
                    ValidationReport: new ScriptEvolutionValidationReport(true, Array.Empty<string>())));
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

        public Task<string> EnsureRuntimeAsync(
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

        public Task RunRuntimeAsync(
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
            return Task.CompletedTask;
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

    }
}
