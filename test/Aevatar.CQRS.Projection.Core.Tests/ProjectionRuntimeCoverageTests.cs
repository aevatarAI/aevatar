using Aevatar.CQRS.Projection.Runtime.Runtime;
using FluentAssertions;
namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionRuntimeCoverageTests
{
    [Fact]
    public void ProjectionStoreDispatcher_WhenNoBindings_ShouldThrow()
    {
        var bindings = Array.Empty<IProjectionWriteSink<TestReadModel>>();

        Action act = () => new ProjectionStoreDispatcher<TestReadModel>(bindings);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No configured projection store bindings*");
    }

    private sealed class TestReadModel : IProjectionReadModel
    {
        public string Id { get; init; } = string.Empty;

        public string ActorId => Id;

        public long StateVersion { get; init; }

        public string LastEventId { get; init; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; init; }
    }
}
