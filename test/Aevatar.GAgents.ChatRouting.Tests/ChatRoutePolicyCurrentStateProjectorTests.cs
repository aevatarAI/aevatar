using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Locks in the projector contract: committed <see cref="ChatRoutePolicyState"/>
/// events are overwrite-materialized into <see cref="ChatRoutePolicyCurrentStateDocument"/>
/// with every business field mirrored, re-projecting the same version is
/// idempotent, and non-committed envelopes produce no write.
/// </summary>
public sealed class ChatRoutePolicyCurrentStateProjectorTests
{
    private const string RootActorId = "chat-route-policy:scope-1";

    [Fact]
    public async Task ProjectAsync_OverwriteWritesDocument_FromCommittedState()
    {
        var dispatcher = new RecordingWriteDispatcher<ChatRoutePolicyCurrentStateDocument>();
        var projector = new ChatRoutePolicyCurrentStateProjector(
            dispatcher, new FixedProjectionClock(DateTimeOffset.Parse("2026-05-19T00:00:00Z")));

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(SampleState(version: 7), version: 7, eventId: "evt-7"));

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts[0];

        // Projection-envelope facts come from the committed StateEvent.
        document.Id.Should().Be(RootActorId);
        document.ActorId.Should().Be(RootActorId);
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");

        // Business fields mirror ChatRoutePolicyState 1:1, strongly typed.
        document.PolicyId.Should().Be(RootActorId);
        document.PolicyVersion.Should().Be(7);
        document.OwnerScope.RegistrationScopeId.Should().Be("scope-1");
        document.DefaultTarget.ForwardToModel.ModelName.Should().Be("chrono-llm/gpt-5.5");
        document.Rules.Select(rule => rule.RuleId).Should().Equal("alpha", "beta");
        document.PolicyUpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProjectAsync_SameVersionTwice_ProducesIdenticalDocument()
    {
        var dispatcher = new RecordingWriteDispatcher<ChatRoutePolicyCurrentStateDocument>();
        var projector = new ChatRoutePolicyCurrentStateProjector(
            dispatcher, new FixedProjectionClock(DateTimeOffset.Parse("2026-05-19T00:00:00Z")));
        var envelope = WrapCommitted(SampleState(version: 3), version: 3, eventId: "evt-3");

        await projector.ProjectAsync(NewContext(), envelope);
        await projector.ProjectAsync(NewContext(), envelope);

        dispatcher.Upserts.Should().HaveCount(2);
        dispatcher.Upserts[0].Should().Be(
            dispatcher.Upserts[1],
            "re-projecting the same committed version is a deterministic, idempotent overwrite");
    }

    [Fact]
    public async Task ProjectAsync_NoOp_WhenEnvelopeIsNotCommittedStateEvent()
    {
        var dispatcher = new RecordingWriteDispatcher<ChatRoutePolicyCurrentStateDocument>();
        var projector = new ChatRoutePolicyCurrentStateProjector(
            dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));

        // A bare domain event without the CommittedStateEventPublished wrapper
        // must not produce a write — the projector is downstream of committed
        // events only.
        var envelope = new EventEnvelope
        {
            Id = "raw",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new ChatRoutePolicyUpdated { State = SampleState(version: 1) }),
        };

        await projector.ProjectAsync(NewContext(), envelope);

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_RejectsNullArguments()
    {
        var projector = new ChatRoutePolicyCurrentStateProjector(
            new RecordingWriteDispatcher<ChatRoutePolicyCurrentStateDocument>(),
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await FluentActions
            .Awaiting(() => projector.ProjectAsync(null!, new EventEnvelope()).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => projector.ProjectAsync(NewContext(), null!).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var dispatcher = new RecordingWriteDispatcher<ChatRoutePolicyCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);

        FluentActions
            .Invoking(() => new ChatRoutePolicyCurrentStateProjector(null!, clock))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new ChatRoutePolicyCurrentStateProjector(dispatcher, null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static ChatRoutePolicyMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = ChatRoutePolicyGAgent.ProjectionKind,
    };

    private static ChatRoutePolicyState SampleState(long version)
    {
        var state = new ChatRoutePolicyState
        {
            PolicyId = RootActorId,
            OwnerScope = new OwnerScope { RegistrationScopeId = "scope-1" },
            DefaultTarget = ForwardToModelAction("chrono-llm/gpt-5.5"),
            Version = version,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        state.Rules.Add(new ChatRouteRule
        {
            RuleId = "alpha", Priority = 10, Action = ForwardToModelAction("model-a"),
        });
        state.Rules.Add(new ChatRouteRule
        {
            RuleId = "beta", Priority = 5, Action = ForwardToModelAction("model-b"),
        });
        return state;
    }

    private static ChatRouteAction ForwardToModelAction(string modelName) =>
        new() { ForwardToModel = new ForwardToModel { ModelName = modelName } };

    private static EventEnvelope WrapCommitted(ChatRoutePolicyState state, long version, string eventId)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(new ChatRoutePolicyUpdated { State = state }),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
