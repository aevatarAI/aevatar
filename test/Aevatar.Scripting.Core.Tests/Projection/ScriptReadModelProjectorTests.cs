using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
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

public sealed class ScriptReadModelProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldReduceCommittedFactIntoReadModelDocument()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            new LenientDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-1:read-model",
            RootActorId = "runtime-1",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-1",
                DefinitionActorId = "definition-1",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = ScriptSources.UppercaseEventTypeUrl,
                DomainEventPayload = Any.Pack(new SimpleTextEvent
                {
                    CommandId = "command-1",
                    Current = new SimpleTextReadModel
                    {
                        HasValue = true,
                        Value = "HELLO",
                    },
                }),
                ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                StateVersion = 1,
            }),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-1", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ScriptId.Should().Be("script-1");
        document.DefinitionActorId.Should().Be("definition-1");
        document.Revision.Should().Be("rev-1");
        document.StateVersion.Should().Be(1);
        document.ReadModelPayload.Should().NotBeNull();
        document.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnore_WhenDomainEventIsNotProjectable()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            new StaticDefinitionSnapshotPort(),
            new StaticArtifactResolver(
                new DescriptorOverrideBehavior(
                    new ProjectingBehavior(),
                    static descriptor =>
                    {
                        var semantics = descriptor.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
                        semantics.Messages.Clear();
                        semantics.Messages.Add(new ScriptMessageSemanticsSpec
                        {
                            TypeUrl = ScriptSources.UppercaseEventTypeUrl,
                            DescriptorFullName = SimpleTextEvent.Descriptor.FullName,
                            Kind = ScriptMessageKind.DomainEvent,
                            Projectable = false,
                            ReplaySafe = true,
                            CommandIdField = "command_id",
                            ReadModelScope = SimpleTextReadModel.Descriptor.FullName,
                        });
                        return descriptor.WithRuntimeSemantics(semantics);
                    })),
            new ProtobufMessageCodec());
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-non-projectable:read-model",
            RootActorId = "runtime-non-projectable",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-non-projectable",
                DefinitionActorId = "definition-1",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = ScriptSources.UppercaseEventTypeUrl,
                DomainEventPayload = Any.Pack(new SimpleTextEvent
                {
                    CommandId = "command-1",
                    Current = new SimpleTextReadModel
                    {
                        HasValue = true,
                        Value = "IGNORED",
                    },
                }),
                ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                StateVersion = 1,
            }),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-non-projectable", CancellationToken.None);
        document.Should().NotBeNull();
        document!.StateVersion.Should().Be(0);
        document.ReadModelPayload.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldReject_WhenCommittedFactEventTypeIsUndeclared()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            new LenientDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-undeclared:read-model",
            RootActorId = "runtime-undeclared",
        };

        await projector.InitializeAsync(context, CancellationToken.None);

        var act = () => projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-undeclared",
                DefinitionActorId = "definition-1",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = ScriptMessageTypes.GetTypeUrl<SimpleTextUnexpectedEvent>(),
                DomainEventPayload = Any.Pack(new SimpleTextUnexpectedEvent
                {
                    Value = "HELLO",
                }),
                ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                StateVersion = 1,
            }),
            CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected undeclared domain event type*");
    }

    [Fact]
    public async Task ProjectAsync_ShouldUseFallbackValues_WhenCommittedFactOmitsOptionalFields()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            new LenientDefinitionSnapshotPort(),
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())),
            new ProtobufMessageCodec());
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-fallbacks:read-model",
            RootActorId = "runtime-fallbacks",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-fallbacks",
                DefinitionActorId = "definition-1",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = ScriptSources.UppercaseEventTypeUrl,
                DomainEventPayload = Any.Pack(new SimpleTextEvent
                {
                    CommandId = "command-1",
                    Current = new SimpleTextReadModel
                    {
                        HasValue = true,
                        Value = "HELLO",
                    },
                }),
                ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                StateVersion = 1,
            }),
            CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-fallbacks",
                DefinitionActorId = string.Empty,
                ScriptId = string.Empty,
                Revision = string.Empty,
                RunId = "run-2",
                EventType = ScriptSources.UppercaseEventTypeUrl,
                DomainEventPayload = Any.Pack(new SimpleTextEvent
                {
                    CommandId = "command-2",
                    Current = new SimpleTextReadModel
                    {
                        HasValue = true,
                        Value = "WORLD",
                    },
                }),
                ReadModelTypeUrl = string.Empty,
                StateVersion = 2,
            }),
            CancellationToken.None);

        var document = await dispatcher.GetAsync("runtime-fallbacks", CancellationToken.None);
        document.Should().NotBeNull();
        document!.ScriptId.Should().Be("script-1");
        document.DefinitionActorId.Should().Be("definition-1");
        document.Revision.Should().Be("rev-1");
        document.ReadModelTypeUrl.Should().Be(ScriptSources.UppercaseReadModelTypeUrl);
        document.StateVersion.Should().Be(2);
        document.ReadModelPayload.Should().NotBeNull();
        document.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("WORLD");
    }

    [Fact]
    public async Task ProjectAsync_ShouldDisposeAsyncBehavior_WhenProjectionCompletes()
    {
        var dispatcher = new InMemoryReadModelDispatcher();
        var behavior = new AsyncDisposingBehavior();
        var projector = new ScriptReadModelProjector(
            dispatcher,
            new FixedProjectionClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            new StaticDefinitionSnapshotPort(),
            new StaticArtifactResolver(behavior),
            new ProtobufMessageCodec());
        var context = new ScriptExecutionProjectionContext
        {
            ProjectionId = "runtime-async-dispose:read-model",
            RootActorId = "runtime-async-dispose",
        };

        await projector.InitializeAsync(context, CancellationToken.None);
        await projector.ProjectAsync(
            context,
            BuildEnvelope(new ScriptDomainFactCommitted
            {
                ActorId = "runtime-async-dispose",
                DefinitionActorId = "definition-1",
                ScriptId = "script-1",
                Revision = "rev-1",
                RunId = "run-1",
                EventType = ScriptSources.UppercaseEventTypeUrl,
                DomainEventPayload = Any.Pack(new SimpleTextEvent
                {
                    CommandId = "command-1",
                    Current = new SimpleTextReadModel
                    {
                        HasValue = true,
                        Value = "HELLO",
                    },
                }),
                ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                StateVersion = 1,
            }),
            CancellationToken.None);

        behavior.DisposeAsyncCalled.Should().BeTrue();
    }

    private static EventEnvelope BuildEnvelope(ScriptDomainFactCommitted fact)
    {
        return new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(fact),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("projection-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-1",
            },
        };
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

    private sealed class LenientDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
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

    private sealed class FixedProjectionClock(DateTimeOffset now) : IProjectionClock
    {
        public DateTimeOffset UtcNow => now;
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

    private class ProjectingBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder.OnEvent<SimpleTextEvent>(
                apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                reduce: static (_, evt, _) => evt.Current);
        }
    }

    private sealed class AsyncDisposingBehavior : ProjectingBehavior, IAsyncDisposable
    {
        public bool DisposeAsyncCalled { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            return ValueTask.CompletedTask;
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
}
