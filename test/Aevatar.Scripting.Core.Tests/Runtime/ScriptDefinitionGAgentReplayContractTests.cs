using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
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
        agent.State.InternalSignalTypeUrls.Should().ContainSingle(Any.Pack(new SimpleTextSignal()).TypeUrl);
        agent.State.RuntimeSemantics.Should().NotBeNull();
        agent.State.RuntimeSemantics.Messages.Should().Contain(x =>
            x.TypeUrl == Any.Pack(new ScriptProfileUpdateCommand()).TypeUrl &&
            x.Kind == ScriptMessageKind.Command);
        agent.State.RuntimeSemantics.Messages.Should().Contain(x =>
            x.TypeUrl == Any.Pack(new ScriptProfileUpdated()).TypeUrl &&
            x.Kind == ScriptMessageKind.DomainEvent &&
            x.Projectable);
        agent.State.RuntimeSemantics.Queries.Should().Contain(x =>
            x.QueryTypeUrl == Any.Pack(new ScriptProfileQueryRequested()).TypeUrl &&
            x.ResultTypeUrl == Any.Pack(new ScriptProfileQueryResponded()).TypeUrl);
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
            new ScriptReadModelMaterializationCompiler(),
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
    public async Task HandleUpsertRequested_ShouldRejectInvalidReadModelPaths_BeforePersistingDefinitionState()
    {
        var invalidPackage = CreateInvalidSchemaPackage();
        var agent = CreateAgent();

        var act = () => agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-invalid-schema",
            ScriptRevision = "rev-invalid",
            SourceText = invalidPackage.GetPrimaryCSharpSource(),
            ScriptPackage = invalidPackage,
            SourceHash = ScriptPackageModel.ComputePackageHash(invalidPackage),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*references path `search.lookup`*");
        agent.State.ScriptId.Should().BeEmpty();
        agent.State.Revision.Should().BeEmpty();
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
            new ScriptReadModelMaterializationCompiler(),
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

    private static ScriptPackageSpec CreateInvalidSchemaPackage()
    {
        const string behaviorSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Aevatar.Scripting.Abstractions;
            using Aevatar.Scripting.Abstractions.Behaviors;
            using Dynamic.InvalidSchema;

            public sealed class InvalidSchemaBehavior : ScriptBehavior<InvalidProfileState, InvalidProfileReadModel>
            {
                protected override void Configure(IScriptBehaviorBuilder<InvalidProfileState, InvalidProfileReadModel> builder)
                {
                    builder
                        .OnCommand<InvalidProfileCommand>(HandleAsync)
                        .OnEvent<InvalidProfileUpdated>(
                            apply: static (_, evt, _) => new InvalidProfileState { CommandCount = 1 },
                            reduce: static (_, evt, _) => evt.Current)
                        .OnQuery<InvalidProfileQueryRequested, InvalidProfileQueryResponded>(HandleQueryAsync);
                }

                private static Task HandleAsync(
                    InvalidProfileCommand inbound,
                    ScriptCommandContext<InvalidProfileState> context,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    context.Emit(new InvalidProfileUpdated
                    {
                        CommandId = inbound.CommandId ?? string.Empty,
                        Current = new InvalidProfileReadModel
                        {
                            ActorId = inbound.ActorId ?? string.Empty,
                            Search = new InvalidProfileSearch
                            {
                                LookupKey = inbound.ActorId ?? string.Empty,
                            },
                        },
                    });
                    return Task.CompletedTask;
                }

                private static Task<InvalidProfileQueryResponded?> HandleQueryAsync(
                    InvalidProfileQueryRequested query,
                    ScriptQueryContext<InvalidProfileReadModel> snapshot,
                    CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<InvalidProfileQueryResponded?>(new InvalidProfileQueryResponded
                    {
                        RequestId = query.RequestId ?? string.Empty,
                        Current = snapshot.CurrentReadModel ?? new InvalidProfileReadModel(),
                    });
                }
            }
            """;

        const string protoSource =
            """
            syntax = "proto3";

            package dynamic.invalidschema;

            option csharp_namespace = "Dynamic.InvalidSchema";

            import "scripting_schema_options.proto";
            import "scripting_runtime_options.proto";

            message InvalidProfileState {
              int32 command_count = 1;
            }

            message InvalidProfileSearch {
              string lookup_key = 1;
            }

            message InvalidProfileReadModel {
              option (aevatar.scripting.schema.scripting_read_model) = {
                schema_id: "invalid_profile"
                schema_version: "1"
                store_kinds: "document"
                document_indexes: {
                  name: "idx_bad_path"
                  paths: "search.lookup"
                  provider: "document"
                }
              };

              string actor_id = 1 [(aevatar.scripting.schema.scripting_field) = { storage_type: "keyword" }];
              InvalidProfileSearch search = 2;
            }

            message InvalidProfileCommand {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_COMMAND
                command_id_field: "command_id"
                aggregate_id_field: "actor_id"
              };
              string command_id = 1;
              string actor_id = 2;
            }

            message InvalidProfileUpdated {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_DOMAIN_EVENT
                projectable: true
                replay_safe: true
                command_id_field: "command_id"
                read_model_scope: "dynamic.invalidschema.InvalidProfileReadModel"
              };
              string command_id = 1;
              InvalidProfileReadModel current = 2;
            }

            message InvalidProfileQueryRequested {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_REQUEST
                read_model_scope: "dynamic.invalidschema.InvalidProfileReadModel"
              };
              option (aevatar.scripting.runtime.scripting_query) = {
                result_full_name: "dynamic.invalidschema.InvalidProfileQueryResponded"
              };
              string request_id = 1;
            }

            message InvalidProfileQueryResponded {
              option (aevatar.scripting.runtime.scripting_runtime) = {
                kind: SCRIPTING_MESSAGE_KIND_QUERY_RESULT
                read_model_scope: "dynamic.invalidschema.InvalidProfileReadModel"
              };
              string request_id = 1;
              InvalidProfileReadModel current = 2;
            }
            """;

        var package = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = "InvalidSchemaBehavior",
            EntrySourcePath = "Behavior.cs",
        };
        package.CsharpSources.Add(new ScriptPackageFile
        {
            Path = "Behavior.cs",
            Content = behaviorSource,
        });
        package.ProtoFiles.Add(new ScriptPackageFile
        {
            Path = "invalid_schema.proto",
            Content = protoSource,
        });
        return package;
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
                            LastCommandId = evt.CommandId ?? string.Empty,
                            NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                        },
                        reduce: static (_, evt, _) => evt.Current)
                    .OnQuery<ScriptProfileQueryRequested, ScriptProfileQueryResponded>(HandleQueryAsync);
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
