using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Projection.Audit;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioAuditTranslatorTests
{
    [Theory]
    [MemberData(nameof(StudioSeedEvents))]
    public void StudioSeedTranslators_ShouldProduceCommittedAuditRecord(
        IAuditCommittedEventTranslator translator,
        IMessage evt,
        string operationName,
        string targetKind,
        string targetId)
    {
        var record = translator.Translate(Context(), Any.Pack(evt)).Should().ContainSingle().Subject;

        record.OperationName.Should().Be(operationName);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Target.Kind.Should().Be(targetKind);
        record.Target.Id.Should().Be(targetId);
        record.CommittedFactRef.StateVersion.Should().Be(9);
    }

    [Fact]
    public void StudioTranslator_ShouldReturnZeroRecords_ForWrongEventType()
    {
        new StudioMemberCreatedAuditTranslator()
            .Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }))
            .Should()
            .BeEmpty();
    }

    public static IEnumerable<object[]> StudioSeedEvents()
    {
        yield return
        [
            new StudioMemberCreatedAuditTranslator(),
            new StudioMemberCreatedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "member",
                PublishedServiceId = "svc-alpha",
            },
            "studio.member.created",
            "studio_member",
            "m-alpha",
        ];
        yield return
        [
            new StudioMemberImplementationUpdatedAuditTranslator(),
            new StudioMemberImplementationUpdatedEvent
            {
                ImplementationKind = StudioMemberImplementationKind.Workflow,
            },
            "studio.member.implementation.updated",
            "studio_member",
            "studio-member-actor",
        ];
        yield return
        [
            new StudioMemberReassignedAuditTranslator(),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-alpha",
                ScopeId = "scope-alpha",
                ToTeamId = "team-alpha",
            },
            "studio.member.reassigned",
            "studio_member",
            "m-alpha",
        ];
        yield return
        [
            new StudioTeamCreatedAuditTranslator(),
            new StudioTeamCreatedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "Team",
            },
            "studio.team.created",
            "studio_team",
            "team-alpha",
        ];
        yield return
        [
            new StudioTeamUpdatedAuditTranslator(),
            new StudioTeamUpdatedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
                DisplayName = "Team 2",
            },
            "studio.team.updated",
            "studio_team",
            "team-alpha",
        ];
        yield return
        [
            new StudioTeamArchivedAuditTranslator(),
            new StudioTeamArchivedEvent
            {
                TeamId = "team-alpha",
                ScopeId = "scope-alpha",
            },
            "studio.team.archived",
            "studio_team",
            "team-alpha",
        ];
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope { Id = "cmd-1" },
            new CommittedStateEventPublished(),
            new StateEvent
            {
                AgentId = "studio-member-actor",
                EventId = "event-1",
                Version = 9,
            },
            "studio-member-actor",
            "type.googleapis.com/test",
            DateTimeOffset.Parse("2026-07-03T09:00:00+00:00"),
            "cmd-1",
            "req-1",
            "corr-1");
}
