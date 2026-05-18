using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using AiTextMessageEndEvent = Aevatar.AI.Abstractions.TextMessageEndEvent;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class GAgentRunTerminalProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCompletedSession_FromCommittedSessionCompletion()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-14T00:00:00+00:00")));
        var observedAt = DateTimeOffset.Parse("2026-05-14T01:00:00+00:00");

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-1"),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "corr-1",
                    Content = "done",
                    ContentEmitted = true,
                },
                stateVersion: 4,
                eventId: "evt-1",
                correlationId: "corr-1",
                observedAt: observedAt));

        var doc = await store.GetAsync(GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-1"));
        doc.Should().NotBeNull();
        doc!.ActorId.Should().Be("actor-1");
        doc.SessionId.Should().Be("corr-1");
        doc.CorrelationId.Should().Be("corr-1");
        doc.Status.Should().Be((int)GAgentRunTerminalStatus.TextMessageCompleted);
        doc.InteractionKind.Should().Be((int)GAgentRunTerminalInteractionKind.DraftRun);
        doc.StateVersion.Should().Be(4);
        doc.LastEventId.Should().Be("evt-1");
        doc.ObservedAt.Should().Be(observedAt);
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeFailedSession_FromLegacyFailureMarker()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-14T00:00:00+00:00")));

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-approval", GAgentRunTerminalInteractionKind.Approval),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "[[AEVATAR_LLM_ERROR]] denied",
                },
                stateVersion: 2,
                eventId: "evt-failed",
                correlationId: "corr-approval",
                observedAt: DateTimeOffset.Parse("2026-05-14T01:00:00+00:00")));

        var doc = await store.GetAsync(GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-approval"));
        doc.Should().NotBeNull();
        doc!.Status.Should().Be((int)GAgentRunTerminalStatus.Failed);
        doc.ReasonCode.Should().Be("legacy_llm_error");
        doc.ReasonMessage.Should().Be("denied");
        doc.InteractionKind.Should().Be((int)GAgentRunTerminalInteractionKind.Approval);
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeFailedSession_FromToolAwareLlmFailureMessage()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-14T00:00:00+00:00")));

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-1", GAgentRunTerminalInteractionKind.DraftRun),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "corr-1",
                    Content = "LLM request failed [tools=search,fetch]: provider exploded",
                },
                stateVersion: 2,
                eventId: "evt-tool-failed",
                correlationId: "corr-1",
                observedAt: DateTimeOffset.Parse("2026-05-14T01:00:00+00:00")));

        var doc = await store.GetAsync(GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-1"));
        doc.Should().NotBeNull();
        doc!.Status.Should().Be((int)GAgentRunTerminalStatus.Failed);
        doc.ReasonCode.Should().Be("legacy_llm_error");
        doc.ReasonMessage.Should().Be("LLM request failed [tools=search,fetch]: provider exploded");
    }

    [Fact]
    public async Task ProjectAsync_ShouldPreserveKnownApprovalReasonCode_FromFailureMarker()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-14T00:00:00+00:00")));

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-approval", GAgentRunTerminalInteractionKind.Approval),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "session-1",
                    Content = "[[AEVATAR_LLM_ERROR]] approval_denied: User said no.",
                },
                stateVersion: 3,
                eventId: "evt-denied",
                correlationId: "corr-approval",
                observedAt: DateTimeOffset.Parse("2026-05-14T01:00:00+00:00")));

        var doc = await store.GetAsync(GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-approval"));
        doc.Should().NotBeNull();
        doc!.Status.Should().Be((int)GAgentRunTerminalStatus.Failed);
        doc.ReasonCode.Should().Be("approval_denied");
        doc.ReasonMessage.Should().Be("User said no.");
    }

    [Fact]
    public async Task ProjectAsync_ShouldKeepDraftRunKind_WhenExplicitSessionDiffersFromCorrelation()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-14T00:00:00+00:00")));

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-1", GAgentRunTerminalInteractionKind.DraftRun),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "explicit-session-1",
                    Content = "done",
                    ContentEmitted = true,
                },
                stateVersion: 5,
                eventId: "evt-explicit-session",
                correlationId: "corr-1",
                observedAt: DateTimeOffset.Parse("2026-05-14T01:00:00+00:00")));

        var doc = await store.GetAsync(GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-1"));
        doc.Should().NotBeNull();
        doc!.SessionId.Should().Be("explicit-session-1");
        doc.CorrelationId.Should().Be("corr-1");
        doc.InteractionKind.Should().Be((int)GAgentRunTerminalInteractionKind.DraftRun);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotOverwriteNewerReadModel_WithOlderStateVersion()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id)
        {
            EnforceMonotonicWrites = true,
        };
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-14T00:00:00+00:00")));

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-1"),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "corr-1",
                    Content = "newer",
                    ContentEmitted = true,
                },
                stateVersion: 6,
                eventId: "evt-newer",
                correlationId: "corr-1",
                observedAt: DateTimeOffset.Parse("2026-05-14T02:00:00+00:00")));
        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-1"),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "corr-1",
                    Content = "older",
                    ContentEmitted = true,
                },
                stateVersion: 5,
                eventId: "evt-older",
                correlationId: "corr-1",
                observedAt: DateTimeOffset.Parse("2026-05-14T01:00:00+00:00")));

        var doc = await store.GetAsync(GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-1"));
        doc.Should().NotBeNull();
        doc!.StateVersion.Should().Be(6);
        doc.LastEventId.Should().Be("evt-newer");
        doc.ObservedAt.Should().Be(DateTimeOffset.Parse("2026-05-14T02:00:00+00:00"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreLiveOnlyTerminalPayloads()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            CreateContext("actor-1"),
            new EventEnvelope
            {
                Id = "live",
                Payload = Any.Pack(new AiTextMessageEndEvent { SessionId = "session-1", Content = "done" }),
            });
        await projector.ProjectAsync(
            CreateContext("actor-1"),
            new EventEnvelope
            {
                Id = "agui",
                Payload = Any.Pack(new AGUIEvent { RunError = new RunErrorEvent { Message = "boom" } }),
            });

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreCompletion_WhenCorrelationIsOutsideActivatedInteraction()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var projector = new GAgentRunTerminalProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            CreateContext("actor-1", "corr-interaction"),
            WrapCommitted(
                new RoleChatSessionCompletedEvent
                {
                    SessionId = "ordinary-session",
                    Content = "ordinary chat done",
                },
                stateVersion: 8,
                eventId: "evt-ordinary",
                correlationId: "ordinary-chat-correlation",
                observedAt: DateTimeOffset.Parse("2026-05-14T01:00:00+00:00")));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task QueryReader_ShouldResolveByCorrelationId_ThenSessionId()
    {
        var store = new RecordingDocumentStore<GAgentRunTerminalReadModel>(x => x.Id);
        var reader = new GAgentRunTerminalQueryReader(store);
        var doc = new GAgentRunTerminalReadModel
        {
            Id = GAgentRunTerminalProjector.BuildDocumentId("actor-1", "corr-1"),
            ActorId = "actor-1",
            SessionId = "session-1",
            CorrelationId = "corr-1",
            InteractionKind = (int)GAgentRunTerminalInteractionKind.Approval,
            Status = (int)GAgentRunTerminalStatus.Failed,
            ReasonCode = "approval_denied",
            ReasonMessage = "denied",
            StateVersion = 7,
            LastEventId = "evt-7",
            ObservedAt = DateTimeOffset.Parse("2026-05-14T01:00:00+00:00"),
        };
        await store.UpsertAsync(doc);

        var byCorrelation = await reader.GetByCorrelationIdAsync("actor-1", "corr-1");
        byCorrelation.Should().NotBeNull();
        byCorrelation!.Status.Should().Be(GAgentRunTerminalStatus.Failed);
        byCorrelation.ReasonCode.Should().Be("approval_denied");

        var bySession = await reader.GetBySessionIdAsync("actor-1", "session-1");
        bySession.Should().NotBeNull();
        bySession!.CorrelationId.Should().Be("corr-1");
    }

    private static GAgentRunTerminalProjectionContext CreateContext(
        string actorId,
        string correlationId = "corr-1",
        GAgentRunTerminalInteractionKind interactionKind = GAgentRunTerminalInteractionKind.DraftRun) =>
        new()
        {
            RootActorId = actorId,
            ProjectionKind = interactionKind == GAgentRunTerminalInteractionKind.Approval
                ? "gagent-run-terminal-approval"
                : "gagent-run-terminal-draft-run",
            CorrelationId = correlationId,
            InteractionKind = interactionKind,
        };

    private static EventEnvelope WrapCommitted(
        RoleChatSessionCompletedEvent evt,
        long stateVersion,
        string eventId,
        string correlationId,
        DateTimeOffset observedAt) =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new RoleGAgentState()),
            }),
        };
}
