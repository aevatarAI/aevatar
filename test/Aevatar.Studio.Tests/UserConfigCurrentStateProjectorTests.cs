using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigCurrentStateProjectorTests
{
    private const string RootActorId = "user-config-scope-alpha";

    [Fact]
    public async Task ProjectAsync_ShouldCloneTypedSelectionAndPreserveCommittedStateVersion()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new UserConfigCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-22T08:00:00Z")));
        var selection = new LLMSelection
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = "/api/v1/proxy/s/chrono-llm-public",
            NyxIdUserServiceId = "us-alpha",
            ServiceSlugSnapshot = "chrono-llm-public",
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = "gpt-5.5",
            },
        };
        var state = new UserConfigGAgentState
        {
            DefaultModel = "gpt-5.5",
            PreferredLlmRoute = selection.RouteValue,
            LlmSelection = selection,
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(state, version: 37, eventId: "evt-37"));

        dispatcher.Upserts.Should().ContainSingle();
        var written = dispatcher.Upserts[0];
        written.StateVersion.Should().Be(37);
        written.LlmSelection.Should().NotBeNull();
        written.LlmSelection.Should().NotBeSameAs(selection);
        written.LlmSelection.RouteKind.Should().Be(LLMRouteKind.NyxIdUserService);
        written.LlmSelection.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm-public");
        written.LlmSelection.NyxIdUserServiceId.Should().Be("us-alpha");
        written.LlmSelection.ServiceSlugSnapshot.Should().Be("chrono-llm-public");
        written.LlmSelection.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ExplicitModel);
        written.LlmSelection.ModelSelection.ModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task ProjectAsync_ShouldLeaveSelectionAbsent_WhenLegacyStateHasNoSelection()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new UserConfigCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-22T08:00:00Z")));
        var state = new UserConfigGAgentState
        {
            DefaultModel = "legacy-model",
            PreferredLlmRoute = "/api/v1/proxy/s/legacy-service",
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(state, version: 11, eventId: "evt-11"));

        dispatcher.Upserts.Should().ContainSingle();
        dispatcher.Upserts[0].LlmSelection.Should().BeNull();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = UserConfigGAgent.ProjectionKind,
    };

    private static EventEnvelope WrapCommitted(
        UserConfigGAgentState state,
        long version,
        string eventId) =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-22T08:00:00Z")),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(new UserConfigUpdatedEvent
                    {
                        DefaultModel = state.DefaultModel,
                        PreferredLlmRoute = state.PreferredLlmRoute,
                    }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-22T08:00:00Z")),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<UserConfigCurrentStateDocument>
    {
        public List<UserConfigCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            UserConfigCurrentStateDocument readModel,
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
}
