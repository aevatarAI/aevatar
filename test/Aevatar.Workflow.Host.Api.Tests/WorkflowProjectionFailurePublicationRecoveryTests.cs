using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionFailurePublicationRecoveryTests
{
    private const string ScopeActorId = "projection.durable.scope:workflow-failure-recovery";
    private const string RunActorId =
        "workflow.definition:wf-alpha:run:run-alpha";
    private const int MaxPublicationBytes = 1024 * 1024;
    private const int FirstRetainedSourceVersion = 1467;
    private const int LastRetainedSourceVersion = 1474;

    [Fact]
    public async Task Activation_ShouldReplayRetainedWorkflowStates_AndMaterializeTerminalFailure()
    {
        var eventStore = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        var committedEvents = BuildScopeEvents();
        await eventStore.AppendAsync(ScopeActorId, committedEvents, expectedVersion: 0);
        await publicationStore.InitializeAsync(
            ScopeActorId,
            baselinePublishedVersion: committedEvents.Count - 1);

        var writeDispatcher = new RecordingWorkflowExecutionWriteDispatcher();
        using var services = BuildServices(eventStore, publicationStore, writeDispatcher);
        var committedPublisher = CreateSizeLimitedPublisher(MaxPublicationBytes);
        var selfPublisher = new RecordingEventPublisher(ScopeActorId);
        var agent = CreateAgent(
            services,
            committedPublisher.Instance,
            selfPublisher);

        await agent.ActivateAsync();

        var unredactedPendingPublication = new CommittedStateEventPublished
        {
            StateEvent = committedEvents[^1].Clone(),
            StateRoot = Any.Pack(agent.State),
        };
        unredactedPendingPublication.CalculateSize().Should().BeGreaterThan(MaxPublicationBytes);
        committedPublisher.Proxy.AttemptedSizes.Should().OnlyContain(
            size => size > 0 && size < MaxPublicationBytes);
        committedPublisher.Proxy.Accepted.Should().OnlyContain(
            publication => publication.CalculateSize() < MaxPublicationBytes);
        committedPublisher.Proxy.Accepted.Should().ContainSingle(
            publication => publication.StateEvent.EventData.Is(
                ProjectionScopeDispatchFailedEvent.Descriptor));

        var replayEnvelope = selfPublisher.Published.Should().ContainSingle().Subject;
        replayEnvelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        var replayCommand = replayEnvelope.Payload!.Unpack<ReplayProjectionFailuresCommand>();
        replayCommand.MaxItems.Should().Be(8);
        replayCommand.AutomaticRecovery.Should().BeTrue();

        await agent.HandleEventAsync(replayEnvelope);

        writeDispatcher.ProcessedVersions.Should().Equal(
            Enumerable.Range(
                FirstRetainedSourceVersion,
                LastRetainedSourceVersion - FirstRetainedSourceVersion + 1)
                .Select(static version => (long)version));
        writeDispatcher.Upserts.Should().HaveCount(8);
        var terminal = writeDispatcher.Upserts[^1];
        terminal.StateVersion.Should().Be(LastRetainedSourceVersion);
        terminal.Status.Should().Be("failed");
        terminal.Success.Should().BeFalse();
        terminal.RootActorId.Should().Be(RunActorId);
        agent.State.LastSuccessfulVersion.Should().Be(LastRetainedSourceVersion);
        agent.State.Failures.Should().BeEmpty();
    }

    private static ServiceProvider BuildServices(
        IEventStore eventStore,
        ICommittedStatePublicationStateStore publicationStore,
        RecordingWorkflowExecutionWriteDispatcher writeDispatcher)
    {
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton(publicationStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<ICommittedStatePublicationStateStore>(publicationStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<IActorRuntimeCallbackScheduler, UnsupportedCallbackScheduler>()
            .AddSingleton(new EventSourcingRuntimeOptions())
            .AddSingleton<IProjectionClock>(new FixedProjectionClock(
                new DateTimeOffset(2026, 8, 17, 15, 30, 0, TimeSpan.Zero)))
            .AddSingleton<IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument>>(
                writeDispatcher)
            .AddTransient(
                typeof(IEventSourcingBehaviorFactory<>),
                typeof(DefaultEventSourcingBehaviorFactory<>));

        services.AddProjectionMaterializationRuntimeCore<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<WorkflowExecutionMaterializationContext>>(
            scopeKey => new WorkflowExecutionMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new WorkflowExecutionMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            WorkflowExecutionMaterializationContext,
            WorkflowExecutionCurrentStateProjector>();
        return services.BuildServiceProvider();
    }

    private static ProjectionMaterializationScopeGAgent<WorkflowExecutionMaterializationContext>
        CreateAgent(
            ServiceProvider services,
            object committedPublisher,
            IEventPublisher eventPublisher)
    {
        var agent = new ProjectionMaterializationScopeGAgent<WorkflowExecutionMaterializationContext>
        {
            Services = services,
            EventPublisher = eventPublisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<
                IEventSourcingBehaviorFactory<ProjectionScopeState>>(),
        };
        typeof(GAgentBase)
            .GetProperty(
                "CommittedStateEventPublisher",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, committedPublisher);
        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, ScopeActorId);
        return agent;
    }

    private static IReadOnlyList<StateEvent> BuildScopeEvents()
    {
        var events = new List<IMessage>
        {
            new ProjectionScopeStartedEvent
            {
                RootActorId = RunActorId,
                ProjectionKind = "workflow-execution-materialization",
                Mode = ProjectionScopeMode.DurableMaterialization,
                ActivationGeneration = 1,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 8, 17, 14, 40, 0, TimeSpan.Zero)),
            },
        };

        for (var version = FirstRetainedSourceVersion;
             version <= LastRetainedSourceVersion;
             version++)
        {
            var sourceEnvelope = BuildWorkflowStateEnvelope(version);
            events.Add(new ProjectionScopeDispatchFailedEvent
            {
                FailureId = $"failure-{version}",
                Stage = "projection-execution",
                EventId = $"workflow-event-{version}",
                EventType = version == LastRetainedSourceVersion
                    ? WorkflowCompletedEvent.Descriptor.FullName
                    : WorkflowRunExecutionStartedEvent.Descriptor.FullName,
                SourceVersion = version,
                Reason = $"Injected materialization failure at version {version}.",
                Envelope = sourceEnvelope,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 8, 17, 14, 47, 56, TimeSpan.Zero)
                        .AddMinutes(version - FirstRetainedSourceVersion)),
                SourceActorId = RunActorId,
            });
        }

        return events.Select((payload, index) => new StateEvent
        {
            AgentId = ScopeActorId,
            EventId = $"scope-event-{index + 1}",
            EventType = payload.Descriptor.FullName,
            EventData = Any.Pack(payload),
            Version = index + 1,
            Timestamp = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 14, 40, 0, TimeSpan.Zero)
                    .AddMinutes(index)),
        }).ToArray();
    }

    private static EventEnvelope BuildWorkflowStateEnvelope(int version)
    {
        var terminal = version == LastRetainedSourceVersion;
        IMessage payload = terminal
            ? new WorkflowCompletedEvent
            {
                WorkflowName = "wf-alpha",
                RunId = RunActorId,
                Success = false,
                Error = "terminal workflow failure",
            }
            : new WorkflowRunExecutionStartedEvent
            {
                WorkflowName = "wf-alpha",
                RunId = RunActorId,
            };
        var state = new WorkflowRunState
        {
            RunId = RunActorId,
            WorkflowId = "wf-alpha",
            WorkflowName = "wf-alpha",
            DefinitionActorId = "workflow.definition:wf-alpha",
            Status = terminal ? "failed" : "running",
            FinalError = terminal ? "terminal workflow failure" : string.Empty,
            Input = BuildIncompressibleInput(version),
            StartedAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 14, 0, 0, TimeSpan.Zero)),
            CompletedAtUtc = terminal
                ? Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 8, 17, 15, 0, 0, TimeSpan.Zero))
                : null,
        };

        return new EventEnvelope
        {
            Id = $"workflow-envelope-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 14, 0, 0, TimeSpan.Zero)
                    .AddMinutes(version - FirstRetainedSourceVersion)),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RunActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = RunActorId,
                    EventId = $"workflow-event-{version}",
                    EventType = payload.Descriptor.FullName,
                    EventData = Any.Pack(payload),
                    Version = version,
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private static string BuildIncompressibleInput(int version)
    {
        var bytes = new byte[90 * 1024];
        new Random(1700 + version).NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static PublisherHandle CreateSizeLimitedPublisher(int maxPublicationBytes)
    {
        var publisherInterface = typeof(GAgentBase).Assembly.GetType(
            "Aevatar.Foundation.Core.EventSourcing.ICommittedStateEventPublisher",
            throwOnError: true)!;
        var instance = DispatchProxy.Create(publisherInterface, typeof(SizeLimitedPublisherProxy));
        var proxy = (SizeLimitedPublisherProxy)instance;
        proxy.MaxPublicationBytes = maxPublicationBytes;
        return new PublisherHandle(instance, proxy);
    }

    private sealed record PublisherHandle(object Instance, SizeLimitedPublisherProxy Proxy);

    public class SizeLimitedPublisherProxy : DispatchProxy
    {
        public int MaxPublicationBytes { get; set; }
        public List<int> AttemptedSizes { get; } = [];
        public List<CommittedStateEventPublished> Accepted { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "PublishAsync" ||
                args is not { Length: 5 } ||
                args[0] is not CommittedStateEventPublished published)
            {
                throw new NotSupportedException($"Unexpected publisher call: {targetMethod?.Name}");
            }

            if (args[2] is CancellationToken ct)
                ct.ThrowIfCancellationRequested();
            var size = published.CalculateSize();
            AttemptedSizes.Add(size);
            if (size > MaxPublicationBytes)
            {
                throw new InvalidOperationException(
                    $"Broker rejected publication size {size}; max is {MaxPublicationBytes}.");
            }

            Accepted.Add(published.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventPublisher(string actorId) : IEventPublisher
    {
        public List<EventEnvelope> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, audience),
                Payload = Any.Pack(evt),
            });
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWorkflowExecutionWriteDispatcher
        : IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument>
    {
        public List<WorkflowExecutionCurrentStateDocument> Upserts { get; } = [];
        public IReadOnlyList<long> ProcessedVersions => Upserts.Select(static item => item.StateVersion).ToArray();

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowExecutionCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class UnsupportedCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
