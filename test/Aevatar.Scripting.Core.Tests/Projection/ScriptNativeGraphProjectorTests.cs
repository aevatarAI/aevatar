using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptNativeGraphProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeRelationsIntoNativeGraphReadModel()
    {
        var dispatcher = new RecordingNativeGraphDispatcher();
        IProjectionGraphMaterializer<ScriptNativeGraphReadModel> graphMaterializer = new ScriptNativeGraphMaterializer();
        var projector = new ScriptNativeGraphProjector(
            dispatcher,
            new ScriptNativeGraphMaterializer());
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "claim-runtime",
            ProjectionKind = "script-execution-read-model",
        };
        var readModel = BuildClaimReadModel();
        var nativeGraphProjection = BuildNativeGraphProjection(readModel);

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                new ScriptDomainFactCommitted
                {
                    ActorId = "claim-runtime",
                    DefinitionActorId = "definition-1",
                    ScriptId = "claim_orchestrator",
                    Revision = "rev-claim-1",
                    RunId = "run-claim-1",
                    EventType = Any.Pack(new ClaimDecisionRecorded()).TypeUrl,
                    DomainEventPayload = Any.Pack(new ClaimDecisionRecorded { Current = readModel.Clone() }),
                    ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
                    ReadModelPayload = Any.Pack(readModel),
                    StateVersion = 3,
                    OccurredAtUnixTimeMs = DateTimeOffset.Parse("2026-03-14T00:00:00Z").ToUnixTimeMilliseconds(),
                    NativeGraph = nativeGraphProjection.Clone(),
                },
                ScriptCommittedEnvelopeFactory.CreateState(
                    "definition-1",
                    "claim_orchestrator",
                    "rev-claim-1",
                    new ClaimState
                    {
                        CaseId = readModel.CaseId,
                        PolicyId = readModel.PolicyId,
                        DecisionStatus = readModel.DecisionStatus,
                        ManualReviewRequired = readModel.ManualReviewRequired,
                        AiSummary = readModel.AiSummary,
                        RiskScore = readModel.RiskScore,
                        CompliancePassed = readModel.CompliancePassed,
                        LastCommandId = readModel.LastCommandId,
                        TraceSteps = { readModel.TraceSteps },
                    },
                    3,
                    Any.Pack(readModel).TypeUrl,
                    ClaimScriptSources.DecisionBehavior,
                    ClaimScriptSources.DecisionBehaviorHash,
                    ScriptPackageSpecExtensions.CreateSingleSource(ClaimScriptSources.DecisionBehavior),
                    "3",
                    "claim-schema")),
            CancellationToken.None);

        dispatcher.LastUpsert.Should().NotBeNull();
        var graphReadModel = dispatcher.LastUpsert!;
        var graph = graphMaterializer.Materialize(graphReadModel);
        graphReadModel.SchemaId.Should().Be("claim_case");
        graphReadModel.GraphScope.Should().Be("script-native-claim_case");
        graphReadModel.StateVersion.Should().Be(3);
        graphReadModel.LastEventId.Should().Be("evt-graph-1");
        graph.Nodes.Should().Contain(x => x.NodeId == "script:claim_case:claim-runtime");
        graph.Nodes.Should().Contain(x => x.NodeId == "ref:policy:POLICY-B");
        graph.Edges.Should().ContainSingle(x =>
            x.FromNodeId == "script:claim_case:claim-runtime" &&
            x.ToNodeId == "ref:policy:POLICY-B" &&
            x.EdgeType == "rel_policy");
    }

    private static ClaimReadModel BuildClaimReadModel()
    {
        return new ClaimReadModel
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
                OwnerActorId = "claim-runtime",
            },
        };
    }

    private static ScriptNativeGraphProjection BuildNativeGraphProjection(ClaimReadModel readModel)
    {
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));
        var artifact = artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            "claim_orchestrator",
            "rev-claim-1",
            ScriptPackageSpecExtensions.CreateSingleSource(ClaimScriptSources.DecisionBehavior),
            ClaimScriptSources.DecisionBehaviorHash));
        var plan = new ScriptReadModelMaterializationCompiler().GetOrCompile(
            artifact,
            "claim-schema",
            "3");
        return new ScriptNativeProjectionBuilder()
            .BuildGraph(
                "claim-runtime",
                "claim_orchestrator",
                "definition-1",
                "rev-claim-1",
                readModel,
                plan)!;
    }

    private static EventEnvelope BuildEnvelope(ScriptDomainFactCommitted fact, ScriptBehaviorState state) =>
        ScriptCommittedEnvelopeFactory.CreateCommittedEnvelope(
            fact,
            state,
            "evt-graph-1",
            DateTimeOffset.Parse("2026-03-14T00:00:00Z"));

    private sealed class RecordingNativeGraphDispatcher : IProjectionWriteDispatcher<ScriptNativeGraphReadModel>
    {
        public ScriptNativeGraphReadModel? LastUpsert { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(ScriptNativeGraphReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastUpsert = readModel.DeepClone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }
}
