using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCommittedStateIntoReadModelDocument()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-1",
            ProjectionKind = "script-execution-read-model",
        };
        var readModel = new SimpleTextReadModel
        {
            HasValue = true,
            Value = "HELLO",
        };
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
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "IGNORED-BY-PROJECTOR",
                },
            }),
            ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
            ReadModelPayload = Any.Pack(readModel),
            StateVersion = 1,
        };
        var state = ScriptCommittedEnvelopeFactory.CreateState(
            "definition-1",
            "script-1",
            "rev-1",
            new SimpleTextState { Value = "HELLO" },
            fact.StateVersion,
            ScriptSources.UppercaseReadModelTypeUrl);

        await projector.ProjectAsync(
            context,
            ScriptCommittedEnvelopeFactory.CreateCommittedEnvelope(
                fact,
                state,
                "evt-1",
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ScriptId.Should().Be("script-1");
        document.DefinitionActorId.Should().Be("definition-1");
        document.Revision.Should().Be("rev-1");
        document.StateVersion.Should().Be(1);
        document.LastEventId.Should().Be("evt-1");
        document.ReadModelPayload.Should().NotBeNull();
        document.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonCommittedEnvelope()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-2",
            ProjectionKind = "script-execution-read-model",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-raw",
                Payload = Any.Pack(new ScriptDomainFactCommitted
                {
                    ActorId = "runtime-2",
                    StateVersion = 1,
                }),
                Timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            },
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-2", CancellationToken.None);
        document.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldUseCommittedReadModelPayloadAsSourceOfTruth()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-3",
            ProjectionKind = "script-execution-read-model",
        };
        var committedReadModel = new SimpleTextReadModel
        {
            HasValue = true,
            Value = "NEW",
        };
        var fact = new ScriptDomainFactCommitted
        {
            ActorId = "runtime-3",
            DefinitionActorId = string.Empty,
            ScriptId = string.Empty,
            Revision = string.Empty,
            RunId = "run-3",
            EventType = Any.Pack(new SimpleTextEvent()).TypeUrl,
            DomainEventPayload = Any.Pack(new SimpleTextEvent
            {
                CommandId = "command-3",
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "OLD",
                },
            }),
            ReadModelTypeUrl = Any.Pack(committedReadModel).TypeUrl,
            ReadModelPayload = Any.Pack(committedReadModel),
            StateVersion = 3,
        };
        var state = ScriptCommittedEnvelopeFactory.CreateState(
            "definition-3",
            "script-3",
            "rev-3",
            new SimpleTextState
            {
                Value = "STALE-STATE",
            },
            fact.StateVersion,
            ScriptSources.UppercaseReadModelTypeUrl);

        await projector.ProjectAsync(
            context,
            ScriptCommittedEnvelopeFactory.CreateCommittedEnvelope(
                fact,
                state,
                "evt-3",
                new DateTimeOffset(2026, 3, 1, 0, 0, 3, TimeSpan.Zero)),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-3", CancellationToken.None);
        document.Should().NotBeNull();
        document!.DefinitionActorId.Should().BeEmpty();
        document.ScriptId.Should().BeEmpty();
        document.Revision.Should().BeEmpty();
        document.ReadModelTypeUrl.Should().Be(Any.Pack(committedReadModel).TypeUrl);
        document.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("NEW");
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
