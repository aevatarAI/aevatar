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

public sealed class StudioMemberDeletedAuditTranslator : StudioAuditTranslatorBase<StudioMemberDeletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberDeletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberDeletedEvent evt) =>
        StudioSeed(
            "studio.member.deleted",
            "studio_member",
            evt.MemberId,
            evt.ScopeId,
            "Studio member deleted.",
            AuditSensitivityLevel.Restricted,
            true,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previous_team_id"] = evt.HasPreviousTeamId ? evt.PreviousTeamId : string.Empty,
                ["published_service_id"] = evt.PublishedServiceId ?? string.Empty,
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

public sealed class StudioMemberRenamedAuditTranslator : StudioAuditTranslatorBase<StudioMemberRenamedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberRenamedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberRenamedEvent evt) =>
        StudioSeed(
            "studio.member.renamed",
            "studio_member",
            context.OriginActorId,
            "",
            "Studio member display name updated.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["display_name"] = evt.DisplayName,
            });
}

public sealed class StudioMemberBindingCompletedAuditTranslator
    : StudioAuditTranslatorBase<StudioMemberBindingCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberBindingCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberBindingCompletedEvent evt) =>
        StudioSeed(
            "studio.member.binding.completed",
            "studio_member",
            context.OriginActorId,
            "",
            "Studio member binding completed.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["binding_run_id"] = evt.BindingRunId,
                ["published_service_id"] = evt.PublishedServiceId,
                ["revision_id"] = evt.RevisionId,
                ["implementation_kind"] = evt.ImplementationKind.ToString(),
            });
}

public sealed class StudioMemberBindingFailedAuditTranslator
    : StudioAuditTranslatorBase<StudioMemberBindingFailedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberBindingFailedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberBindingFailedEvent evt) =>
        StudioSeed(
            "studio.member.binding.failed",
            "studio_member",
            context.OriginActorId,
            "",
            "Studio member binding failed.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["binding_run_id"] = evt.BindingRunId,
            },
            TerminalOutcome: AuditTerminalOutcome.Failed,
            Failure: BindingFailure(
                "studio_member_binding_failed",
                AuditFailureCategory.Execution,
                "Studio member binding failed."));
}

public sealed class StudioMemberBindingRejectedAuditTranslator
    : StudioAuditTranslatorBase<StudioMemberBindingRejectedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioMemberBindingRejectedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioMemberBindingRejectedEvent evt) =>
        StudioSeed(
            "studio.member.binding.rejected",
            "studio_member",
            evt.MemberId,
            evt.ScopeId,
            "Studio member binding rejected.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["binding_run_id"] = evt.BindingRunId,
            },
            TerminalOutcome: AuditTerminalOutcome.Failed,
            Failure: BindingFailure(
                "studio_member_binding_rejected",
                AuditFailureCategory.Validation,
                "Studio member binding was rejected."));
}

public sealed class StudioTeamEntryMemberChangedAuditTranslator
    : StudioAuditTranslatorBase<StudioTeamEntryMemberChangedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(StudioTeamEntryMemberChangedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        StudioTeamEntryMemberChangedEvent evt) =>
        StudioSeed(
            "studio.team.entry-member.changed",
            "studio_team",
            evt.TeamId,
            evt.ScopeId,
            "Studio team entry member changed.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entry_member_id"] = evt.HasEntryMemberId ? evt.EntryMemberId : string.Empty,
            });
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
        IReadOnlyDictionary<string, string>? Annotations = null,
        AuditLifecyclePhase LifecyclePhase = AuditLifecyclePhase.Terminal,
        AuditTerminalOutcome TerminalOutcome = AuditTerminalOutcome.Succeeded,
        AuditFailure? Failure = null) =>
        new(
            operationName,
            targetKind,
            targetId,
            scopeId,
            sensitivityLevel,
            isDestructive,
            ResultSummary: resultSummary,
            Annotations: Annotations,
            LifecyclePhase: LifecyclePhase,
            TerminalOutcome: TerminalOutcome,
            Failure: Failure);

    protected static AuditFailure BindingFailure(
        string? code,
        AuditFailureCategory category,
        string sanitizedMessage) =>
        new()
        {
            Code = code,
            Category = category,
            Retryability = AuditRetryability.Unknown,
            FailedPhase = AuditLifecyclePhase.Running,
            SanitizedMessage = sanitizedMessage,
        };
}
