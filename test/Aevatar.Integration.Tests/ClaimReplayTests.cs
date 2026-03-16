using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Integration.Tests.Fixtures.ScriptDocuments;
using Aevatar.Integration.Tests.Protocols;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Integration.Tests;

public class ClaimReplayTests
{
    [Fact]
    public async Task Should_recompile_from_definition_source_without_external_repository()
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        var definitionPort = provider.GetRequiredService<IScriptDefinitionCommandPort>();
        var provisioningPort = provider.GetRequiredService<IScriptRuntimeProvisioningPort>();

        const string definitionActorId = "claim-recompile-definition";
        const string revision = "rev-claim-recompile-1";
        const string runtimeActorId1 = "claim-recompile-runtime-1";
        const string runtimeActorId2 = "claim-recompile-runtime-2";

        var persistedDefinitionSource = BuildPersistedSource("definition-source-v1");
        var definition = await definitionPort.UpsertDefinitionWithSnapshotAsync(
            "claim-recompile-script",
            revision,
            persistedDefinitionSource,
            ScriptingCommandEnvelopeTestKit.ComputeSourceHash(persistedDefinitionSource),
            definitionActorId,
            CancellationToken.None);

        await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId1, definition.Snapshot, CancellationToken.None);
        var first = await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId1,
            revision,
            "run-recompile-1",
            new ClaimSubmitted
            {
                CommandId = "run-recompile-1",
                CaseId = "Case-A",
                PolicyId = "POLICY-A",
                RiskScore = 0.12d,
                CompliancePassed = true,
            },
            CancellationToken.None);
        first.Snapshot.ReadModelPayload!.Unpack<ClaimCaseReadModel>().AiSummary.Should().Be("definition-source-v1");

        var externalUpdatedSourceButNotPersisted = BuildPersistedSource("definition-source-v2");
        externalUpdatedSourceButNotPersisted.Should().Contain("definition-source-v2");

        await provisioningPort.EnsureRuntimeAsync(definitionActorId, revision, runtimeActorId2, definition.Snapshot, CancellationToken.None);
        var second = await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId2,
            revision,
            "run-recompile-2",
            new ClaimSubmitted
            {
                CommandId = "run-recompile-2",
                CaseId = "Case-A",
                PolicyId = "POLICY-A",
                RiskScore = 0.12d,
                CompliancePassed = true,
            },
            CancellationToken.None);
        second.Snapshot.ReadModelPayload!.Unpack<ClaimCaseReadModel>().AiSummary.Should().Be("definition-source-v1");
    }

    [Fact]
    public async Task Should_rebuild_same_state_from_event_stream()
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestrator = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");

        const string definitionActorId = "claim-replay-definition";
        const string runtimeActorId = "claim-replay-runtime";
        await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
        await ClaimIntegrationTestKit.EnsureRuntimeAsync(provider, definitionActorId, orchestrator.Revision, runtimeActorId, CancellationToken.None);

        await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId,
            orchestrator.Revision,
            "run-replay-case-b",
            new ClaimSubmitted
            {
                CommandId = "run-replay-case-b",
                CaseId = "Case-B",
                PolicyId = "POLICY-B",
                RiskScore = 0.91d,
                CompliancePassed = true,
            },
            CancellationToken.None);

        var beforeActor = await runtime.GetAsync(runtimeActorId);
        var beforeAgent = beforeActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;
        var beforeStateRoot = beforeAgent.State.StateRoot;
        beforeStateRoot.Should().NotBeNull();
        var beforeState = beforeStateRoot!.Clone();

        await runtime.DestroyAsync(runtimeActorId, CancellationToken.None);
        await ClaimIntegrationTestKit.EnsureRuntimeAsync(provider, definitionActorId, orchestrator.Revision, runtimeActorId, CancellationToken.None);

        var replayedActor = await runtime.GetAsync(runtimeActorId);
        var replayedAgent = replayedActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;

        replayedAgent.State.Revision.Should().Be(beforeAgent.State.Revision);
        replayedAgent.State.LastRunId.Should().Be(beforeAgent.State.LastRunId);
        replayedAgent.State.StateRoot.Should().NotBeNull();
        replayedAgent.State.StateRoot!.Unpack<ClaimCaseState>().Should().BeEquivalentTo(beforeState.Unpack<ClaimCaseState>());
        replayedAgent.State.LastAppliedEventVersion.Should().Be(beforeAgent.State.LastAppliedEventVersion);
    }

    [Fact]
    public async Task Should_rebuild_same_readmodel_from_committed_fact_stream()
    {
        await using var provider = ClaimIntegrationTestKit.BuildProvider();
        var eventStore = provider.GetRequiredService<IEventStore>();
        var runtime = provider.GetRequiredService<IActorRuntime>();
        var definitionSnapshotPort = provider.GetRequiredService<IScriptDefinitionSnapshotPort>();
        var artifactResolver = provider.GetRequiredService<Aevatar.Scripting.Core.Runtime.IScriptBehaviorArtifactResolver>();
        var codec = provider.GetRequiredService<IProtobufMessageCodec>();
        var document = ClaimScriptScenarioDocument.CreateEmbedded();
        var orchestrator = document.Scripts.Single(x => x.ScriptId == "claim_orchestrator");

        const string definitionActorId = "claim-readmodel-definition";
        const string runtimeActorId = "claim-readmodel-runtime";
        await ClaimIntegrationTestKit.UpsertOrchestratorAsync(provider, definitionActorId, CancellationToken.None);
        await ClaimIntegrationTestKit.EnsureRuntimeAsync(provider, definitionActorId, orchestrator.Revision, runtimeActorId, CancellationToken.None);

        await ClaimIntegrationTestKit.RunClaimAsync(
            provider,
            definitionActorId,
            runtimeActorId,
            orchestrator.Revision,
            "run-readmodel",
            new ClaimSubmitted
            {
                CommandId = "run-readmodel",
                CaseId = "Case-B",
                PolicyId = "POLICY-B",
                RiskScore = 0.91d,
                CompliancePassed = true,
            },
            CancellationToken.None);

        var runtimeActor = await runtime.GetAsync(runtimeActorId);
        var runtimeAgent = runtimeActor!.Agent.Should().BeOfType<ScriptBehaviorGAgent>().Subject;
        var committedState = runtimeAgent.State.Clone();
        var persisted = await eventStore.GetEventsAsync(runtimeActorId, ct: CancellationToken.None);
        var committedEvents = persisted
            .Where(x => x.EventData?.Is(ScriptDomainFactCommitted.Descriptor) == true)
            .Select(x => new EventEnvelope
            {
                Id = x.EventId,
                Timestamp = x.Timestamp,
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = x.Clone(),
                    StateRoot = Any.Pack(committedState),
                }),
                Route = EnvelopeRouteSemantics.CreateObserverPublication(runtimeActorId),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "run-readmodel",
                },
            })
            .ToArray();

        var context = new ScriptExecutionProjectionContext
        {
            SessionId = "projection-claim-readmodel",
            RootActorId = runtimeActorId,
            ProjectionKind = "script-execution-read-model",
        };

        var projectionNow = DateTimeOffset.UtcNow;
        var dispatcher1 = new InMemoryReadModelDispatcher();
        var projector1 = new ScriptReadModelProjector(
            dispatcher1,
            new FixedProjectionClock(projectionNow));
        foreach (var envelope in committedEvents)
            await projector1.ProjectAsync(context, envelope, CancellationToken.None);
        var readModel1 = await dispatcher1.GetAsync(runtimeActorId, CancellationToken.None);

        var dispatcher2 = new InMemoryReadModelDispatcher();
        var projector2 = new ScriptReadModelProjector(
            dispatcher2,
            new FixedProjectionClock(projectionNow));
        foreach (var envelope in committedEvents)
            await projector2.ProjectAsync(context, envelope, CancellationToken.None);
        var readModel2 = await dispatcher2.GetAsync(runtimeActorId, CancellationToken.None);

        readModel2.Should().BeEquivalentTo(readModel1);
    }

    private static string BuildPersistedSource(string marker) =>
        $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Integration.Tests.Protocols;
        using Aevatar.Scripting.Abstractions.Behaviors;

        public sealed class PersistedDefinitionSourceBehavior : ScriptBehavior<ClaimCaseState, ClaimCaseReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ClaimCaseState, ClaimCaseReadModel> builder)
            {
                builder
                    .OnCommand<ClaimSubmitted>(HandleAsync)
                    .OnEvent<ClaimDecisionRecorded>(
                        apply: static (_, evt, _) => evt.Current == null ? new ClaimCaseState() : new ClaimCaseState
                        {
                            CaseId = evt.Current.CaseId,
                            PolicyId = evt.Current.PolicyId,
                            DecisionStatus = evt.Current.DecisionStatus,
                            ManualReviewRequired = evt.Current.ManualReviewRequired,
                            AiSummary = evt.Current.AiSummary,
                            RiskScore = evt.Current.RiskScore,
                            CompliancePassed = evt.Current.CompliancePassed,
                            LastCommandId = evt.CommandId ?? string.Empty,
                        })
                    .ProjectState(static (state, fact) => state == null
                        ? new ClaimCaseReadModel()
                        : new ClaimCaseReadModel
                        {
                            HasValue = true,
                            CaseId = state.CaseId,
                            PolicyId = state.PolicyId,
                            DecisionStatus = state.DecisionStatus,
                            ManualReviewRequired = state.ManualReviewRequired,
                            AiSummary = state.AiSummary,
                            RiskScore = state.RiskScore,
                            CompliancePassed = state.CompliancePassed,
                            LastCommandId = state.LastCommandId,
                            Search = new ClaimSearchIndex
                            {
                                LookupKey = string.Concat(state.CaseId ?? string.Empty, ":", state.PolicyId ?? string.Empty).ToLowerInvariant(),
                                DecisionKey = (state.DecisionStatus ?? string.Empty).ToLowerInvariant(),
                            },
                            Refs = new ClaimRefs
                            {
                                PolicyId = state.PolicyId ?? string.Empty,
                                OwnerActorId = fact.ActorId,
                            },
                        });
            }

            private static Task HandleAsync(
                ClaimSubmitted command,
                ScriptCommandContext<ClaimCaseState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                context.Emit(new ClaimDecisionRecorded
                {
                    CommandId = command.CommandId ?? string.Empty,
                    Current = new ClaimCaseReadModel
                    {
                        HasValue = true,
                        CaseId = command.CaseId ?? string.Empty,
                        PolicyId = command.PolicyId ?? string.Empty,
                        DecisionStatus = "Approved",
                        AiSummary = "{{marker}}",
                        LastCommandId = command.CommandId ?? string.Empty,
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<ClaimQueryResponded?> HandleQueryAsync(
                ClaimQueryRequested request,
                ScriptQueryContext<ClaimCaseReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ClaimQueryResponded?>(new ClaimQueryResponded
                {
                    RequestId = request.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ClaimCaseReadModel(),
                });
            }
        }
        """;

    private sealed class InMemoryReadModelDispatcher
        : IProjectionDocumentReader<ScriptReadModelDocument, string>,
          IProjectionWriteDispatcher<ScriptReadModelDocument>
    {
        private readonly Dictionary<string, ScriptReadModelDocument> _store = new(StringComparer.Ordinal);

        public Task<ProjectionWriteResult> UpsertAsync(ScriptReadModelDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store[readModel.Id] = readModel.DeepClone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ScriptReadModelDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _store.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel?.DeepClone());
        }

        public Task<ProjectionDocumentQueryResult<ScriptReadModelDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectionDocumentQueryResult<ScriptReadModelDocument>
            {
                Items = _store.Values
                    .Take(query.Take <= 0 ? 50 : query.Take)
                    .Select(static x => x.DeepClone())
                    .ToArray(),
            });
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
