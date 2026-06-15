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

public sealed class VoteAgreementModuleTests
{
    [Fact]
    public async Task HandleAsync_WhenNoCandidates_ShouldFailClearly()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new VoteAgreementModule();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-empty",
                StepType = "vote",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("at least one candidate");
    }

    [Fact]
    public async Task HandleAsync_WhenTypedCandidatesAgree_ShouldPublishAgreedBranch()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new VoteAgreementModule();
        var request = new StepRequestEvent
        {
            StepId = "vote-1",
            StepType = "vote",
            RunId = "run-1",
            StepParameters = new WorkflowStepParameters(),
        };
        request.StepParameters.VoteAgreementRule = new VoteAgreementRule
        {
            Mode = AgreementRuleMode.Majority,
            OnAgreed = "accepted",
        };
        request.StepParameters.VoteAgreementCandidates = Candidates(
            Candidate("a", true, "first"),
            Candidate("b", true, "second"),
            Candidate("c", false, "third"));

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.BranchKey.Should().Be("accepted");
        completed.Output.Should().Be("first");
        completed.VoteAgreementDecision.Kind.Should().Be(AgreementDecisionKind.Agreed);
        completed.Annotations["vote.agreement.label_count.approve"].Should().Be("2");
    }

    [Fact]
    public async Task HandleAsync_WhenRejectConstraintFails_ShouldPublishRejectedBranch()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new VoteAgreementModule();
        var request = new StepRequestEvent
        {
            StepId = "vote-constraints",
            StepType = "vote",
            StepParameters = new WorkflowStepParameters(),
        };
        request.StepParameters.VoteAgreementRule = new VoteAgreementRule
        {
            Mode = AgreementRuleMode.LabelCountConstraints,
            OnRejected = "needs_retry",
            CountConstraints =
            {
                new AgreementCountConstraint { Label = AgreementVoteLabel.Reject, MaxCount = 0 },
            },
        };
        request.StepParameters.VoteAgreementCandidates = Candidates(
            Candidate("a", true, "ok"),
            Candidate("b", false, "bad"));

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.BranchKey.Should().Be("needs_retry");
        completed.VoteAgreementDecision.Kind.Should().Be(AgreementDecisionKind.Rejected);
        completed.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenLegacyDelimitedInput_ShouldNotPickLongestByDefault()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new VoteAgreementModule();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-legacy",
                StepType = "vote",
                Input = "short\n---\nvery very long candidate\n---\nmid",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("short");
        completed.Output.Should().NotBe("very very long candidate");
    }

    [Fact]
    public async Task HandleAsync_WhenRuleMalformed_ShouldFailWithoutSideEffects()
    {
        var ctx = new RecordingWorkflowContext();
        var module = new VoteAgreementModule();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "vote-bad-rule",
                StepType = "vote",
                Input = "candidate",
                Parameters =
                {
                    ["rule_mode"] = "quorum",
                    ["quorum_ratio"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StepCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("quorum_ratio");
    }

    private static EventEnvelope Envelope(IMessage message) =>
        new()
        {
            Payload = Any.Pack(message),
        };

    private static VoteAgreementCandidateSet Candidates(params VoteAgreementCandidate[] candidates)
    {
        var set = new VoteAgreementCandidateSet();
        set.Candidates.Add(candidates);
        return set;
    }

    private static VoteAgreementCandidate Candidate(string id, bool success, string output) =>
        new()
        {
            CandidateId = id,
            WorkerId = id,
            Success = success,
            Output = output,
        };

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public IServiceProvider Services => EmptyServiceProvider.Instance;

        public ILogger Logger { get; } = NullLogger.Instance;

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() =>
            new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> =>
            Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) =>
            Task.CompletedTask;

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
