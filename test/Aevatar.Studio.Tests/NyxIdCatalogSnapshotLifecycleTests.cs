using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdCatalogSnapshotLifecycleTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-14T01:00:00Z");

    [Fact]
    public async Task Query_ShouldUseExactAuthorityOwnerKindAndSubject()
    {
        var personal = Document("https://nyx.example", NyxIdCatalogOwnerKind.Personal, "user-a");
        var organization = Document("https://nyx.example", NyxIdCatalogOwnerKind.Organization, "user-a");
        var otherAuthority = Document("https://other.example", NyxIdCatalogOwnerKind.Personal, "user-a");
        var reader = new FilteringReader([personal, organization, otherAuthority]);
        var port = new ProjectionNyxIdCatalogSnapshotQueryPort(reader);

        var result = await port.GetAsync(Owner("https://nyx.example", NyxIdCatalogOwnerKind.Personal, "user-a"));

        result.Should().NotBeNull();
        result!.Owner.OwnerKind.Should().Be(NyxIdCatalogOwnerKind.Personal);
        result.Services.Should().ContainSingle().Which.NodeGrants.Should().ContainSingle()
            .Which.NodeId.Should().Be("node-primary");
        reader.LastQuery!.Filters.Should().HaveCount(3);
    }

    [Fact]
    public async Task Query_ShouldHideInvalidatedSnapshot()
    {
        var document = Document("https://nyx.example", NyxIdCatalogOwnerKind.Personal, "user-a");
        document.Invalidated = true;
        var port = new ProjectionNyxIdCatalogSnapshotQueryPort(new FilteringReader([document]));

        var result = await port.GetAsync(Owner("https://nyx.example", NyxIdCatalogOwnerKind.Personal, "user-a"));

        result.Should().BeNull();
    }

    [Fact]
    public void StateTransition_ShouldPreserveFactsOnRefreshFailureAndClearInvalidationOnObserve()
    {
        var agent = new NyxIdCatalogSnapshotGAgent();
        var owner = ActorOwner();
        var observed = Observed(owner, "digest-1");
        var initial = Transition(agent, new NyxIdCatalogSnapshotState(), observed);
        var failed = Transition(agent, initial, new NyxIdCatalogSnapshotRefreshFailedEvent
        {
            Owner = owner,
            FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
            FailureCode = "provider_unavailable",
        });
        var invalidated = Transition(agent, failed, new NyxIdCatalogSnapshotInvalidatedEvent
        {
            Owner = owner,
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(2)),
            Reason = "credential_revoked",
        });
        var refreshed = Transition(agent, invalidated, Observed(owner, "digest-2"));

        failed.Should().BeEquivalentTo(initial);
        invalidated.Invalidated.Should().BeTrue();
        invalidated.InvalidationReason.Should().Be("credential_revoked");
        refreshed.Invalidated.Should().BeFalse();
        refreshed.InvalidationReason.Should().BeEmpty();
        refreshed.ContentDigest.Should().Be("digest-2");
    }

    [Fact]
    public async Task HandleObserved_WithValidCatalog_ShouldCommitAndUpdateAuthoritativeState()
    {
        var (agent, eventSourcing) = NewAgent();

        await agent.HandleObserved(Observed(ActorOwner(), "digest-1"));

        eventSourcing.ConfirmCallCount.Should().Be(1);
        eventSourcing.RaisedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<NyxIdCatalogSnapshotObservedEvent>();
        agent.State.Owner.Should().BeEquivalentTo(ActorOwner());
        agent.State.ContentDigest.Should().Be("digest-1");
        agent.State.Services.Should().ContainSingle().Which.UserServiceId.Should().Be("svc-1");
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("owner-kind")]
    [InlineData("subject")]
    public async Task HandleObserved_WithIncompleteOwner_ShouldRejectWithoutCommit(string missingField)
    {
        var owner = ActorOwner();
        if (missingField == "authority")
            owner.Authority = string.Empty;
        else if (missingField == "owner-kind")
            owner.OwnerKind = NyxIdCatalogSnapshotOwnerKind.Unspecified;
        else
            owner.OwnerSubject = string.Empty;
        var (agent, eventSourcing) = NewAgent();

        var action = () => agent.HandleObserved(Observed(owner, "digest-1"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog owner identity is incomplete.");
        eventSourcing.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleObserved_WithNonIncreasingFreshnessInterval_ShouldRejectWithoutCommit()
    {
        var evt = Observed(ActorOwner(), "digest-1");
        evt.FreshUntil = evt.ObservedAt;
        var (agent, eventSourcing) = NewAgent();

        var action = () => agent.HandleObserved(evt);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog freshness interval is invalid.");
        eventSourcing.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleObserved_WithBlankDigest_ShouldRejectWithoutCommit()
    {
        var (agent, eventSourcing) = NewAgent();

        var action = () => agent.HandleObserved(Observed(ActorOwner(), " "));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog content digest is required.");
        eventSourcing.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleObserved_WhenOwnerChanges_ShouldRejectWithoutSecondCommit()
    {
        var (agent, eventSourcing) = NewAgent();
        await agent.HandleObserved(Observed(ActorOwner(), "digest-1"));
        var differentOwner = ActorOwner();
        differentOwner.OwnerSubject = "user-b";

        var action = () => agent.HandleObserved(Observed(differentOwner, "digest-2"));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog snapshot owner cannot change.");
        eventSourcing.ConfirmCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("service-id")]
    [InlineData("node-grants")]
    public async Task HandleObserved_WithIncompleteServiceFacts_ShouldRejectWithoutCommit(string missingFact)
    {
        var evt = Observed(ActorOwner(), "digest-1");
        if (missingFact == "service-id")
            evt.Services[0].UserServiceId = string.Empty;
        else
            evt.Services[0].Nodes.Clear();
        var (agent, eventSourcing) = NewAgent();

        var action = () => agent.HandleObserved(evt);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog services require exact service and node facts.");
        eventSourcing.ConfirmCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("invalidate")]
    public async Task OwnerBoundHandlers_WithMismatchedOwner_ShouldRejectWithoutSecondCommit(string handler)
    {
        var (agent, eventSourcing) = NewAgent();
        await agent.HandleObserved(Observed(ActorOwner(), "digest-1"));
        var differentOwner = ActorOwner();
        differentOwner.OwnerSubject = "user-b";

        Func<Task> action = handler == "refresh"
            ? () => agent.HandleRefreshFailed(new NyxIdCatalogSnapshotRefreshFailedEvent
            {
                Owner = differentOwner,
                FailedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                FailureCode = "provider_unavailable",
            })
            : () => agent.HandleInvalidated(new NyxIdCatalogSnapshotInvalidatedEvent
            {
                Owner = differentOwner,
                InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
                Reason = "credential_revoked",
            });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("catalog snapshot owner mismatch.");
        eventSourcing.ConfirmCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleInvalidated_WithSameReasonTwice_ShouldNotCommitSecondInvalidation()
    {
        var (agent, eventSourcing) = NewAgent();
        var owner = ActorOwner();
        await agent.HandleObserved(Observed(owner, "digest-1"));
        var invalidated = new NyxIdCatalogSnapshotInvalidatedEvent
        {
            Owner = owner,
            InvalidatedAt = Timestamp.FromDateTimeOffset(ObservedAt.AddMinutes(1)),
            Reason = "credential_revoked",
        };

        await agent.HandleInvalidated(invalidated);
        await agent.HandleInvalidated(invalidated.Clone());

        eventSourcing.ConfirmCallCount.Should().Be(2);
        eventSourcing.RaisedEvents.OfType<NyxIdCatalogSnapshotInvalidatedEvent>().Should().ContainSingle();
        agent.State.Invalidated.Should().BeTrue();
        agent.State.InvalidationReason.Should().Be("credential_revoked");
    }

    [Fact]
    public async Task Projector_ShouldMaterializeCommittedAuthoritativeState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdCatalogSnapshotCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(ObservedAt.AddMinutes(3)));
        var state = new NyxIdCatalogSnapshotState
        {
            Owner = ActorOwner(),
            ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddHours(1)),
            ExternalRevision = "revision-19",
            ContentDigest = "digest-19",
            Invalidated = true,
            InvalidationReason = "credential_revoked",
            Services =
            {
                new NyxIdCatalogSnapshotService
                {
                    UserServiceId = "svc-alpha",
                    DisplayName = "Connector Alpha",
                    ServiceSlug = "connector-alpha",
                    Reachable = true,
                    Nodes =
                    {
                        new NyxIdCatalogSnapshotNode
                        {
                            NodeId = "node-primary",
                            DisplayName = "Primary",
                            Primary = true,
                        },
                    },
                },
            },
        };

        await projector.ProjectAsync(Context(), CommittedEnvelope(state, version: 19, eventId: "evt-19"));

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts[0];
        document.ActorId.Should().Be("nyx-catalog-alpha");
        document.StateVersion.Should().Be(19);
        document.LastEventId.Should().Be("evt-19");
        document.Invalidated.Should().BeTrue();
        document.InvalidationReason.Should().Be("credential_revoked");
        document.Services.Should().ContainSingle().Which.Nodes.Should().ContainSingle()
            .Which.NodeId.Should().Be("node-primary");
    }

    [Fact]
    public async Task Projector_WhenEnvelopeIsNotMatchingCommittedState_ShouldNotWrite()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new NyxIdCatalogSnapshotCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(ObservedAt));

        await projector.ProjectAsync(Context(), new EventEnvelope
        {
            Id = "evt-unrelated",
            Payload = Any.Pack(new NyxIdCatalogSnapshotObservedEvent()),
        });
        await projector.ProjectAsync(Context(), new EventEnvelope
        {
            Id = "evt-wrong-state",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent { EventId = "evt-wrong-state", Version = 20 },
                StateRoot = Any.Pack(new NyxIdCatalogSnapshotObservedEvent()),
            }),
        });

        dispatcher.Upserts.Should().BeEmpty();
    }

    private static NyxIdCatalogSnapshotCurrentStateDocument Document(
        string authority,
        NyxIdCatalogOwnerKind ownerKind,
        string ownerSubject)
    {
        var document = new NyxIdCatalogSnapshotCurrentStateDocument
        {
            Id = $"catalog:{authority}:{ownerKind}:{ownerSubject}",
            ActorId = $"catalog:{authority}:{ownerKind}:{ownerSubject}",
            StateVersion = 7,
            Authority = authority,
            OwnerKind = (int)ownerKind,
            OwnerSubject = ownerSubject,
            ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddHours(1)),
            ContentDigest = "catalog-digest",
            ExternalRevision = "revision-7",
        };
        document.Services.Add(new NyxIdCatalogSnapshotServiceReadModel
        {
            UserServiceId = "svc-1",
            ServiceSlug = "service-one",
            Reachable = true,
            Nodes = { new NyxIdCatalogSnapshotNodeReadModel { NodeId = "node-primary", Primary = true } },
        });
        return document;
    }

    private static NyxIdCatalogOwnerIdentity Owner(
        string authority,
        NyxIdCatalogOwnerKind ownerKind,
        string subject) => new()
    {
        Authority = authority,
        OwnerKind = ownerKind,
        OwnerSubject = subject,
    };

    private static NyxIdCatalogSnapshotOwner ActorOwner() => new()
    {
        Authority = "https://nyx.example",
        OwnerKind = NyxIdCatalogSnapshotOwnerKind.Personal,
        OwnerSubject = "user-a",
    };

    private static NyxIdCatalogSnapshotObservedEvent Observed(
        NyxIdCatalogSnapshotOwner owner,
        string digest) => new()
    {
        Owner = owner,
        ObservedAt = Timestamp.FromDateTimeOffset(ObservedAt),
        FreshUntil = Timestamp.FromDateTimeOffset(ObservedAt.AddHours(1)),
        ContentDigest = digest,
        ExternalRevision = "revision-1",
        Services =
        {
            new NyxIdCatalogSnapshotService
            {
                UserServiceId = "svc-1",
                ServiceSlug = "service-one",
                Reachable = true,
                Nodes = { new NyxIdCatalogSnapshotNode { NodeId = "node-primary", Primary = true } },
            },
        },
    };

    private static NyxIdCatalogSnapshotState Transition(
        NyxIdCatalogSnapshotGAgent agent,
        NyxIdCatalogSnapshotState state,
        IMessage evt)
    {
        var method = typeof(NyxIdCatalogSnapshotGAgent).GetMethod(
            "TransitionState",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");
        return (NyxIdCatalogSnapshotState)method.Invoke(agent, [state, evt])!;
    }

    private static (NyxIdCatalogSnapshotGAgent Agent, RecordingEventSourcing EventSourcing) NewAgent()
    {
        var agent = new NyxIdCatalogSnapshotGAgent();
        var eventSourcing = new RecordingEventSourcing(agent);
        agent.EventSourcing = eventSourcing;
        return (agent, eventSourcing);
    }

    private static StudioMaterializationContext Context() => new()
    {
        RootActorId = "nyx-catalog-alpha",
        ProjectionKind = NyxIdCatalogSnapshotGAgent.ProjectionKind,
    };

    private static EventEnvelope CommittedEnvelope(
        NyxIdCatalogSnapshotState state,
        long version,
        string eventId) => new()
    {
        Id = eventId,
        Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
        Route = EnvelopeRouteSemantics.CreateObserverPublication("nyx-catalog-alpha"),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = version,
                EventData = Any.Pack(Observed(ActorOwner(), "digest-19")),
                Timestamp = Timestamp.FromDateTimeOffset(ObservedAt),
            },
            StateRoot = Any.Pack(state),
        }),
    };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<NyxIdCatalogSnapshotCurrentStateDocument>
    {
        public List<NyxIdCatalogSnapshotCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            NyxIdCatalogSnapshotCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingEventSourcing(NyxIdCatalogSnapshotGAgent agent)
        : IEventSourcingBehavior<NyxIdCatalogSnapshotState>
    {
        public List<IMessage> RaisedEvents { get; } = [];
        public int ConfirmCallCount { get; private set; }
        public long CurrentVersion => ConfirmCallCount;

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => RaisedEvents.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            ConfirmCallCount++;
            return Task.FromResult(new EventStoreCommitResult());
        }

        public Task PersistSnapshotAsync(
            NyxIdCatalogSnapshotState currentState,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<NyxIdCatalogSnapshotState?> ReplayAsync(
            string agentId,
            CancellationToken ct = default) => Task.FromResult<NyxIdCatalogSnapshotState?>(new());

        public void DiscardPendingEvents() => RaisedEvents.Clear();

        public NyxIdCatalogSnapshotState TransitionState(NyxIdCatalogSnapshotState current, IMessage evt) =>
            Transition(agent, current, evt);
    }

    private sealed class FilteringReader(IReadOnlyList<NyxIdCatalogSnapshotCurrentStateDocument> documents)
        : IProjectionDocumentReader<NyxIdCatalogSnapshotCurrentStateDocument, string>
    {
        public ProjectionDocumentQuery? LastQuery { get; private set; }

        public Task<NyxIdCatalogSnapshotCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default) =>
            Task.FromResult(documents.SingleOrDefault(document => document.Id == key));

        public Task<ProjectionDocumentQueryResult<NyxIdCatalogSnapshotCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            var filters = query.Filters.ToDictionary(filter => filter.FieldPath, StringComparer.Ordinal);
            var authority = (string)filters["authority"].Value.RawValue!;
            var ownerKind = Convert.ToInt32(filters["owner_kind"].Value.RawValue);
            var ownerSubject = (string)filters["owner_subject"].Value.RawValue!;
            var matches = documents.Where(document =>
                document.Authority == authority &&
                document.OwnerKind == ownerKind &&
                document.OwnerSubject == ownerSubject).ToList();
            return Task.FromResult(new ProjectionDocumentQueryResult<NyxIdCatalogSnapshotCurrentStateDocument>
            {
                Items = matches,
            });
        }
    }
}
