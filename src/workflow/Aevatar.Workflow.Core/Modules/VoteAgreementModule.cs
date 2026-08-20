using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Agreement;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Aevatar.Workflow.Core.Modules;

public sealed class VoteAgreementModule : IEventModule<IWorkflowExecutionContext>
{
    private readonly VoteAgreementRuleEvaluator _evaluator;

    public VoteAgreementModule()
        : this(new VoteAgreementRuleEvaluator(new DefaultVoteAgreementPredicateProvider()))
    {
    }

    internal VoteAgreementModule(VoteAgreementRuleEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    public string Name => "vote";

    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var evt = envelope.Payload!.Unpack<StepRequestEvent>();
        if (!string.Equals(evt.StepType, "vote", StringComparison.OrdinalIgnoreCase))
            return;

        var candidates = ResolveCandidates(evt);
        if (candidates.Candidates.Count == 0)
        {
            await PublishFailureAsync(evt, ctx, "vote agreement requires at least one candidate", ct);
            return;
        }

        if (!VoteAgreementRuleConfigurationParser.TryResolveRule(
                evt.StepParameters?.VoteAgreementRule,
                evt.Parameters,
                out var rule,
                out var error))
        {
            await PublishFailureAsync(evt, ctx, error, ct);
            return;
        }

        if (!_evaluator.TryEvaluate(candidates, rule, out var decision, out error))
        {
            await PublishFailureAsync(evt, ctx, error, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "VoteAgreement: step={StepId} candidates={Count} decision={Decision} branch={BranchKey}",
            evt.StepId,
            candidates.Candidates.Count,
            decision.Kind,
            decision.BranchKey);

        var completed = new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = evt.RunId,
            ExecutionId = evt.ExecutionId,
            Success = true,
            Output = decision.Output,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
            BranchKey = decision.BranchKey,
            VoteAgreementDecision = decision,
        };
        completed.Annotations["vote.agreement.kind"] = decision.Kind.ToString();
        completed.Annotations["vote.agreement.branch_key"] = decision.BranchKey;
        completed.Annotations["vote.agreement.winner_candidate_id"] = decision.WinnerCandidateId;
        completed.Annotations["vote.agreement.reason"] = decision.Reason;
        foreach (var (label, count) in decision.LabelCounts)
            completed.Annotations[$"vote.agreement.label_count.{label}"] = count.ToString(CultureInfo.InvariantCulture);

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
    }

    private static VoteAgreementCandidateSet ResolveCandidates(StepRequestEvent evt)
    {
        if (evt.StepParameters?.VoteAgreementCandidates is { Candidates.Count: > 0 } typedCandidates)
            return typedCandidates.Clone();

        var set = new VoteAgreementCandidateSet();
        var candidates = (evt.Input ?? string.Empty)
            .Split("\n---\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < candidates.Length; i++)
        {
            set.Candidates.Add(new VoteAgreementCandidate
            {
                CandidateId = $"input-{i}",
                Success = true,
                Output = candidates[i],
                WorkerId = $"input-{i}",
            });
        }

        return set;
    }

    private static Task PublishFailureAsync(
        StepRequestEvent evt,
        IWorkflowExecutionContext ctx,
        string error,
        CancellationToken ct) =>
        ctx.PublishAsync(
            new StepCompletedEvent
            {
                StepId = evt.StepId,
                RunId = evt.RunId,
                ExecutionId = evt.ExecutionId,
                Success = false,
                Error = error,
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            },
            TopologyAudience.Self,
            ct);
}
