using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptRuntimeGAgentBranchCoverageTests
{
    [Theory]
    [InlineData("dispatcher")]
    [InlineData("capabilityFactory")]
    [InlineData("artifactResolver")]
    [InlineData("codec")]
    public void Ctor_ShouldRejectNullDependencies(string parameterName)
    {
        Action act = parameterName switch
        {
            "dispatcher" => () => _ = new ScriptBehaviorGAgent(
                null!,
                new NoOpCapabilityFactory(),
                new NoOpArtifactResolver(),
                new ProtobufMessageCodec()),
            "capabilityFactory" => () => _ = new ScriptBehaviorGAgent(
                new NoOpDispatcher(),
                null!,
                new NoOpArtifactResolver(),
                new ProtobufMessageCodec()),
            "artifactResolver" => () => _ = new ScriptBehaviorGAgent(
                new NoOpDispatcher(),
                new NoOpCapabilityFactory(),
                null!,
                new ProtobufMessageCodec()),
            "codec" => () => _ = new ScriptBehaviorGAgent(
                new NoOpDispatcher(),
                new NoOpCapabilityFactory(),
                new NoOpArtifactResolver(),
                null!),
            _ => throw new InvalidOperationException("Unexpected parameter name."),
        };

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(parameterName);
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreEnvelopeWithoutPayload()
    {
        var harness = CreateHarness();

        await harness.Agent.HandleEnvelopeAsync(new EventEnvelope
        {
            Id = "evt-empty",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("runtime-test", TopologyAudience.Self),
        });

        harness.Agent.State.LastAppliedEventVersion.Should().Be(0);
        harness.Publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldTreatIdenticalBindingAsNoOp()
    {
        var harness = CreateHarness();
        var bind = new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        };

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(bind));
        var versionAfterFirstBind = harness.Agent.State.LastAppliedEventVersion;
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(bind));

        harness.Agent.State.LastAppliedEventVersion.Should().Be(versionAfterFirstBind);
        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().ContainSingle(x => x.EventData.Is(ScriptBehaviorBoundEvent.Descriptor));
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreIncompleteBindingQuery()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "request-1",
            ReplyStreamId = string.Empty,
        }));

        harness.Publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreBindingQuery_WhenRequestIdIsMissing()
    {
        var harness = CreateHarness();

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = string.Empty,
            ReplyStreamId = "reply-stream",
        }));

        harness.Publisher.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectDispatch_WhenActorIsNotBound()
    {
        var harness = CreateHarness();

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "hello",
            }),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is not bound*");
    }

    [Theory]
    [InlineData("", "script-1", "rev-1", "source", "DefinitionActorId is required.")]
    [InlineData("definition-1", "", "rev-1", "source", "ScriptId is required.")]
    [InlineData("definition-1", "script-1", "", "source", "Revision is required.")]
    [InlineData("definition-1", "script-1", "rev-1", "", "ScriptPackage must contain at least one C# source.")]
    public async Task HandleEnvelopeAsync_ShouldRejectInvalidBinding(
        string definitionActorId,
        string scriptId,
        string revision,
        string sourceText,
        string expectedMessage)
    {
        var harness = CreateHarness();

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = definitionActorId,
            ScriptId = scriptId,
            Revision = revision,
            SourceText = sourceText,
            SourceHash = "hash-1",
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldAcceptBinding_WhenScriptPackageProvidesSource()
    {
        var harness = CreateHarness();

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = string.Empty,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));

        harness.Agent.State.DefinitionActorId.Should().Be("definition-1");
        harness.Agent.State.ScriptPackage.CsharpSources.Should().NotBeEmpty();
        harness.Agent.State.SourceText.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldBind_WhenOnlySourceTextIsProvided()
    {
        var harness = CreateHarness();

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));

        harness.Agent.State.DefinitionActorId.Should().Be("definition-1");
        harness.Agent.State.SourceText.Should().Be(ScriptSources.UppercaseBehavior);
        harness.Agent.State.ScriptPackage.CsharpSources.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRespondBindingQuery_WhenActorIsUnbound()
    {
        var harness = CreateHarness();

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "request-unbound",
            ReplyStreamId = "reply-stream",
        }));

        harness.Publisher.Sent.Should().ContainSingle();
        var response = harness.Publisher.Sent[0].Should().BeOfType<ScriptBehaviorBindingRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-unbound");
        response.Found.Should().BeFalse();
        response.FailureReason.Should().Contain("not bound");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRespondBindingQuery_WhenActorIsBound()
    {
        var harness = CreateHarness();
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "request-bound",
            ReplyStreamId = "reply-stream",
        }));

        harness.Publisher.Sent.Should().ContainSingle();
        var response = harness.Publisher.Sent[0].Should().BeOfType<ScriptBehaviorBindingRespondedEvent>().Subject;
        response.RequestId.Should().Be("request-bound");
        response.Found.Should().BeTrue();
        response.DefinitionActorId.Should().Be("definition-1");
        response.Revision.Should().Be("rev-1");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectDispatch_WhenRunTargetsDifferentDefinition()
    {
        var harness = CreateHarness();
        await BindAsync(harness.Agent);

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            DefinitionActorId = "definition-2",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "hello",
            }),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to definition `definition-1`*definition-2*");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectDispatch_WhenRunTargetsDifferentRevision()
    {
        var harness = CreateHarness();
        await BindAsync(harness.Agent);

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-2",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "hello",
            }),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to revision `rev-1`*rev-2*");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectProvision_WhenDefinitionActorIdIsMissing()
    {
        var harness = CreateHarness();

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = string.Empty,
            RequestedRevision = "rev-1",
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DefinitionActorId is required.*");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectProvision_WhenDifferentBindingIsAlreadyPending()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            RequestedRevision = "rev-1",
            RequestId = "request-pending",
        }));

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-2",
            RequestedRevision = "rev-2",
            RequestId = "request-conflict",
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already binding definition `definition-1` revision `rev-1`*");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldQueueRunsWhileBinding_AndReplayAfterBindingCompletes()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            RequestedRevision = "rev-1",
            RequestId = "request-queue",
        }));

        var run = new RunScriptRequestedEvent
        {
            RunId = "run-queued",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-queued",
                Value = "hello",
            }),
        };

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(run));
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(run));
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "query-pending",
            ReplyStreamId = "reply-stream",
        }));

        harness.Agent.State.PendingRunRequests.Should().ContainSingle(x => x.RunId == "run-queued");
        harness.Publisher.Sent.OfType<ScriptBehaviorBindingRespondedEvent>()
            .Single(x => x.RequestId == "query-pending")
            .Should()
            .Match<ScriptBehaviorBindingRespondedEvent>(x =>
                !x.Found &&
                x.Pending &&
                x.FailureReason.Contains("binding definition `definition-1` revision `rev-1`", StringComparison.Ordinal));

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = "request-queue",
            Found = true,
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));

        harness.Agent.State.DefinitionActorId.Should().Be("definition-1");
        harness.Agent.State.PendingBindingRequestId.Should().BeEmpty();
        harness.Agent.State.PendingRunRequests.Should().BeEmpty();
        harness.Publisher.Sent.OfType<RunScriptRequestedEvent>()
            .Should()
            .ContainSingle(x => x.RunId == "run-queued");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldPersistBindingFailure_WhenDefinitionQueryDispatchThrows()
    {
        var publisher = new RecordingEventPublisher
        {
            SendFailureFactory = static (_, evt) => evt is QueryScriptDefinitionSnapshotRequestedEvent
                ? new InvalidOperationException("dispatch-boom")
                : null,
        };
        var eventStore = new InMemoryEventStore();
        var agent = CreateAgent(eventStore, publisher: publisher);

        await agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            RequestedRevision = "rev-1",
            RequestId = "request-dispatch-failure",
        }));
        await agent.HandleEnvelopeAsync(BuildEnvelope(new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "query-failed",
            ReplyStreamId = "reply-stream",
        }));

        agent.State.PendingBindingRequestId.Should().BeEmpty();
        agent.State.BindingFailureReason.Should().Contain("Failed to dispatch definition query. reason=dispatch-boom");
        publisher.Sent.OfType<ScriptBehaviorBindingRespondedEvent>()
            .Single(x => x.RequestId == "query-failed")
            .Should()
            .Match<ScriptBehaviorBindingRespondedEvent>(x =>
                !x.Found &&
                !x.Pending &&
                x.FailureReason.Contains("dispatch-boom", StringComparison.Ordinal));

        var persisted = await eventStore.GetEventsAsync(agent.Id, ct: CancellationToken.None);
        persisted.Should().Contain(x => x.EventData.Is(ScriptBehaviorBindingFailedEvent.Descriptor));
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreStaleDefinitionReply_AndFailWhenDefinitionIsNotFound()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            RequestedRevision = "rev-1",
            RequestId = "request-not-found",
        }));

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = "other-request",
            Found = true,
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
        }));

        harness.Agent.State.PendingBindingRequestId.Should().Be("request-not-found");

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = "request-not-found",
            Found = false,
            FailureReason = string.Empty,
        }));

        harness.Agent.State.PendingBindingRequestId.Should().BeEmpty();
        harness.Agent.State.BindingFailureReason.Should().Be("Script definition query returned not found.");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldPersistBindingFailure_WhenDefinitionReplyIsInvalid()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            RequestedRevision = "rev-1",
            RequestId = "request-invalid-snapshot",
        }));

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = "request-invalid-snapshot",
            Found = true,
            ScriptId = string.Empty,
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
        }));

        harness.Agent.State.PendingBindingRequestId.Should().BeEmpty();
        harness.Agent.State.BindingFailureReason.Should().Be("ScriptId is required.");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldIgnoreMismatchedBindingTimeout_AndFailOnMatchingTimeout()
    {
        var harness = CreateHarness();
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new ProvisionScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            RequestedRevision = "rev-1",
            RequestId = "request-timeout",
        }));

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(
            new ScriptBehaviorBindingTimeoutFiredEvent
            {
                RequestId = "other-request",
            }));
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(
            new ScriptBehaviorBindingTimeoutFiredEvent
            {
                RequestId = "request-timeout",
            }));
        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(
            new ScriptBehaviorBindingTimeoutFiredEvent
            {
                RequestId = "request-timeout",
            },
            callback: CreateCallbackMetadata("other-callback")));

        harness.Agent.State.PendingBindingRequestId.Should().Be("request-timeout");

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(
            new ScriptBehaviorBindingTimeoutFiredEvent
            {
                RequestId = "request-timeout",
            },
            callback: CreateCallbackMetadata(RuntimeCallbackKeyComposer.BuildCallbackId(
                "script-behavior-binding",
                harness.Agent.Id,
                "request-timeout"))));

        harness.Agent.State.PendingBindingRequestId.Should().BeEmpty();
        harness.Agent.State.BindingFailureReason.Should().Contain("Timed out waiting for script definition response. request_id=request-timeout");
    }

    [Theory]
    [InlineData("run-correlation", "run-correlation")]
    [InlineData("", "run-fallback")]
    public async Task HandleEnvelopeAsync_ShouldUseRunCorrelationFallback_WhenPropagationCorrelationIsMissing(
        string runCorrelationId,
        string expectedCorrelationId)
    {
        ScriptBehaviorRuntimeCapabilityContext? capturedContext = null;
        var harness = CreateHarness(
            dispatcher: new NoOpDispatcher(),
            capabilityFactory: new CapturingCapabilityFactory(context => capturedContext = context));
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(
            new RunScriptRequestedEvent
            {
                RunId = "run-fallback",
                CorrelationId = runCorrelationId,
                RequestedEventType = "integration.requested",
                InputPayload = Any.Pack(new SimpleTextCommand
                {
                    CommandId = "command-fallback",
                    Value = "hello",
                }),
            },
            correlationId: string.Empty));

        capturedContext.Should().NotBeNull();
        capturedContext!.RunId.Should().Be("run-fallback");
        capturedContext.CorrelationId.Should().Be(expectedCorrelationId);
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldDispatch_WhenRunOmitsDefinitionAndRevision()
    {
        ScriptBehaviorDispatchRequest? capturedRequest = null;
        var harness = CreateHarness(
            dispatcher: new ReturningDispatcher(request =>
            {
                capturedRequest = request;
                return [];
            }));
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-no-target",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-no-target",
                Value = "hello",
            }),
        }));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.DefinitionActorId.Should().Be("definition-1");
        capturedRequest.Revision.Should().Be("rev-1");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldDispatchDirectCommandEnvelope()
    {
        ScriptBehaviorDispatchRequest? capturedRequest = null;
        var harness = CreateHarness(
            dispatcher: new ReturningDispatcher(request =>
            {
                capturedRequest = request;
                return [];
            }));
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new SimpleTextCommand
        {
            CommandId = string.Empty,
            Value = "hello",
        }));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.DefinitionActorId.Should().Be("definition-1");
        capturedRequest.Envelope.Id.Should().NotBeNullOrWhiteSpace();
        capturedRequest.Envelope.Payload.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldPersistFactsAndApplyState_WhenDispatcherReturnsCommittedFact()
    {
        var harness = CreateHarness(
            dispatcher: new ReturningDispatcher(request =>
            {
                var fact = new ScriptDomainFactCommitted
                {
                    ActorId = request.ActorId,
                    DefinitionActorId = request.DefinitionActorId,
                    ScriptId = request.ScriptId,
                    Revision = request.Revision,
                    RunId = "run-committed",
                    CommandId = "command-committed",
                    CorrelationId = "correlation-committed",
                    EventSequence = 1,
                    EventType = ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextEvent)),
                    DomainEventPayload = Any.Pack(new SimpleTextEvent
                    {
                        CommandId = "command-committed",
                        Current = new SimpleTextReadModel
                        {
                            HasValue = true,
                            Value = "HELLO",
                        },
                    }),
                    StateTypeUrl = ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextState)),
                    ReadModelTypeUrl = ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextReadModel)),
                    StateVersion = request.CurrentStateVersion + 1,
                    OccurredAtUnixTimeMs = 1234,
                };
                return [fact];
            }),
            artifactResolver: new StaticArtifactResolver(new ApplyingBehavior()));
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-committed",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-committed",
                Value = "hello",
            }),
        }));

        harness.Agent.State.LastRunId.Should().Be("run-committed");
        harness.Agent.State.LastAppliedEventVersion.Should().Be(2);
        harness.Agent.State.LastEventId.Should().Be(ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextEvent)));
        harness.Agent.State.StateRoot.Should().NotBeNull();
        harness.Agent.State.StateRoot.Unpack<SimpleTextState>().Value.Should().Be("HELLO");
        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().Contain(x => x.EventData.Is(ScriptDomainFactCommitted.Descriptor));
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldPreserveBindingValues_WhenCommittedFactOmitsOptionalFields()
    {
        var harness = CreateHarness(
            dispatcher: new ReturningDispatcher(request =>
            [
                new ScriptDomainFactCommitted
                {
                    ActorId = request.ActorId,
                    DefinitionActorId = string.Empty,
                    ScriptId = string.Empty,
                    Revision = string.Empty,
                    RunId = "run-fallback",
                    CommandId = "command-fallback",
                    CorrelationId = "correlation-fallback",
                    EventSequence = 1,
                    EventType = string.Empty,
                    DomainEventPayload = Any.Pack(new SimpleTextEvent
                    {
                        CommandId = "command-fallback",
                        Current = new SimpleTextReadModel
                        {
                            HasValue = true,
                            Value = "FALLBACK",
                        },
                    }),
                    StateTypeUrl = string.Empty,
                    ReadModelTypeUrl = string.Empty,
                    StateVersion = request.CurrentStateVersion + 1,
                    OccurredAtUnixTimeMs = 1234,
                },
            ]),
            artifactResolver: new StaticArtifactResolver(new ApplyingBehavior()));
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-fallback",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-fallback",
                Value = "hello",
            }),
        }));

        harness.Agent.State.DefinitionActorId.Should().Be("definition-1");
        harness.Agent.State.ScriptId.Should().Be("script-1");
        harness.Agent.State.Revision.Should().Be("rev-1");
        harness.Agent.State.StateTypeUrl.Should().Be(ScriptSources.UppercaseStateTypeUrl);
        harness.Agent.State.ReadModelTypeUrl.Should().Be(ScriptSources.UppercaseReadModelTypeUrl);
        harness.Agent.State.LastEventId.Should().Be(ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextEvent)));
        harness.Agent.State.StateRoot.Should().NotBeNull();
        harness.Agent.State.StateRoot.Unpack<SimpleTextState>().Value.Should().Be("FALLBACK");
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectCommittedFact_WhenArtifactDoesNotDeclareEventType()
    {
        var harness = CreateHarness(
            dispatcher: new ReturningDispatcher(request =>
            [
                new ScriptDomainFactCommitted
                {
                    ActorId = request.ActorId,
                    DefinitionActorId = request.DefinitionActorId,
                    ScriptId = request.ScriptId,
                    Revision = request.Revision,
                    RunId = "run-undeclared-event",
                    CommandId = "command-undeclared-event",
                    CorrelationId = "correlation-undeclared-event",
                    EventSequence = 1,
                    EventType = ScriptMessageTypes.GetTypeUrl(typeof(SimpleTextUnexpectedEvent)),
                    DomainEventPayload = Any.Pack(new SimpleTextUnexpectedEvent
                    {
                        Value = "HELLO",
                    }),
                    StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
                    ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                    StateVersion = request.CurrentStateVersion + 1,
                    OccurredAtUnixTimeMs = 1234,
                },
            ]),
            artifactResolver: new StaticArtifactResolver(new ApplyingBehavior()));
        await BindAsync(harness.Agent);

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-undeclared-event",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-undeclared-event",
                Value = "hello",
            }),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot apply undeclared domain event type*");
    }

    private static BranchCoverageHarness CreateHarness(
        IScriptBehaviorDispatcher? dispatcher = null,
        IScriptBehaviorArtifactResolver? artifactResolver = null,
        IScriptBehaviorRuntimeCapabilityFactory? capabilityFactory = null,
        RecordingEventPublisher? publisher = null)
    {
        var eventStore = new InMemoryEventStore();
        var resolvedPublisher = publisher ?? new RecordingEventPublisher();
        var agent = CreateAgent(
            eventStore,
            dispatcher,
            artifactResolver,
            capabilityFactory,
            resolvedPublisher);

        return new BranchCoverageHarness(agent, resolvedPublisher, eventStore);
    }

    private static ScriptBehaviorGAgent CreateAgent(
        InMemoryEventStore eventStore,
        IScriptBehaviorDispatcher? dispatcher = null,
        IScriptBehaviorArtifactResolver? artifactResolver = null,
        IScriptBehaviorRuntimeCapabilityFactory? capabilityFactory = null,
        IEventPublisher? publisher = null)
    {
        return new ScriptBehaviorGAgent(
            dispatcher ?? new NoOpDispatcher(),
            capabilityFactory ?? new NoOpCapabilityFactory(),
            artifactResolver ?? new NoOpArtifactResolver(),
            new ProtobufMessageCodec())
        {
            EventPublisher = publisher ?? new RecordingEventPublisher(),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(eventStore),
            Services = new StaticServiceProvider(new StubCallbackScheduler()),
        };
    }

    private static EventEnvelope BuildEnvelope(
        IMessage payload,
        string? correlationId = "corr-1",
        EnvelopeCallbackContext? callback = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("runtime-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? string.Empty,
            },
            Runtime = callback == null
                ? null
                : new EnvelopeRuntime
                {
                    Callback = callback.Clone(),
                },
        };

    private static EnvelopeCallbackContext CreateCallbackMetadata(string callbackId) =>
        new()
        {
            CallbackId = callbackId,
            Generation = 0,
            FireIndex = 0,
            FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private sealed record BranchCoverageHarness(
        ScriptBehaviorGAgent Agent,
        RecordingEventPublisher Publisher,
        InMemoryEventStore EventStore);

    private static async Task BindAsync(ScriptBehaviorGAgent agent)
    {
        await agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));
    }

    private sealed class NoOpDispatcher : IScriptBehaviorDispatcher
    {
        public Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
            ScriptBehaviorDispatchRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptDomainFactCommitted>>([]);
        }
    }

    private sealed class ReturningDispatcher(
        Func<ScriptBehaviorDispatchRequest, IReadOnlyList<ScriptDomainFactCommitted>> factory) : IScriptBehaviorDispatcher
    {
        public Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
            ScriptBehaviorDispatchRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(factory(request));
        }
    }

    private sealed class NoOpArtifactResolver : IScriptBehaviorArtifactResolver
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

    private sealed class NoOpCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
    {
        public IScriptBehaviorRuntimeCapabilities Create(
            ScriptBehaviorRuntimeCapabilityContext context,
            Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
            Func<string, IMessage, CancellationToken, Task> sendToAsync,
            Func<IMessage, CancellationToken, Task> publishToSelfAsync,
            Func<string, TimeSpan, IMessage, CancellationToken, Task<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease>> scheduleSelfSignalAsync,
            Func<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
        {
            _ = context;
            _ = publishAsync;
            _ = sendToAsync;
            _ = publishToSelfAsync;
            _ = scheduleSelfSignalAsync;
            _ = cancelCallbackAsync;
            return new NoOpCapabilities();
        }
    }

    private sealed class CapturingCapabilityFactory(
        Action<ScriptBehaviorRuntimeCapabilityContext> capture) : IScriptBehaviorRuntimeCapabilityFactory
    {
        public IScriptBehaviorRuntimeCapabilities Create(
            ScriptBehaviorRuntimeCapabilityContext context,
            Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
            Func<string, IMessage, CancellationToken, Task> sendToAsync,
            Func<IMessage, CancellationToken, Task> publishToSelfAsync,
            Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
            Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
        {
            capture(context);
            _ = publishAsync;
            _ = sendToAsync;
            _ = publishToSelfAsync;
            _ = scheduleSelfSignalAsync;
            _ = cancelCallbackAsync;
            return new NoOpCapabilities();
        }
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease("runtime-1", callbackId, 0, Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<Aevatar.Scripting.Abstractions.Queries.ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) =>
            Task.FromResult<Aevatar.Scripting.Abstractions.Queries.ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<Aevatar.Scripting.Abstractions.Definitions.ScriptPromotionDecision> ProposeScriptEvolutionAsync(Aevatar.Scripting.Abstractions.Definitions.ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new Aevatar.Scripting.Abstractions.Definitions.ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new Aevatar.Scripting.Abstractions.Definitions.ScriptEvolutionValidationReport(false, [])));
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

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Sent { get; } = [];
        public Func<string, IMessage, Exception?>? SendFailureFactory { get; init; }
        public Func<IMessage, Exception?>? PublishFailureFactory { get; init; }

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
            if (PublishFailureFactory?.Invoke(evt) is { } publishFailure)
                throw publishFailure;
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
            if (SendFailureFactory?.Invoke(targetActorId, evt) is { } sendFailure)
                throw sendFailure;
            Sent.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class StubCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StaticServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(System.Type serviceType) =>
            serviceType == service.GetType() || serviceType.IsInstanceOfType(service)
                ? service
                : null;
    }

    private sealed class ApplyingBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
    {
        protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
        {
            builder
                .OnCommand<SimpleTextCommand>(HandleAsync)
                .OnEvent<SimpleTextEvent>(
                    apply: static (_, evt, _) => new SimpleTextState { Value = evt.Current?.Value ?? string.Empty },
                    reduce: static (_, evt, _) => evt.Current)
                .OnQuery<SimpleTextQueryRequested, SimpleTextQueryResponded>(HandleQueryAsync);
        }

        private static Task HandleAsync(
            SimpleTextCommand command,
            ScriptCommandContext<SimpleTextState> context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            context.Emit(new SimpleTextEvent
            {
                CommandId = command.CommandId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = command.Value ?? string.Empty,
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
}
