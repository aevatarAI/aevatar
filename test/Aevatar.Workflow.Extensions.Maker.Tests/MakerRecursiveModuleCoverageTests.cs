using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Extensions.Maker.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public class MakerRecursiveModuleCoverageTests
{
    [Fact]
    public async Task HandleAsync_ShouldPropagateRunIdToInternalAndFinalEvents()
    {
        var module = new MakerRecursiveModule();
        var ctx = CreateContext();
        const string runId = "run-maker-recursive-propagation";

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "solve_root",
                StepType = "maker_recursive",
                RunId = runId,
                Input = "ROOT_TASK",
            }),
            ctx,
            CancellationToken.None);

        var atomicRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        atomicRequest.StepId.Should().Be("solve_root_atomic_vote");
        atomicRequest.RunId.Should().Be(runId);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = atomicRequest.StepId,
                RunId = runId,
                Success = true,
                Output = "ATOMIC",
            }),
            ctx,
            CancellationToken.None);

        var leafRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        leafRequest.StepId.Should().Be("solve_root_leaf_vote");
        leafRequest.RunId.Should().Be(runId);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = leafRequest.StepId,
                RunId = runId,
                Success = true,
                Output = "LEAF_ANSWER",
            }),
            ctx,
            CancellationToken.None);

        var final = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        final.StepId.Should().Be("solve_root");
        final.RunId.Should().Be(runId);
        final.Success.Should().BeTrue();
        final.Output.Should().Be("LEAF_ANSWER");
        final.Metadata["maker.recursive"].Should().Be("true");
        final.Metadata["maker.stage"].Should().Be("leaf");
    }

    [Fact]
    public async Task HandleAsync_WhenSameStepIdAcrossRuns_ShouldKeepRunScopedIsolation()
    {
        var module = new MakerRecursiveModule();
        var ctx = CreateContext();
        const string sharedStepId = "solve_shared";
        const string runA = "run-maker-a";
        const string runB = "run-maker-b";

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = sharedStepId,
                StepType = "maker_recursive",
                RunId = runA,
                Input = "TASK_A",
            }),
            ctx,
            CancellationToken.None);
        var atomicA = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        atomicA.StepId.Should().Be("solve_shared_atomic_vote");
        atomicA.RunId.Should().Be(runA);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = sharedStepId,
                StepType = "maker_recursive",
                RunId = runB,
                Input = "TASK_B",
            }),
            ctx,
            CancellationToken.None);
        var atomicB = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        atomicB.StepId.Should().Be("solve_shared_atomic_vote");
        atomicB.RunId.Should().Be(runB);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = atomicB.StepId,
                RunId = runB,
                Success = true,
                Output = "ATOMIC",
            }),
            ctx,
            CancellationToken.None);
        var leafB = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        leafB.StepId.Should().Be("solve_shared_leaf_vote");
        leafB.RunId.Should().Be(runB);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = atomicA.StepId,
                RunId = runA,
                Success = true,
                Output = "ATOMIC",
            }),
            ctx,
            CancellationToken.None);
        var leafA = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        leafA.StepId.Should().Be("solve_shared_leaf_vote");
        leafA.RunId.Should().Be(runA);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = leafB.StepId,
                RunId = runB,
                Success = true,
                Output = "ANSWER_B",
            }),
            ctx,
            CancellationToken.None);
        var finalB = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        finalB.StepId.Should().Be(sharedStepId);
        finalB.RunId.Should().Be(runB);
        finalB.Output.Should().Be("ANSWER_B");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = leafA.StepId,
                RunId = runA,
                Success = true,
                Output = "ANSWER_A",
            }),
            ctx,
            CancellationToken.None);
        var finalA = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        finalA.StepId.Should().Be(sharedStepId);
        finalA.RunId.Should().Be(runA);
        finalA.Output.Should().Be("ANSWER_A");
    }

    private static RecordingEventHandlerContext CreateContext()
    {
        return new RecordingEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new StubAgent("maker-recursive-test"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test-publisher",
            Direction = EventDirection.Self,
        };
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            _ = dueTime;
            _ = evt;
            _ = metadata;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = callbackId;
            _ = dueTime;
            _ = period;
            _ = evt;
            _ = metadata;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }

        public Task CancelDurableCallbackAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = ct;
            throw new NotSupportedException("This test context does not support scheduling.");
        }
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
