using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ClaimReadModelProjectorTests
{
    [Fact]
    public async Task Should_reduce_manual_review_decision_into_typed_readmodel()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = CreateProjector(dispatcher);
        var context = CreateContext("claim-runtime-manual");

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "claim-runtime-manual",
                DefinitionActorId = "definition-1",
                ScriptId = "claim_orchestrator",
                Revision = "rev-claim-1",
                RunId = "run-case-b",
                EventType = ClaimDecisionRecorded.Descriptor.FullName,
                DomainEventPayload = Any.Pack(new ClaimDecisionRecorded
                {
                    CommandId = "command-case-b",
                    Current = new ClaimReadModel
                    {
                        HasValue = true,
                        CaseId = "Case-B",
                        PolicyId = "POLICY-B",
                        DecisionStatus = "ManualReview",
                        ManualReviewRequired = true,
                        AiSummary = "high-risk-profile",
                        Search = new ClaimSearchIndex
                        {
                            LookupKey = "case-b:policy-b",
                            DecisionKey = "manualreview",
                        },
                        Refs = new ClaimRefs
                        {
                            PolicyId = "POLICY-B",
                            OwnerActorId = "claim-runtime-manual",
                        },
                    },
                }),
                ReadModelTypeUrl = Any.Pack(new ClaimReadModel()).TypeUrl,
                StateVersion = 1,
            }),
            CancellationToken.None);

        var document = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        document.Should().NotBeNull();
        document!.StateVersion.Should().Be(1);
        document.ReadModelPayload.Should().NotBeNull();
        var readModel = document.ReadModelPayload!.Unpack<ClaimReadModel>();
        readModel.DecisionStatus.Should().Be("ManualReview");
        readModel.ManualReviewRequired.Should().BeTrue();
        readModel.Search.LookupKey.Should().Be("case-b:policy-b");
        readModel.Refs.PolicyId.Should().Be("POLICY-B");
    }

    [Fact]
    public async Task Should_noop_for_unrelated_envelopes()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = CreateProjector(dispatcher);
        var context = CreateContext("claim-runtime-noop");

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-noop",
                Payload = Any.Pack(new SimpleTextCommand
                {
                    CommandId = "noop",
                    Value = "not-a-fact",
                }),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
            },
            CancellationToken.None);

        var document = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        document.Should().NotBeNull();
        document!.StateVersion.Should().Be(0);
        document.ReadModelPayload.Should().BeNull();
    }

    private static ScriptReadModelProjector CreateProjector(InMemoryReadModelDispatcher dispatcher)
    {
        return new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)),
            new StaticDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());
    }

    private static ScriptExecutionProjectionContext CreateContext(string rootActorId) =>
        new()
        {
            ProjectionId = rootActorId + ":projection",
            RootActorId = rootActorId,
        };

    private static EventEnvelope BuildEnvelope(ScriptDomainFactCommitted fact)
    {
        return new EventEnvelope
        {
            Id = "evt-" + fact.RunId,
            Payload = Any.Pack(fact),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = fact.CorrelationId,
            },
        };
    }

    private sealed class StaticDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            definitionActorId.Should().Be("definition-1");
            requestedRevision.Should().Be("rev-claim-1");
            return Task.FromResult(new ScriptDefinitionSnapshot(
                ScriptId: "claim_orchestrator",
                Revision: "rev-claim-1",
                SourceText: ClaimScriptSources.DecisionBehavior,
                SourceHash: ClaimScriptSources.DecisionBehaviorHash,
                ScriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(ClaimScriptSources.DecisionBehavior),
                StateTypeUrl: Any.Pack(new ClaimState()).TypeUrl,
                ReadModelTypeUrl: Any.Pack(new ClaimReadModel()).TypeUrl,
                ReadModelSchemaVersion: "3",
                ReadModelSchemaHash: "claim-schema-hash",
                ProtocolDescriptorSet: ByteString.Empty,
                StateDescriptorFullName: ClaimState.Descriptor.FullName,
                ReadModelDescriptorFullName: ClaimReadModel.Descriptor.FullName));
        }
    }

    private sealed class InMemoryReadModelDispatcher : IProjectionStoreDispatcher<ScriptReadModelDocument, string>
    {
        private readonly Dictionary<string, ScriptReadModelDocument> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptReadModelDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<ScriptReadModelDocument> mutate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptReadModelDocument { Id = key };
                _items[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptReadModelDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptReadModelDocument>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptReadModelDocument>>(_items.Values.Take(take).ToArray());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
