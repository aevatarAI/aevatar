using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelProjectorNeutralityTests
{
    [Fact]
    public async Task ProjectAsync_ShouldNotMutateCommittedStateMirror()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));
        var readModel = new SimpleTextReadModel
        {
            HasValue = true,
            Value = "HELLO",
        };
        var state = ScriptCommittedEnvelopeFactory.CreateState(
            "definition-1",
            "script-1",
            "rev-1",
            new SimpleTextState { Value = "HELLO" },
            1,
            ScriptSources.UppercaseReadModelTypeUrl);
        var stateClone = state.Clone();
        var fact = new ScriptDomainFactCommitted
        {
            ActorId = "runtime-1",
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            RunId = "run-1",
            EventType = Any.Pack(new SimpleTextEvent()).TypeUrl,
            DomainEventPayload = Any.Pack(new SimpleTextEvent
            {
                CommandId = "command-1",
                Current = readModel.Clone(),
            }),
            ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
            ReadModelPayload = Any.Pack(readModel),
            StateVersion = 1,
        };
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-1",
            ProjectionKind = "script-execution-read-model",
        };

        await projector.ProjectAsync(
            context,
            ScriptCommittedEnvelopeFactory.CreateCommittedEnvelope(
                fact,
                state,
                "evt-1",
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        state.Should().BeEquivalentTo(stateClone);
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
