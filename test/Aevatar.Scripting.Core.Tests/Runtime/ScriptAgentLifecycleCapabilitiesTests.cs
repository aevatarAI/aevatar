using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using SystemType = System.Type;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptAgentLifecycleCapabilitiesTests
{
    [Fact]
    public async Task CreateDestroyLinkAndUnlink_ShouldDelegateToRuntime()
    {
        var runtime = new RecordingRuntime();
        var capabilities = CreateCapabilities(runtime: runtime);

        var actorId = await capabilities.CreateAgentAsync(
            typeof(FakeTestAgent).AssemblyQualifiedName!,
            "agent-x",
            CancellationToken.None);
        await capabilities.LinkAgentsAsync("parent-1", "child-1", CancellationToken.None);
        await capabilities.UnlinkAgentAsync("child-1", CancellationToken.None);
        await capabilities.DestroyAgentAsync("child-1", CancellationToken.None);

        actorId.Should().Be("agent-x");
        runtime.CreatedType.Should().Be(typeof(FakeTestAgent));
        runtime.CreatedActorId.Should().Be("agent-x");
        runtime.LinkedParentId.Should().Be("parent-1");
        runtime.LinkedChildId.Should().Be("child-1");
        runtime.UnlinkedChildId.Should().Be("child-1");
        runtime.DestroyedActorId.Should().Be("child-1");
    }

    [Fact]
    public async Task MessagingAndCallbackApis_ShouldDelegateToInjectedHandlers()
    {
        var published = new List<(IMessage Payload, TopologyAudience Audience)>();
        var sent = new List<(string TargetActorId, IMessage Payload)>();
        var selfPublished = new List<IMessage>();
        var scheduled = new List<(string CallbackId, TimeSpan DueTime, IMessage Payload)>();
        var canceled = new List<RuntimeCallbackLease>();
        var capabilities = CreateCapabilities(
            publishAsync: (payload, audience, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                published.Add((payload, audience));
                return Task.CompletedTask;
            },
            sendToAsync: (targetActorId, payload, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                sent.Add((targetActorId, payload));
                return Task.CompletedTask;
            },
            publishToSelfAsync: (payload, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                selfPublished.Add(payload);
                return Task.CompletedTask;
            },
            scheduleSelfSignalAsync: (callbackId, dueTime, payload, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                scheduled.Add((callbackId, dueTime, payload));
                return Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 3, RuntimeCallbackBackend.InMemory));
            },
            cancelCallbackAsync: (lease, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                canceled.Add(lease);
                return Task.CompletedTask;
            });

        await capabilities.PublishAsync(new SimpleTextSignal { Value = "published" }, TopologyAudience.Parent, CancellationToken.None);
        await capabilities.SendToAsync("target-1", new SimpleTextSignal { Value = "sent" }, CancellationToken.None);
        await capabilities.PublishToSelfAsync(new SimpleTextSignal { Value = "self" }, CancellationToken.None);
        var lease = await capabilities.ScheduleSelfDurableSignalAsync(
            "cb-1",
            TimeSpan.FromSeconds(5),
            new SimpleTextSignal { Value = "scheduled" },
            CancellationToken.None);
        await capabilities.CancelDurableCallbackAsync(lease, CancellationToken.None);

        published.Should().ContainSingle(x => x.Audience == TopologyAudience.Parent);
        sent.Should().ContainSingle(x => x.TargetActorId == "target-1");
        selfPublished.Should().ContainSingle();
        scheduled.Should().ContainSingle(x => x.CallbackId == "cb-1" && x.DueTime == TimeSpan.FromSeconds(5));
        canceled.Should().ContainSingle(x => x.CallbackId == "cb-1");
    }

    [Fact]
    public async Task ReadModelEvolutionAndProvisioningApis_ShouldDelegateToPorts()
    {
        var executionProjectionPort = new RecordingExecutionProjectionPort();
        var readModelPort = new RecordingReadModelQueryPort();
        var proposalPort = new RecordingProposalPort();
        var definitionPort = new RecordingDefinitionCommandPort();
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var runtimeCommandPort = new RecordingRuntimeCommandPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var aiCapability = new RecordingAICapability();
        var capabilities = CreateCapabilities(
            aiCapability: aiCapability,
            executionProjectionPort: executionProjectionPort,
            readModelQueryPort: readModelPort,
            proposalPort: proposalPort,
            definitionCommandPort: definitionPort,
            runtimeProvisioningPort: provisioningPort,
            runtimeCommandPort: runtimeCommandPort,
            catalogCommandPort: catalogCommandPort);

        var aiResponse = await capabilities.AskAIAsync("hello", CancellationToken.None);
        var snapshot = await capabilities.GetReadModelSnapshotAsync("runtime-1", CancellationToken.None);
        var queryResult = await capabilities.ExecuteReadModelQueryAsync(
            "runtime-1",
            Any.Pack(new SimpleTextQueryRequested { RequestId = "request-1" }),
            CancellationToken.None);
        var decision = await capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: "reason"),
            CancellationToken.None);
        var definitionActorId = await capabilities.UpsertScriptDefinitionAsync(
            "script-1",
            "rev-2",
            "source",
            "hash",
            "definition-1",
            CancellationToken.None);
        var runtimeActorId = await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-1",
            CancellationToken.None);
        await capabilities.RunScriptInstanceAsync(
            "runtime-1",
            "run-1",
            Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "input",
            }),
            "rev-2",
            "definition-1",
            "integration.requested",
            CancellationToken.None);
        await capabilities.PromoteRevisionAsync(
            "catalog-1",
            "script-1",
            "rev-2",
            "definition-1",
            "hash",
            "proposal-1",
            CancellationToken.None);
        await capabilities.RollbackRevisionAsync(
            "catalog-1",
            "script-1",
            "rev-1",
            "rollback",
            "proposal-2",
            CancellationToken.None);

        aiResponse.Should().Be("ok:hello");
        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("runtime-1");
        queryResult.Should().NotBeNull();
        queryResult!.Unpack<SimpleTextQueryResponded>().Current.Value.Should().Be("query-result");
        decision.Accepted.Should().BeTrue();
        definitionActorId.Should().Be("definition-1");
        runtimeActorId.Should().Be("runtime-1");
        readModelPort.LastActorId.Should().Be("runtime-1");
        proposalPort.LastProposal.Should().NotBeNull();
        proposalPort.LastProposal!.CandidateRevision.Should().Be("rev-2");
        definitionPort.Upserts.Should().ContainSingle();
        provisioningPort.EnsureCalls.Should().ContainSingle();
        executionProjectionPort.EnsureCalls.Should().ContainInOrder("runtime-1", "runtime-1");
        runtimeCommandPort.RunCalls.Should().ContainSingle(x => x.RunId == "run-1");
        catalogCommandPort.PromoteCalls.Should().ContainSingle(x => x.Revision == "rev-2");
        catalogCommandPort.RollbackCalls.Should().ContainSingle(x => x.TargetRevision == "rev-1");
    }

    private static ScriptBehaviorRuntimeCapabilities CreateCapabilities(
        IActorRuntime? runtime = null,
        IAICapability? aiCapability = null,
        IScriptExecutionProjectionPort? executionProjectionPort = null,
        IScriptReadModelQueryPort? readModelQueryPort = null,
        IScriptEvolutionProposalPort? proposalPort = null,
        IScriptDefinitionCommandPort? definitionCommandPort = null,
        IScriptRuntimeProvisioningPort? runtimeProvisioningPort = null,
        IScriptRuntimeCommandPort? runtimeCommandPort = null,
        IScriptCatalogCommandPort? catalogCommandPort = null,
        Func<IMessage, TopologyAudience, CancellationToken, Task>? publishAsync = null,
        Func<string, IMessage, CancellationToken, Task>? sendToAsync = null,
        Func<IMessage, CancellationToken, Task>? publishToSelfAsync = null,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>>? scheduleSelfSignalAsync = null,
        Func<RuntimeCallbackLease, CancellationToken, Task>? cancelCallbackAsync = null)
    {
        return new ScriptBehaviorRuntimeCapabilities(
            runId: "run-1",
            correlationId: "corr-1",
            publishAsync: publishAsync ?? ((_, _, _) => Task.CompletedTask),
            sendToAsync: sendToAsync ?? ((_, _, _) => Task.CompletedTask),
            publishToSelfAsync: publishToSelfAsync ?? ((_, _) => Task.CompletedTask),
            scheduleSelfSignalAsync: scheduleSelfSignalAsync ?? ((callbackId, _, _, _) =>
                Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory))),
            cancelCallbackAsync: cancelCallbackAsync ?? ((_, _) => Task.CompletedTask),
            aiCapability: aiCapability ?? new RecordingAICapability(),
            runtime: runtime ?? new RecordingRuntime(),
            executionProjectionPort: executionProjectionPort ?? new RecordingExecutionProjectionPort(),
            readModelQueryPort: readModelQueryPort ?? new RecordingReadModelQueryPort(),
            proposalPort: proposalPort ?? new RecordingProposalPort(),
            definitionCommandPort: definitionCommandPort ?? new RecordingDefinitionCommandPort(),
            runtimeProvisioningPort: runtimeProvisioningPort ?? new RecordingRuntimeProvisioningPort(),
            runtimeCommandPort: runtimeCommandPort ?? new RecordingRuntimeCommandPort(),
            catalogCommandPort: catalogCommandPort ?? new RecordingCatalogCommandPort());
    }

    private sealed class RecordingAICapability : IAICapability
    {
        public Task<string> AskAsync(string runId, string correlationId, string prompt, CancellationToken ct)
        {
            runId.Should().Be("run-1");
            correlationId.Should().Be("corr-1");
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("ok:" + prompt);
        }
    }

    private sealed class RecordingExecutionProjectionPort : IScriptExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public List<string> EnsureCalls { get; } = [];

        public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCalls.Add(actorId);
            return Task.FromResult<IScriptExecutionProjectionLease?>(new RecordingLease(actorId));
        }

        public Task<IScriptExecutionProjectionLease?> EnsureProjectionAsync(
            string actorId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            _ = projectionName;
            _ = input;
            _ = commandId;
            return EnsureActorProjectionAsync(actorId, ct);
        }

        public Task AttachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private sealed record RecordingLease(string ActorId) : IScriptExecutionProjectionLease;
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public SystemType? CreatedType { get; private set; }
        public string? CreatedActorId { get; private set; }
        public string? DestroyedActorId { get; private set; }
        public string? LinkedParentId { get; private set; }
        public string? LinkedChildId { get; private set; }
        public string? UnlinkedChildId { get; private set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(SystemType agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreatedType = agentType;
            CreatedActorId = id ?? string.Empty;
            return Task.FromResult<IActor>(new FakeActor(id ?? string.Empty, new FakeTestAgent(id ?? string.Empty)));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyedActorId = id;
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _ = id;
            return Task.FromResult<IActor?>(null);
        }

        public Task<bool> ExistsAsync(string id)
        {
            _ = id;
            return Task.FromResult(false);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LinkedParentId = parentId;
            LinkedChildId = childId;
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UnlinkedChildId = childId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReadModelQueryPort : IScriptReadModelQueryPort
    {
        public string? LastActorId { get; private set; }

        public Task<ScriptReadModelSnapshot?> GetSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastActorId = actorId;
            return Task.FromResult<ScriptReadModelSnapshot?>(new ScriptReadModelSnapshot(
                ActorId: actorId,
                ScriptId: "script-1",
                DefinitionActorId: "definition-1",
                Revision: "rev-1",
                ReadModelTypeUrl: Any.Pack(new SimpleTextReadModel()).TypeUrl,
                ReadModelPayload: Any.Pack(new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "snapshot",
                }),
                StateVersion: 1,
                LastEventId: "evt-1",
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptReadModelSnapshot>>([]);
        }

        public Task<Any?> ExecuteDeclaredQueryAsync(string actorId, Any queryPayload, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastActorId = actorId;
            queryPayload.Is(SimpleTextQueryRequested.Descriptor).Should().BeTrue();
            var requested = queryPayload.Unpack<SimpleTextQueryRequested>();
            return Task.FromResult<Any?>(Any.Pack(new SimpleTextQueryResponded
            {
                RequestId = requested.RequestId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "query-result",
                },
            }));
        }
    }

    private sealed class RecordingProposalPort : IScriptEvolutionProposalPort
    {
        public ScriptEvolutionProposal? LastProposal { get; private set; }

        public Task<ScriptPromotionDecision> ProposeAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastProposal = proposal;
            return Task.FromResult(new ScriptPromotionDecision(
                Accepted: true,
                ProposalId: proposal.ProposalId,
                ScriptId: proposal.ScriptId,
                BaseRevision: proposal.BaseRevision,
                CandidateRevision: proposal.CandidateRevision,
                Status: "promoted",
                FailureReason: string.Empty,
                DefinitionActorId: "definition-1",
                CatalogActorId: "catalog-1",
                ValidationReport: new ScriptEvolutionValidationReport(true, [])));
        }
    }

    private sealed class RecordingDefinitionCommandPort : IScriptDefinitionCommandPort
    {
        public List<(string ScriptId, string Revision, string? DefinitionActorId)> Upserts { get; } = [];

        public Task<string> UpsertDefinitionAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct)
        {
            _ = sourceText;
            _ = sourceHash;
            ct.ThrowIfCancellationRequested();
            Upserts.Add((scriptId, scriptRevision, definitionActorId));
            return Task.FromResult(definitionActorId ?? "definition-created");
        }
    }

    private sealed class RecordingRuntimeProvisioningPort : IScriptRuntimeProvisioningPort
    {
        public List<(string DefinitionActorId, string Revision, string? RuntimeActorId)> EnsureCalls { get; } = [];

        public Task<string> EnsureRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCalls.Add((definitionActorId, scriptRevision, runtimeActorId));
            return Task.FromResult(runtimeActorId ?? "runtime-created");
        }
    }

    private sealed class RecordingRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public List<(string RuntimeActorId, string RunId, string Revision, string DefinitionActorId, string RequestedEventType)> RunCalls { get; } = [];

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct)
        {
            _ = inputPayload;
            ct.ThrowIfCancellationRequested();
            RunCalls.Add((runtimeActorId, runId, scriptRevision, definitionActorId, requestedEventType));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        public List<(string? CatalogActorId, string ScriptId, string Revision, string ProposalId)> PromoteCalls { get; } = [];
        public List<(string? CatalogActorId, string ScriptId, string TargetRevision, string ProposalId)> RollbackCalls { get; } = [];

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
            _ = expectedBaseRevision;
            _ = definitionActorId;
            _ = sourceHash;
            ct.ThrowIfCancellationRequested();
            PromoteCalls.Add((catalogActorId, scriptId, revision, proposalId));
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
            _ = reason;
            _ = expectedCurrentRevision;
            ct.ThrowIfCancellationRequested();
            RollbackCalls.Add((catalogActorId, scriptId, targetRevision, proposalId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeTestAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");

        public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<SystemType>>([]);
    }
}
