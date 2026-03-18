using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionStoreValueAndResultCoverageTests
{
    [Fact]
    public void ProjectionDocumentValue_FactoryMethods_ShouldNormalizeNullsAndUtcValues()
    {
        var local = new DateTime(2026, 3, 18, 9, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 3, 18, 10, 0, 0, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 3, 18, 11, 0, 0, DateTimeKind.Utc);

        ProjectionDocumentValue.Empty.Kind.Should().Be(ProjectionDocumentValueKind.None);
        ProjectionDocumentValue.Empty.RawValue.Should().BeNull();

        ProjectionDocumentValue.FromString(null).RawValue.Should().Be(string.Empty);
        ProjectionDocumentValue.FromStrings(["a", null]).RawValue.Should().BeEquivalentTo(new[] { "a", string.Empty });
        ProjectionDocumentValue.FromStrings(null!).RawValue.Should().BeEquivalentTo(Array.Empty<string>());

        ProjectionDocumentValue.FromInt64(42).RawValue.Should().Be(42L);
        ProjectionDocumentValue.FromInt64s([1, 2]).RawValue.Should().BeEquivalentTo(new long[] { 1, 2 });
        ProjectionDocumentValue.FromInt64s(null!).RawValue.Should().BeEquivalentTo(Array.Empty<long>());

        ProjectionDocumentValue.FromDouble(3.14).RawValue.Should().Be(3.14);
        ProjectionDocumentValue.FromDoubles([1.5, 2.5]).RawValue.Should().BeEquivalentTo(new[] { 1.5, 2.5 });
        ProjectionDocumentValue.FromDoubles(null!).RawValue.Should().BeEquivalentTo(Array.Empty<double>());

        ProjectionDocumentValue.FromBool(true).RawValue.Should().Be(true);
        ProjectionDocumentValue.FromBools([true, false]).RawValue.Should().BeEquivalentTo(new[] { true, false });
        ProjectionDocumentValue.FromBools(null!).RawValue.Should().BeEquivalentTo(Array.Empty<bool>());

        ProjectionDocumentValue.FromDateTime(utc).RawValue.Should().Be(utc);
        ProjectionDocumentValue.FromDateTime(local).RawValue.Should().Be(local.ToUniversalTime());
        ProjectionDocumentValue.FromDateTimes([utc, unspecified]).RawValue.Should().BeEquivalentTo(
            new[]
            {
                utc,
                unspecified.ToUniversalTime(),
            });
        ProjectionDocumentValue.FromDateTimes(null!).RawValue.Should().BeEquivalentTo(Array.Empty<DateTime>());
    }

    [Fact]
    public void ProjectionWriteResult_ShouldExposeDispositionFlags()
    {
        ProjectionWriteResult.Applied().IsApplied.Should().BeTrue();
        ProjectionWriteResult.Applied().IsNonTerminal.Should().BeFalse();
        ProjectionWriteResult.Applied().IsRejected.Should().BeFalse();

        ProjectionWriteResult.Duplicate().IsNonTerminal.Should().BeTrue();
        ProjectionWriteResult.Stale().IsNonTerminal.Should().BeTrue();
        ProjectionWriteResult.Gap().IsRejected.Should().BeTrue();
        ProjectionWriteResult.Conflict().IsRejected.Should().BeTrue();
    }

    [Fact]
    public void ProjectionWriteResultEvaluator_ShouldEnforceVersionAndIdentityRules()
    {
        var incoming = CreateReadModel("actor-1", "evt-2", 2);

        ProjectionWriteResultEvaluator.Evaluate(null, incoming)
            .Disposition
            .Should()
            .Be(ProjectionWriteDisposition.Applied);
        ProjectionWriteResultEvaluator.Evaluate(
                CreateReadModel("actor-1", "evt-1", 3),
                CreateReadModel("actor-1", "evt-2", 2))
            .Disposition
            .Should()
            .Be(ProjectionWriteDisposition.Stale);
        ProjectionWriteResultEvaluator.Evaluate(
                CreateReadModel("actor-1", "evt-1", 2),
                CreateReadModel("actor-1", "evt-2", 2))
            .Disposition
            .Should()
            .Be(ProjectionWriteDisposition.Conflict);
        ProjectionWriteResultEvaluator.Evaluate(
                CreateReadModel("actor-1", "evt-2", 2),
                CreateReadModel("actor-1", "evt-2", 2))
            .Disposition
            .Should()
            .Be(ProjectionWriteDisposition.Applied);
        ProjectionWriteResultEvaluator.Evaluate(
                CreateReadModel("actor-2", "evt-1", 1),
                CreateReadModel("actor-1", "evt-2", 2))
            .Disposition
            .Should()
            .Be(ProjectionWriteDisposition.Conflict);
        ProjectionWriteResultEvaluator.Evaluate(
                CreateReadModel("actor-1", "evt-1", 1),
                CreateReadModel("actor-1", "evt-3", 4))
            .Disposition
            .Should()
            .Be(ProjectionWriteDisposition.Gap);
    }

    [Fact]
    public void ProjectionWriteResultEvaluator_ShouldRejectMissingReadModelIdentity()
    {
        var missingId = new TestProjectionReadModel
        {
            ActorId = "actor-1",
            Id = string.Empty,
        };
        var missingActorId = new TestProjectionReadModel
        {
            ActorId = string.Empty,
            Id = "doc-1",
        };

        Action actMissingId = () => ProjectionWriteResultEvaluator.Evaluate(null, missingId);
        Action actMissingActorId = () => ProjectionWriteResultEvaluator.Evaluate(null, missingActorId);

        actMissingId.Should().Throw<InvalidOperationException>()
            .WithMessage("*id must be non-empty*");
        actMissingActorId.Should().Throw<InvalidOperationException>()
            .WithMessage("*actor id must be non-empty*");
    }

    private static TestProjectionReadModel CreateReadModel(
        string actorId,
        string eventId,
        long stateVersion)
    {
        return new TestProjectionReadModel
        {
            ActorId = actorId,
            Id = $"{actorId}:{stateVersion}",
            LastEventId = eventId,
            StateVersion = stateVersion,
        };
    }

    private sealed class TestProjectionReadModel : IProjectionReadModel
    {
        public string Id { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string LastEventId { get; set; } = string.Empty;

        public long StateVersion { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
