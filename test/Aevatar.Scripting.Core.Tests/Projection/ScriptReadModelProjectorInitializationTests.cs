using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelProjectorInitializationTests
{
    [Fact]
    public async Task InitializeAsync_ShouldSeedDocumentForProjectionRoot()
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
        document.Should().NotBeNull();
        document!.Id.Should().Be("runtime-1");
        document.ReadModelPayload.Should().BeNull();
        document.StateVersion.Should().Be(0);
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

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-non-committed",
                Payload = Any.Pack(new RunScriptRequestedEvent
                {
                    RunId = "run-1",
                    DefinitionActorId = "definition-1",
                    ScriptRevision = "rev-1",
                    RequestedEventType = "integration.requested",
                    InputPayload = Any.Pack(new SimpleTextCommand
                    {
                        CommandId = "command-1",
                        Value = "hello",
                    }),
                }),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            },
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-2", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ReadModelPayload.Should().BeNull();
        document.StateVersion.Should().Be(0);
    }

    private static ScriptReadModelProjector CreateProjector(
        InMemoryProjectionDocumentStore<ScriptReadModelDocument> dispatcher)
    {
        return new ScriptReadModelProjector(
            dispatcher,
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero)),
            new StaticDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());
    }

    private sealed class StaticDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            definitionActorId.Should().Be("definition-1");
            requestedRevision.Should().Be("rev-1");
            return Task.FromResult(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: ScriptSources.UppercaseBehavior,
                SourceHash: ScriptSources.UppercaseBehaviorHash,
                ScriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
                StateTypeUrl: ScriptSources.UppercaseStateTypeUrl,
                ReadModelTypeUrl: ScriptSources.UppercaseReadModelTypeUrl,
                ReadModelSchemaVersion: "v1",
                ReadModelSchemaHash: "schema-hash",
                ProtocolDescriptorSet: ByteString.Empty,
                StateDescriptorFullName: SimpleTextState.Descriptor.FullName,
                ReadModelDescriptorFullName: SimpleTextReadModel.Descriptor.FullName));
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
