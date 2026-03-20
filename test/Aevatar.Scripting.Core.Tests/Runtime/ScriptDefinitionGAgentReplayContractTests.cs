using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptDefinitionGAgentReplayContractTests
{
    [Fact]
    public async Task HandleUpsertRequested_ShouldPersistDefinitionEvent_AndMutateViaTransitionOnly()
    {
        var agent = CreateAgent();

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            SourceText = DefinitionBehaviorSource,
            SourceHash = "hash-1",
        });

        agent.State.ScriptId.Should().Be("script-1");
        agent.State.Revision.Should().Be("rev-1");
        agent.State.SourceText.Should().Contain("DefinitionReplayBehavior");
        agent.State.SourceHash.Should().Be("hash-1");
        agent.State.StateTypeUrl.Should().Be(Any.Pack(new ScriptProfileState()).TypeUrl);
        agent.State.ReadModelTypeUrl.Should().Be(Any.Pack(new ScriptProfileReadModel()).TypeUrl);
        agent.State.CommandTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl);
        agent.State.DomainEventTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileUpdated()).TypeUrl);
        agent.State.InternalSignalTypeUrls.Should().ContainSingle(Any.Pack(new SimpleTextSignal()).TypeUrl);
        agent.State.RuntimeSemantics.Should().NotBeNull();
        agent.State.RuntimeSemantics.Messages.Should().Contain(x =>
            x.TypeUrl == Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl &&
            x.Kind == ScriptMessageKind.Command);
        agent.State.RuntimeSemantics.Messages.Should().Contain(x =>
            x.TypeUrl == Any.Pack(new ScriptProfileUpdated()).TypeUrl &&
            x.Kind == ScriptMessageKind.DomainEvent &&
            x.Projectable);
        agent.State.ReadModelSchemaVersion.Should().Be("3");
        agent.State.ReadModelSchema.Should().NotBeNull();
        agent.State.ReadModelSchemaHash.Should().NotBeNullOrWhiteSpace();
        agent.State.ReadModelSchemaStoreKinds.Should().Contain("document");
        agent.State.ReadModelSchemaStoreKinds.Should().Contain("graph");
        agent.State.ReadModelSchemaStatus.Should().Be("validated");
        agent.State.ReadModelSchemaFailureReason.Should().BeEmpty();
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleUpsertRequested_ShouldMarkSchemaActivationFailed_WhenRequiredStoreKindMissing()
    {
        var agent = CreateAgent(new DefaultScriptReadModelSchemaActivationPolicy([ScriptReadModelStoreKind.Document]));

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-unsupported",
            ScriptRevision = "rev-unsupported-1",
            SourceText = DefinitionBehaviorSource,
            SourceHash = "hash-unsupported-1",
        });

        agent.State.ScriptId.Should().Be("script-unsupported");
        agent.State.Revision.Should().Be("rev-unsupported-1");
        agent.State.ReadModelSchemaVersion.Should().Be("3");
        agent.State.ReadModelSchemaStatus.Should().Be("activation_failed");
        agent.State.ReadModelSchemaFailureReason.Should().Contain("Graph");
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleUpsertRequested_ShouldDisposeCompiledArtifact_WhenCompilerReturnsAsyncDisposableArtifact()
    {
        var trackingCompiler = new DisposableTrackingCompiler();
        var agent = new ScriptDefinitionGAgent(
            trackingCompiler,
            new DefaultScriptReadModelSchemaActivationPolicy())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
                new InMemoryEventStore()),
        };

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-dispose",
            ScriptRevision = "rev-1",
            SourceText = DefinitionBehaviorSource,
            SourceHash = "hash-dispose",
        });

        trackingCompiler.DisposeCalled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleUpsertRequested_ShouldComputePackageHash_WhenSourceHashIsMissing()
    {
        var agent = CreateAgent();
        var package = ScriptPackageSpecExtensions.CreateSingleSource(DefinitionBehaviorSource);
        var expectedHash = ScriptPackageModel.ComputePackageHash(package);

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-computed-hash",
            ScriptRevision = "rev-hash",
            SourceText = string.Empty,
            ScriptPackage = package,
            SourceHash = string.Empty,
        });

        agent.State.ScriptId.Should().Be("script-computed-hash");
        agent.State.Revision.Should().Be("rev-hash");
        agent.State.SourceHash.Should().Be(expectedHash);
        agent.State.SourceText.Should().Contain("DefinitionReplayBehavior");
    }

    [Fact]
    public async Task HandleUpsertRequested_ShouldThrow_WhenCompilationFails_AndKeepStateEmpty()
    {
        var agent = CreateAgent();

        var act = () => agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-invalid",
            ScriptRevision = "rev-invalid",
            SourceText = "public sealed class BrokenBehavior :",
            SourceHash = "hash-invalid",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Script definition compilation failed*");
        agent.State.ScriptId.Should().BeEmpty();
        agent.State.Revision.Should().BeEmpty();
        agent.State.LastAppliedEventVersion.Should().Be(0);
    }

    private static ScriptDefinitionGAgent CreateAgent(
        IScriptReadModelSchemaActivationPolicy? activationPolicy = null)
    {
        return new ScriptDefinitionGAgent(
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()),
            activationPolicy ?? new DefaultScriptReadModelSchemaActivationPolicy([ScriptReadModelStoreKind.Document, ScriptReadModelStoreKind.Graph]))
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
                new InMemoryEventStore()),
        };
    }

    private sealed class DisposableTrackingCompiler : IScriptBehaviorCompiler
    {
        public bool DisposeCalled { get; private set; }

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            _ = request;
            var behavior = new PassiveBehavior();
            return new ScriptBehaviorCompilationResult(
                true,
                new ScriptBehaviorArtifact(
                    "script-dispose",
                    "rev-1",
                    "hash-dispose",
                    behavior.Descriptor,
                    behavior.Descriptor.ToContract(),
                    () => new PassiveBehavior(),
                    () =>
                    {
                        DisposeCalled = true;
                        return ValueTask.CompletedTask;
                    }),
                Array.Empty<string>());
        }
    }

    private sealed class PassiveBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
        {
            _ = builder;
        }

        private static Task<ScriptProfileQueryResponded?> HandleQueryAsync(
            ScriptProfileQueryRequested query,
            ScriptQueryContext<ScriptProfileReadModel> snapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptProfileQueryResponded?>(new ScriptProfileQueryResponded
            {
                RequestId = query.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
            });
        }
    }

    private const string DefinitionBehaviorSource =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class DefinitionReplayBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
            {
                builder
                    .OnCommand<ScriptProfileUpdateCommand>(HandleAsync)
                    .OnSignal<SimpleTextSignal>(HandleSignalAsync)
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (state, evt, _) => new ScriptProfileState
                        {
                            CommandCount = (state?.CommandCount ?? 0) + 1,
                            ActorId = evt.Current?.ActorId ?? string.Empty,
                            PolicyId = evt.Current?.PolicyId ?? string.Empty,
                            LastCommandId = evt.CommandId ?? string.Empty,
                            InputText = evt.Current?.InputText ?? string.Empty,
                            NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                            Tags = { evt.Current == null ? global::System.Array.Empty<string>() : (global::System.Collections.Generic.IEnumerable<string>)evt.Current.Tags },
                        })
                    .ProjectState(static (state, _) => state == null
                        ? new ScriptProfileReadModel()
                        : new ScriptProfileReadModel
                        {
                            HasValue = true,
                            ActorId = state.ActorId,
                            PolicyId = state.PolicyId,
                            LastCommandId = state.LastCommandId,
                            InputText = state.InputText,
                            NormalizedText = state.NormalizedText,
                            Search = new ScriptProfileSearchIndex
                            {
                                LookupKey = $"{state.ActorId}:{state.PolicyId}".ToLowerInvariant(),
                                SortKey = state.NormalizedText ?? string.Empty,
                            },
                            Refs = new ScriptProfileDocumentRef
                            {
                                ActorId = state.ActorId ?? string.Empty,
                                PolicyId = state.PolicyId ?? string.Empty,
                            },
                            Tags = { state.Tags },
                        });
            }

            private static Task HandleAsync(
                ScriptProfileUpdateCommand inbound,
                ScriptCommandContext<ScriptProfileState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var evt = new ScriptProfileUpdated
                {
                    CommandId = inbound.CommandId ?? string.Empty,
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = inbound.ActorId ?? string.Empty,
                        PolicyId = inbound.PolicyId ?? string.Empty,
                        LastCommandId = inbound.CommandId ?? string.Empty,
                        InputText = inbound.InputText ?? string.Empty,
                        NormalizedText = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = $"{inbound.ActorId}:{inbound.PolicyId}".ToLowerInvariant(),
                            SortKey = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = inbound.ActorId ?? string.Empty,
                            PolicyId = inbound.PolicyId ?? string.Empty,
                        },
                    },
                };
                evt.Current.Tags.AddRange(inbound.Tags.Select(static tag => tag.Trim().ToLowerInvariant()));
                context.Emit(evt);
                return Task.CompletedTask;
            }

            private static Task HandleSignalAsync(
                SimpleTextSignal signal,
                ScriptCommandContext<ScriptProfileState> context,
                CancellationToken ct)
            {
                _ = signal;
                ct.ThrowIfCancellationRequested();
                context.Emit(new ScriptProfileUpdated
                {
                    CommandId = context.CommandId,
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = context.ActorId,
                        PolicyId = "signal",
                        LastCommandId = context.CommandId,
                        InputText = context.MessageType,
                        NormalizedText = context.MessageType,
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = $"{context.ActorId}:signal".ToLowerInvariant(),
                            SortKey = context.MessageType,
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = context.ActorId,
                            PolicyId = "signal",
                        },
                    },
                });
                return Task.CompletedTask;
            }

            private static Task<ScriptProfileQueryResponded?> HandleQueryAsync(
                ScriptProfileQueryRequested query,
                ScriptQueryContext<ScriptProfileReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ScriptProfileQueryResponded?>(new ScriptProfileQueryResponded
                {
                    RequestId = query.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
                });
            }
        }
        """;
}
