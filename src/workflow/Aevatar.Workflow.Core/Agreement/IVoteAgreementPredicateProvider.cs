namespace Aevatar.Workflow.Core.Agreement;

internal interface IVoteAgreementPredicateProvider
{
    bool TryEvaluate(
        string predicateId,
        VoteAgreementCandidateSet candidates,
        VoteAgreementRule rule,
        out VoteAgreementPredicateResult result);
}

internal sealed record VoteAgreementPredicateResult(
    bool IsAgreed,
    string? WinnerCandidateId,
    string Output,
    string Reason);
