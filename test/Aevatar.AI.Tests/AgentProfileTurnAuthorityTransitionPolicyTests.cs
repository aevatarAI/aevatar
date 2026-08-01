using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnAuthorityTransitionPolicyTests
{
    [Theory]
    [InlineData(AgentProfileTurnAuthorityKind.Recovery)]
    [InlineData(AgentProfileTurnAuthorityKind.RestrictedEmpty)]
    public void RetryStarted_ShouldAdvanceCandidateLessAuthorityExactlyOneAttempt(
        AgentProfileTurnAuthorityKind authorityKind)
    {
        var active = CreateAuthority(authorityKind);
        var retry = active.Clone();
        retry.ReconciliationKey.Attempt = 2;

        var applied = AgentProfileTurnAuthorityTransitionPolicy.TryApply(
            active,
            new AgentProfileTurnAuthorityCommittedEvent
            {
                CommitKind = AgentProfileTurnAuthorityCommitKind.RetryStarted,
                Authority = retry,
            },
            static (_, _) => false,
            out var accepted);

        applied.Should().BeTrue();
        accepted.Should().BeEquivalentTo(retry);
    }

    [Fact]
    public void IsValid_ShouldRequireBindingIdentityExceptForLegacyRestrictedEmpty()
    {
        var recovery = CreateAuthority(AgentProfileTurnAuthorityKind.Recovery);
        recovery.BindingIdentity = null;
        var legacy = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = "turn-legacy",
                Attempt = 1,
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty,
            DegradationReasons = { AgentProfileTurnDegradationReason.LegacyAuthorityMissing },
        };

        AgentProfileTurnAuthorityTransitionPolicy.IsValid(recovery).Should().BeFalse();
        AgentProfileTurnAuthorityTransitionPolicy.IsValid(legacy).Should().BeTrue();
    }

    [Fact]
    public void RetryAndReconcile_ShouldRejectChangedBindingIdentity()
    {
        var active = CreateAuthority(AgentProfileTurnAuthorityKind.Selected);
        var changedRetry = active.Clone();
        changedRetry.ReconciliationKey.Attempt = 2;
        changedRetry.BindingIdentity.ExecutionBindingSha256 = Digest(0x72);
        var changedReconcile = active.Clone();
        changedReconcile.BindingIdentity.Source.ProfileId = "profile-substituted";

        AgentProfileTurnAuthorityTransitionPolicy.TryApply(
                active,
                new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.RetryStarted,
                    Authority = changedRetry,
                },
                static (_, _) => false,
                out _)
            .Should().BeFalse();
        AgentProfileTurnAuthorityTransitionPolicy.TryApply(
                active,
                new AgentProfileTurnAuthorityCommittedEvent
                {
                    CommitKind = AgentProfileTurnAuthorityCommitKind.Reconcile,
                    Authority = changedReconcile,
                },
                static (_, _) => false,
                out _)
            .Should().BeFalse();
    }

    private static AgentProfileTurnAuthorityState CreateAuthority(
        AgentProfileTurnAuthorityKind authorityKind)
    {
        var authority = new AgentProfileTurnAuthorityState
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = "turn-policy-alpha",
                Attempt = 1,
            },
            BindingIdentity = new AgentProfileTurnBindingIdentity
            {
                Source = new AgentProfileExecutionSourceProvenance
                {
                    ProfileId = "profile-policy-alpha",
                    StateVersion = 17,
                    PublishedRevision = 5,
                    PublishedSnapshotSha256 = Digest(0x21),
                },
                ExecutionBindingSha256 = Digest(0x61),
            },
            AuthorityKind = authorityKind,
        };
        switch (authorityKind)
        {
            case AgentProfileTurnAuthorityKind.Selected:
                authority.CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
                {
                    SourceProfileId = authority.BindingIdentity.Source.ProfileId,
                    SourceStateVersion = authority.BindingIdentity.Source.StateVersion,
                    PublishedRevision = authority.BindingIdentity.Source.PublishedRevision,
                    PublishedSnapshotSha256 = authority.BindingIdentity.Source.PublishedSnapshotSha256,
                    ExecutionBindingSha256 = authority.BindingIdentity.ExecutionBindingSha256,
                    IntentId = "intent-policy-alpha",
                };
                authority.SelectedExactSkillRef = new ExactRemoteSkillRef
                {
                    Guid = "11111111-1111-1111-1111-111111111111",
                    LiteralVersion = "1.2",
                };
                authority.AuthorityCeilingToolNames.Add(["recovery", "task"]);
                break;
            case AgentProfileTurnAuthorityKind.Recovery:
                authority.AuthorityCeilingToolNames.Add("recovery");
                break;
            case AgentProfileTurnAuthorityKind.RestrictedEmpty:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(authorityKind), authorityKind, null);
        }

        return authority;
    }

    private static ByteString Digest(byte value) =>
        ByteString.CopyFrom(Enumerable.Repeat(value, 32).ToArray());
}
