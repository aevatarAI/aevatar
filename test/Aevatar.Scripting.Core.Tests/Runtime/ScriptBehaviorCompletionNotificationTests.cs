using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptBehaviorCompletionNotificationTests
{
    private const string ActorId = "script-runtime-1";
    private const string CompletionActorId = "service-run:tenant:svc:run-1";

    [Fact]
    public async Task CompletionNotification_ShouldReplayCommittedOutcomeAfterRestart()
    {
        var eventStore = new InMemoryEventStore();
        var failingPublisher = new RecordingEventPublisher { FailSends = true };
        var first = CreateAgent(eventStore, failingPublisher);
        await first.ActivateAsync();
        await BindAsync(first);
        var request = new RunScriptRequestedEvent
        {
            RunId = "run-1",
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            ScopeId = "scope-1",
            CompletionNotificationActorId = CompletionActorId,
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = "cmd-1",
                Value = "hello",
            }),
        };
        var act = () => first.HandleEnvelopeAsync(BuildEnvelope(request, "corr-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated completion notification failure");
        var committed = (await eventStore.GetEventsAsync(ActorId, ct: CancellationToken.None))
            .Single(persisted => persisted.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor))
            .EventData
            .Unpack<ScriptRunOutcomeRecordedEvent>();
        committed.CompletionNotificationActorId.Should().Be(CompletionActorId);
        committed.Status.Should().Be(ScriptRunOutcomeStatus.Succeeded);

        var recoveredPublisher = new RecordingEventPublisher();
        var recovered = CreateAgent(eventStore, recoveredPublisher);

        await recovered.ActivateAsync();

        var sent = recoveredPublisher.Sends.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be(CompletionActorId);
        sent.Options!.Delivery!.DeduplicationOperationId.Should()
            .Be("script-run-terminal:run-1:cmd-1");
        sent.Event.Should().BeOfType<ScriptRunOutcomeRecordedEvent>()
            .Which.Should().BeEquivalentTo(committed);
        recovered.State.LastRunOutcomeNotificationDispatched.Should().BeTrue();
    }

    private static ScriptBehaviorGAgent CreateAgent(
        InMemoryEventStore eventStore,
        IEventPublisher publisher)
    {
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));
        var codec = new ProtobufMessageCodec();
        var agent = new ScriptBehaviorGAgent(
            new ScriptBehaviorDispatcher(artifactResolver, codec),
            new ScriptBehaviorRuntimeCapabilityFactory(
                new RecordingAICapability(),
                new RecordingProposalPort(),
                new RecordingDefinitionCommandPort(),
                new RecordingRuntimeProvisioningPort(),
                new RecordingRuntimeCommandPort(),
                new RecordingCatalogCommandPort()),
            artifactResolver,
            codec)
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(eventStore),
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler>(new StubCallbackScheduler())
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .BuildServiceProvider(),
        };
        var setId = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setId.Should().NotBeNull();
        setId!.Invoke(agent, [ActorId]);
        return agent;
    }

    private static Task BindAsync(ScriptBehaviorGAgent agent) =>
        agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
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
            ScopeId = "scope-1",
        }, "bind-1"));

    private static EventEnvelope BuildEnvelope(IMessage payload, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                "script-completion-notification-test",
                TopologyAudience.Self),
            Propagation = new EnvelopePropagation { CorrelationId = correlationId },
        };

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public bool FailSends { get; init; }

        public List<SentMessage> Sends { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
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
            ct.ThrowIfCancellationRequested();
            Sends.Add(new SentMessage(targetActorId, evt, options));
            if (FailSends)
                throw new InvalidOperationException("simulated completion notification failure");

            return Task.CompletedTask;
        }
    }

    private sealed class StubCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record SentMessage(
        string TargetActorId,
        IMessage Event,
        EventEnvelopePublishOptions? Options);
}
