using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Artifacts;
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
        agent.State.QueryTypeUrls.Should().ContainSingle(Any.Pack(new ScriptProfileQueryRequested()).TypeUrl);
        agent.State.InternalSignalTypeUrls.Should().ContainSingle("type.googleapis.com/google.protobuf.Empty");
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
    public async Task QuerySnapshot_ShouldReturnMismatch_WhenRequestedRevisionDiffers()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-query",
            ScriptRevision = "rev-1",
            SourceText = DefinitionBehaviorSource,
            SourceHash = "hash-1",
        });

        await agent.HandleQueryScriptDefinitionSnapshotRequested(new QueryScriptDefinitionSnapshotRequestedEvent
        {
            RequestId = "request-mismatch",
            ReplyStreamId = "reply-stream",
            RequestedRevision = "rev-2",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Should().BeOfType<ScriptDefinitionSnapshotRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-mismatch");
        response.Found.Should().BeFalse();
        response.FailureReason.Should().Contain("does not match active revision");
    }

    [Fact]
    public async Task QuerySnapshot_ShouldReturnCurrentDefinition_WhenRevisionMatches()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-query",
            ScriptRevision = "rev-1",
            SourceText = DefinitionBehaviorSource,
            SourceHash = "hash-1",
        });

        await agent.HandleQueryScriptDefinitionSnapshotRequested(new QueryScriptDefinitionSnapshotRequestedEvent
        {
            RequestId = "request-ok",
            ReplyStreamId = "reply-stream",
            RequestedRevision = "rev-1",
        });

        publisher.Sent.Should().ContainSingle();
        var response = publisher.Sent[0].Should().BeOfType<ScriptDefinitionSnapshotRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-ok");
        response.Found.Should().BeTrue();
        response.ScriptId.Should().Be("script-query");
        response.Revision.Should().Be("rev-1");
        response.SourceText.Should().Contain("DefinitionReplayBehavior");
        response.StateTypeUrl.Should().Be(Any.Pack(new ScriptProfileState()).TypeUrl);
        response.ReadModelTypeUrl.Should().Be(Any.Pack(new ScriptProfileReadModel()).TypeUrl);
        response.ReadModelSchemaVersion.Should().Be("3");
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
            builder.OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync);
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

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(evt);
            return Task.CompletedTask;
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
        using Aevatar.Scripting.Abstractions.Definitions;
        using Aevatar.Scripting.Core.Tests.Messages;
        using Google.Protobuf.WellKnownTypes;

        public sealed class DefinitionReplayBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
            {
                builder
                    .OnCommand<ScriptProfileUpdateCommand>(HandleAsync)
                    .OnSignal<Empty>(HandleSignalAsync)
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (state, evt, _) => new ScriptProfileState
                        {
                            CommandCount = (state?.CommandCount ?? 0) + 1,
                            LastCommandId = evt.CommandId ?? string.Empty,
                            NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                        },
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync)
                    .DescribeReadModel(
                        new ScriptReadModelDefinition(
                            "definition_case",
                            "3",
                            new[]
                            {
                                new ScriptReadModelFieldDefinition("actor_id", "keyword", "actor_id", false),
                                new ScriptReadModelFieldDefinition("policy_id", "keyword", "policy_id", false),
                                new ScriptReadModelFieldDefinition("normalized_text", "text", "normalized_text", false),
                                new ScriptReadModelFieldDefinition("search.lookup_key", "keyword", "search.lookup_key", false),
                            },
                            new[]
                            {
                                new ScriptReadModelIndexDefinition("idx_actor_policy", new[] { "actor_id", "policy_id" }, true, "document"),
                            },
                            new[]
                            {
                                new ScriptReadModelRelationDefinition("rel_policy", "refs.policy_id", "policy", "policy_id", "many_to_one", "graph"),
                            }),
                        new[] { "document", "graph" });
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
                Empty signal,
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
