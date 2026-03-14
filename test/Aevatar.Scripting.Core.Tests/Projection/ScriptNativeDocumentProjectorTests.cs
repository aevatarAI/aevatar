using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptNativeDocumentProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeStructuredFieldsIntoNativeDocumentReadModel()
    {
        var dispatcher = new RecordingNativeDocumentDispatcher();
        var projector = new ScriptNativeDocumentProjector(
            dispatcher,
            new StaticStructuredDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ScriptReadModelMaterializationCompiler(),
            new ScriptNativeDocumentMaterializer(),
            new ProtobufMessageCodec());
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-1:native-document",
            RootActorId = "runtime-1",
            CurrentSemanticReadModelDocument = new ScriptReadModelDocument
            {
                Id = "runtime-1",
                ScriptId = "script-1",
                DefinitionActorId = "definition-1",
                Revision = "rev-1",
                ReadModelTypeUrl = Any.Pack(new ScriptProfileReadModel()).TypeUrl,
                ReadModelPayload = Any.Pack(BuildProfileReadModel()),
                StateVersion = 7,
            },
        };

        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-1",
                DefinitionActorId = "definition-1",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = Any.Pack(new ScriptProfileUpdated()).TypeUrl,
                DomainEventPayload = Any.Pack(new ScriptProfileUpdated { Current = BuildProfileReadModel() }),
                StateVersion = 7,
                OccurredAtUnixTimeMs = DateTimeOffset.Parse("2026-03-14T00:00:00Z").ToUnixTimeMilliseconds(),
            }),
            CancellationToken.None);

        dispatcher.LastUpsert.Should().NotBeNull();
        var nativeDocument = dispatcher.LastUpsert!;
        nativeDocument.Id.Should().Be("runtime-1");
        nativeDocument.SchemaId.Should().Be("script_profile");
        nativeDocument.DocumentIndexScope.Should().StartWith("script-native-script-profile-");
        nativeDocument.Fields["actor_id"].Should().Be("actor-1");
        nativeDocument.Fields["policy_id"].Should().Be("policy-1");
        nativeDocument.Fields["tags"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        nativeDocument.Fields["tags"].As<IReadOnlyList<object?>>().Should().BeEquivalentTo(["gold", "vip"]);
        nativeDocument.Fields["search"].Should().BeAssignableTo<IDictionary<string, object?>>();
        nativeDocument.Fields["search"].As<IDictionary<string, object?>>()["lookup_key"].Should().Be("actor-1:policy-1");
    }

    private static ScriptProfileReadModel BuildProfileReadModel()
    {
        var readModel = new ScriptProfileReadModel
        {
            HasValue = true,
            ActorId = "actor-1",
            PolicyId = "policy-1",
            LastCommandId = "command-1",
            NormalizedText = "HELLO",
            Search = new ScriptProfileSearchIndex
            {
                LookupKey = "actor-1:policy-1",
                SortKey = "HELLO",
            },
            Refs = new ScriptProfileDocumentRef
            {
                ActorId = "actor-1",
                PolicyId = "policy-1",
            },
        };
        readModel.Tags.Add("gold");
        readModel.Tags.Add("vip");
        return readModel;
    }

    private static EventEnvelope BuildEnvelope(ScriptDomainFactCommitted fact)
    {
        return new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(fact),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
        };
    }

    private sealed class StaticStructuredDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
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
                SourceText: ScriptSources.StructuredProfileBehavior,
                SourceHash: ScriptSources.StructuredProfileBehaviorHash,
                ScriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior),
                StateTypeUrl: Any.Pack(new ScriptProfileState()).TypeUrl,
                ReadModelTypeUrl: Any.Pack(new ScriptProfileReadModel()).TypeUrl,
                ReadModelSchemaVersion: "3",
                ReadModelSchemaHash: "structured-schema",
                ProtocolDescriptorSet: ByteString.Empty,
                StateDescriptorFullName: ScriptProfileState.Descriptor.FullName,
                ReadModelDescriptorFullName: ScriptProfileReadModel.Descriptor.FullName));
        }
    }

    private sealed class RecordingNativeDocumentDispatcher : IProjectionWriteDispatcher<ScriptNativeDocumentReadModel>
    {
        public ScriptNativeDocumentReadModel? LastUpsert { get; private set; }

        public Task UpsertAsync(ScriptNativeDocumentReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastUpsert = readModel.DeepClone();
            return Task.CompletedTask;
        }
    }
}
