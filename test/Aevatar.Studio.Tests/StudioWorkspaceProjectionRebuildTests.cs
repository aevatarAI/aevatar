using System.Reflection;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkspaceProjectionRebuildTests
{
    private const string ScopeId = "scope-alpha";
    private const string WorkflowId = "wf-alpha";

    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    private static readonly PropertyInfo CommittedStateEventPublisherProperty = typeof(GAgentBase)
        .GetProperty(
            "CommittedStateEventPublisher",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GAgentBase.CommittedStateEventPublisher was not found.");

    [Fact]
    public async Task RepairProjection_WhenCurrentVersionExceedsMinimum_ShouldRepublishLatestCommittedState()
    {
        var eventStore = new InMemoryEventStore();
        var fixture = await CreateActorAsync(eventStore);
        await using var subscription = fixture.Subscription;
        var actor = fixture.Actor;
        await SaveDraftAsync(actor);
        _ = await fixture.Publications.Reader.ReadAsync();
        await SaveDraftAsync(actor);
        _ = await fixture.Publications.Reader.ReadAsync();
        var eventsBefore = await eventStore.GetEventsAsync(actor.Id);

        await actor.HandleEventAsync(Envelope(actor.Id, new RepairStudioWorkspaceProjectionCommand
        {
            WorkspaceId = actor.Id,
            ScopeId = ScopeId,
            MinimumStateVersion = 1,
            RepairRequestId = "repair-alpha",
        }));

        var eventsAfter = await eventStore.GetEventsAsync(actor.Id);
        eventsAfter.Should().HaveCount(eventsBefore.Count);
        var publication = await fixture.Publications.Reader.ReadAsync();
        publication.StateEvent.Version.Should().Be(2);
        publication.StateEvent.EventId.Should().Be($"rebuild:{actor.Id}:2");
        var rebuiltState = publication.StateRoot.Unpack<StudioWorkspaceState>();
        rebuiltState.Drafts.Should().ContainKey(WorkflowId);
        rebuiltState.Drafts[WorkflowId].Version.Should().Be(2);
    }

    [Fact]
    public async Task RepairProjection_WhenCurrentVersionEqualsMinimum_ShouldRepublishCommittedState()
    {
        var eventStore = new InMemoryEventStore();
        var fixture = await CreateActorAsync(eventStore);
        await using var subscription = fixture.Subscription;
        var actor = fixture.Actor;
        await SaveDraftAsync(actor);
        _ = await fixture.Publications.Reader.ReadAsync();
        var eventsBefore = await eventStore.GetEventsAsync(actor.Id);

        await actor.HandleEventAsync(Envelope(actor.Id, new RepairStudioWorkspaceProjectionCommand
        {
            WorkspaceId = actor.Id,
            ScopeId = ScopeId,
            MinimumStateVersion = 1,
            RepairRequestId = "repair-equal",
        }));

        (await eventStore.GetEventsAsync(actor.Id)).Should().HaveCount(eventsBefore.Count);
        var publication = await fixture.Publications.Reader.ReadAsync();
        publication.StateEvent.Version.Should().Be(1);
        publication.StateEvent.EventId.Should().Be($"rebuild:{actor.Id}:1");
        publication.StateRoot.Unpack<StudioWorkspaceState>()
            .Drafts[WorkflowId].Version.Should().Be(1);
    }

    [Fact]
    public async Task RepairProjection_WhenMinimumExceedsCurrentVersion_ShouldRejectWithoutPublishingOrAppending()
    {
        var eventStore = new InMemoryEventStore();
        var fixture = await CreateActorAsync(eventStore);
        await using var subscription = fixture.Subscription;
        var actor = fixture.Actor;
        await SaveDraftAsync(actor);
        _ = await fixture.Publications.Reader.ReadAsync();
        var eventsBefore = await eventStore.GetEventsAsync(actor.Id);

        var repair = () => actor.HandleEventAsync(Envelope(
            actor.Id,
            new RepairStudioWorkspaceProjectionCommand
            {
                WorkspaceId = actor.Id,
                ScopeId = ScopeId,
                MinimumStateVersion = 2,
                RepairRequestId = "repair-stale",
            }));

        await repair.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Workspace projection repair source version changed.");
        fixture.Publications.Reader.TryRead(out _).Should().BeFalse();
        (await eventStore.GetEventsAsync(actor.Id)).Should().HaveCount(eventsBefore.Count);
    }

    private static async Task<ActorFixture> CreateActorAsync(InMemoryEventStore eventStore)
    {
        var actorId = StudioWorkspaceConventions.BuildActorId(ScopeId);
        var streams = new InMemoryStreamProvider();
        var publications = Channel.CreateUnbounded<CommittedStateEventPublished>();
        var subscription = await streams.GetStream(actorId).SubscribeAsync<EventEnvelope>(envelope =>
        {
            if (envelope.Route.IsObserverPublication() &&
                envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
            {
                publications.Writer.TryWrite(
                    envelope.Payload.Unpack<CommittedStateEventPublished>());
            }

            return Task.CompletedTask;
        });
        var publisher = new LocalActorPublisher(actorId, () => null, () => 0, streams);
        var actor = new StudioWorkspaceGAgent
        {
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<StudioWorkspaceState>(eventStore),
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(actor, [actorId]);
        CommittedStateEventPublisherProperty.SetValue(actor, publisher);
        await actor.ActivateAsync();
        return new ActorFixture(actor, publications, subscription);
    }

    private static Task SaveDraftAsync(StudioWorkspaceGAgent actor) =>
        actor.HandleEventAsync(Envelope(actor.Id, new StudioWorkflowDraftSaved
        {
            WorkspaceId = actor.Id,
            ScopeId = ScopeId,
            Draft = new StudioWorkflowDraft
            {
                WorkflowId = WorkflowId,
                Name = "Workflow Alpha",
                FileName = "workflow-alpha.yaml",
                DirectoryId = "dir-alpha",
                DirectoryLabel = "Drafts",
                Yaml = "name: workflow-alpha",
            },
            SavedAtUtc = Timestamp.FromDateTimeOffset(
                DateTimeOffset.Parse("2026-07-25T00:00:00Z")),
        }));

    private static EventEnvelope Envelope(string actorId, Google.Protobuf.IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", actorId),
        };

    private sealed record ActorFixture(
        StudioWorkspaceGAgent Actor,
        Channel<CommittedStateEventPublished> Publications,
        IAsyncDisposable Subscription);

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
