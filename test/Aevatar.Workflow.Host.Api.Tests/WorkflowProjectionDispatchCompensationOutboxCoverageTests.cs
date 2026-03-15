using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ActorProjectionDispatchCompensationOutboxCoverageTests
{
    [Fact]
    public async Task EnqueueAsync_WhenExistingActorAndTypeMatches_ShouldDispatchCompensationEvent()
    {
        var actor = new RecordingActor("projection.compensation.outbox:workflow");
        var runtime = new RecordingActorRuntime();
        runtime.EnqueueGetResult(actor);
        var verifier = new RecordingAgentTypeVerifier { Result = true };
        var outbox = new ActorProjectionDispatchCompensationOutbox(runtime, runtime, verifier);
        var evt = new ProjectionCompensationEnqueuedEvent
        {
            RecordId = "record-1",
            Operation = "upsert",
            FailedStore = "Graph",
            ReadModel = WorkflowExecutionReportArtifactPayloadMapper.Pack(new WorkflowExecutionReport
            {
                Id = "record-1",
                RootActorId = "record-1",
                CommandId = "cmd-1",
                WorkflowName = "workflow",
            }),
            ReadModelType = "type",
            Key = "key",
        };

        await outbox.EnqueueAsync(evt);

        runtime.GetCalls.Should().Be(1);
        runtime.CreateCalls.Should().Be(0);
        verifier.Calls.Should().ContainSingle(x =>
            x.ActorId == "projection.compensation.outbox:workflow" &&
            x.ExpectedType == typeof(WorkflowProjectionDispatchCompensationOutboxGAgent));

        actor.HandledEnvelopes.Should().ContainSingle();
        var envelope = actor.HandledEnvelopes.Single();
        envelope.Propagation!.CorrelationId.Should().Be("record-1");
        envelope.Route!.PublisherActorId.Should().Be("projection.compensation.outbox");
        envelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        envelope.Payload.Unpack<ProjectionCompensationEnqueuedEvent>().RecordId.Should().Be("record-1");
    }

    [Fact]
    public async Task TriggerReplayAsync_WhenActorMissing_ShouldCreateAndDispatchReplayEvent()
    {
        var actor = new RecordingActor("projection.compensation.outbox:workflow");
        var runtime = new RecordingActorRuntime
        {
            CreatedActor = actor,
        };
        runtime.EnqueueGetResult(null);
        var verifier = new RecordingAgentTypeVerifier { Result = true };
        var outbox = new ActorProjectionDispatchCompensationOutbox(runtime, runtime, verifier);

        await outbox.TriggerReplayAsync(17);

        runtime.CreateCalls.Should().Be(1);
        runtime.LastCreatedId.Should().Be("projection.compensation.outbox:workflow");
        actor.HandledEnvelopes.Should().ContainSingle();
        var replay = actor.HandledEnvelopes.Single().Payload.Unpack<ProjectionCompensationTriggerReplayEvent>();
        replay.BatchSize.Should().Be(17);
        actor.HandledEnvelopes.Single().Propagation!.CorrelationId.Should().Be("replay");
    }

    [Fact]
    public async Task TriggerReplayAsync_WhenCreateRaces_ShouldUseRacedActorFromSecondLookup()
    {
        var racedActor = new RecordingActor("projection.compensation.outbox:workflow");
        var runtime = new RecordingActorRuntime
        {
            CreateException = new InvalidOperationException("create raced"),
        };
        runtime.EnqueueGetResult(null);
        runtime.EnqueueGetResult(racedActor);

        var outbox = new ActorProjectionDispatchCompensationOutbox(
            runtime,
            runtime,
            new RecordingAgentTypeVerifier { Result = true });

        await outbox.TriggerReplayAsync(9);

        runtime.GetCalls.Should().Be(2);
        runtime.CreateCalls.Should().Be(1);
        racedActor.HandledEnvelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task TriggerReplayAsync_WhenActorTypeMismatch_ShouldThrow()
    {
        var actor = new RecordingActor("projection.compensation.outbox:workflow");
        var runtime = new RecordingActorRuntime();
        runtime.EnqueueGetResult(actor);
        var outbox = new ActorProjectionDispatchCompensationOutbox(
            runtime,
            runtime,
            new RecordingAgentTypeVerifier { Result = false });

        Func<Task> act = () => outbox.TriggerReplayAsync(1);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*projection.compensation.outbox:workflow*");
        actor.HandledEnvelopes.Should().BeEmpty();
    }

    [Fact]
    public async Task TriggerReplayAsync_WhenCreateFailsAndNoRacedActor_ShouldRethrow()
    {
        var runtime = new RecordingActorRuntime
        {
            CreateException = new InvalidOperationException("create failed"),
        };
        runtime.EnqueueGetResult(null);
        runtime.EnqueueGetResult(null);
        var outbox = new ActorProjectionDispatchCompensationOutbox(
            runtime,
            runtime,
            new RecordingAgentTypeVerifier { Result = true });

        Func<Task> act = () => outbox.TriggerReplayAsync(3);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("create failed");
    }

    [Fact]
    public async Task EnqueueAsync_WhenEventIsNull_ShouldThrowArgumentNullException()
    {
        var runtime = new RecordingActorRuntime();
        var outbox = new ActorProjectionDispatchCompensationOutbox(
            runtime,
            runtime,
            new RecordingAgentTypeVerifier { Result = true });

        Func<Task> act = () => outbox.EnqueueAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private readonly Queue<IActor?> _getResults = new();
        private readonly Dictionary<string, IActor> _knownActors = new(StringComparer.Ordinal);

        public IActor? CreatedActor { get; init; }

        public Exception? CreateException { get; init; }

        public int GetCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public string? LastCreatedId { get; private set; }

        public void EnqueueGetResult(IActor? actor) => _getResults.Enqueue(actor);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreatedId = id;
            if (CreateException != null)
                throw CreateException;

            var actor = CreatedActor ?? new RecordingActor(id ?? Guid.NewGuid().ToString("N"));
            _knownActors[actor.Id] = actor;
            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id)
        {
            GetCalls++;
            if (_getResults.Count > 0)
            {
                var queuedActor = _getResults.Dequeue();
                if (queuedActor != null)
                    _knownActors[id] = queuedActor;

                return Task.FromResult(queuedActor);
            }

            return Task.FromResult(_knownActors.TryGetValue(id, out var actor) ? actor : null);
        }

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_knownActors.TryGetValue(actorId, out var actor))
                actor = CreatedActor ?? throw new InvalidOperationException($"Actor {actorId} not found.");

            await actor.HandleEventAsync(envelope, ct);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

    }

    private sealed class RecordingAgentTypeVerifier : IAgentTypeVerifier
    {
        public bool Result { get; init; }

        public List<(string ActorId, System.Type ExpectedType)> Calls { get; } = [];

        public Task<bool> IsExpectedAsync(string actorId, System.Type expectedType, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, expectedType));
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new NullAgent();

        public List<EventEnvelope> HandledEnvelopes { get; } = [];

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            HandledEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NullAgent : IAgent
    {
        public string Id => "null-agent";

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("null");

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
    }
}

public sealed class WorkflowProjectionDispatchCompensationReplayHostedServiceCoverageTests
{
    [Fact]
    public async Task StartAsync_WhenDisabled_ShouldNotStartWorker()
    {
        var outbox = new RecordingReplayOutbox();
        using var service = new WorkflowProjectionDispatchCompensationReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                Enabled = false,
                EnableDispatchCompensationReplay = true,
            });

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        outbox.TriggerCalls.Should().Be(0);
    }

    [Fact]
    public async Task ReplayOnceAsync_ShouldForwardConfiguredBatchSize()
    {
        var outbox = new RecordingReplayOutbox();
        using var service = new WorkflowProjectionDispatchCompensationReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableDispatchCompensationReplay = true,
                DispatchCompensationReplayBatchSize = 23,
            });

        await service.ReplayOnceAsync();

        outbox.TriggerCalls.Should().Be(1);
        outbox.LastBatchSize.Should().Be(23);
    }

    [Fact]
    public async Task ReplayOnceAsync_WhenCanceled_ShouldThrowOperationCanceledException()
    {
        var outbox = new RecordingReplayOutbox();
        using var service = new WorkflowProjectionDispatchCompensationReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableDispatchCompensationReplay = true,
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => service.ReplayOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        outbox.TriggerCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartAndStopAsync_ShouldInvokeOutboxInBackgroundLoop()
    {
        var firstReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new RecordingReplayOutbox
        {
            TriggerReplayHandler = (_, _) =>
            {
                firstReplay.TrySetResult();
                return Task.CompletedTask;
            },
        };
        using var service = new WorkflowProjectionDispatchCompensationReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableDispatchCompensationReplay = true,
                DispatchCompensationReplayPollIntervalMs = 1,
            });

        await service.StartAsync(CancellationToken.None);
        await firstReplay.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        outbox.TriggerCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task StopAsync_WhenCallerCancellationWins_ShouldThrowOperationCanceledException()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new RecordingReplayOutbox
        {
            TriggerReplayHandler = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
        };
        using var service = new WorkflowProjectionDispatchCompensationReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableDispatchCompensationReplay = true,
                DispatchCompensationReplayPollIntervalMs = 1,
            });

        await service.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Func<Task> act = () => service.StopAsync(canceled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        release.TrySetResult();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAndStopAsync_WhenReplayThrows_ShouldKeepWorkerAliveUntilStopped()
    {
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new RecordingReplayOutbox
        {
            TriggerReplayHandler = (_, _) =>
            {
                firstFailure.TrySetResult();
                throw new InvalidOperationException("boom");
            },
        };
        using var service = new WorkflowProjectionDispatchCompensationReplayHostedService(
            outbox,
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableDispatchCompensationReplay = true,
                DispatchCompensationReplayPollIntervalMs = 1,
            });

        await service.StartAsync(CancellationToken.None);
        await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        outbox.TriggerCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed class RecordingReplayOutbox : IProjectionDispatchCompensationOutbox
    {
        public Func<ProjectionCompensationEnqueuedEvent, CancellationToken, Task>? EnqueueHandler { get; init; }

        public Func<int, CancellationToken, Task>? TriggerReplayHandler { get; init; }

        public int TriggerCalls { get; private set; }

        public int LastBatchSize { get; private set; }

        public Task EnqueueAsync(ProjectionCompensationEnqueuedEvent evt, CancellationToken ct = default) =>
            EnqueueHandler?.Invoke(evt, ct) ?? Task.CompletedTask;

        public Task TriggerReplayAsync(int batchSize, CancellationToken ct = default)
        {
            TriggerCalls++;
            LastBatchSize = batchSize;
            return TriggerReplayHandler?.Invoke(batchSize, ct) ?? Task.CompletedTask;
        }
    }
}
