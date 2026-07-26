using Aevatar.CQRS.Projection.Stores.Abstractions;
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
