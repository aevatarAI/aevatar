
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ProjectionCoordinatorTests
{
    [Fact]
    public async Task ProjectionCoordinator_ShouldExecuteProjectorsInRegistrationOrder()
    {
        var traces = new List<string>();
        IProjectionProjector<TestProjectionContext, string>[] projectors =
        [
            new RecordingProjector("projector-2", traces),
            new RecordingProjector("projector-1", traces),
        ];
        var coordinator = new ProjectionCoordinator<TestProjectionContext, string>(projectors);
        var context = new TestProjectionContext
        {
            ProjectionId = "projection-1",
            RootActorId = "actor-1",
        };

        await coordinator.InitializeAsync(context);
        await coordinator.ProjectAsync(context, new EventEnvelope { Id = "evt-1" });
        await coordinator.CompleteAsync(context, "done");

        traces.Should().Equal(
            "projector-2.initialize",
            "projector-1.initialize",
            "projector-2.project",
            "projector-1.project",
            "projector-2.complete",
            "projector-1.complete");
    }

    private sealed class RecordingProjector : IProjectionProjector<TestProjectionContext, string>
    {
        private readonly string _name;
        private readonly List<string> _traces;

        public RecordingProjector(string name, List<string> traces)
        {
            _name = name;
            _traces = traces;
        }

        public ValueTask InitializeAsync(TestProjectionContext context, CancellationToken ct = default)
        {
            _traces.Add($"{_name}.initialize");
            return ValueTask.CompletedTask;
        }

        public ValueTask ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
        {
            _traces.Add($"{_name}.project");
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(TestProjectionContext context, string topology, CancellationToken ct = default)
        {
            _traces.Add($"{_name}.complete");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestProjectionContext : IProjectionContext
    {
        public string ProjectionId { get; init; } = string.Empty;
        public string RootActorId { get; init; } = string.Empty;
    }
}
