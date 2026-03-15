using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
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
            new StaticUppercaseDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec(),
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-1:read-model",
            RootActorId = "runtime-1",
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
            new StaticUppercaseDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec(),
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-2:read-model",
            RootActorId = "runtime-2",
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
    public async Task ProjectAsync_ShouldUseStateMirrorAsSourceOfTruth()
    {
        var dispatcher = new InMemoryProjectionDocumentStore<ScriptReadModelDocument>();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new StaticUppercaseDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec(),
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-3:read-model",
            RootActorId = "runtime-3",
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
            ReadModelTypeUrl = string.Empty,
            StateVersion = 3,
        };
        var state = ScriptCommittedEnvelopeFactory.CreateState(
            "definition-3",
            "script-3",
            "rev-3",
            new SimpleTextState
            {
                Value = "NEW",
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
        document!.DefinitionActorId.Should().Be("definition-3");
        document.ScriptId.Should().Be("script-3");
        document.Revision.Should().Be("rev-3");
        document.ReadModelTypeUrl.Should().Be(ScriptSources.UppercaseReadModelTypeUrl);
        document.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("NEW");
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StaticUppercaseDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            definitionActorId.Should().NotBeNullOrWhiteSpace();
            requestedRevision.Should().NotBeNullOrWhiteSpace();
            return Task.FromResult(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: requestedRevision,
                SourceText: ScriptSources.UppercaseBehavior,
                SourceHash: ScriptSources.UppercaseBehaviorHash,
                ScriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
                StateTypeUrl: Any.Pack(new SimpleTextState()).TypeUrl,
                ReadModelTypeUrl: Any.Pack(new SimpleTextReadModel()).TypeUrl,
                ReadModelSchemaVersion: string.Empty,
                ReadModelSchemaHash: string.Empty,
                ProtocolDescriptorSet: ByteString.Empty,
                StateDescriptorFullName: SimpleTextState.Descriptor.FullName,
                ReadModelDescriptorFullName: SimpleTextReadModel.Descriptor.FullName));
        }
    }
}
