using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ClaimReadModelProjectorTests
{
    [Fact]
    public async Task Should_materialize_claim_state_mirror_into_typed_readmodel()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = CreateProjector(dispatcher);
        var context = CreateContext("claim-runtime-manual");
        var readModel = new ClaimReadModel
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
        };
        var fact = new ScriptDomainFactCommitted
        {
            ActorId = "claim-runtime-manual",
            DefinitionActorId = "definition-1",
            ScriptId = "claim_orchestrator",
            Revision = "rev-claim-1",
            RunId = "run-case-b",
            EventType = Any.Pack(new ClaimDecisionRecorded()).TypeUrl,
            DomainEventPayload = Any.Pack(new ClaimDecisionRecorded
            {
                CommandId = "command-case-b",
                Current = new ClaimReadModel
                {
                    HasValue = true,
                    DecisionStatus = "STALE",
                },
            }),
            ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
            ReadModelPayload = Any.Pack(readModel),
            StateVersion = 1,
        };
        var state = ScriptCommittedEnvelopeFactory.CreateState(
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
            fact.StateVersion,
            Any.Pack(readModel).TypeUrl);

        await projector.ProjectAsync(
            context,
            ScriptCommittedEnvelopeFactory.CreateCommittedEnvelope(
                fact,
                state,
                "evt-run-case-b",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var document = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        document.Should().NotBeNull();
        document!.StateVersion.Should().Be(1);
        document.ReadModelPayload.Should().NotBeNull();
        var projected = document.ReadModelPayload.Unpack<ClaimReadModel>();
        projected.DecisionStatus.Should().Be("ManualReview");
        projected.ManualReviewRequired.Should().BeTrue();
        projected.Search.LookupKey.Should().Be("case-b:policy-b");
        projected.Refs.PolicyId.Should().Be("POLICY-B");
    }

    [Fact]
    public async Task Should_noop_for_unrelated_envelopes()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = CreateProjector(dispatcher);
        var context = CreateContext("claim-runtime-noop");

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
            },
            CancellationToken.None);

        var document = await dispatcher.GetAsync(context.RootActorId, CancellationToken.None);
        document.Should().BeNull();
    }

    private static ScriptReadModelProjector CreateProjector(
        InMemoryProjectionDocumentStore<ScriptReadModelDocument> dispatcher) =>
        new(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)));

    private static ScriptExecutionProjectionContext CreateContext(string rootActorId) =>
        new()
        {
            SessionId = rootActorId,
            RootActorId = rootActorId,
            ProjectionKind = "script-execution-read-model",
        };

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
