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
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelProjectorNeutralityTests
{
    [Fact]
    public async Task ProjectAsync_ShouldNotMutateCommittedFactPayload()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow),
            new StaticDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());
        var payload = Any.Pack(new SimpleTextEvent
        {
            CommandId = "command-1",
            Current = new SimpleTextReadModel
            {
                HasValue = true,
                Value = "HELLO",
            },
        });
        var fact = new ScriptDomainFactCommitted
        {
            ActorId = "runtime-1",
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            RunId = "run-1",
            EventType = payload.TypeUrl,
            DomainEventPayload = payload.Clone(),
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            StateVersion = 1,
        };
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-1:read-model",
            RootActorId = "runtime-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "evt-1",
                Payload = Any.Pack(fact),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
            },
            CancellationToken.None);

        fact.DomainEventPayload.Should().NotBeNull();
        fact.DomainEventPayload.Unpack<SimpleTextEvent>().Current.Value.Should().Be("HELLO");

        var document = await dispatcher.GetAsync("runtime-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ReadModelPayload.Should().NotBeNull();
        document.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("HELLO");
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
