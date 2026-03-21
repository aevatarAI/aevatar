using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Reflection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptBehaviorDispatcherTests
{
    private static readonly string SimpleTextStateTypeUrl = Any.Pack(new SimpleTextState()).TypeUrl;
    private static readonly string SimpleTextReadModelTypeUrl = Any.Pack(new SimpleTextReadModel()).TypeUrl;

    [Theory]
    [InlineData("artifactResolver")]
    [InlineData("codec")]
    public void Ctor_ShouldRejectNullDependencies(string parameterName)
    {
        Action act = parameterName switch
        {
            "artifactResolver" => () => _ = new ScriptBehaviorDispatcher(null!, new ScriptReadModelMaterializationCompiler(), new ScriptNativeProjectionBuilder(), new ProtobufMessageCodec()),
            "codec" => () => _ = new ScriptBehaviorDispatcher(
                new StaticArtifactResolver(CreateArtifact(new UppercaseBehavior())),
                new ScriptReadModelMaterializationCompiler(),
                new ScriptNativeProjectionBuilder(),
                null!),
            _ => throw new InvalidOperationException("Unexpected parameter name."),
        };

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(parameterName);
    }

    [Fact]
    public async Task DispatchAsync_ShouldEmitCommittedFactsWithResolvedContract()
    {
        var behavior = new UppercaseBehavior();
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(
                CreateArtifact(behavior)));
        var envelope = new EventEnvelope
        {
            Id = "command-1",
            Payload = Any.Pack(new RunScriptRequestedEvent
            {
                        RunId = "run-1",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = "integration.requested",
                        InputPayload = Any.Pack(new SimpleTextCommand
                        {
                            CommandId = "command-1",
                            Value = "  hello ",
                        }),
                        CommandId = "command-1",
                        CorrelationId = "correlation-1",
                    }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "correlation-1",
            },
        };

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 7,
                Envelope: envelope,
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().ContainSingle();
        var fact = facts[0];
        fact.ActorId.Should().Be("runtime-1");
        fact.DefinitionActorId.Should().Be("definition-1");
        fact.ScriptId.Should().Be("script-1");
        fact.Revision.Should().Be("rev-1");
        fact.RunId.Should().Be("run-1");
        fact.CommandId.Should().Be("command-1");
        fact.CorrelationId.Should().Be("correlation-1");
        fact.StateVersion.Should().Be(8);
        fact.StateTypeUrl.Should().Be(SimpleTextStateTypeUrl);
        fact.ReadModelTypeUrl.Should().Be(SimpleTextReadModelTypeUrl);
        fact.DomainEventPayload.Should().NotBeNull();
        fact.DomainEventPayload.Unpack<SimpleTextEvent>().Current.Value.Should().Be("HELLO");
        fact.ReadModelPayload.Should().NotBeNull();
        fact.ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task DispatchAsync_ShouldEmitNativeMaterializations_WhenSchemaIsDeclared()
    {
        var dispatcher = CreateDispatcher(
            new CachedScriptBehaviorArtifactResolver(new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy())));
        var envelope = new EventEnvelope
        {
            Id = "profile-command-1",
            Payload = Any.Pack(new RunScriptRequestedEvent
            {
                RunId = "profile-run-1",
                DefinitionActorId = "definition-structured-1",
                ScriptRevision = "rev-structured-1",
                RequestedEventType = ScriptSources.StructuredProfileCommandTypeUrl,
                InputPayload = Any.Pack(new ScriptProfileUpdateCommand
                {
                    CommandId = "profile-command-1",
                    ActorId = "actor-1",
                    PolicyId = "policy-1",
                    InputText = " hello ",
                    Tags = { "gold", "vip" },
                }),
                CommandId = "profile-command-1",
                CorrelationId = "profile-correlation-1",
            }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "profile-correlation-1",
            },
        };

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "profile-runtime-1",
                DefinitionActorId: "definition-structured-1",
                ScriptId: "script-profile-1",
                Revision: "rev-structured-1",
                SourceText: ScriptSources.StructuredProfileBehavior,
                SourceHash: ScriptSources.StructuredProfileBehaviorHash,
                ScriptPackage: ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior),
                StateTypeUrl: ScriptSources.StructuredProfileStateTypeUrl,
                ReadModelTypeUrl: ScriptSources.StructuredProfileReadModelTypeUrl,
                CurrentStateRoot: null,
                CurrentStateVersion: 4,
                Envelope: envelope,
                Capabilities: new NoOpCapabilities())
            {
                ReadModelSchemaVersion = "3",
                ReadModelSchemaHash = "structured-schema",
            },
            CancellationToken.None);

        facts.Should().ContainSingle();
        var fact = facts[0];
        fact.NativeDocument.Should().NotBeNull();
        fact.NativeGraph.Should().NotBeNull();
        fact.NativeDocument!.SchemaId.Should().Be("script_profile");
        fact.NativeDocument.FieldsValue.Fields["actor_id"].StringValue.Should().Be("actor-1");
        fact.NativeGraph!.GraphScope.Should().Be("script-native-script_profile");
        fact.NativeGraph.NodeEntries.Should().Contain(x => x.NodeId == "script:script_profile:profile-runtime-1");
        fact.NativeGraph.EdgeEntries.Should().Contain(x =>
            x.FromNodeId == "script:script_profile:profile-runtime-1" &&
            x.EdgeType == "rel_policy");
    }

    [Fact]
    public async Task DispatchAsync_ShouldAcceptDirectCommandEnvelope_AndUseEnvelopeFallbackIdentifiers()
    {
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(new UppercaseBehavior())));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-direct-command",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 5,
                Envelope: new EventEnvelope
                {
                    Id = "direct-command-envelope",
                    Payload = Any.Pack(new SimpleTextCommand
                    {
                        CommandId = string.Empty,
                        Value = "  hello  ",
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                    Propagation = new EnvelopePropagation
                    {
                        CorrelationId = "corr-direct",
                    },
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().ContainSingle();
        var fact = facts[0];
        fact.RunId.Should().Be("direct-command-envelope");
        fact.CommandId.Should().Be("direct-command-envelope");
        fact.CorrelationId.Should().Be("corr-direct");
        fact.StateVersion.Should().Be(6);
        fact.DomainEventPayload.Unpack<SimpleTextEvent>().Current.Value.Should().Be("HELLO");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectRun_WhenCommandPayloadTypeIsNotDeclared()
    {
        var behavior = new UppercaseBehavior();
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(
                CreateArtifact(behavior)));

        var act = () => dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "command-2",
                    Payload = Any.Pack(new RunScriptRequestedEvent
                    {
                        RunId = "run-2",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = "integration.requested",
                        InputPayload = Any.Pack(new SimpleTextSignal
                        {
                            Value = "not-a-command",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected command payload type*aevatar.scripting.tests.SimpleTextSignal*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldReject_WhenBehaviorEmitsUndeclaredDomainEventType()
    {
        var behavior = new InvalidEventBehavior();
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(
                CreateArtifact(behavior)));

        var act = () => dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "command-3",
                    Payload = Any.Pack(new RunScriptRequestedEvent
                    {
                        RunId = "run-3",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = "integration.requested",
                        InputPayload = Any.Pack(new SimpleTextCommand
                        {
                            CommandId = "command-3",
                            Value = "ok",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*undeclared domain event type*" + "type.googleapis.com/aevatar.scripting.tests.SimpleTextUnexpectedEvent*");
    }

    private static ScriptBehaviorDispatcher CreateDispatcher(IScriptBehaviorArtifactResolver artifactResolver) =>
        new(
            artifactResolver,
            new ScriptReadModelMaterializationCompiler(),
            new ScriptNativeProjectionBuilder(),
            new ProtobufMessageCodec());

    [Fact]
    public async Task DispatchAsync_ShouldRejectDirectEnvelope_WhenPayloadTypeIsUndeclared()
    {
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(new UppercaseBehavior())));

        var act = () => dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-undeclared-direct",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "direct-undeclared",
                    Payload = Any.Pack(new SimpleTextUnexpectedEvent
                    {
                        Value = "bad",
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected inbound payload type*SimpleTextUnexpectedEvent*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnEmpty_WhenEnvelopePayloadIsMissing()
    {
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(new UppercaseBehavior())));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-1",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "envelope-without-payload",
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldAcceptRunEnvelope_WhenSignalPayloadIsDeclared()
    {
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(new SignalEchoBehavior())));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-signal",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 3,
                Envelope: new EventEnvelope
                {
                    Id = "signal-envelope",
                    Payload = Any.Pack(new RunScriptRequestedEvent
                    {
                        RunId = "run-signal",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextSignal)),
                        InputPayload = Any.Pack(new SimpleTextSignal
                        {
                            Value = "  ping  ",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().ContainSingle();
        facts[0].StateVersion.Should().Be(4);
        facts[0].DomainEventPayload.Unpack<SimpleTextEvent>().Current.Value.Should().Be("PING");
    }

    [Fact]
    public async Task DispatchAsync_ShouldAcceptDirectSignalEnvelope_WhenSignalPayloadIsDeclared()
    {
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(new SignalEchoBehavior())));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-direct-signal",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 10,
                Envelope: new EventEnvelope
                {
                    Id = "signal-direct",
                    Payload = Any.Pack(new SimpleTextSignal
                    {
                        Value = "hello",
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().ContainSingle();
        facts[0].EventSequence.Should().Be(1);
        facts[0].CommandId.Should().Be("signal-direct");
    }

    [Fact]
    public async Task DispatchAsync_ShouldMaterializeEachFactFromItsOwnPostEventState()
    {
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(new SequentialProjectionBehavior())));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-sequential",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 10,
                Envelope: new EventEnvelope
                {
                    Id = "command-sequential",
                    Payload = Any.Pack(new SimpleTextCommand
                    {
                        CommandId = "command-sequential",
                        Value = "hello",
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().HaveCount(2);
        facts[0].EventSequence.Should().Be(1);
        facts[0].StateVersion.Should().Be(11);
        facts[0].ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("FIRST");
        facts[1].EventSequence.Should().Be(2);
        facts[1].StateVersion.Should().Be(12);
        facts[1].ReadModelPayload.Unpack<SimpleTextReadModel>().Value.Should().Be("SECOND");
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnEmpty_WhenBehaviorReturnsOnlyNullDomainEvents()
    {
        var behavior = new NullEventBehavior();
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(behavior)));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-null-events",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "command-null-events",
                    Payload = Any.Pack(new RunScriptRequestedEvent
                    {
                        RunId = "run-null-events",
                        DefinitionActorId = "definition-1",
                        ScriptRevision = "rev-1",
                        RequestedEventType = "integration.requested",
                        InputPayload = Any.Pack(new SimpleTextCommand
                        {
                            CommandId = "command-null-events",
                            Value = "hello",
                        }),
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().BeEmpty();
        behavior.DisposeAsyncCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_ShouldDisposeSyncBehavior_WhenDispatchCompletes()
    {
        var behavior = new DisposableBehavior();
        var dispatcher = CreateDispatcher(
            new StaticArtifactResolver(CreateArtifact(behavior)));

        var facts = await dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: "runtime-disposable",
                DefinitionActorId: "definition-1",
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "ignored",
                SourceHash: "hash-1",
                ScriptPackage: new ScriptPackageSpec(),
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                CurrentStateRoot: null,
                CurrentStateVersion: 0,
                Envelope: new EventEnvelope
                {
                    Id = "command-disposable",
                    Payload = Any.Pack(new SimpleTextCommand
                    {
                        CommandId = "command-disposable",
                        Value = "hello",
                    }),
                    Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
                },
                Capabilities: new NoOpCapabilities()),
            CancellationToken.None);

        facts.Should().BeEmpty();
        behavior.DisposeCalled.Should().BeTrue();
    }

    [Fact]
    public void BuildInboundMessage_ShouldPreferRequestedEventType_AndFallbackToPayloadType()
    {
        var withRequestedType = InvokePrivateStatic<object>(
            "BuildInboundMessage",
            new EventEnvelope
            {
                Id = "envelope-1",
                Payload = Any.Pack(new RunScriptRequestedEvent
                {
                    RunId = "run-1",
                    RequestedEventType = "custom.requested",
                    InputPayload = Any.Pack(new SimpleTextCommand
                    {
                        CommandId = "command-1",
                        Value = "hello",
                    }),
                }),
                Propagation = new EnvelopePropagation { CorrelationId = "corr-1" },
            });
        var withoutRequestedType = InvokePrivateStatic<object>(
            "BuildInboundMessage",
            new EventEnvelope
            {
                Id = "envelope-2",
                Payload = Any.Pack(new RunScriptRequestedEvent
                {
                    RunId = "run-2",
                    RequestedEventType = string.Empty,
                    InputPayload = Any.Pack(new SimpleTextCommand
                    {
                        CommandId = string.Empty,
                        Value = "hello",
                    }),
                }),
                Propagation = new EnvelopePropagation { CorrelationId = "corr-2" },
            });

        ReadInboundPayloadString(withRequestedType, "MessageType").Should().Be("custom.requested");
        ReadInboundPayloadString(withRequestedType, "MessageId").Should().Be("run-1");
        ReadInboundPayloadString(withRequestedType, "CorrelationId").Should().Be("corr-1");
        ReadInboundPayloadString(withoutRequestedType, "MessageType")
            .Should().Be(ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextCommand)));
        ReadInboundPayloadString(withoutRequestedType, "CommandId").Should().Be("envelope-2");
    }

    [Fact]
    public void ResolveInboundExpectedKind_ShouldCoverDeclaredAndFallbackBranches()
    {
        var commandDescriptor = new UppercaseBehavior().Descriptor;
        var signalDescriptor = new SignalEchoBehavior().Descriptor;

        InvokePrivateStatic<ScriptMessageKind>(
            "ResolveInboundExpectedKind",
            new EventEnvelope
            {
                Payload = Any.Pack(new RunScriptRequestedEvent()),
            },
            commandDescriptor,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextCommand)))
            .Should().Be(ScriptMessageKind.Command);

        InvokePrivateStatic<ScriptMessageKind>(
            "ResolveInboundExpectedKind",
            new EventEnvelope
            {
                Payload = Any.Pack(new RunScriptRequestedEvent()),
            },
            signalDescriptor,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextSignal)))
            .Should().Be(ScriptMessageKind.InternalSignal);

        InvokePrivateStatic<ScriptMessageKind>(
            "ResolveInboundExpectedKind",
            new EventEnvelope
            {
                Payload = Any.Pack(new RunScriptRequestedEvent()),
            },
            commandDescriptor,
            "type.googleapis.com/aevatar.scripting.tests.Unknown")
            .Should().Be(ScriptMessageKind.Command);

        InvokePrivateStatic<ScriptMessageKind>(
            "ResolveInboundExpectedKind",
            new EventEnvelope
            {
                Payload = Any.Pack(new SimpleTextUnexpectedEvent()),
            },
            commandDescriptor,
            "type.googleapis.com/aevatar.scripting.tests.Unknown")
            .Should().Be(ScriptMessageKind.Unspecified);
    }

    [Fact]
    public void ResolveInboundMessageClrType_ShouldReturnDeclaredTypes_AndRejectUnknown()
    {
        var commandDescriptor = new UppercaseBehavior().Descriptor;
        var signalDescriptor = new SignalEchoBehavior().Descriptor;

        InvokePrivateStatic<System.Type>(
            "ResolveInboundMessageClrType",
            new EventEnvelope
            {
                Payload = Any.Pack(new RunScriptRequestedEvent()),
            },
            commandDescriptor,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextCommand)))
            .Should().Be(typeof(SimpleTextCommand));

        InvokePrivateStatic<System.Type>(
            "ResolveInboundMessageClrType",
            new EventEnvelope
            {
                Payload = Any.Pack(new SimpleTextSignal()),
            },
            signalDescriptor,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextSignal)))
            .Should().Be(typeof(SimpleTextSignal));

        var act = () => InvokePrivateStatic<System.Type>(
            "ResolveInboundMessageClrType",
            new EventEnvelope
            {
                Payload = Any.Pack(new SimpleTextUnexpectedEvent()),
            },
            commandDescriptor,
            ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextUnexpectedEvent)));

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Inbound payload type*not declared*");
    }

    [Fact]
    public void ResolveRunIdAndSemanticIdentity_ShouldCoverFallbackBranches()
    {
        InvokePrivateStatic<string>(
            "ResolveRunId",
            new EventEnvelope
            {
                Id = "direct-envelope",
                Payload = Any.Pack(new SimpleTextCommand()),
            })
            .Should().Be("direct-envelope");

        InvokePrivateStatic<string>(
            "ResolveRunId",
            new EventEnvelope
            {
                Id = "wrapped-envelope",
                Payload = Any.Pack(new RunScriptRequestedEvent
                {
                    RunId = "run-123",
                }),
            })
            .Should().Be("run-123");

        InvokePrivateStatic<string?>(
            "ResolveSemanticIdentity",
            new SimpleTextCommand { CommandId = "command-42" },
            "command_id")
            .Should().Be("command-42");

        InvokePrivateStatic<string?>(
            "ResolveSemanticIdentity",
            new SimpleTextCommand { CommandId = string.Empty },
            "command_id")
            .Should().BeNull();

        InvokePrivateStatic<string?>(
            "ResolveSemanticIdentity",
            new SimpleTextCommand { CommandId = "ignored" },
            string.Empty)
            .Should().BeNull();
    }

    [Fact]
    public void NormalizeDomainEvents_ShouldHandleNullEmptyAndNullEntries()
    {
        InvokePrivateStatic<IReadOnlyList<IMessage>>("NormalizeDomainEvents", (object?)null)
            .Should().BeEmpty();
        InvokePrivateStatic<IReadOnlyList<IMessage>>("NormalizeDomainEvents", (object)Array.Empty<IMessage>())
            .Should().BeEmpty();

        var normalized = InvokePrivateStatic<IReadOnlyList<IMessage>>(
            "NormalizeDomainEvents",
            (object)new IMessage?[]
            {
                null,
                new SimpleTextEvent
                {
                    Current = new SimpleTextReadModel { Value = "hello", HasValue = true },
                },
            });

        normalized.Should().ContainSingle().Which.Should().BeOfType<SimpleTextEvent>();
    }

    private sealed class StaticArtifactResolver : IScriptBehaviorArtifactResolver
    {
        private readonly ScriptBehaviorArtifact _artifact;

        public StaticArtifactResolver(ScriptBehaviorArtifact artifact)
        {
            _artifact = artifact;
        }

        public ScriptBehaviorArtifact Resolve(ScriptBehaviorArtifactRequest request)
        {
            request.ScriptId.Should().Be("script-1");
            request.Revision.Should().Be("rev-1");
            return _artifact;
        }
    }

    private static ScriptBehaviorArtifact CreateArtifact(IScriptBehaviorBridge behavior) =>
        new(
            "script-1",
            "rev-1",
            "hash-1",
            behavior.Descriptor,
            behavior.Descriptor.ToContract(),
            () => behavior);

    private sealed class UppercaseBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty })
                .ProjectState(static (state, _) => state == null
                    ? null
                    : new SimpleTextReadModel
                    {
                        HasValue = !string.IsNullOrWhiteSpace(state.Value),
                        Value = state.Value ?? string.Empty,
                    });
        }

        private static Task HandleAsync(
            SimpleTextCommand inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = inbound.CommandId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = (inbound.Value ?? string.Empty).Trim().ToUpperInvariant(),
                },
            });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested query,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
            {
                RequestId = query.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
            });
        }
    }

    private sealed class InvalidEventBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty })
                .ProjectState(static (state, _) => state == null
                    ? null
                    : new SimpleTextReadModel
                    {
                        HasValue = !string.IsNullOrWhiteSpace(state.Value),
                        Value = state.Value ?? string.Empty,
                    });
        }

        private static Task HandleAsync(
            SimpleTextCommand inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            _ = inbound;
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextUnexpectedEvent { Value = "unexpected" });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested query,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            _ = query;
            _ = snapshot;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(null);
        }
    }

    private sealed class SignalEchoBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnSignal<SimpleTextSignal>(HandleSignalAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty })
                .ProjectState(static (state, _) => state == null
                    ? null
                    : new SimpleTextReadModel
                    {
                        HasValue = !string.IsNullOrWhiteSpace(state.Value),
                        Value = state.Value ?? string.Empty,
                    });
        }

        private static Task HandleSignalAsync(
            SimpleTextSignal inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = context.CommandId,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = (inbound.Value ?? string.Empty).Trim().ToUpperInvariant(),
                },
            });
            return Task.CompletedTask;
        }

        private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
            SimpleTextQueryRequested query,
            ScriptQueryContext<SimpleTextReadModel> snapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
            {
                RequestId = query.RequestId ?? string.Empty,
                Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
            });
        }
    }

    private sealed class SequentialProjectionBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty })
                .ProjectState(static (state, _) => new SimpleTextReadModel
                {
                    HasValue = !string.IsNullOrWhiteSpace(state?.Value),
                    Value = state?.Value ?? string.Empty,
                });
        }

        private static Task HandleAsync(
            SimpleTextCommand inbound,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            _ = inbound;
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = context.CommandId,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "FIRST",
                },
            });
            context.Emit(new SimpleTextEvent
            {
                CommandId = context.CommandId,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "SECOND",
                },
            });
            return Task.CompletedTask;
        }
    }

    private sealed class NullEventBehavior : IScriptBehaviorBridge, IAsyncDisposable
    {
        public bool DisposeAsyncCalled { get; private set; }

        public ScriptBehaviorDescriptor Descriptor { get; } = new UppercaseBehavior().Descriptor;

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct)
        {
            _ = inbound;
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IMessage>>([null!]);
        }

        public IMessage? ApplyDomainEvent(IMessage? currentState, IMessage domainEvent, ScriptFactContext context)
        {
            _ = currentState;
            _ = domainEvent;
            _ = context;
            return null;
        }

        public IMessage? BuildReadModel(IMessage? currentState, ScriptFactContext context)
        {
            _ = currentState;
            _ = context;
            return null;
        }

        public Task<IMessage?> ExecuteQueryAsync(IMessage query, ScriptTypedReadModelSnapshot snapshot, CancellationToken ct)
        {
            _ = query;
            _ = snapshot;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IMessage?>(null);
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableBehavior : IScriptBehaviorBridge, IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public ScriptBehaviorDescriptor Descriptor { get; } = new UppercaseBehavior().Descriptor;

        public Task<IReadOnlyList<IMessage>> DispatchAsync(
            IMessage inbound,
            ScriptDispatchContext context,
            CancellationToken ct)
        {
            _ = inbound;
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IMessage>>([]);
        }

        public IMessage? ApplyDomainEvent(IMessage? currentState, IMessage domainEvent, ScriptFactContext context)
        {
            _ = currentState;
            _ = domainEvent;
            _ = context;
            return null;
        }

        public IMessage? BuildReadModel(IMessage? currentState, ScriptFactContext context)
        {
            _ = currentState;
            _ = context;
            return null;
        }

        public Task<IMessage?> ExecuteQueryAsync(IMessage query, ScriptTypedReadModelSnapshot snapshot, CancellationToken ct)
        {
            _ = query;
            _ = snapshot;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IMessage?>(null);
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(ScriptBehaviorDispatcher).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (T)method!.Invoke(null, arguments)!;
    }

    private static string ReadInboundPayloadString(object inboundPayload, string propertyName)
    {
        var property = inboundPayload.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        property.Should().NotBeNull();
        return (string)property!.GetValue(inboundPayload)!;
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 0, RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new ScriptPromotionDecision(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                new ScriptEvolutionValidationReport(false, [])));
        public Task<string> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) =>
            Task.FromResult(definitionActorId ?? string.Empty);
        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) =>
            Task.FromResult(runtimeActorId ?? string.Empty);
        public Task RunScriptInstanceAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PromoteRevisionAsync(string catalogActorId, string scriptId, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RollbackRevisionAsync(string catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
