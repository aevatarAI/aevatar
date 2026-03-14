using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
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
    [Theory]
    [InlineData("documentStore")]
    [InlineData("definitionSnapshotPort")]
    [InlineData("artifactResolver")]
    [InlineData("codec")]
    public void Ctor_ShouldRejectNullDependencies(string parameterName)
    {
        Action act = parameterName switch
        {
            "documentStore" => () => _ = new ScriptReadModelQueryReader(
                null!,
                new ThrowingDefinitionSnapshotPort(),
                new ThrowingArtifactResolver(),
                new ProtobufMessageCodec()),
            "definitionSnapshotPort" => () => _ = new ScriptReadModelQueryReader(
                new InMemoryReadModelStore(),
                null!,
                new ThrowingArtifactResolver(),
                new ProtobufMessageCodec()),
            "artifactResolver" => () => _ = new ScriptReadModelQueryReader(
                new InMemoryReadModelStore(),
                new ThrowingDefinitionSnapshotPort(),
                null!,
                new ProtobufMessageCodec()),
            "codec" => () => _ = new ScriptReadModelQueryReader(
                new InMemoryReadModelStore(),
                new ThrowingDefinitionSnapshotPort(),
                new ThrowingArtifactResolver(),
                null!),
            _ => throw new InvalidOperationException("Unexpected parameter."),
        };

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(parameterName);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnNull_WhenDocumentDoesNotExist()
    {
        var reader = new ScriptReadModelQueryReader(
            new InMemoryReadModelStore(),
            new ThrowingDefinitionSnapshotPort(),
            new ThrowingArtifactResolver(),
            new ProtobufMessageCodec());

        var snapshot = await reader.GetSnapshotAsync("missing-runtime", CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task ListSnapshotsAsync_ShouldClampRequestedTake_ToSupportedRange()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument { Id = "runtime-1" }, CancellationToken.None);
        await store.UpsertAsync(new ScriptReadModelDocument { Id = "runtime-2" }, CancellationToken.None);
        await store.UpsertAsync(new ScriptReadModelDocument { Id = "runtime-3" }, CancellationToken.None);
        var reader = new ScriptReadModelQueryReader(
            store,
            new ThrowingDefinitionSnapshotPort(),
            new ThrowingArtifactResolver(),
            new ProtobufMessageCodec());

        var minimum = await reader.ListSnapshotsAsync(0, CancellationToken.None);
        var maximum = await reader.ListSnapshotsAsync(5000, CancellationToken.None);

        minimum.Should().HaveCount(1);
        maximum.Should().HaveCount(3);
    }

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
    public async Task ExecuteDeclaredQueryAsync_ShouldThrow_WhenSnapshotIsMissing()
    {
        var reader = new ScriptReadModelQueryReader(
            new InMemoryReadModelStore(),
            new ThrowingDefinitionSnapshotPort(),
            new ThrowingArtifactResolver(),
            new ProtobufMessageCodec());

        var act = () => reader.ExecuteDeclaredQueryAsync(
            "missing-runtime",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-missing" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Script read model not found*missing-runtime*");
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
            Any.Pack(new SimpleTextSignal { Value = "wrong-query-type" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected query payload type*aevatar.scripting.tests.SimpleTextSignal*");
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldReject_WhenReadModelScopeDoesNotMatchSnapshot()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-scope-mismatch",
            ScriptId = "script-scope",
            DefinitionActorId = "definition-scope",
            Revision = "rev-scope",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel { HasValue = true, NormalizedText = "HELLO" }),
            StateVersion = 1,
            LastEventId = "evt-scope",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var behavior = new DescriptorOverrideBehavior(
            new QueryBehavior(),
            static descriptor =>
            {
                var semantics = descriptor.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
                semantics.Queries.Clear();
                semantics.Queries.Add(new ScriptQuerySemanticsSpec
                {
                    QueryTypeUrl = ScriptMessageTypes.GetTypeUrl<ScriptProfileQueryRequested>(),
                    QueryDescriptorFullName = ScriptProfileQueryRequested.Descriptor.FullName,
                    ResultTypeUrl = ScriptMessageTypes.GetTypeUrl<ScriptProfileQueryResponded>(),
                    ResultDescriptorFullName = ScriptProfileQueryResponded.Descriptor.FullName,
                    ReadModelScope = "other.scope.ReadModel",
                });
                return descriptor.WithRuntimeSemantics(semantics);
            });
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-scope",
                revision: "rev-scope",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());

        var act = () => reader.ExecuteDeclaredQueryAsync(
            "runtime-scope-mismatch",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-scope" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*read model scope `other.scope.ReadModel` does not match*");
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldFallbackToSourceTextPackage_WhenSnapshotPackageIsMissing()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-fallback-package",
            ScriptId = "script-fallback-package",
            DefinitionActorId = "definition-fallback-package",
            Revision = "rev-fallback-package",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel { HasValue = true, NormalizedText = "HELLO" }),
            StateVersion = 1,
            LastEventId = "evt-fallback-package",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var resolver = new CapturingArtifactResolver(new QueryBehavior());
        var reader = new ScriptReadModelQueryReader(
            store,
            new MissingPackageDefinitionSnapshotPort(
                "definition-fallback-package",
                "rev-fallback-package",
                ScriptSources.StructuredProfileBehavior,
                ScriptSources.StructuredProfileBehaviorHash),
            resolver,
            new ProtobufMessageCodec());

        var result = await reader.ExecuteDeclaredQueryAsync(
            "runtime-fallback-package",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-fallback-package" }),
            CancellationToken.None);

        result.Should().NotBeNull();
        resolver.LastRequest.Should().NotBeNull();
        resolver.LastRequest!.Package.CSharpSources.Should().ContainSingle(x => x.Content == ScriptSources.StructuredProfileBehavior);
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldReturnNull_WhenBehaviorReturnsNull()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-null-result",
            ScriptId = "script-null",
            DefinitionActorId = "definition-null",
            Revision = "rev-null",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel { HasValue = true, NormalizedText = "HELLO" }),
            StateVersion = 1,
            LastEventId = "evt-null",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-null",
                revision: "rev-null",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(new NullResultBehavior(new QueryBehavior())),
            new ProtobufMessageCodec());

        var result = await reader.ExecuteDeclaredQueryAsync(
            "runtime-null-result",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-null" }),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldAcceptScope_WhenItMatchesReadModelTypeUrl()
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = "runtime-type-url-scope",
            ScriptId = "script-type-url",
            DefinitionActorId = "definition-type-url",
            Revision = "rev-type-url",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel { HasValue = true, NormalizedText = "HELLO" }),
            StateVersion = 1,
            LastEventId = "evt-type-url",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        var behavior = new DescriptorOverrideBehavior(
            new QueryBehavior(),
            static descriptor =>
            {
                var semantics = descriptor.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
                semantics.Queries.Clear();
                semantics.Queries.Add(new ScriptQuerySemanticsSpec
                {
                    QueryTypeUrl = ScriptMessageTypes.GetTypeUrl<ScriptProfileQueryRequested>(),
                    QueryDescriptorFullName = ScriptProfileQueryRequested.Descriptor.FullName,
                    ResultTypeUrl = ScriptMessageTypes.GetTypeUrl<ScriptProfileQueryResponded>(),
                    ResultDescriptorFullName = ScriptProfileQueryResponded.Descriptor.FullName,
                    ReadModelScope = ScriptSources.StructuredProfileReadModelTypeUrl,
                });
                return descriptor.WithRuntimeSemantics(semantics);
            });
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-type-url",
                revision: "rev-type-url",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());

        var result = await reader.ExecuteDeclaredQueryAsync(
            "runtime-type-url-scope",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-type-url" }),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Unpack<ScriptProfileQueryResponded>().RequestId.Should().Be("request-type-url");
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
            resultExpression: "new SimpleTextEvent()");
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
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-3" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Script artifact resolution failed*");
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldDisposeAsyncDisposableBehavior()
    {
        var store = await CreateProfileReadModelStoreAsync("runtime-async-dispose");
        var behavior = new AsyncDisposableBehavior(new QueryBehavior());
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-1",
                revision: "rev-1",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());

        var result = await reader.ExecuteDeclaredQueryAsync(
            "runtime-async-dispose",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-async-dispose" }),
            CancellationToken.None);

        result.Should().NotBeNull();
        behavior.DisposeAsyncCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldDisposeDisposableBehavior()
    {
        var store = await CreateProfileReadModelStoreAsync("runtime-dispose");
        var behavior = new DisposableBehavior(new QueryBehavior());
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-1",
                revision: "rev-1",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());

        var result = await reader.ExecuteDeclaredQueryAsync(
            "runtime-dispose",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-dispose" }),
            CancellationToken.None);

        result.Should().NotBeNull();
        behavior.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldReject_WhenDeclaredResultTypeDoesNotMatchReturnedMessage()
    {
        var store = await CreateProfileReadModelStoreAsync("runtime-result-type-mismatch");
        var behavior = new DescriptorOverrideBehavior(
            new QueryBehavior(),
            descriptor =>
            {
                var registration = descriptor.Queries.Values.Single();
                var queries = new Dictionary<string, ScriptQueryRegistration>(descriptor.Queries, StringComparer.Ordinal)
                {
                    [registration.TypeUrl] = registration with { ResultClrType = typeof(SimpleTextEvent) },
                };
                return descriptor with { Queries = queries };
            });
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-1",
                revision: "rev-1",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());

        var act = () => reader.ExecuteDeclaredQueryAsync(
            "runtime-result-type-mismatch",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-result-type-mismatch" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*declared result type*");
    }

    [Fact]
    public async Task ExecuteDeclaredQueryAsync_ShouldReject_WhenRuntimeSemanticsDeclareDifferentResultType()
    {
        var store = await CreateProfileReadModelStoreAsync("runtime-semantics-result-mismatch");
        var behavior = new DescriptorOverrideBehavior(
            new QueryBehavior(),
            descriptor =>
            {
                var semantics = descriptor.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
                semantics.Queries.Clear();
                semantics.Queries.Add(new ScriptQuerySemanticsSpec
                {
                    QueryTypeUrl = ScriptMessageTypes.GetTypeUrl<ScriptProfileQueryRequested>(),
                    QueryDescriptorFullName = ScriptProfileQueryRequested.Descriptor.FullName,
                    ResultTypeUrl = ScriptMessageTypes.GetTypeUrl<SimpleTextEvent>(),
                    ResultDescriptorFullName = SimpleTextEvent.Descriptor.FullName,
                    ReadModelScope = descriptor.ReadModelDescriptor.FullName,
                });
                return descriptor.WithRuntimeSemantics(semantics);
            });
        var reader = new ScriptReadModelQueryReader(
            store,
            new StaticDefinitionSnapshotPort(
                definitionActorId: "definition-1",
                revision: "rev-1",
                sourceText: ScriptSources.StructuredProfileBehavior,
                sourceHash: ScriptSources.StructuredProfileBehaviorHash),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());

        var act = () => reader.ExecuteDeclaredQueryAsync(
            "runtime-semantics-result-mismatch",
            Any.Pack(new ScriptProfileQueryRequested { RequestId = "request-semantics-result-mismatch" }),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime semantics declare*");
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

    private sealed class MissingPackageDefinitionSnapshotPort(
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
                ScriptPackage: null!,
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

    private sealed class ThrowingDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Definition snapshot should not be requested in this test.");
        }
    }

    private sealed class ThrowingArtifactResolver : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            throw new InvalidOperationException($"Artifact resolution should not be reached in this test. request={request}");
        }
    }

    private sealed class StaticArtifactResolver(IScriptBehaviorBridge behavior) : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            _ = request;
            return new ScriptBehaviorArtifact(
                "script-1",
                "rev-1",
                "hash-1",
                behavior.Descriptor,
                behavior.Descriptor.ToContract(),
                () => behavior);
        }
    }

    private sealed class CapturingArtifactResolver(IScriptBehaviorBridge behavior) : IScriptBehaviorArtifactResolver
    {
        public ScriptBehaviorArtifactRequest? LastRequest { get; private set; }

        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            LastRequest = request;
            return new ScriptBehaviorArtifact(
                "script-1",
                "rev-1",
                "hash-1",
                behavior.Descriptor,
                behavior.Descriptor.ToContract(),
                () => behavior);
        }
    }

    private sealed class QueryBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
        {
            builder
                .OnEvent<ScriptProfileUpdated>(
                    apply: static (state, evt, _) => state,
                    reduce: static (readModel, evt, _) => readModel ?? evt.Current)
                .OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync);
        }

        private static Task<ScriptProfileQueryResponded?> HandleQueryAsync(
            ScriptProfileQueryRequested queryPayload,
            ScriptQueryContext<ScriptProfileReadModel> snapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptProfileQueryResponded?>(new ScriptProfileQueryResponded
            {
                RequestId = queryPayload.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
            });
        }
    }

    private sealed class NullResultBehavior(IScriptBehaviorBridge inner) : IScriptBehaviorBridge
    {
        public ScriptBehaviorDescriptor Descriptor => inner.Descriptor;

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct) =>
            inner.DispatchAsync(inbound, context, ct);

        public IMessage? ApplyDomainEvent(
            IMessage? currentState,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ApplyDomainEvent(currentState, domainEvent, context);

        public IMessage? ReduceReadModel(
            IMessage? currentReadModel,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ReduceReadModel(currentReadModel, domainEvent, context);

        public Task<IMessage?> ExecuteQueryAsync(
            IMessage query,
            ScriptTypedReadModelSnapshot snapshot,
            CancellationToken ct)
        {
            _ = query;
            _ = snapshot;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IMessage?>(null);
        }
    }

    private sealed class DescriptorOverrideBehavior(
        IScriptBehaviorBridge inner,
        Func<ScriptBehaviorDescriptor, ScriptBehaviorDescriptor> map) : IScriptBehaviorBridge
    {
        public ScriptBehaviorDescriptor Descriptor { get; } = map(inner.Descriptor);

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct) =>
            inner.DispatchAsync(inbound, context, ct);

        public IMessage? ApplyDomainEvent(
            IMessage? currentState,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ApplyDomainEvent(currentState, domainEvent, context);

        public IMessage? ReduceReadModel(
            IMessage? currentReadModel,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ReduceReadModel(currentReadModel, domainEvent, context);

        public Task<IMessage?> ExecuteQueryAsync(
            IMessage query,
            ScriptTypedReadModelSnapshot snapshot,
            CancellationToken ct) =>
            inner.ExecuteQueryAsync(query, snapshot, ct);
    }

    private sealed class AsyncDisposableBehavior(IScriptBehaviorBridge inner) : IScriptBehaviorBridge, IAsyncDisposable
    {
        public int DisposeAsyncCalls { get; private set; }
        public ScriptBehaviorDescriptor Descriptor => inner.Descriptor;

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct) =>
            inner.DispatchAsync(inbound, context, ct);

        public IMessage? ApplyDomainEvent(
            IMessage? currentState,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ApplyDomainEvent(currentState, domainEvent, context);

        public IMessage? ReduceReadModel(
            IMessage? currentReadModel,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ReduceReadModel(currentReadModel, domainEvent, context);

        public Task<IMessage?> ExecuteQueryAsync(
            IMessage query,
            ScriptTypedReadModelSnapshot snapshot,
            CancellationToken ct) =>
            inner.ExecuteQueryAsync(query, snapshot, ct);

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableBehavior(IScriptBehaviorBridge inner) : IScriptBehaviorBridge, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public ScriptBehaviorDescriptor Descriptor => inner.Descriptor;

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct) =>
            inner.DispatchAsync(inbound, context, ct);

        public IMessage? ApplyDomainEvent(
            IMessage? currentState,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ApplyDomainEvent(currentState, domainEvent, context);

        public IMessage? ReduceReadModel(
            IMessage? currentReadModel,
            IMessage domainEvent,
            ScriptFactContext context) =>
            inner.ReduceReadModel(currentReadModel, domainEvent, context);

        public Task<IMessage?> ExecuteQueryAsync(
            IMessage query,
            ScriptTypedReadModelSnapshot snapshot,
            CancellationToken ct) =>
            inner.ExecuteQueryAsync(query, snapshot, ct);

        public void Dispose()
        {
            DisposeCalls++;
        }
    }

    private static async Task<InMemoryReadModelStore> CreateProfileReadModelStoreAsync(string actorId)
    {
        var store = new InMemoryReadModelStore();
        await store.UpsertAsync(new ScriptReadModelDocument
        {
            Id = actorId,
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            ReadModelTypeUrl = ScriptSources.StructuredProfileReadModelTypeUrl,
            ReadModelPayload = Any.Pack(new ScriptProfileReadModel
            {
                HasValue = true,
                NormalizedText = "HELLO",
            }),
            StateVersion = 1,
            LastEventId = "evt-1",
            UpdatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        return store;
    }
}
