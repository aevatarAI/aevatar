using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Audit;

public sealed class StudioMemberCreatedAuditTranslator : StudioAuditTranslatorBase<StudioMemberCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberCreatedEvent evt) =>
        StudioSeed("studio.member.created", "studio_member", evt.MemberId, evt.ScopeId, "Studio member created.");
}

public sealed class StudioMemberImplementationUpdatedAuditTranslator
    : StudioAuditTranslatorBase<StudioMemberImplementationUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberImplementationUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberImplementationUpdatedEvent evt) =>
        StudioSeed(
            "studio.member.implementation.updated",
            "studio_member",
            context.OriginActorId,
            "",
            "Studio member implementation updated.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["implementation_kind"] = evt.ImplementationKind.ToString(),
            });
}

public sealed class StudioMemberReassignedAuditTranslator : StudioAuditTranslatorBase<StudioMemberReassignedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberReassignedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberReassignedEvent evt) =>
        StudioSeed(
            "studio.member.reassigned",
            "studio_member",
            evt.MemberId,
            evt.ScopeId,
            "Studio member team assignment changed.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from_team_id"] = evt.HasFromTeamId ? evt.FromTeamId : string.Empty,
                ["to_team_id"] = evt.HasToTeamId ? evt.ToTeamId : string.Empty,
            });
}

public sealed class StudioTeamCreatedAuditTranslator : StudioAuditTranslatorBase<StudioTeamCreatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioTeamCreatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioTeamCreatedEvent evt) =>
        StudioSeed("studio.team.created", "studio_team", evt.TeamId, evt.ScopeId, "Studio team created.");
}

public sealed class StudioTeamUpdatedAuditTranslator : StudioAuditTranslatorBase<StudioTeamUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioTeamUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioTeamUpdatedEvent evt) =>
        StudioSeed("studio.team.updated", "studio_team", evt.TeamId, evt.ScopeId, "Studio team updated.");
}

public sealed class StudioTeamArchivedAuditTranslator : StudioAuditTranslatorBase<StudioTeamArchivedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioTeamArchivedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioTeamArchivedEvent evt) =>
        StudioSeed(
            "studio.team.archived",
            "studio_team",
            evt.TeamId,
            evt.ScopeId,
            "Studio team archived.",
            AuditSensitivityLevel.Restricted,
            true);
}

public abstract class StudioAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);

    protected static CommittedAuditSeed StudioSeed(
        string operationName,
        string targetKind,
        string targetId,
        string scopeId,
        string resultSummary,
        AuditSensitivityLevel sensitivityLevel = AuditSensitivityLevel.Confidential,
        bool isDestructive = false,
        IReadOnlyDictionary<string, string>? Annotations = null) =>
        new(
            operationName,
            targetKind,
            targetId,
            scopeId,
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: Annotations);
}
