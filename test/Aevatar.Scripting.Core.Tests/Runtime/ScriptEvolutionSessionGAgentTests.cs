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

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptEvolutionSessionGAgentTests
{
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
            x.Direction == EventDirection.Self &&
            x.Payload.GetType() == typeof(ScriptEvolutionExecutionRequestedEvent) &&
            ((ScriptEvolutionExecutionRequestedEvent)x.Payload).ProposalId == "proposal-1");
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
            Route = new EnvelopeRoute
            {
                PublisherActorId = "other-actor",
                Direction = EventDirection.Down,
            },
        });

        agent.State.Completed.Should().BeFalse();
        definitionPort.Requests.Should().BeEmpty();
        catalogCommandPort.PromoteCalls.Should().BeEmpty();
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

    private static ScriptEvolutionSessionGAgent CreateAgent(
        RecordingEventPublisher publisher,
        string policyFailure,
        ScriptEvolutionValidationReport validation,
        ScriptCatalogBaselineResolution baselineResolution,
        RecordingDefinitionPort? definitionPort = null,
        RecordingCatalogCommandPort? catalogCommandPort = null,
        RecordingCompensationService? compensationService = null,
        RecordingRollbackService? rollbackService = null)
    {
        return new ScriptEvolutionSessionGAgent(
            new StaticAddressResolver(),
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
                new InMemoryEventStore()),
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
                x.Direction == EventDirection.Self &&
                x.Payload is ScriptEvolutionExecutionRequestedEvent);

        if (executeRequest?.Payload is ScriptEvolutionExecutionRequestedEvent executionRequested)
        {
            await agent.HandleEventAsync(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(executionRequested),
                Route = new EnvelopeRoute
                {
                    PublisherActorId = agent.Id,
                    Direction = EventDirection.Self,
                },
            });
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Sent { get; } = [];
        public List<PublishedMessage> Published { get; } = [];
        public RecordingCallbackScheduler CallbackScheduler { get; } = new();

        public Task PublishAsync<T>(
            T evt,
            EventDirection direction = EventDirection.Down,
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

        public Task SendToAsync<T>(
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
    }

    private sealed record PublishedMessage(EventDirection? Direction, IMessage Payload, string? TargetActorId = null);

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

        public virtual Task<string> UpsertDefinitionAsync(
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
            return Task.FromResult(DefinitionActorId);
        }
    }

    private sealed class ThrowingDefinitionPort(string message) : RecordingDefinitionPort
    {
        public override Task<string> UpsertDefinitionAsync(
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

        public virtual Task PromoteCatalogRevisionAsync(
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
            _ = reason;
            _ = proposalId;
            _ = expectedCurrentRevision;
            ct.ThrowIfCancellationRequested();
            RollbackCalls.Add((scriptId, targetRevision));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCatalogCommandPort(string message) : RecordingCatalogCommandPort
    {
        public override Task PromoteCatalogRevisionAsync(
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
}
