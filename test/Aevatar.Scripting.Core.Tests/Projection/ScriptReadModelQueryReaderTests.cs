using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Artifacts;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using Aevatar.Scripting.Projection.Queries;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelQueryReaderTests
{
    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldRunBehaviorAgainstPersistedSnapshot()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-1",
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel
            {
                HasValue = true,
                ActorId = "runtime-1",
                PolicyId = "policy-1",
                LastCommandId = "command-1",
                InputText = " hello ",
                NormalizedText = "HELLO",
                Search = new ScriptProfileSearchIndex
                {
                    LookupKey = "runtime-1:policy-1",
                    SortKey = "HELLO",
                },
                Refs = new ScriptProfileDocumentRef
                {
                    ActorId = "runtime-1",
                    PolicyId = "policy-1",
                },
                Tags = { "hot", "vip" },
            }),
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-1",
                revision: "rev-1",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());

        var snapshot = await reader.GetSnapshotAsync("runtime-1", CancellationToken.None);
        var listed = await reader.ListSnapshotsAsync(10, CancellationToken.None);
        var result = await reader.ExecuteDeclaredQueryAsync(
            "runtime-1",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-1" }),
            CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.ScriptId.Should().Be("script-1");
        listed.Should().ContainSingle(x => x.ActorId == "runtime-1");
        result.Should().NotBeNull();
        result!.Unpack<ScriptProfileQueryResponded>().Current.NormalizedText.Should().Be("HELLO");
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldReject_WhenQueryPayloadTypeIsNotDeclared()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-2",
            ScriptId = "script-2",
            DefinitionActorId = "definition-2",
            Revision = "rev-2",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel { HasValue = true, NormalizedText = "HELLO" }),
            StateVersion = 1,
            LastEventId = "evt-2",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var source = CreateDeclaredQueryBehaviorSource(
            resultTypeName: nameof(ScriptProfileQueryResponded),
            resultExpression: """
                             new ScriptProfileQueryResponded
                             {
                                 RequestId = queryPayload.RequestId ?? string.Empty,
                                 Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
                             }
                             """);
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-2",
                revision: "rev-2",
                sourceText: source,
                sourceHash: ComputeSourceHash(source)),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());

        var act = () => reader.ExecuteDeclaredQueryAsync(
            "runtime-2",
            Any.Pack(new Struct()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected query payload type*google.protobuf.Struct*");
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldReject_WhenBehaviorSourceViolatesTypedQueryResultAtCompileTime()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-3",
            ScriptId = "script-3",
            DefinitionActorId = "definition-3",
            Revision = "rev-3",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel { HasValue = true, NormalizedText = "HELLO" }),
            StateVersion = 1,
            LastEventId = "evt-3",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var source = CreateDeclaredQueryBehaviorSource(
            resultTypeName: nameof(ScriptProfileQueryResponded),
            resultExpression: "new Struct()");
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-3",
                revision: "rev-3",
                sourceText: source,
                sourceHash: ComputeSourceHash(source)),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());

        var act = () => reader.ExecuteDeclaredQueryAsync(
            "runtime-3",
            Any.Pack(new Empty()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Script behavior compilation failed*");
    }

    private sealed class StaticDefinitionSnapshotPort(
        string definitionActorId,
        string revision,
        string sourceText,
        string sourceHash) : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string requestedDefinitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            requestedDefinitionActorId.Should().Be(definitionActorId);
            requestedRevision.Should().Be(revision);
            return Task.FromResult(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: revision,
                SourceText: sourceText,
                SourceHash: sourceHash,
                ScriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(sourceText),
                StateTypeUrl: ScriptSources.StructuredProfileStateTypeUrl,
                ReadModelTypeUrl: ScriptSources.StructuredProfileReadModelTypeUrl,
                ReadModelSchemaVersion: "v1",
                ReadModelSchemaHash: "schema-hash",
                ProtocolDescriptorSet: ByteString.Empty,
                StateDescriptorFullName: ScriptProfileState.Descriptor.FullName,
                ReadModelDescriptorFullName: ScriptProfileReadModel.Descriptor.FullName));
        }
    }

    private sealed class InMemoryReadModelStore : IProjectionDocumentStore<ScriptReadModelDocument, string>
    {
        private readonly Dictionary<string, ScriptReadModelDocument> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptReadModelDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[readModel.Id] = readModel;
            return Task.CompletedTask;
        }

        public Task MutateAsync(string key, Action<ScriptReadModelDocument> mutate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_items.TryGetValue(key, out var readModel))
            {
                readModel = new ScriptReadModelDocument { Id = key };
                _items[key] = readModel;
            }

            mutate(readModel);
            return Task.CompletedTask;
        }

        public Task<ScriptReadModelDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items.TryGetValue(key, out var readModel);
            return Task.FromResult(readModel);
        }

        public Task<IReadOnlyList<ScriptReadModelDocument>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptReadModelDocument>>(_items.Values.Take(take).ToArray());
        }
    }

    private static string CreateDeclaredQueryBehaviorSource(
        string resultTypeName,
        string resultExpression)
    {
        return $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;
        using Google.Protobuf.WellKnownTypes;

        public sealed class DeclaredQueryBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
            {
                builder
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (state, evt, _) => state,
                        reduce: static (readModel, evt, _) => readModel ?? evt.Current)
                    .OnQuery<ScriptProfileQueryRequested, {{resultTypeName}}>(HandleQueryAsync);
            }

            private static Task<{{resultTypeName}}?> HandleQueryAsync(
                ScriptProfileQueryRequested queryPayload,
                ScriptQueryContext<ScriptProfileReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<{{resultTypeName}}?>({{resultExpression}});
            }
        }
        """;
    }

    private static string ComputeSourceHash(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
