using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionSessionGAgentTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("execute")]
    [InlineData("rollback")]
    public async Task HandlerMethods_ShouldRejectNullEvents(string handler)
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));

        Func<Task> act = handler switch
        {
            "start" => () => agent.HandleStartScriptEvolutionSessionRequested(null!),
            "execute" => () => agent.HandleScriptEvolutionExecutionRequested(null!),
            "rollback" => () => agent.HandleScriptEvolutionRollbackRequested(null!),
            _ => throw new InvalidOperationException("Unexpected handler."),
        };

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("addressResolver")]
    [InlineData("policyEvaluator")]
    [InlineData("validationService")]
    [InlineData("catalogBaselineReader")]
    [InlineData("definitionCommandPort")]
    [InlineData("catalogCommandPort")]
    [InlineData("promotionCompensationService")]
    [InlineData("rollbackService")]
    public void Ctor_ShouldRejectNullDependencies(string parameterName)
    {
        Action act = parameterName switch
        {
            "addressResolver" => () => _ = new ScriptEvolutionSessionGAgent(
                null!,
                new StaticPolicyEvaluator(string.Empty),
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                new RecordingDefinitionPort(),
                new RecordingCatalogCommandPort(),
                new RecordingCompensationService(),
                new RecordingRollbackService()),
            "policyEvaluator" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                null!,
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                new RecordingDefinitionPort(),
                new RecordingCatalogCommandPort(),
                new RecordingCompensationService(),
                new RecordingRollbackService()),
            "validationService" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                new StaticPolicyEvaluator(string.Empty),
                null!,
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                new RecordingDefinitionPort(),
                new RecordingCatalogCommandPort(),
                new RecordingCompensationService(),
                new RecordingRollbackService()),
            "catalogBaselineReader" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                new StaticPolicyEvaluator(string.Empty),
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                null!,
                new RecordingDefinitionPort(),
                new RecordingCatalogCommandPort(),
                new RecordingCompensationService(),
                new RecordingRollbackService()),
            "definitionCommandPort" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                new StaticPolicyEvaluator(string.Empty),
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                null!,
                new RecordingCatalogCommandPort(),
                new RecordingCompensationService(),
                new RecordingRollbackService()),
            "catalogCommandPort" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                new StaticPolicyEvaluator(string.Empty),
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                new RecordingDefinitionPort(),
                null!,
                new RecordingCompensationService(),
                new RecordingRollbackService()),
            "promotionCompensationService" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                new StaticPolicyEvaluator(string.Empty),
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                new RecordingDefinitionPort(),
                new RecordingCatalogCommandPort(),
                null!,
                new RecordingRollbackService()),
            "rollbackService" => () => _ = new ScriptEvolutionSessionGAgent(
                new StaticAddressResolver(),
                new StaticPolicyEvaluator(string.Empty),
                new StaticValidationService(new ScriptEvolutionValidationReport(true, [])),
                new StaticBaselineReader(new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty)),
                new RecordingDefinitionPort(),
                new RecordingCatalogCommandPort(),
                new RecordingCompensationService(),
                null!),
            _ => throw new InvalidOperationException("Unexpected parameter name.")
        };

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(parameterName);
    }

    [Fact]
    public async Task Start_ShouldOwnProposalExecution_AndCompletePromotion()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort { DefinitionActorId = "definition-2" };
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
            Reason = "rollout",
        });

        definitionPort.Requests.Should().ContainSingle();
        catalogCommandPort.PromoteCalls.Should().ContainSingle();
        agent.State.ProposalId.Should().Be("proposal-1");
        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        agent.State.DefinitionActorId.Should().Be("definition-2");
        agent.State.CatalogActorId.Should().Be("script-catalog");
        agent.State.ValidationSucceeded.Should().BeTrue();
        publisher.Sent.Select(x => x.Payload.GetType().Name).Should().ContainInOrder(
            nameof(ScriptEvolutionProposedEvent),
            nameof(ScriptEvolutionBuildRequestedEvent),
            nameof(ScriptEvolutionValidatedEvent),
            nameof(ScriptEvolutionPromotedEvent));
        publisher.Published.Should().ContainSingle(x =>
            x.Direction == TopologyAudience.Self &&
            x.Payload.GetType() == typeof(ScriptEvolutionExecutionRequestedEvent) &&
            ((ScriptEvolutionExecutionRequestedEvent)x.Payload).ProposalId == "proposal-1");
    }

    [Fact]
    public async Task Start_ShouldPersistCompletedEventWithDefinitionSnapshot()
    {
        var publisher = new RecordingEventPublisher();
        var eventStore = new InMemoryEventStore();
        var definitionPort = new RecordingDefinitionPort { DefinitionActorId = "definition-2" };
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: new RecordingCatalogCommandPort(),
            eventStore: eventStore);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
            Reason = "rollout",
        });

        var stateEvents = await eventStore.GetEventsAsync(agent.Id, ct: CancellationToken.None);
        var completed = stateEvents
            .Select(x => x.EventData)
            .Where(x => x.Is(ScriptEvolutionSessionCompletedEvent.Descriptor))
            .Select(x => x.Unpack<ScriptEvolutionSessionCompletedEvent>())
            .Single();

        completed.Accepted.Should().BeTrue();
        completed.DefinitionActorId.Should().Be("definition-2");
        completed.DefinitionSnapshot.Should().NotBeNull();
        completed.DefinitionSnapshot.ScriptId.Should().Be("script-1");
        completed.DefinitionSnapshot.Revision.Should().Be("rev-2");
        completed.DefinitionSnapshot.SourceHash.Should().Be("hash-v2");
    }

    [Fact]
    public async Task Start_ShouldGenerateProposalId_WhenRequestDoesNotProvideOne()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort { DefinitionActorId = "definition-2" };
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: new RecordingCatalogCommandPort());

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = string.Empty,
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
            Reason = "rollout",
        });

        agent.State.ProposalId.Should().NotBeNullOrWhiteSpace();
        agent.State.ProposalId.Should().HaveLength(32);
        var executeRequest = publisher.Published.Should().ContainSingle().Subject.Payload
            .Should().BeOfType<ScriptEvolutionExecutionRequestedEvent>().Subject;
        executeRequest.ProposalId.Should().Be(agent.State.ProposalId);
    }

    [Theory]
    [InlineData("", "rev-2", "source-v2", "ScriptId is required.")]
    [InlineData("script-1", "", "source-v2", "CandidateRevision is required.")]
    [InlineData("script-1", "rev-2", "", "CandidateSource is required.")]
    public async Task Start_ShouldRejectMissingRequiredProposalFields(
        string scriptId,
        string candidateRevision,
        string candidateSource,
        string expectedMessage)
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));

        var act = () => agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-invalid",
            ScriptId = scriptId,
            BaseRevision = "rev-1",
            CandidateRevision = candidateRevision,
            CandidateSource = candidateSource,
            CandidateSourceHash = "hash-v2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
        agent.State.ProposalId.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldReject_WhenPolicyFails()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            publisher,
            policyFailure: "policy-denied",
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty));

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-policy",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeFalse();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        agent.State.FailureReason.Should().Contain("policy-denied");
        agent.State.ValidationSucceeded.Should().BeFalse();
        publisher.Sent.Select(x => x.Payload.GetType().Name).Should().ContainInOrder(
            nameof(ScriptEvolutionProposedEvent),
            nameof(ScriptEvolutionBuildRequestedEvent),
            nameof(ScriptEvolutionRejectedEvent));
    }

    [Fact]
    public async Task Start_ShouldReject_WhenValidationFails()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(false, ["compile-failed"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-validation",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeFalse();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        agent.State.ValidationSucceeded.Should().BeFalse();
        agent.State.Diagnostics.Should().ContainSingle(x => x == "compile-failed");
        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
        publisher.Sent.Select(x => x.Payload.GetType().Name).Should().ContainInOrder(
            nameof(ScriptEvolutionProposedEvent),
            nameof(ScriptEvolutionBuildRequestedEvent),
            nameof(ScriptEvolutionValidatedEvent),
            nameof(ScriptEvolutionRejectedEvent));
    }

    [Fact]
    public async Task ExecutionRequested_ShouldIgnore_WhenEnvelopeIsNotSelf()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-non-self",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ScriptEvolutionExecutionRequestedEvent
            {
                ProposalId = "proposal-non-self",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("other-actor", TopologyAudience.Children),
        });

        agent.State.Completed.Should().BeFalse();
        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecutionRequested_ShouldIgnore_WhenSessionHasNoProposal()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await agent.HandleScriptEvolutionExecutionRequested(new ScriptEvolutionExecutionRequestedEvent
        {
            ProposalId = "proposal-missing",
        });

        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
        agent.State.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task ExecutionRequested_ShouldIgnore_WhenProposalIdDoesNotMatchSession()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-bound",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        await agent.HandleScriptEvolutionExecutionRequested(new ScriptEvolutionExecutionRequestedEvent
        {
            ProposalId = "proposal-other",
        });

        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
        agent.State.Completed.Should().BeFalse();
        agent.State.ProposalId.Should().Be("proposal-bound");
    }

    [Fact]
    public async Task ExecutionRequested_ShouldIgnore_WhenSessionAlreadyCompleted()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: "policy-denied",
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-completed",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        await agent.HandleScriptEvolutionExecutionRequested(new ScriptEvolutionExecutionRequestedEvent
        {
            ProposalId = "proposal-completed",
        });

        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
        agent.State.Completed.Should().BeTrue();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
    }

    [Fact]
    public async Task Start_ShouldRejectWithPromotionFailed_WhenBaselineResolutionFails()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: "base revision was not found"),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-baseline",
            ScriptId = "script-1",
            BaseRevision = "rev-missing",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeFalse();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.PromotionFailed);
        agent.State.FailureReason.Should().Contain("base revision was not found");
        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldRejectWithPromotionFailed_WhenDefinitionUpsertThrows()
    {
        var publisher = new RecordingEventPublisher();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: new ThrowingDefinitionPort("definition-store-down"),
            catalogCommandPort: catalogCommandPort);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-upsert",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeFalse();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.PromotionFailed);
        agent.State.DefinitionActorId.Should().Be("script-definition:script-1");
        agent.State.FailureReason.Should().Contain("Failed to upsert candidate definition");
        agent.State.FailureReason.Should().Contain("definition-store-down");
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_ShouldCompensateAndReject_WhenPromotionThrows()
    {
        var publisher = new RecordingEventPublisher();
        var compensationService = new RecordingCompensationService();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: new RecordingDefinitionPort { DefinitionActorId = "definition-2" },
            catalogCommandPort: new ThrowingCatalogCommandPort("catalog-promote-down"),
            compensationService: compensationService);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-promote",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        compensationService.Calls.Should().ContainSingle();
        compensationService.Calls[0].CatalogActorId.Should().Be("script-catalog");
        compensationService.Calls[0].Proposal.ProposalId.Should().Be("proposal-promote");
        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeFalse();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.PromotionFailed);
        agent.State.DefinitionActorId.Should().Be("definition-2");
        agent.State.FailureReason.Should().Contain("catalog-promote-down");
        agent.State.FailureReason.Should().Contain("compensation=rollback_to_previous_active_revision_success");
    }

    [Fact]
    public async Task Start_ShouldRejectAndComplete_WhenExecutionThrowsUnexpectedly()
    {
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptEvolutionSessionGAgent(
            new StaticAddressResolver(),
            new StaticPolicyEvaluator(string.Empty),
            new ThrowingValidationService("validator-crashed"),
            new StaticBaselineReader(new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty)),
            new RecordingDefinitionPort(),
            new RecordingCatalogCommandPort(),
            new RecordingCompensationService(),
            new RecordingRollbackService())
        {
            EventPublisher = publisher,
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler>(publisher.CallbackScheduler)
                .BuildServiceProvider(),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionSessionState>(
                new InMemoryEventStore()),
        };

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-unexpected-failure",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeFalse();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.PromotionFailed);
        agent.State.FailureReason.Should().Contain("Unexpected script evolution execution failure");
        agent.State.FailureReason.Should().Contain("validator-crashed");
        agent.State.Diagnostics.Should().ContainSingle(x => x == nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Start_ShouldContinue_WhenManagerMirrorSendFails()
    {
        var publisher = new ThrowingManagerMirrorPublisher();
        var definitionPort = new RecordingDefinitionPort { DefinitionActorId = "definition-2" };
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-mirror-failure",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        publisher.ManagerMirrorFailures.Should().BeGreaterThan(0);
        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
        catalogCommandPort.PromoteCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_ShouldSkipManagerMirror_WhenManagerActorIdIsBlank()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            addressResolver: new BlankManagerAddressResolver());

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-no-manager",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        publisher.Sent.Should().BeEmpty();
        agent.State.Completed.Should().BeTrue();
        agent.State.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Start_ShouldBeIdempotent_ForSameProposal()
    {
        var publisher = new RecordingEventPublisher();
        var definitionPort = new RecordingDefinitionPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty),
            definitionPort: definitionPort,
            catalogCommandPort: catalogCommandPort);

        var request = new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-idempotent",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        };

        await StartSessionAsync(agent, publisher, request);
        var appliedVersion = agent.State.LastAppliedEventVersion;
        await agent.HandleStartScriptEvolutionSessionRequested(request);

        definitionPort.Requests.Should().ContainSingle();
        catalogCommandPort.PromoteCalls.Should().ContainSingle();
        agent.State.LastAppliedEventVersion.Should().Be(appliedVersion);
    }

    [Fact]
    public async Task Start_ShouldThrow_WhenSessionReceivesDifferentProposal()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, ["compile-ok"]),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: new ScriptCatalogEntrySnapshot(
                    ScriptId: "script-1",
                    ActiveRevision: "rev-1",
                    ActiveDefinitionActorId: "definition-1",
                    ActiveSourceHash: "hash-1",
                    PreviousRevision: string.Empty,
                    RevisionHistory: ["rev-1"],
                    LastProposalId: "proposal-prev"),
                BaselineSource: "query",
                FailureReason: string.Empty));

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        var act = () => agent.HandleStartScriptEvolutionSessionRequested(new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-2",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-3",
            CandidateSource = "source-v3",
            CandidateSourceHash = "hash-v3",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*proposal-1*proposal-2*");
    }

    [Fact]
    public async Task Rollback_ShouldUseRollbackService_AndMirrorIndexEvents()
    {
        var publisher = new RecordingEventPublisher();
        var rollbackService = new RecordingRollbackService();
        var agent = CreateAgent(
            publisher,
            policyFailure: "policy-denied",
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            rollbackService: rollbackService);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        publisher.Sent.Clear();

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-rollback",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "manual-rollback",
            CatalogActorId = string.Empty,
        });

        rollbackService.Requests.Should().ContainSingle();
        rollbackService.Requests[0].TargetRevision.Should().Be("rev-1");
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.RolledBack);
        publisher.Sent.Select(x => x.Payload.GetType().Name).Should().ContainInOrder(
            nameof(ScriptEvolutionRollbackRequestedEvent),
            nameof(ScriptEvolutionRolledBackEvent));
    }

    [Fact]
    public async Task Rollback_ShouldUseExplicitCatalogActorId_WhenProvided()
    {
        var publisher = new RecordingEventPublisher();
        var rollbackService = new RecordingRollbackService();
        var agent = CreateAgent(
            publisher,
            policyFailure: "policy-denied",
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            rollbackService: rollbackService);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-rollback-explicit",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        publisher.Sent.Clear();

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-rollback-explicit",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "manual-rollback",
            CatalogActorId = "catalog-explicit",
        });

        rollbackService.Requests.Should().ContainSingle();
        rollbackService.Requests[0].CatalogActorId.Should().Be("catalog-explicit");
        agent.State.CatalogActorId.Should().Be("catalog-explicit");
        publisher.Sent.Last().Payload.Should().BeOfType<ScriptEvolutionRolledBackEvent>()
            .Which.CatalogActorId.Should().Be("catalog-explicit");
    }

    [Fact]
    public async Task Rollback_ShouldIgnore_WhenSessionHasNoProposal()
    {
        var publisher = new RecordingEventPublisher();
        var rollbackService = new RecordingRollbackService();
        var agent = CreateAgent(
            publisher,
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            rollbackService: rollbackService);

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-missing",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "manual-rollback",
        });

        rollbackService.Requests.Should().BeEmpty();
        publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Rollback_ShouldIgnore_WhenProposalDoesNotMatchSession()
    {
        var publisher = new RecordingEventPublisher();
        var rollbackService = new RecordingRollbackService();
        var agent = CreateAgent(
            publisher,
            policyFailure: "policy-denied",
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(
                CatalogActorId: "script-catalog",
                Baseline: null,
                BaselineSource: "query",
                FailureReason: string.Empty),
            rollbackService: rollbackService);

        await StartSessionAsync(agent, publisher, new StartScriptEvolutionSessionRequestedEvent
        {
            ProposalId = "proposal-bound",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            CandidateSource = "source-v2",
            CandidateSourceHash = "hash-v2",
        });

        publisher.Sent.Clear();

        await agent.HandleScriptEvolutionRollbackRequested(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = "proposal-other",
            ScriptId = "script-1",
            TargetRevision = "rev-1",
            Reason = "manual-rollback",
        });

        rollbackService.Requests.Should().BeEmpty();
        publisher.Sent.Should().BeEmpty();
        agent.State.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
    }

    [Fact]
    public void BuildProposalFromState_ShouldRejectMissingRequiredState()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));

        var missingProposal = () => InvokePrivate<ScriptEvolutionProposal>(agent, "BuildProposalFromState");
        missingProposal.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*ProposalId is required*");

        agent.State.ProposalId = "proposal-1";
        var missingScript = () => InvokePrivate<ScriptEvolutionProposal>(agent, "BuildProposalFromState");
        missingScript.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*ScriptId is required*");

        agent.State.ScriptId = "script-1";
        var missingRevision = () => InvokePrivate<ScriptEvolutionProposal>(agent, "BuildProposalFromState");
        missingRevision.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*CandidateRevision is required*");

        agent.State.CandidateRevision = "rev-2";
        var missingSource = () => InvokePrivate<ScriptEvolutionProposal>(agent, "BuildProposalFromState");
        missingSource.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*CandidateSource is required*");
    }

    [Fact]
    public void BuildBestEffortProposalFromState_ShouldUseFallbackProposalId_WhenStateProposalIdMissing()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        agent.State.ScriptId = "script-1";
        agent.State.CandidateRevision = "rev-2";
        agent.State.CandidateSource = "source-v2";
        agent.State.CandidateSourceHash = "hash-v2";

        var proposal = InvokePrivate<ScriptEvolutionProposal>(
            agent,
            "BuildBestEffortProposalFromState",
            "fallback-proposal");

        proposal.ProposalId.Should().Be("fallback-proposal");
        proposal.ScriptId.Should().Be("script-1");
        proposal.CandidateRevision.Should().Be("rev-2");
        proposal.CandidateSource.Should().Be("source-v2");
    }

    [Fact]
    public void BuildBestEffortProposalFromState_ShouldPreferStateProposalId_WhenPresent()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        agent.State.ProposalId = "state-proposal";
        agent.State.ScriptId = "script-1";
        agent.State.CandidateRevision = "rev-2";
        agent.State.CandidateSource = "source-v2";

        var proposal = InvokePrivate<ScriptEvolutionProposal>(
            agent,
            "BuildBestEffortProposalFromState",
            "fallback-proposal");

        proposal.ProposalId.Should().Be("state-proposal");
    }

    [Fact]
    public void TransitionState_ShouldDefaultRejectedStatus_WhenRejectedEventOmitsStatus()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        var state = new ScriptEvolutionSessionState
        {
            ProposalId = "proposal-1",
            LastAppliedEventVersion = 2,
        };

        var next = InvokeTransition(
            agent,
            state,
            new ScriptEvolutionRejectedEvent
            {
                ProposalId = "proposal-1",
                FailureReason = "rejected",
                Status = string.Empty,
            });

        next.Status.Should().Be(ScriptEvolutionStatuses.Rejected);
        next.FailureReason.Should().Be("rejected");
        next.LastAppliedEventVersion.Should().Be(3);
    }

    [Fact]
    public void TransitionState_ShouldRetainCandidateRevision_WhenRollbackRequestTargetIsBlank()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        var state = new ScriptEvolutionSessionState
        {
            ProposalId = "proposal-1",
            CandidateRevision = "rev-2",
            LastAppliedEventVersion = 4,
        };

        var next = InvokeTransition(
            agent,
            state,
            new ScriptEvolutionRollbackRequestedEvent
            {
                ProposalId = "proposal-1",
                TargetRevision = string.Empty,
                Reason = "operator rollback",
                CatalogActorId = "catalog-explicit",
            });

        next.CandidateRevision.Should().Be("rev-2");
        next.CatalogActorId.Should().Be("catalog-explicit");
        next.Status.Should().Be(ScriptEvolutionStatuses.RollbackRequested);
        next.LastAppliedEventVersion.Should().Be(5);
    }

    [Fact]
    public void TransitionState_ShouldUseValidationFailedStatus_WhenValidationIsInvalid()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        var state = new ScriptEvolutionSessionState
        {
            ProposalId = "proposal-1",
            LastAppliedEventVersion = 1,
        };

        var next = InvokeTransition(
            agent,
            state,
            new ScriptEvolutionValidatedEvent
            {
                ProposalId = "proposal-1",
                IsValid = false,
                Diagnostics = { "compile-failed" },
            });

        next.Status.Should().Be(ScriptEvolutionStatuses.ValidationFailed);
        next.ValidationSucceeded.Should().BeFalse();
        next.Diagnostics.Should().ContainSingle(x => x == "compile-failed");
    }

    [Fact]
    public void TransitionState_ShouldApplyRolledBackFields()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        var state = new ScriptEvolutionSessionState
        {
            ProposalId = "proposal-1",
            FailureReason = "failed",
            LastAppliedEventVersion = 7,
        };

        var next = InvokeTransition(
            agent,
            state,
            new ScriptEvolutionRolledBackEvent
            {
                ProposalId = "proposal-1",
                TargetRevision = "rev-1",
                CatalogActorId = "catalog-1",
            });

        next.CandidateRevision.Should().Be("rev-1");
        next.CatalogActorId.Should().Be("catalog-1");
        next.FailureReason.Should().BeEmpty();
        next.Status.Should().Be(ScriptEvolutionStatuses.RolledBack);
        next.LastAppliedEventVersion.Should().Be(8);
    }

    [Fact]
    public void TransitionState_ShouldApplyCompletedFields()
    {
        var agent = CreateAgent(
            new RecordingEventPublisher(),
            policyFailure: string.Empty,
            validation: new ScriptEvolutionValidationReport(true, []),
            baselineResolution: new ScriptCatalogBaselineResolution(string.Empty, null, string.Empty, string.Empty));
        var state = new ScriptEvolutionSessionState
        {
            ProposalId = "proposal-1",
            LastAppliedEventVersion = 9,
        };

        var next = InvokeTransition(
            agent,
            state,
            new ScriptEvolutionSessionCompletedEvent
            {
                ProposalId = "proposal-1",
                Accepted = true,
                Status = ScriptEvolutionStatuses.Promoted,
                FailureReason = string.Empty,
                DefinitionActorId = "definition-1",
                CatalogActorId = "catalog-1",
                Diagnostics = { "ok" },
            });

        next.Completed.Should().BeTrue();
        next.Accepted.Should().BeTrue();
        next.Status.Should().Be(ScriptEvolutionStatuses.Promoted);
        next.DefinitionActorId.Should().Be("definition-1");
        next.CatalogActorId.Should().Be("catalog-1");
        next.Diagnostics.Should().ContainSingle("ok");
        next.LastAppliedEventVersion.Should().Be(10);
        next.LastEventId.Should().Be("proposal-1:session-completed");
    }

    private static ScriptEvolutionSessionGAgent CreateAgent(
        RecordingEventPublisher publisher,
        string policyFailure,
        ScriptEvolutionValidationReport validation,
        ScriptCatalogBaselineResolution baselineResolution,
        IScriptingActorAddressResolver? addressResolver = null,
        RecordingDefinitionPort? definitionPort = null,
        RecordingCatalogCommandPort? catalogCommandPort = null,
        RecordingCompensationService? compensationService = null,
        RecordingRollbackService? rollbackService = null,
        InMemoryEventStore? eventStore = null)
    {
        return new ScriptEvolutionSessionGAgent(
            addressResolver ?? new StaticAddressResolver(),
            new StaticPolicyEvaluator(policyFailure),
            new StaticValidationService(validation),
            new StaticBaselineReader(baselineResolution),
            definitionPort ?? new RecordingDefinitionPort(),
            catalogCommandPort ?? new RecordingCatalogCommandPort(),
            compensationService ?? new RecordingCompensationService(),
            rollbackService ?? new RecordingRollbackService())
        {
            EventPublisher = publisher,
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler>(publisher.CallbackScheduler)
                .BuildServiceProvider(),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptEvolutionSessionState>(
                eventStore ?? new InMemoryEventStore()),
        };
    }

    private static async Task StartSessionAsync(
        ScriptEvolutionSessionGAgent agent,
        RecordingEventPublisher publisher,
        StartScriptEvolutionSessionRequestedEvent request)
    {
        await agent.HandleStartScriptEvolutionSessionRequested(request);

        var executeRequest = publisher.Published
            .LastOrDefault(x =>
                x.Direction == TopologyAudience.Self &&
                x.Payload is ScriptEvolutionExecutionRequestedEvent);

        if (executeRequest?.Payload is ScriptEvolutionExecutionRequestedEvent executionRequested)
        {
            await agent.HandleEventAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(executionRequested),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(agent.Id, TopologyAudience.Self),
            });
        }
    }

    private class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];
        public List<PublishedMessage> Published { get; } = [];
        public RecordingCallbackScheduler CallbackScheduler { get; } = new();

        public virtual Task PublishAsync<T>(
            T evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            _ = evt;
            _ = direction;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Published.Add(new PublishedMessage(direction, evt));
            return Task.CompletedTask;
        }

        public virtual Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(new PublishedMessage(null, evt, targetActorId));
            return Task.CompletedTask;
        }

        public Task PublishCommittedStateEventAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            _ = evt;
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingManagerMirrorPublisher : RecordingEventPublisher
    {
        public int ManagerMirrorFailures { get; private set; }

        public override Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(targetActorId, "script-evolution-manager", StringComparison.Ordinal))
            {
                ManagerMirrorFailures++;
                throw new InvalidOperationException("manager-mirror-down");
            }

            return base.SendToAsync(targetActorId, evt, ct, sourceEnvelope, options);
        }
    }

    private sealed record PublishedMessage(TopologyAudience? Direction, IMessage Payload, string? TargetActorId = null);

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Timer scheduling is not required for this test.");
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

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class BlankManagerAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => string.Empty;

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "script-catalog";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class StaticPolicyEvaluator(string failure) : IScriptEvolutionPolicyEvaluator
    {
        public string EvaluateFailure(ScriptEvolutionProposal proposal)
        {
            _ = proposal;
            return failure;
        }
    }

    private sealed class StaticValidationService(ScriptEvolutionValidationReport report) : IScriptEvolutionValidationService
    {
        public Task<ScriptEvolutionValidationReport> ValidateAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(report);
        }
    }

    private sealed class ThrowingValidationService(string message) : IScriptEvolutionValidationService
    {
        public Task<ScriptEvolutionValidationReport> ValidateAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StaticBaselineReader(ScriptCatalogBaselineResolution resolution) : IScriptCatalogBaselineReader
    {
        public Task<ScriptCatalogBaselineResolution> ReadAsync(
            ScriptEvolutionProposal proposal,
            CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(resolution);
        }
    }

    private class RecordingDefinitionPort : IScriptDefinitionCommandPort
    {
        public string DefinitionActorId { get; init; } = "script-definition:script-1";
        public List<(string ScriptId, string Revision, string SourceHash)> Requests { get; } = [];

        public virtual Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            string sourceText,
            string sourceHash,
            string? definitionActorId,
            CancellationToken ct)
        {
            _ = sourceText;
            _ = definitionActorId;
            ct.ThrowIfCancellationRequested();
            Requests.Add((scriptId, scriptRevision, sourceHash));
            return Task.FromResult(new ScriptDefinitionUpsertResult(
                DefinitionActorId,
                new ScriptDefinitionSnapshot(
                    scriptId,
                    scriptRevision,
                    sourceText,
                    sourceHash,
                    "type.googleapis.com/example.State",
                    "type.googleapis.com/example.ReadModel",
                    "1",
                    "schema-hash-1"),
                new ScriptingCommandAcceptedReceipt(DefinitionActorId, "definition-command-1", "definition-correlation-1")));
        }
    }

    private sealed class ThrowingDefinitionPort(string message) : RecordingDefinitionPort
    {
        public override Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
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
            _ = definitionActorId;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }

    private class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        public List<(string ScriptId, string Revision, string DefinitionActorId)> PromoteCalls { get; } = [];
        public List<(string ScriptId, string TargetRevision)> RollbackCalls { get; } = [];

        public virtual Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
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
            _ = expectedBaseRevision;
            _ = sourceHash;
            _ = proposalId;
            ct.ThrowIfCancellationRequested();
            PromoteCalls.Add((scriptId, revision, definitionActorId));
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-1",
                "catalog-command-1",
                proposalId));
        }

        public Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct)
        {
            _ = catalogActorId;
            _ = reason;
            _ = proposalId;
            _ = expectedCurrentRevision;
            ct.ThrowIfCancellationRequested();
            RollbackCalls.Add((scriptId, targetRevision));
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-1",
                "catalog-rollback-command-1",
                proposalId));
        }
    }

    private sealed class ThrowingCatalogCommandPort(string message) : RecordingCatalogCommandPort
    {
        public override Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
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
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingCompensationService : IScriptPromotionCompensationService
    {
        public List<(string CatalogActorId, ScriptEvolutionProposal Proposal, ScriptCatalogEntrySnapshot? CatalogBefore)> Calls
        {
            get;
        } = [];

        public Task<string> TryCompensateAsync(
            string catalogActorId,
            ScriptEvolutionProposal proposal,
            ScriptCatalogEntrySnapshot? catalogBefore,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((catalogActorId, proposal, catalogBefore));
            return Task.FromResult("rollback_to_previous_active_revision_success");
        }
    }

    private sealed class RecordingRollbackService : IScriptEvolutionRollbackService
    {
        public List<ScriptRollbackRequest> Requests { get; } = [];

        public Task RollbackAsync(
            ScriptRollbackRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private static T InvokePrivate<T>(object instance, string methodName, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return (T)method!.Invoke(instance, arguments)!;
    }

    private static ScriptEvolutionSessionState InvokeTransition(
        ScriptEvolutionSessionGAgent agent,
        ScriptEvolutionSessionState current,
        IMessage evt)
    {
        return InvokePrivate<ScriptEvolutionSessionState>(agent, "TransitionState", current, evt);
    }
}
