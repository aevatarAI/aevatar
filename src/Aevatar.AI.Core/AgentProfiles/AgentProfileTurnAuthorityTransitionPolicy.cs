using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Core.AgentProfiles;

public static class AgentProfileTurnAuthorityTransitionPolicy
{
    private const int Sha256Length = 32;

    public static AgentProfileTurnBindingIdentity CreateBindingIdentity(
        AgentProfileExecutionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!AgentProfileExecutionBindingCodec.Verify(binding))
            throw new ArgumentException("A turn binding identity requires a verified execution binding.", nameof(binding));

        return new AgentProfileTurnBindingIdentity
        {
            Source = binding.Source.Clone(),
            ExecutionBindingSha256 = binding.DeterministicBindingSha256,
        };
    }

    public static bool MatchesBinding(
        AgentProfileExecutionBinding? binding,
        AgentProfileTurnAuthorityState? authority) =>
        binding is not null &&
        authority?.BindingIdentity is { } identity &&
        AgentProfileExecutionBindingCodec.Verify(binding) &&
        HasValidBindingIdentity(identity) &&
        identity.Source.Equals(binding.Source) &&
        identity.ExecutionBindingSha256.Equals(binding.DeterministicBindingSha256);

    public static bool MatchesAttemptSource(
        AgentProfileTurnAuthorityState? authority,
        AgentProfileTurnReconciliationKey? attemptSource) =>
        attemptSource is { Attempt: > 0 } &&
        authority?.ReconciliationKey is { } authoritySource &&
        authoritySource.Attempt == attemptSource.Attempt &&
        string.Equals(
            authoritySource.SessionId,
            attemptSource.SessionId,
            StringComparison.Ordinal);

    public static bool TryApply(
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityCommittedEvent authorityEvent,
        Func<AgentProfileTurnAuthorityState, AgentProfileTurnAuthorityState, bool> canReplaceInitial,
        out AgentProfileTurnAuthorityState accepted)
    {
        ArgumentNullException.ThrowIfNull(authorityEvent);
        ArgumentNullException.ThrowIfNull(canReplaceInitial);

        accepted = null!;
        if (authorityEvent.Authority?.ReconciliationKey is null)
            return false;

        var incoming = Canonicalize(authorityEvent.Authority);
        if (!IsValid(incoming))
        {
            return false;
        }

        switch (authorityEvent.CommitKind)
        {
            case AgentProfileTurnAuthorityCommitKind.Initial:
                if (incoming.ReconciliationKey.Attempt != 1 ||
                    !CanApplyInitial(active, incoming, canReplaceInitial))
                {
                    return false;
                }
                accepted = incoming;
                break;
            case AgentProfileTurnAuthorityCommitKind.RetryStarted:
                if (!CanApplyRetry(active, incoming))
                    return false;
                accepted = incoming;
                break;
            case AgentProfileTurnAuthorityCommitKind.Reconcile:
                if (!CanApplyReconcile(active, incoming))
                    return false;
                accepted = MergeReconciled(active!, incoming);
                break;
            default:
                return false;
        }

        return true;
    }

    public static bool HasSameReconciliationKey(
        AgentProfileTurnAuthorityState left,
        AgentProfileTurnAuthorityState right) =>
        left.ReconciliationKey is not null &&
        right.ReconciliationKey is not null &&
        left.ReconciliationKey.Attempt == right.ReconciliationKey.Attempt &&
        string.Equals(
            left.ReconciliationKey.SessionId,
            right.ReconciliationKey.SessionId,
            StringComparison.Ordinal);

    public static bool IsValid(AgentProfileTurnAuthorityState authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return authority.ReconciliationKey is not null &&
               !string.IsNullOrWhiteSpace(authority.ReconciliationKey.SessionId) &&
               authority.ReconciliationKey.Attempt > 0 &&
               AuthorityRank(authority.AuthorityKind) >= 0 &&
               (IsLegacyRestrictedEmpty(authority) ||
                HasValidBindingIdentity(authority.BindingIdentity)) &&
               HasConsistentCandidateBindingIdentity(authority) &&
               HasConsistentAuthorityKindAndCeiling(authority) &&
               authority.AuthorityKind switch
               {
                   AgentProfileTurnAuthorityKind.Selected =>
                       authority.CandidateRoute is not null &&
                       authority.SelectedExactSkillRef is not null,
                   AgentProfileTurnAuthorityKind.Recovery => authority.SelectedExactSkillRef is null,
                   AgentProfileTurnAuthorityKind.RestrictedEmpty =>
                       authority.CandidateRoute is null &&
                       authority.SelectedExactSkillRef is null,
                   _ => false,
               };
    }

    public static bool IsLegacyRestrictedEmpty(AgentProfileTurnAuthorityState authority) =>
        authority.AuthorityKind == AgentProfileTurnAuthorityKind.RestrictedEmpty &&
        authority.BindingIdentity is null &&
        authority.CandidateRoute is null &&
        authority.SelectedExactSkillRef is null &&
        authority.AuthorityCeilingToolNames.Count == 0 &&
        authority.DegradationReasons.Count == 1 &&
        authority.DegradationReasons[0] == AgentProfileTurnDegradationReason.LegacyAuthorityMissing;

    private static bool CanApplyInitial(
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityState incoming,
        Func<AgentProfileTurnAuthorityState, AgentProfileTurnAuthorityState, bool> canReplaceInitial)
    {
        if (active is null)
            return true;

        if (HasSameReconciliationKey(active, incoming))
            return Canonicalize(active).Equals(incoming);

        if (!HasSameBindingIdentity(active, incoming) &&
            !IsLegacyRestrictedEmpty(active) &&
            !IsLegacyRestrictedEmpty(incoming))
        {
            return false;
        }

        return canReplaceInitial(active, incoming);
    }

    private static bool CanApplyRetry(
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityState incoming)
    {
        if (active?.ReconciliationKey is null ||
            !string.Equals(
                active.ReconciliationKey.SessionId,
                incoming.ReconciliationKey.SessionId,
                StringComparison.Ordinal) ||
            incoming.ReconciliationKey.Attempt != active.ReconciliationKey.Attempt + 1)
        {
            return false;
        }

        var expected = Canonicalize(active);
        expected.ReconciliationKey.Attempt = incoming.ReconciliationKey.Attempt;
        return expected.Equals(incoming);
    }

    private static bool CanApplyReconcile(
        AgentProfileTurnAuthorityState? active,
        AgentProfileTurnAuthorityState incoming)
    {
        if (active?.ReconciliationKey is null ||
            !HasSameReconciliationKey(active, incoming) ||
            !HasSameBindingIdentity(active, incoming))
            return false;

        var activeRank = AuthorityRank(active.AuthorityKind);
        var incomingRank = AuthorityRank(incoming.AuthorityKind);
        if (incomingRank > activeRank ||
            !HasValidReconciledIdentityTransition(active, incoming, activeRank, incomingRank))
        {
            return false;
        }

        var activeNames = active.AuthorityCeilingToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return incoming.AuthorityCeilingToolNames.All(activeNames.Contains);
    }

    private static bool HasValidReconciledIdentityTransition(
        AgentProfileTurnAuthorityState active,
        AgentProfileTurnAuthorityState incoming,
        int activeRank,
        int incomingRank)
    {
        if (incomingRank < activeRank &&
            active.CandidateRoute is not null &&
            active.SelectedExactSkillRef is not null)
        {
            return incoming.CandidateRoute is null && incoming.SelectedExactSkillRef is null;
        }

        return Equals(active.CandidateRoute, incoming.CandidateRoute) &&
               Equals(active.SelectedExactSkillRef, incoming.SelectedExactSkillRef);
    }

    private static AgentProfileTurnAuthorityState MergeReconciled(
        AgentProfileTurnAuthorityState active,
        AgentProfileTurnAuthorityState incoming)
    {
        var accepted = incoming.Clone();
        accepted.DegradationReasons.Clear();
        accepted.DegradationReasons.Add(
            active.DegradationReasons
                .Concat(incoming.DegradationReasons)
                .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static reason => (int)reason));
        return accepted;
    }

    private static AgentProfileTurnAuthorityState Canonicalize(
        AgentProfileTurnAuthorityState authority)
    {
        var canonical = authority.Clone();
        canonical.AuthorityCeilingToolNames.Clear();
        canonical.AuthorityCeilingToolNames.Add(
            authority.AuthorityCeilingToolNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.Ordinal));
        canonical.DegradationReasons.Clear();
        canonical.DegradationReasons.Add(
            authority.DegradationReasons
                .Where(static reason => reason != AgentProfileTurnDegradationReason.Unspecified)
                .Distinct()
                .OrderBy(static reason => (int)reason));
        return canonical;
    }

    private static bool HasConsistentAuthorityKindAndCeiling(
        AgentProfileTurnAuthorityState authority) => authority.AuthorityKind switch
        {
            AgentProfileTurnAuthorityKind.RestrictedEmpty =>
                authority.AuthorityCeilingToolNames.Count == 0,
            AgentProfileTurnAuthorityKind.Recovery =>
                authority.AuthorityCeilingToolNames.Count > 0,
            AgentProfileTurnAuthorityKind.Selected => true,
            _ => false,
        };

    private static bool HasValidBindingIdentity(AgentProfileTurnBindingIdentity? identity) =>
        identity?.Source is { } source &&
        !string.IsNullOrWhiteSpace(source.ProfileId) &&
        source.StateVersion > 0 &&
        source.PublishedRevision > 0 &&
        source.PublishedSnapshotSha256.Length == Sha256Length &&
        identity.ExecutionBindingSha256.Length == Sha256Length;

    private static bool HasConsistentCandidateBindingIdentity(
        AgentProfileTurnAuthorityState authority)
    {
        var candidate = authority.CandidateRoute;
        if (candidate is null)
            return true;

        var bindingIdentity = authority.BindingIdentity;
        return bindingIdentity?.Source is { } source &&
               !string.IsNullOrWhiteSpace(candidate.IntentId) &&
               string.Equals(candidate.SourceProfileId, source.ProfileId, StringComparison.Ordinal) &&
               candidate.SourceStateVersion == source.StateVersion &&
               candidate.PublishedRevision == source.PublishedRevision &&
               candidate.PublishedSnapshotSha256.Equals(source.PublishedSnapshotSha256) &&
               candidate.ExecutionBindingSha256.Equals(bindingIdentity.ExecutionBindingSha256);
    }

    private static bool HasSameBindingIdentity(
        AgentProfileTurnAuthorityState left,
        AgentProfileTurnAuthorityState right) =>
        Equals(left.BindingIdentity, right.BindingIdentity);

    private static int AuthorityRank(AgentProfileTurnAuthorityKind kind) => kind switch
    {
        AgentProfileTurnAuthorityKind.RestrictedEmpty => 1,
        AgentProfileTurnAuthorityKind.Recovery => 2,
        AgentProfileTurnAuthorityKind.Selected => 3,
        _ => -1,
    };
}
