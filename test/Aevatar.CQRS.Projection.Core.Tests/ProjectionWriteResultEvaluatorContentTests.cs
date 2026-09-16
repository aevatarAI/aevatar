using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionWriteResultEvaluatorContentTests
{
    [Fact]
    public void Evaluate_ShouldTreatByteEquivalentProtobufAtSameVersionAsDuplicate()
    {
        var existing = Build("stable");
        var incoming = existing.Clone();

        var result = ProjectionWriteResultEvaluator.Evaluate(existing, incoming);

        result.Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public void Evaluate_ShouldRejectDifferentProtobufContentAtSameVersionAndEventId()
    {
        var existing = Build("stable");
        var incoming = Build("conflicting");

        var result = ProjectionWriteResultEvaluator.Evaluate(existing, incoming);

        result.Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
    }

    [Fact]
    public void Evaluate_ShouldApplyAuthoritativeMaintenanceRepublishAtSameVersion()
    {
        var existing = Build("stale-running");
        var incoming = Build("authoritative-terminal");
        incoming.LastEventId = CommittedStateRepublish.BuildEventId(incoming.ActorId, incoming.StateVersion);

        var result = ProjectionWriteResultEvaluator.Evaluate(existing, incoming);

        result.Disposition.Should().Be(ProjectionWriteDisposition.Applied);
    }

    [Fact]
    public void Evaluate_ShouldRejectDelayedOrdinaryWriteAfterMaintenanceRepublishAtSameVersion()
    {
        var existing = Build("authoritative-terminal");
        existing.LastEventId = CommittedStateRepublish.BuildEventId(existing.ActorId, existing.StateVersion);
        var incoming = Build("stale-running");

        var result = ProjectionWriteResultEvaluator.Evaluate(existing, incoming);

        result.Disposition.Should().Be(ProjectionWriteDisposition.Stale);
    }

    private static TestStoreReadModel Build(string value) => new()
    {
        Id = "conversation-alpha",
        ActorId = "conversation-alpha",
        StateVersion = 7,
        LastEventId = "event-alpha-7",
        UpdatedAt = DateTimeOffset.Parse("2026-07-25T06:00:00Z"),
        Value = value,
    };
}
