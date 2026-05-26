using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Runtime;
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
            StubScriptProjectionPayloadMaterializer.WithNativeDocument(BuildNativeDocumentProjection(BuildProfileReadModel())),
            new ScriptNativeDocumentMaterializer());
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-1",
            ProjectionKind = "script-execution-read-model",
        };
        var readModel = BuildProfileReadModel();

        await projector.ProjectAsync(
            context,
            BuildEnvelope(
                new ScriptDomainFactCommitted
                {
                    ActorId = "runtime-1",
                    DefinitionActorId = "definition-1",
                    ScriptId = "script-1",
                    Revision = "rev-1",
                    RunId = "run-1",
                    EventType = Any.Pack(new ScriptProfileUpdated()).TypeUrl,
                    DomainEventPayload = Any.Pack(new ScriptProfileUpdated { Current = readModel.Clone() }),
                    ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
                    StateVersion = 7,
                    OccurredAtUnixTimeMs = DateTimeOffset.Parse("2026-03-14T00:00:00Z").ToUnixTimeMilliseconds(),
                },
                ScriptCommittedEnvelopeFactory.CreateState(
                    "definition-1",
                    "script-1",
                    "rev-1",
                    new ScriptProfileState
                    {
                        CommandCount = 1,
                        LastCommandId = readModel.LastCommandId,
                        NormalizedText = readModel.NormalizedText,
                    },
                    7,
                    Any.Pack(readModel).TypeUrl,
                    ScriptSources.StructuredProfileBehavior,
                    ScriptSources.StructuredProfileBehaviorHash,
                    ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior),
                    "3",
                    "structured-schema")),
            CancellationToken.None);

        dispatcher.LastUpsert.Should().NotBeNull();
        var nativeDocument = dispatcher.LastUpsert!;
        nativeDocument.Id.Should().Be("runtime-1");
        nativeDocument.SchemaId.Should().Be("script_profile");
        nativeDocument.DocumentIndexScope.Should().StartWith("script-native-script-profile-");
        nativeDocument.StateVersion.Should().Be(7);
        nativeDocument.LastEventId.Should().Be("evt-1");
        nativeDocument.Fields["actor_id"].Should().Be("actor-1");
        nativeDocument.Fields["policy_id"].Should().Be("policy-1");
        nativeDocument.Fields["tags"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        nativeDocument.Fields["tags"].As<IReadOnlyList<object?>>().Should().BeEquivalentTo(["gold", "vip"]);
        nativeDocument.Fields["search"].Should().BeAssignableTo<IDictionary<string, object?>>();
        nativeDocument.Fields["search"].As<IDictionary<string, object?>>()["lookup_key"].Should().Be("actor-1:policy-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldDeriveNativeDocumentFromCommittedStateRoot_WithRealMaterializer()
    {
        var dispatcher = new RecordingNativeDocumentDispatcher();
        var projector = new ScriptNativeDocumentProjector(
            dispatcher,
            CreateRealMaterializer(),
            new ScriptNativeDocumentMaterializer());
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-derived-document",
            ProjectionKind = "script-execution-read-model",
        };
        var readModel = BuildProfileReadModel();
        var fact = new ScriptDomainFactCommitted
        {
            ActorId = "runtime-derived-document",
            DefinitionActorId = "definition-derived-document",
            ScriptId = "script-1",
            Revision = "rev-1",
            RunId = "run-derived-document",
            EventType = Any.Pack(new ScriptProfileUpdated()).TypeUrl,
            DomainEventPayload = Any.Pack(new ScriptProfileUpdated
            {
                CommandId = "command-derived-document",
                Current = BuildIgnoredProfileReadModel(),
            }),
            ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
            StateVersion = 11,
            OccurredAtUnixTimeMs = DateTimeOffset.Parse("2026-03-14T00:00:00Z").ToUnixTimeMilliseconds(),
        };
        var state = ScriptCommittedEnvelopeFactory.CreateState(
            "definition-derived-document",
            "script-1",
            "rev-1",
            new ScriptProfileState
            {
                ActorId = readModel.ActorId,
                PolicyId = readModel.PolicyId,
                LastCommandId = readModel.LastCommandId,
                InputText = readModel.InputText,
                NormalizedText = readModel.NormalizedText,
                Tags = { readModel.Tags },
            },
            fact.StateVersion,
            Any.Pack(readModel).TypeUrl,
            ScriptSources.StructuredProfileBehavior,
            ScriptSources.StructuredProfileBehaviorHash,
            ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior),
            "3",
            "structured-schema");

        await projector.ProjectAsync(
            context,
            BuildEnvelope(fact, state),
            CancellationToken.None);

        var expected = BuildNativeDocumentProjection(readModel);
        dispatcher.LastUpsert.Should().NotBeNull();
        var nativeDocument = dispatcher.LastUpsert!;
        nativeDocument.Id.Should().Be("runtime-derived-document");
        nativeDocument.ScriptId.Should().Be("script-1");
        nativeDocument.DefinitionActorId.Should().Be("definition-derived-document");
        nativeDocument.Revision.Should().Be("rev-1");
        nativeDocument.SchemaId.Should().Be(expected.SchemaId);
        nativeDocument.SchemaVersion.Should().Be(expected.SchemaVersion);
        nativeDocument.SchemaHash.Should().Be(expected.SchemaHash);
        nativeDocument.DocumentIndexScope.Should().Be(expected.DocumentIndexScope);
        nativeDocument.StateVersion.Should().Be(11);
        nativeDocument.LastEventId.Should().Be("evt-1");
        nativeDocument.FieldsValue.Should().BeEquivalentTo(expected.FieldsValue);
    }

    [Fact]
    public async Task ProjectAsync_ShouldFallbackToLegacyNativeDocumentField16_WhenStateRootCannotDerive()
    {
        var dispatcher = new RecordingNativeDocumentDispatcher();
        var projector = new ScriptNativeDocumentProjector(
            dispatcher,
            CreateRealMaterializer(),
            new ScriptNativeDocumentMaterializer());
        var context = new ScriptExecutionMaterializationContext
        {
            RootActorId = "runtime-legacy-document",
            ProjectionKind = "script-execution-read-model",
        };
        var readModel = BuildProfileReadModel();
        var legacyDocument = BuildNativeDocumentProjection(readModel);
        var fact = ScriptLegacyFactPayloadTestHelper.WithLegacyPayloads(
            new ScriptDomainFactCommitted
            {
                ActorId = "runtime-legacy-document",
                DefinitionActorId = "definition-legacy-document",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-legacy-document",
                EventType = Any.Pack(new ScriptProfileUpdated()).TypeUrl,
                DomainEventPayload = Any.Pack(new ScriptProfileUpdated
                {
                    CommandId = "command-legacy-document",
                    Current = readModel.Clone(),
                }),
                ReadModelTypeUrl = Any.Pack(readModel).TypeUrl,
                StateVersion = 12,
                OccurredAtUnixTimeMs = DateTimeOffset.Parse("2026-03-14T00:00:00Z").ToUnixTimeMilliseconds(),
            },
            nativeDocument: legacyDocument);
        var state = ScriptCommittedEnvelopeFactory.CreateState(
            "definition-legacy-document",
            "script-1",
            "rev-1",
            new ScriptProfileState
            {
                ActorId = readModel.ActorId,
            },
            fact.StateVersion,
            Any.Pack(readModel).TypeUrl);

        fact.TryGetLegacyNativeDocument().Should().NotBeNull();

        await projector.ProjectAsync(
            context,
            BuildEnvelope(fact, state),
            CancellationToken.None);

        dispatcher.LastUpsert.Should().NotBeNull();
        var nativeDocument = dispatcher.LastUpsert!;
        nativeDocument.Id.Should().Be("runtime-legacy-document");
        nativeDocument.StateVersion.Should().Be(12);
        nativeDocument.SchemaId.Should().Be(legacyDocument.SchemaId);
        nativeDocument.DocumentIndexScope.Should().Be(legacyDocument.DocumentIndexScope);
        nativeDocument.FieldsValue.Should().BeEquivalentTo(legacyDocument.FieldsValue);
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

    private static ScriptProfileReadModel BuildIgnoredProfileReadModel()
    {
        var readModel = new ScriptProfileReadModel
        {
            HasValue = true,
            ActorId = "ignored-actor",
            PolicyId = "ignored-policy",
            LastCommandId = "IGNORED-BY-PROJECTOR",
            InputText = "ignored-by-projector",
            NormalizedText = "IGNORED-BY-PROJECTOR",
            Search = new ScriptProfileSearchIndex
            {
                LookupKey = "ignored-actor:ignored-policy",
                SortKey = "IGNORED-BY-PROJECTOR",
            },
            Refs = new ScriptProfileDocumentRef
            {
                ActorId = "ignored-actor",
                PolicyId = "ignored-policy",
            },
        };
        readModel.Tags.Add("ignored-by-projector");
        return readModel;
    }

    private static ScriptNativeDocumentProjection BuildNativeDocumentProjection(ScriptProfileReadModel readModel)
    {
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));
        var artifact = artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            "script-1",
            "rev-1",
            ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior),
            ScriptSources.StructuredProfileBehaviorHash));
        var plan = new ScriptReadModelMaterializationCompiler().Compile(
            artifact,
            "structured-schema",
            "3");
        return new ScriptNativeProjectionBuilder()
            .BuildDocument(readModel, plan)!;
    }

    private static IScriptProjectionPayloadMaterializer CreateRealMaterializer() =>
        new ScriptProjectionPayloadMaterializer(
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ScriptReadModelMaterializationCompiler(),
            new ScriptNativeProjectionBuilder(),
            new ProtobufMessageCodec());

    private static EventEnvelope BuildEnvelope(ScriptDomainFactCommitted fact, ScriptBehaviorState state) =>
        ScriptCommittedEnvelopeFactory.CreateCommittedEnvelope(
            fact,
            state,
            "evt-1",
            DateTimeOffset.Parse("2026-03-14T00:00:00Z"));

    private sealed class RecordingNativeDocumentDispatcher : IProjectionWriteDispatcher<ScriptNativeDocumentReadModel>
    {
        public ScriptNativeDocumentReadModel? LastUpsert { get; private set; }

        public string? LastDeletedId { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(ScriptNativeDocumentReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastUpsert = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastDeletedId = id;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }
}
