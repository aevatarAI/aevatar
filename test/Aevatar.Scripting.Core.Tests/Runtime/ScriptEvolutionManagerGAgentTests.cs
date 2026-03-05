using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionManagerGAgentTests
{
    [Fact]
    public async Task Propose_ShouldPromote_WhenFlowReturnsPromoted()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));

        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        flowPort.ExecutedProposals.Should().ContainSingle();
        flowPort.ExecutedProposals[0].ProposalId.Should().Be("proposal-1");

        agent.State.Proposals.Should().ContainKey("proposal-1");
        var proposal = agent.State.Proposals["proposal-1"];
        proposal.Status.Should().Be("promoted");
        proposal.ValidationSucceeded.Should().BeTrue();
        proposal.ValidationDiagnostics.Should().ContainSingle(x => x == "compile-ok");
    }

    [Fact]
    public async Task Propose_ShouldReject_WhenFlowReturnsPolicyRejected()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));

        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-denied",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        agent.State.Proposals.Should().ContainKey("proposal-denied");
        var proposal = agent.State.Proposals["proposal-denied"];
        proposal.Status.Should().Be("rejected");
        proposal.FailureReason.Should().Contain("policy-denied");
        proposal.ValidationSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Propose_ShouldSendDecisionToCallbackActor_WhenCallbackProvided()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));

        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            CallbackActorId = "script-evolution-session:proposal-1",
            CallbackRequestId = "session-request-1",
        });

        publisher.Sent.Should().ContainSingle();
        publisher.Sent[0].TargetActorId.Should().Be("script-evolution-session:proposal-1");
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.RequestId.Should().Be("session-request-1");
        response.Accepted.Should().BeTrue();
        response.ProposalId.Should().Be("proposal-1");
    }

    [Fact]
    public async Task Propose_ShouldReturnPromotionFailedStatus_WhenFlowPromotionFailsAfterUpsert()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PromotionFailed(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                "Promotion failed after definition upsert.",
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-candidate",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));

        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-failed",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            CallbackActorId = "script-evolution-session:proposal-failed",
            CallbackRequestId = "session-request-failed",
        });

        agent.State.Proposals.Should().ContainKey("proposal-failed");
        agent.State.Proposals["proposal-failed"].Status.Should().Be("promotion_failed");
        agent.State.Proposals["proposal-failed"].FailureReason.Should().Contain("Promotion failed");

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Status.Should().Be("promotion_failed");
        response.DefinitionActorId.Should().Be("definition-candidate");
        response.CatalogActorId.Should().Be("catalog-1");
        response.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task Propose_ShouldReject_WhenFlowReturnsValidationFailed()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.ValidationFailed(
                new ScriptEvolutionValidationReport(false, ["validation-failed"])));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-validation-failed",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            CallbackActorId = "script-evolution-session:proposal-validation-failed",
            CallbackRequestId = "session-validation-failed",
        });

        agent.State.Proposals.Should().ContainKey("proposal-validation-failed");
        var proposal = agent.State.Proposals["proposal-validation-failed"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        proposal.ValidationSucceeded.Should().BeFalse();
        proposal.ValidationDiagnostics.Should().ContainSingle(x => x == "validation-failed");
        proposal.FailureReason.Should().Contain("validation-failed");

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        response.Accepted.Should().BeFalse();
        response.Diagnostics.Should().ContainSingle(x => x == "validation-failed");
    }

    [Fact]
    public async Task QueryDecision_ShouldIgnore_WhenRequestOrReplyStreamMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = string.Empty,
            ReplyStreamId = "reply-stream",
            ProposalId = "proposal-1",
        });
        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-1",
            ReplyStreamId = string.Empty,
            ProposalId = "proposal-1",
        });

        publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryDecision_ShouldReturnNotFound_WhenProposalIdMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-missing-proposal",
            ReplyStreamId = "reply-stream",
            ProposalId = string.Empty,
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-missing-proposal");
        response.Found.Should().BeFalse();
        response.FailureReason.Should().Contain("ProposalId is required");
    }

    [Fact]
    public async Task QueryDecision_ShouldReturnNotFound_WhenProposalDoesNotExist()
    {
        var flowPort = new FakeEvolutionFlowPort(ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-missing-entry",
            ReplyStreamId = "reply-stream",
            ProposalId = "proposal-missing",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-missing-entry");
        response.Found.Should().BeFalse();
        response.ProposalId.Should().Be("proposal-missing");
        response.FailureReason.Should().Contain("not found");
    }

    [Fact]
    public async Task QueryDecision_ShouldReturnResolvedDecision_WhenProposalExists()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: string.Empty,
                    CatalogActorId: string.Empty,
                    PromotedRevision: "rev-2")));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-query-hit",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        publisher.Sent.Clear();
        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-hit",
            ReplyStreamId = "reply-stream",
            ProposalId = "proposal-query-hit",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Found.Should().BeTrue();
        response.Accepted.Should().BeTrue();
        response.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        response.ProposalId.Should().Be("proposal-query-hit");
        response.DefinitionActorId.Should().Be("script-definition:script-1");
        response.CatalogActorId.Should().Be("script-catalog");
        response.Diagnostics.Should().ContainSingle(x => x == "compile-ok");
    }

    [Fact]
    public async Task Rollback_ShouldInvokeFlowPortRollback_AndUpdateState()
    {
        var flowPort = new FakeEvolutionFlowPort(ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            CatalogActorId = "catalog-1",
            Reason = "manual rollback",
        });

        flowPort.RollbackRequests.Should().ContainSingle();
        flowPort.RollbackRequests[0].ProposalId.Should().Be("proposal-rollback");
        flowPort.RollbackRequests[0].TargetRevision.Should().Be("rev-1");
        flowPort.RollbackRequests[0].CatalogActorId.Should().Be("catalog-1");

        agent.State.Proposals.Should().ContainKey("proposal-rollback");
        var proposal = agent.State.Proposals["proposal-rollback"];
        proposal.Status.Should().Be(ScriptEvolutionStatuses.RolledBack);
        proposal.PromotedRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task Propose_ShouldGenerateProposalId_WhenMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = string.Empty,
            ScriptId = "script-auto-id",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        flowPort.ExecutedProposals.Should().ContainSingle();
        var normalizedProposalId = flowPort.ExecutedProposals[0].ProposalId;
        normalizedProposalId.Should().NotBeNullOrWhiteSpace();
        agent.State.Proposals.Should().ContainKey(normalizedProposalId);
    }

    [Fact]
    public async Task Propose_ShouldThrow_WhenScriptIdMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        var act = () => agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-missing-script",
            ScriptId = string.Empty,
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ScriptId is required*");
    }

    [Fact]
    public async Task Propose_ShouldThrow_WhenCandidateRevisionMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        var act = () => agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-missing-revision",
            ScriptId = "script-1",
            CandidateRevision = string.Empty,
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CandidateRevision is required*");
    }

    [Fact]
    public async Task Propose_ShouldThrow_WhenCandidateSourceMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        var act = () => agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-missing-source",
            ScriptId = "script-1",
            CandidateRevision = "rev-2",
            CandidateSource = string.Empty,
            CandidateSourceHash = "hash-rev-2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CandidateSource is required*");
    }

    [Fact]
    public async Task Propose_ShouldThrow_WhenFlowPromotedButPromotionPayloadMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(
            new ScriptEvolutionFlowResult(
                ScriptEvolutionFlowStatus.Promoted,
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                null,
                string.Empty));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        var act = () => agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-promoted-missing-payload",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Promotion result is required*");
    }

    [Fact]
    public async Task Propose_ShouldNotSendCallback_WhenCallbackMetadataMissing()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-no-callback",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            CallbackActorId = "script-evolution-session:proposal-no-callback",
            CallbackRequestId = string.Empty,
        });

        publisher.Sent.Should().BeEmpty();
        agent.State.Proposals["proposal-no-callback"].Status.Should().Be(ScriptEvolutionStatuses.Promoted);
    }

    [Fact]
    public async Task Propose_ShouldFallbackPromotionActorIds_WhenPromotionFailedPayloadIdsAreEmpty()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.PromotionFailed(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                "promotion failed with empty ids",
                new ScriptPromotionResult(
                    DefinitionActorId: string.Empty,
                    CatalogActorId: string.Empty,
                    PromotedRevision: "rev-2")));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-promotion-failed-fallback",
            ScriptId = "script-fallback",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
            CallbackActorId = "script-evolution-session:proposal-promotion-failed-fallback",
            CallbackRequestId = "session-fallback",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Accepted.Should().BeFalse();
        response.Status.Should().Be("promotion_failed");
        response.DefinitionActorId.Should().Be("script-definition:script-fallback");
        response.CatalogActorId.Should().Be("script-catalog");
    }

    [Fact]
    public async Task QueryDecision_ShouldUseStoredPromotedDefinitionActorId_WhenAvailable()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-custom",
                    CatalogActorId: "catalog-custom",
                    PromotedRevision: "rev-2")));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-query-definition-hit",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        publisher.Sent.Clear();
        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-definition-hit",
            ReplyStreamId = "reply-stream",
            ProposalId = "proposal-query-definition-hit",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.DefinitionActorId.Should().Be("definition-custom");
        response.CatalogActorId.Should().Be("script-catalog");
    }

    [Fact]
    public async Task QueryDecision_ShouldReturnRejectedDecision_WhenProposalIsRejected()
    {
        var flowPort = new FakeEvolutionFlowPort(ScriptEvolutionFlowResult.PolicyRejected("policy-denied"));
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-rejected",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });

        publisher.Sent.Clear();
        await agent.HandleQueryScriptEvolutionDecisionRequested(new QueryScriptEvolutionDecisionRequestedEvent
        {
            RequestId = "request-rejected",
            ReplyStreamId = "reply-stream",
            ProposalId = "proposal-rejected",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Payload.Should().BeOfType<ScriptEvolutionDecisionRespondedEvent>().Subject;
        response.Found.Should().BeTrue();
        response.Accepted.Should().BeFalse();
        response.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        response.FailureReason.Should().Contain("policy-denied");
    }

    [Fact]
    public async Task Rollback_ShouldClearPromotedRevision_WhenTargetRevisionIsEmpty()
    {
        var flowPort = new FakeEvolutionFlowPort(
            ScriptEvolutionFlowResult.Promoted(
                new ScriptEvolutionValidationReport(true, ["compile-ok"]),
                new ScriptPromotionResult(
                    DefinitionActorId: "definition-1",
                    CatalogActorId: "catalog-1",
                    PromotedRevision: "rev-2")));
        var agent = new ScriptEvolutionManagerGAgent(flowPort, new StaticAddressResolver())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionManagerState>(
                new InMemoryEventStore()),
        };

        await agent.HandleProposeScriptEvolutionRequested(new ProposeScriptEvolutionRequestedEvent
        {
            ProposalId = "proposal-rollback-empty-target",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-rev-2",
            CandidateSourceHash = "hash-rev-2",
        });
        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-rollback-empty-target",
            ScriptId = "script-1",
            TargetRevision = string.Empty,
            Reason = "manual rollback without target",
            CatalogActorId = "catalog-1",
        });

        var proposal = agent.State.Proposals["proposal-rollback-empty-target"];
        proposal.PromotedRevision.Should().BeEmpty();
        proposal.Status.Should().Be(ScriptEvolutionStatuses.RolledBack);
    }

    private sealed class FakeEvolutionFlowPort(ScriptEvolutionFlowResult result) : IScriptEvolutionFlowPort
    {
        public List<ScriptEvolutionProposal> ExecutedProposals { get; } = [];
        public List<ScriptRollbackRequest> RollbackRequests { get; } = [];

        public Task<ScriptEvolutionFlowResult> ExecuteAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExecutedProposals.Add(proposal);
            return Task.FromResult(result);
        }

        public Task RollbackAsync(ScriptRollbackRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RollbackRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];

        public Task PublishAsync<T>(
            T evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where T : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where T : IMessage
        {
            _ = sourceEnvelope;
            ct.ThrowIfCancellationRequested();
            Sent.Add(new PublishedMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string TargetActorId, IMessage Payload);
}
