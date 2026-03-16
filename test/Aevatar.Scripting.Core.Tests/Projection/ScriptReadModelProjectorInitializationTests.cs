using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelProjectorInitializationTests
{
    [Fact]
    public async Task InitializeAsync_ShouldNotSeedDocument()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = CreateProjector(dispatcher);
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-1:read-model",
            RootActorId = "runtime-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-1", CancellationToken.None);
        document.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonCommittedEnvelope()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = CreateProjector(dispatcher);
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-2:read-model",
            RootActorId = "runtime-2",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-non-committed",
                Payload = Any.Pack(new Empty()),
                Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)),
            },
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-2", CancellationToken.None);
        document.Should().BeNull();
    }

    private static ScriptReadModelProjector CreateProjector(
        InMemoryProjectionDocumentStore<ScriptReadModelDocument> dispatcher) =>
        new(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)));

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
