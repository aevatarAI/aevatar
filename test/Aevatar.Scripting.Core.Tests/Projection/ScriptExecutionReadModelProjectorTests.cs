using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Infrastructure.Artifacts;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptExecutionReadModelProjectorTests
{
    [Fact]
    public async Task InitializeAsync_ShouldSeedDocumentForProjectionRoot()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
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
        var dispatcher = new InMemoryReadModelDispatcher();
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
                    InputPayload = Any.Pack(new StringValue { Value = "hello" }),
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

    private static ScriptReadModelProjector CreateProjector(InMemoryReadModelDispatcher dispatcher)
    {
        return new ScriptReadModelProjector(
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
                StateTypeUrl: Any.Pack(new StringValue()).TypeUrl,
                ReadModelTypeUrl: Any.Pack(new StringValue()).TypeUrl,
                ReadModelSchemaVersion: "v1",
                ReadModelSchemaHash: "schema-hash",
                ProtocolDescriptorSet: ByteString.Empty,
                StateDescriptorFullName: StringValue.Descriptor.FullName,
                ReadModelDescriptorFullName: StringValue.Descriptor.FullName));
        }
    }

    private sealed class InMemoryReadModelDispatcher : IProjectionStoreDispatcher<ScriptReadModelDocument, string>
    {
        private readonly Dictionary<string, ScriptReadModelDocument> _items = new(StringComparer.Ordinal);

        public Task UpsertAsync(ScriptReadModelDocument readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _items[readModel.Id] = readModel.DeepClone();
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
            return Task.FromResult(readModel?.DeepClone());
        }

        public Task<IReadOnlyList<ScriptReadModelDocument>> ListAsync(int take = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptReadModelDocument>>(_items.Values.Take(take).Select(static x => x.DeepClone()).ToArray());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
