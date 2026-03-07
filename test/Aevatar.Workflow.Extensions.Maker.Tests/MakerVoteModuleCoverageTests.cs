using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Maker.Modules;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Extensions.Maker.Tests;

public sealed class MakerVoteModuleCoverageTests
{
    [Fact]
    public async Task HandleAsync_NonMakerVoteStep_ShouldIgnore()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            new StepRequestEvent
            {
                StepId = "vote-ignored",
                StepType = "vote",
                Input = "A\n---\nB",
            },
            ctx.Context,
            CancellationToken.None);

        ctx.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_NoCandidates_ShouldPublishFailureWithDefaultMetadata()
    {
        var module = new MakerVoteModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            new StepRequestEvent
            {
                StepId = "vote-empty",
                StepType = "maker_vote",
                RunId = "run-vote-empty",
                Input = string.Empty,
            },
            ctx.Context,
            CancellationToken.None);

        var completed = ctx.Events.Should().ContainSingle().Subject.Should().BeOfType<StepCompletedEvent>().Subject;
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
            new StepRequestEvent
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
            },
            ctx.Context,
            CancellationToken.None);

        var completed = ctx.Events.Should().ContainSingle().Subject.Should().BeOfType<StepCompletedEvent>().Subject;
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
            new StepRequestEvent
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
            },
            ctx.Context,
            CancellationToken.None);

        var completed = ctx.Events.Should().ContainSingle().Subject.Should().BeOfType<StepCompletedEvent>().Subject;
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
            new StepRequestEvent
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
            },
            ctx.Context,
            CancellationToken.None);

        var completed = ctx.Events.Should().ContainSingle().Subject.Should().BeOfType<StepCompletedEvent>().Subject;
        completed.RunId.Should().Be("run-vote-defaults");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("short");
        completed.Metadata["maker_vote.k"].Should().Be("1");
        completed.Metadata["maker_vote.max_response_length"].Should().Be("2200");
        completed.Metadata["maker_vote.red_flagged"].Should().Be("1");
        completed.Metadata["maker_vote.valid_candidates"].Should().Be("1");
        completed.Metadata["maker_vote.used_majority_fallback"].Should().Be("False");
    }

    private static PrimitiveRecorder CreateContext() =>
        new(
            new ServiceCollection().BuildServiceProvider(),
            new StubAgent("maker-module-test"),
            NullLogger.Instance);

    private sealed class PrimitiveRecorder
    {
        public PrimitiveRecorder(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Context = new WorkflowPrimitiveExecutionContext(
                agent.Id,
                services,
                logger,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                (evt, _, _) =>
                {
                    Events.Add(evt);
                    return Task.CompletedTask;
                });
        }

        public WorkflowPrimitiveExecutionContext Context { get; }

        public List<IMessage> Events { get; } = [];
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
