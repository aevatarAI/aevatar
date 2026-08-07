using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ParallelFanOutVoteAgreementTests
{
    [Fact]
    public async Task HandleAsync_WhenVoteConfigured_ShouldStoreTypedRuleAndDispatchTypedCandidates()
    {
        var module = new ParallelFanOutModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-1",
                Input = "work",
                Parameters =
                {
                    ["workers"] = "maker,checker",
                    ["vote_step_type"] = "vote_consensus",
                    ["vote_param_rule_mode"] = "label_count_constraints",
                    ["vote_param_label_source"] = "annotation",
                    ["vote_param_label_field"] = "vote",
                    ["vote_param_min_approve_count"] = "1",
                    ["vote_param_max_reject_count"] = "0",
                    ["vote_param_on_agreed"] = "accepted",
                },
            }),
            ctx,
            CancellationToken.None);

        var stored = ctx.LoadState<ParallelFanOutModuleState>("parallel_fanout");
        stored.Parents["fanout"].VoteConfig.StepType.Should().Be("vote");
        stored.Parents["fanout"].VoteConfig.VoteRule.Mode.Should().Be(AgreementRuleMode.LabelCountConstraints);
        stored.Parents["fanout"].VoteConfig.VoteRule.LabelSource.Should().Be(AgreementCandidateLabelSource.Annotation);
        stored.Parents["fanout"].VoteConfig.VoteRule.CountConstraints.Should().HaveCount(2);

        ctx.Published.Clear();
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "fanout_sub_0",
                RunId = "run-1",
                Success = true,
                Output = "A",
                WorkerId = "maker",
                BranchKey = "done",
                Annotations = { ["vote"] = "approve", ["trace"] = "one" },
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "fanout_sub_1",
                RunId = "run-1",
                Success = true,
                Output = "B",
                WorkerId = "checker",
                Annotations = { ["vote"] = "approve" },
            }),
            ctx,
            CancellationToken.None);

        var voteRequest = ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single(x => x.StepId == "fanout_vote");
        voteRequest.StepType.Should().Be("vote");
        voteRequest.Input.Should().Be("A\n---\nB");
        voteRequest.StepParameters.VoteAgreementRule.Mode.Should().Be(AgreementRuleMode.LabelCountConstraints);
        voteRequest.StepParameters.VoteAgreementCandidates.Candidates.Should().HaveCount(2);
        voteRequest.StepParameters.VoteAgreementCandidates.Candidates[0].WorkerId.Should().Be("maker");
        voteRequest.StepParameters.VoteAgreementCandidates.Candidates[0].Annotations["trace"].Should().Be("one");
        voteRequest.StepParameters.VoteAgreementCandidates.Candidates[0].BranchKey.Should().Be("done");
    }

    [Fact]
    public async Task HandleAsync_WhenVoteNotConfigured_ShouldKeepMergedOutputPath()
    {
        var module = new ParallelFanOutModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-1",
                Input = "work",
                Parameters = { ["workers"] = "a,b" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_0", RunId = "run-1", Success = true, Output = "A" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_1", RunId = "run-1", Success = false, Error = "bad", Output = "B" }), ctx, CancellationToken.None);

        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("fanout");
        completed.Success.Should().BeFalse();
        completed.Output.Should().Be("A\n---\nB");
        completed.Annotations["parallel.used_vote"].Should().Be("false");
    }

    [Fact]
    public async Task HandleAsync_WhenVoteRuleInvalid_ShouldPublishParentFailureWithoutChildDispatch()
    {
        var module = new ParallelFanOutModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-1",
                Input = "work",
                Parameters =
                {
                    ["workers"] = "a,b",
                    ["vote_step_type"] = "vote",
                    ["vote_param_rule_mode"] = "quorum",
                    ["vote_param_quorum_count"] = "0",
                },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("fanout");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("quorum_count");
        ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().Should().BeEmpty();
        ctx.LoadState<ParallelFanOutModuleState>("parallel_fanout").Parents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenMakerVoteConfigured_ShouldKeepDelimiterHandoff()
    {
        var module = new ParallelFanOutModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-1",
                Input = "work",
                Parameters =
                {
                    ["workers"] = "a,b",
                    ["vote_step_type"] = "maker_vote",
                    ["vote_param_k"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_0", RunId = "run-1", Success = true, Output = "A" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_1", RunId = "run-1", Success = true, Output = "B" }), ctx, CancellationToken.None);

        var voteRequest = ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single(x => x.StepId == "fanout_vote");
        voteRequest.StepType.Should().Be("maker_vote");
        voteRequest.Input.Should().Be("A\n---\nB");
        voteRequest.Parameters["k"].Should().Be("2");
        voteRequest.StepParameters.VoteAgreementCandidates.Candidates.Should().HaveCount(2);
        voteRequest.StepParameters.VoteAgreementRule.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenVoteCompletes_ShouldPublishParentWithDecisionAnnotations()
    {
        var module = new ParallelFanOutModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-1",
                Input = "work",
                Parameters =
                {
                    ["workers"] = "a",
                    ["vote_step_type"] = "vote",
                },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_0", RunId = "run-1", Success = true, Output = "A" }), ctx, CancellationToken.None);
        ctx.Published.Clear();
        var decision = new VoteAgreementDecision
        {
            Kind = AgreementDecisionKind.Agreed,
            BranchKey = "agreed",
            WinnerCandidateId = "winner",
            Output = "A",
            Reason = "majority approved",
        };
        decision.LabelCounts["approve"] = 1;

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "fanout_vote",
                RunId = "run-1",
                Success = true,
                Output = "A",
                BranchKey = "agreed",
                VoteAgreementDecision = decision,
                Annotations = { ["vote.agreement.kind"] = "Agreed" },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("fanout");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("A");
        completed.BranchKey.Should().Be("agreed");
        completed.VoteAgreementDecision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        completed.VoteAgreementDecision.BranchKey.Should().Be("agreed");
        completed.VoteAgreementDecision.WinnerCandidateId.Should().Be("winner");
        completed.VoteAgreementDecision.LabelCounts["approve"].Should().Be(1);
        completed.Annotations["parallel.used_vote"].Should().Be("true");
        completed.Annotations["vote.agreement.kind"].Should().Be("Agreed");
    }

    [Fact]
    public async Task HandleAsync_WithDeterministicSubStep_ShouldExpandParametersAndMergeInDispatchOrder()
    {
        var module = new ParallelFanOutModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "fanout",
                StepType = "parallel",
                RunId = "run-1",
                Input = "synthetic",
                Parameters =
                {
                    ["parallel_count"] = "3",
                    ["sub_step_type"] = "assign",
                    ["sub_param_target"] = "worker_${index}",
                    ["sub_param_value"] = "result-${index}",
                },
            }),
            ctx,
            CancellationToken.None);

        var requests = ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().ToArray();
        requests.Should().HaveCount(3);
        requests.Select(x => x.StepType).Should().OnlyContain(x => x == "assign");
        requests.Select(x => x.Parameters["target"]).Should().Equal("worker_0", "worker_1", "worker_2");
        requests.Select(x => x.Parameters["value"]).Should().Equal("result-0", "result-1", "result-2");
        requests.Select(x => x.TargetRole).Should().OnlyContain(x => string.IsNullOrEmpty(x));

        ctx.Published.Clear();
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_2", RunId = "run-1", Success = true, Output = "result-2" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_0", RunId = "run-1", Success = true, Output = "result-0" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "fanout_sub_1", RunId = "run-1", Success = true, Output = "result-1" }), ctx, CancellationToken.None);

        var completed = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("fanout");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("result-0\n---\nresult-1\n---\nresult-2");
    }

    private static EventEnvelope Envelope(IMessage message) =>
        new()
        {
            Payload = Any.Pack(message),
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services => EmptyServiceProvider.Instance;

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var state) || !state.Is(new TState().Descriptor))
                return new TState();

            return state.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(System.Type serviceType) => null;
    }
}
