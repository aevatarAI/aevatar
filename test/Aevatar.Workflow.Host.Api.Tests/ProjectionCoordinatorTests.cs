using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ProjectionCoordinatorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldExecuteProjectorsInRegistrationOrder()
    {
        var traces = new List<string>();
        IProjectionProjector<TestProjectionContext>[] projectors =
        [
            new RecordingProjector("projector-2", traces),
            new RecordingProjector("projector-1", traces),
        ];
        var coordinator = new ProjectionCoordinator<TestProjectionContext>(projectors);
        var context = new TestProjectionContext
        {
            SessionId = "session-1",
            RootActorId = "actor-1",
            ProjectionKind = "test-projection",
        };

        await coordinator.ProjectAsync(context, new EventEnvelope { Id = "evt-1" });

        traces.Should().Equal(
            "projector-2.project",
            "projector-1.project");
    }

    private sealed class RecordingProjector : IProjectionProjector<TestProjectionContext>
    {
        private readonly string _name;
        private readonly List<string> _traces;

        public RecordingProjector(string name, List<string> traces)
        {
            _name = name;
            _traces = traces;
        }

        public ValueTask ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            _traces.Add($"{_name}.project");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestProjectionContext : IProjectionSessionContext
    {
        public required string SessionId { get; init; }
        public required string RootActorId { get; init; }
        public required string ProjectionKind { get; init; }
    }
}
