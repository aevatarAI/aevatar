using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class HealthProbeTargetProjectorTests
{
    [Fact]
    public async Task Projects_CurrentStateIntoDocument()
    {
        var dispatcher = new RecordingDispatcher();
        var projector = new HealthProbeTargetProjector(dispatcher, new FrozenClock());
        var context = new HealthProbeMaterializationContext
        {
            RootActorId = "health-probe::nyxid-auth",
            ProjectionKind = HealthProbeTargetGAgent.ProjectionKind,
        };
        var state = new HealthProbeTargetState
        {
            Spec = new HealthProbeTargetDescriptor
            {
                Slug = "nyxid-auth",
                DisplayName = "NyxID Auth",
                Category = "upstream",
                ProbeKind = "http_status",
                IntervalSeconds = 30,
                Enabled = true,
            },
            LastOutcome = new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Ok,
                LatencyMs = 42,
                Detail = "http_200",
                ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-21T10:00:00+00:00")),
            },
            ConsecutiveFailures = 0,
            LastSuccessAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-21T10:00:00+00:00")),
            LastCheckAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-21T10:00:00+00:00")),
        };
        state.RecentOutcomes.Add(new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Down,
            Detail = "http_500",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-21T09:59:00+00:00")),
        });
        state.RecentOutcomes.Add(state.LastOutcome.Clone());

        await projector.ProjectAsync(context, BuildEnvelope(7, state));

        dispatcher.Upserted.Should().ContainSingle();
        var doc = dispatcher.Upserted[0];
        doc.Id.Should().Be("nyxid-auth");
        doc.Slug.Should().Be("nyxid-auth");
        doc.Status.Should().Be(HealthOutcomeStatus.Ok);
        doc.LatencyMs.Should().Be(42);
        doc.StateVersion.Should().Be(7);
        doc.ActorId.Should().Be("health-probe::nyxid-auth");
        doc.RecentOutcomes.Should().HaveCount(2);
        doc.RecentOutcomes[0].Detail.Should().Be("http_500");
        doc.RecentOutcomes[1].Detail.Should().Be("http_200");
    }

    [Fact]
    public async Task Ignores_EnvelopeWithoutState()
    {
        var dispatcher = new RecordingDispatcher();
        var projector = new HealthProbeTargetProjector(dispatcher, new FrozenClock());
        var context = new HealthProbeMaterializationContext
        {
            RootActorId = "health-probe::orphan",
            ProjectionKind = HealthProbeTargetGAgent.ProjectionKind,
        };

        await projector.ProjectAsync(context, new EventEnvelope());

        dispatcher.Upserted.Should().BeEmpty();
    }

    private static EventEnvelope BuildEnvelope(long version, HealthProbeTargetState state)
    {
        var timestamp = DateTimeOffset.Parse("2026-05-21T10:00:00+00:00");
        return new EventEnvelope
        {
            Id = $"env-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = $"evt-{version}",
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(new HealthProbeObserved
                    {
                        Outcome = state.LastOutcome,
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingDispatcher : IProjectionWriteDispatcher<HealthProbeTargetDocument>
    {
        public List<HealthProbeTargetDocument> Upserted { get; } = new();

        public Task<ProjectionWriteResult> UpsertAsync(HealthProbeTargetDocument readModel, CancellationToken ct = default)
        {
            Upserted.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FrozenClock : IProjectionClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-05-21T10:00:00+00:00");
    }
}
