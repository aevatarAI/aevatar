using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Extensions.Maker.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public class MakerVoteModuleCoverageTests
{
    [Fact]
    public void CanHandle_ShouldMatchOnlyStepRequestPayload()
    {
        var module = new MakerVoteModule();

        module.CanHandle(Envelope(new StepRequestEvent { StepType = "maker_vote", StepId = "s1" })).Should().BeTrue();
        module.CanHandle(Envelope(new ChatResponseEvent { Content = "x" })).Should().BeFalse();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_NonMakerVoteStep_ShouldIgnore()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-ignored",
                StepType = "vote",
                Input = "A\n---\nB",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_NoCandidates_ShouldPublishFailureWithDefaultMetadata()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-empty",
                StepType = "maker_vote",
                RunId = "run-vote-empty",
                Input = "",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.RunId.Should().Be("run-vote-empty");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("No candidates provided");
        completed.Metadata["maker_vote.total_candidates"].Should().Be("0");
        completed.Metadata["maker_vote.red_flagged"].Should().Be("0");
        completed.Metadata["maker_vote.valid_candidates"].Should().Be("0");
        completed.Metadata["maker_vote.k"].Should().Be("1");
        completed.Metadata["maker_vote.max_response_length"].Should().Be("2200");
    }

    [Fact]
    public async Task HandleAsync_AllCandidatesFlagged_ShouldPublishFailure()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-flagged",
                StepType = "maker_vote",
                RunId = "run-vote-flagged",
                Input = "abcd\n---\nefgh",
                Parameters =
                {
                    ["max_response_length"] = "3",
                    ["k"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.RunId.Should().Be("run-vote-flagged");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("red-flagged");
        completed.Metadata["maker_vote.total_candidates"].Should().Be("2");
        completed.Metadata["maker_vote.red_flagged"].Should().Be("2");
        completed.Metadata["maker_vote.valid_candidates"].Should().Be("0");
        completed.Metadata["maker_vote.k"].Should().Be("2");
        completed.Metadata["maker_vote.max_response_length"].Should().Be("3");
    }

    [Fact]
    public async Task HandleAsync_ShouldPickWinnerAndMarkMajorityFallback()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-ok",
                StepType = "maker_vote",
                RunId = "run-vote-ok",
                Input = "A\n---\nB\n---\nB",
                Parameters =
                {
                    ["k"] = "2",
                    ["max_response_length"] = "100",
                },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.RunId.Should().Be("run-vote-ok");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("B");
        completed.Metadata["maker_vote.total_candidates"].Should().Be("3");
        completed.Metadata["maker_vote.red_flagged"].Should().Be("0");
        completed.Metadata["maker_vote.valid_candidates"].Should().Be("3");
        completed.Metadata["maker_vote.top_votes"].Should().Be("2");
        completed.Metadata["maker_vote.runner_up_votes"].Should().Be("1");
        completed.Metadata["maker_vote.used_majority_fallback"].Should().Be("True");
    }

    [Fact]
    public async Task HandleAsync_InvalidParameters_ShouldFallbackToDefaults()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();
        var longCandidate = new string('X', 2300);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-defaults",
                StepType = "maker_vote",
                RunId = "run-vote-defaults",
                Input = $"short\n---\n{longCandidate}",
                Parameters =
                {
                    ["k"] = "invalid",
                    ["max_response_length"] = "invalid",
                },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.RunId.Should().Be("run-vote-defaults");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("short");
        completed.Metadata["maker_vote.k"].Should().Be("1");
        completed.Metadata["maker_vote.max_response_length"].Should().Be("2200");
        completed.Metadata["maker_vote.red_flagged"].Should().Be("1");
        completed.Metadata["maker_vote.valid_candidates"].Should().Be("1");
        completed.Metadata["maker_vote.used_majority_fallback"].Should().Be("False");
    }

    private static RecordingWorkflowExecutionContext CreateContext()
    {
        return new RecordingWorkflowExecutionContext(
            new ServiceCollection().BuildServiceProvider(),
            new StubAgent("maker-module-test"),
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

    private sealed class RecordingWorkflowExecutionContext : IWorkflowExecutionContext
    {
        public RecordingWorkflowExecutionContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            AgentId = agent.Id;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId { get; }
        public string RunId => AgentId;
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            _ = scopeKey;
            return new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new()
        {
            _ = scopeKeyPrefix;
            return [];
        }

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            _ = scopeKey;
            _ = state;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _ = scopeKey;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, EventDirection.Self, ct);
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
