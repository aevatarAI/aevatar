using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnAuthorityContractTests
{
    [Fact]
    public void AuthorityStateAndEvent_ShouldRoundTripTypedContract()
    {
        var authority = CreateAuthority();
        var committed = new AgentProfileTurnAuthorityCommittedEvent
        {
            CommitKind = AgentProfileTurnAuthorityCommitKind.Reconcile,
            Authority = authority,
        };

        var stateRoundTrip = RoundTrip(
            new RoleGAgentState { AgentProfileTurnAuthority = authority },
            RoleGAgentState.Parser);
        var eventRoundTrip = RoundTrip(committed, AgentProfileTurnAuthorityCommittedEvent.Parser);

        stateRoundTrip.AgentProfileTurnAuthority.Should().BeEquivalentTo(authority);
        eventRoundTrip.Should().BeEquivalentTo(committed);
    }

    [Fact]
    public void AuthorityEnums_ShouldKeepStableNumericValues()
    {
        new[]
        {
            (int)AgentProfileTurnAuthorityKind.RestrictedEmpty,
            (int)AgentProfileTurnAuthorityKind.Recovery,
            (int)AgentProfileTurnAuthorityKind.Selected,
        }.Should().Equal(1, 2, 3);
        new[]
        {
            (int)AgentProfileTurnAuthorityCommitKind.Initial,
            (int)AgentProfileTurnAuthorityCommitKind.RetryStarted,
            (int)AgentProfileTurnAuthorityCommitKind.Reconcile,
        }.Should().Equal(1, 2, 3);
        ((int)AgentProfileTurnDegradationReason.MaterializationFailed).Should().Be(15);
        AiMessagesReflection.Descriptor.EnumTypes
            .Single(enumType => enumType.Name == nameof(AgentProfileTurnDegradationReason))
            .Values
            .Select(value => value.Number)
            .Should()
            .Equal(Enumerable.Range(0, 16));
    }

    [Fact]
    public void AuthorityStateAndEvent_ShouldExcludeSensitiveFields()
    {
        var forbiddenFragments = new[]
        {
            "body",
            "prompt",
            "tool_object",
            "token",
            "credential",
            "header",
            "model_argument",
            "diagnostic",
            "metadata",
            "adapter",
            "runtime_instance",
        };

        new[] { AgentProfileTurnAuthorityState.Descriptor, AgentProfileTurnAuthorityCommittedEvent.Descriptor }
            .SelectMany(descriptor => descriptor.Fields.InDeclarationOrder())
            .Select(field => field.Name)
            .Should()
            .NotContain(name => forbiddenFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AuthorityMessages_ShouldKeepStableWireFieldNumbers()
    {
        RoleGAgentState.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((13, "agent_profile_turn_authority"));
        AgentProfileTurnAuthorityCommittedEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Equal(
                (1, "commit_kind"),
                (2, "authority"));
        AgentProfileTurnAuthorityState.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Equal(
                (1, "reconciliation_key"),
                (2, "candidate_route"),
                (3, "selected_exact_skill_ref"),
                (4, "authority_kind"),
                (5, "degradation_reasons"),
                (6, "authority_ceiling_tool_names"));
    }

    private static AgentProfileTurnAuthorityState CreateAuthority() =>
        new()
        {
            ReconciliationKey = new AgentProfileTurnReconciliationKey
            {
                SessionId = "session-authority",
                Attempt = 2,
            },
            CandidateRoute = new AgentProfileTurnCandidateRouteIdentity
            {
                ProfileId = "profile-a",
                ProfileVersion = "v3",
                PolicyRevision = "policy-7",
                IntentId = "intent-a",
            },
            SelectedExactSkillRef = new ExactRemoteSkillRef
            {
                Guid = "skill-guid",
                LiteralVersion = "1.2.3",
            },
            AuthorityKind = AgentProfileTurnAuthorityKind.Selected,
            DegradationReasons =
            {
                AgentProfileTurnDegradationReason.ToolNameCollision,
                AgentProfileTurnDegradationReason.ExactSkillFetchFailed,
            },
            AuthorityCeilingToolNames = { "search", "task" },
        };

    private static T RoundTrip<T>(T message, MessageParser<T> parser)
        where T : class, IMessage<T>, new()
    {
        var bytes = message.ToByteArray();
        var parsed = parser.ParseFrom(bytes);
        parsed.Should().Be(message);

        var merged = new T();
        merged.MergeFrom(message);
        merged.Should().Be(message);

        return parsed;
    }
}
