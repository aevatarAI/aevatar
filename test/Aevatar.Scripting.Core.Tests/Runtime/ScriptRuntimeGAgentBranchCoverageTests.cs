using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
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
            "dispatcher" => () => _ = new ScriptBehaviorGAgent(null!, new NoOpCapabilityFactory(), CreateArtifactResolver(), new ProtobufMessageCodec()),
            "capabilityFactory" => () => _ = new ScriptBehaviorGAgent(new NoOpDispatcher(), null!, CreateArtifactResolver(), new ProtobufMessageCodec()),
            "artifactResolver" => () => _ = new ScriptBehaviorGAgent(new NoOpDispatcher(), new NoOpCapabilityFactory(), null!, new ProtobufMessageCodec()),
            "codec" => () => _ = new ScriptBehaviorGAgent(new NoOpDispatcher(), new NoOpCapabilityFactory(), CreateArtifactResolver(), null!),
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
        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldTreatIdenticalBindingAsNoOp()
    {
        var harness = CreateHarness();
        var bind = CreateBindRequest();

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(bind));
        var versionAfterFirstBind = harness.Agent.State.LastAppliedEventVersion;

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(bind));

        harness.Agent.State.LastAppliedEventVersion.Should().Be(versionAfterFirstBind);
        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().ContainSingle(x => x.EventData.Is(ScriptBehaviorBoundEvent.Descriptor));
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
            SourceHash = "hash-1",
            ScriptPackage = string.IsNullOrWhiteSpace(sourceText)
                ? new ScriptPackageSpec()
                : ScriptPackageSpecExtensions.CreateSingleSource(sourceText),
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
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));

        harness.Agent.State.DefinitionActorId.Should().Be("definition-1");
        harness.Agent.State.ScriptPackage.CsharpSources.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectBinding_WhenScriptPackageIsMissing()
    {
        var harness = CreateHarness();

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ScriptPackage must contain at least one C# source*");
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

    [Theory]
    [InlineData("run-correlation", "run-correlation")]
    [InlineData("", "run-fallback")]
    public async Task HandleEnvelopeAsync_ShouldUseRunCorrelationFallback_WhenPropagationCorrelationIsMissing(
        string runCorrelationId,
        string expectedCorrelationId)
    {
        ScriptBehaviorRuntimeCapabilityContext? capturedContext = null;
        var harness = CreateHarness(
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
            CommandId = "command-no-target",
            Value = "hello",
        }));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.DefinitionActorId.Should().Be("definition-1");
        capturedRequest.Revision.Should().Be("rev-1");
        capturedRequest.Envelope.Payload.Should().NotBeNull();
        capturedRequest.Envelope.Payload!.Is(SimpleTextCommand.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldPersistFactsAndApplyState_WhenDispatcherReturnsCommittedFact()
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
                    RunId = "run-committed",
                    CommandId = "command-committed",
                    CorrelationId = "correlation-committed",
                    EventSequence = 1,
                    EventType = ScriptSources.UppercaseEventTypeUrl,
                    DomainEventPayload = Any.Pack(new SimpleTextEvent
                    {
                        CommandId = "command-committed",
                        Current = new SimpleTextReadModel
                        {
                            HasValue = true,
                            Value = "HELLO",
                        },
                    }),
                    StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
                    ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
                    StateVersion = request.CurrentStateVersion + 1,
                    OccurredAtUnixTimeMs = 1234,
                },
            ]));
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-committed",
            CommandId = "command-committed",
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
        harness.Agent.State.LastAppliedEventVersion.Should().Be(3);
        harness.Agent.State.LastEventId.Should().Be(ScriptSources.UppercaseEventTypeUrl);
        harness.Agent.State.StateRoot.Should().NotBeNull();
        harness.Agent.State.StateRoot!.Unpack<SimpleTextState>().Value.Should().Be("HELLO");
        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        var factStateEvent = persisted.Single(x => x.EventData.Is(ScriptDomainFactCommitted.Descriptor));
        var outcomeStateEvent = persisted.Single(x => x.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor));
        var outcome = outcomeStateEvent.EventData
            .Unpack<ScriptRunOutcomeRecordedEvent>();
        outcome.ScriptRunId.Should().Be("run-committed");
        outcome.Status.Should().Be(ScriptRunOutcomeStatus.Succeeded);
        outcome.CommandId.Should().Be("command-committed");
        outcome.CorrelationId.Should().Be("corr-1");
        outcome.CommittedFactCount.Should().Be(1);
        outcome.StateVersion.Should().Be(outcomeStateEvent.Version);
        outcome.StateVersion.Should().Be(factStateEvent.Version + 1);
        outcome.Result.Should().NotBeNull();
        var delivery = harness.Agent.State.RunOutcomes["run-committed"];
        delivery.Outcome.ScriptRunId.Should().Be("run-committed");
        delivery.Outcome.CommittedFactCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRecordSucceededOutcome_WhenRunProducesNoFacts()
    {
        var harness = CreateHarness();
        await BindAsync(harness.Agent);

        await harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-no-facts",
            CommandId = "command-no-facts",
            CorrelationId = "correlation-no-facts",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-no-facts",
                Value = "hello",
            }),
        }, correlationId: string.Empty));

        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().NotContain(x => x.EventData.Is(ScriptDomainFactCommitted.Descriptor));
        var outcomeStateEvent = persisted.Single(x => x.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor));
        var outcome = outcomeStateEvent.EventData
            .Unpack<ScriptRunOutcomeRecordedEvent>();
        outcome.ScriptRunId.Should().Be("run-no-facts");
        outcome.Status.Should().Be(ScriptRunOutcomeStatus.Succeeded);
        outcome.CommandId.Should().Be("command-no-facts");
        outcome.CorrelationId.Should().Be("correlation-no-facts");
        outcome.CommittedFactCount.Should().Be(0);
        outcome.StateVersion.Should().Be(outcomeStateEvent.Version);
        outcome.Result.Should().BeNull();
        harness.Agent.State.RunOutcomes["run-no-facts"].Outcome.Status.Should().Be(ScriptRunOutcomeStatus.Succeeded);
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRecordFailedOutcome_WhenRunDispatchFails()
    {
        var harness = CreateHarness(
            dispatcher: new ThrowingDispatcher(new InvalidOperationException("script contract rejected")));
        await BindAsync(harness.Agent);

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-failed",
            CommandId = "command-failed",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-failed",
                Value = "hello",
            }),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script contract rejected*");

        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        var outcomeStateEvent = persisted.Single(x => x.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor));
        var outcome = outcomeStateEvent.EventData
            .Unpack<ScriptRunOutcomeRecordedEvent>();
        outcome.ScriptRunId.Should().Be("run-failed");
        outcome.Status.Should().Be(ScriptRunOutcomeStatus.Failed);
        outcome.CommandId.Should().Be("command-failed");
        outcome.Error.Should().Be("script contract rejected");
        outcome.CommittedFactCount.Should().Be(0);
        outcome.StateVersion.Should().Be(outcomeStateEvent.Version);
        harness.Agent.State.RunOutcomes["run-failed"].Outcome.Status.Should().Be(ScriptRunOutcomeStatus.Failed);
    }

    [Theory]
    [InlineData("definition-2", "scope-1", "bound to definition `definition-1`", "scope-1")]
    [InlineData("definition-1", "scope-2", "bound to scope `scope-1`", "scope-2")]
    public async Task HandleEnvelopeAsync_ShouldRecordFailedOutcome_WhenRunTargetValidationFails(
        string targetDefinitionActorId,
        string targetScopeId,
        string expectedError,
        string expectedOutcomeScopeId)
    {
        var harness = CreateHarness();
        await BindAsync(harness.Agent, "scope-1");

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(
            new RunScriptRequestedEvent
            {
                RunId = "run-validation-failed",
                CommandId = "command-validation-failed",
                CorrelationId = "correlation-validation-failed",
                ScopeId = targetScopeId,
                DefinitionActorId = targetDefinitionActorId,
                ScriptRevision = "rev-1",
                RequestedEventType = "integration.requested",
                InputPayload = Any.Pack(new SimpleTextCommand
                {
                    CommandId = "command-validation-failed",
                    Value = "hello",
                }),
            },
            correlationId: string.Empty));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedError}*");

        var persisted = await harness.EventStore.GetEventsAsync(harness.Agent.Id, ct: CancellationToken.None);
        persisted.Should().NotContain(x => x.EventData.Is(ScriptDomainFactCommitted.Descriptor));
        var outcomeStateEvent = persisted
            .Where(x => x.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor))
            .Should()
            .ContainSingle()
            .Subject;
        var outcome = outcomeStateEvent.EventData.Unpack<ScriptRunOutcomeRecordedEvent>();
        outcome.ScriptRunId.Should().Be("run-validation-failed");
        outcome.CommandId.Should().Be("command-validation-failed");
        outcome.CorrelationId.Should().Be("correlation-validation-failed");
        outcome.ScopeId.Should().Be(expectedOutcomeScopeId);
        outcome.Status.Should().Be(ScriptRunOutcomeStatus.Failed);
        outcome.CommittedFactCount.Should().Be(0);
        outcome.Error.Should().Contain(expectedError);
        outcome.StateVersion.Should().Be(outcomeStateEvent.Version);
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
            ]));
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
        harness.Agent.State.LastEventId.Should().Be(ScriptSources.UppercaseEventTypeUrl);
        harness.Agent.State.StateRoot.Should().NotBeNull();
        harness.Agent.State.StateRoot!.Unpack<SimpleTextState>().Value.Should().Be("FALLBACK");
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
            ]));
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
        IScriptBehaviorRuntimeCapabilityFactory? capabilityFactory = null)
    {
        var eventStore = new InMemoryEventStore();
        var agent = new ScriptBehaviorGAgent(dispatcher ?? new NoOpDispatcher(), capabilityFactory ?? new NoOpCapabilityFactory(), CreateArtifactResolver(), new ProtobufMessageCodec())
        {
            EventPublisher = new RecordingEventPublisher(),
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(eventStore),
            Services = new StaticServiceProvider(new StubCallbackScheduler()),
        };

        return new BranchCoverageHarness(agent, eventStore);
    }

    private static IScriptBehaviorArtifactResolver CreateArtifactResolver()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        return new CachedScriptBehaviorArtifactResolver(compiler);
    }

    private static BindScriptBehaviorRequestedEvent CreateBindRequest(string scopeId = "") =>
        new()
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
            ScopeId = scopeId,
        };

    private static async Task BindAsync(ScriptBehaviorGAgent agent, string scopeId = "") =>
        await agent.HandleEnvelopeAsync(BuildEnvelope(CreateBindRequest(scopeId)));

    private static EventEnvelope BuildEnvelope(IMessage payload, string? correlationId = "corr-1") =>
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
        };

    private sealed record BranchCoverageHarness(
        ScriptBehaviorGAgent Agent,
        InMemoryEventStore EventStore);

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

    private sealed class ThrowingDispatcher(Exception exception) : IScriptBehaviorDispatcher
    {
        public Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
            ScriptBehaviorDispatchRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromException<IReadOnlyList<ScriptDomainFactCommitted>>(exception);
        }
    }

    private sealed class NoOpCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
    {
        public IScriptBehaviorRuntimeCapabilities Create(
            ScriptBehaviorRuntimeCapabilityContext context,
            Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
            Func<string, IMessage, CancellationToken, Task> sendToAsync,
            Func<IMessage, CancellationToken, Task> publishToSelfAsync,
            Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfSignalAsync,
            Func<RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
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
        public Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 0, RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<Aevatar.Scripting.Abstractions.Definitions.ScriptPromotionDecision> ProposeScriptEvolutionAsync(Aevatar.Scripting.Abstractions.Definitions.ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new Aevatar.Scripting.Abstractions.Definitions.ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new Aevatar.Scripting.Abstractions.Definitions.ScriptEvolutionValidationReport(false, [])));
        public Task<ScriptDefinitionUpsertResult> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) =>
            Task.FromResult(new ScriptDefinitionUpsertResult(definitionActorId ?? string.Empty, new ScriptDefinitionBindingSpec { ScriptId = scriptId, Revision = scriptRevision, SourceHash = sourceHash }));
        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, ScriptDefinitionBindingSpec definitionSnapshot, CancellationToken ct) =>
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
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
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
            _ = evt;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
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
}
