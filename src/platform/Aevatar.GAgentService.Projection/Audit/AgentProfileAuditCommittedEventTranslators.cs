using System.Globalization;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Audit;

public sealed class AgentProfileStateChangedAuditTranslator : IAuditCommittedEventTranslator
{
    public string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileStateChangedEvent.Descriptor);

    public IReadOnlyList<AuditRecord> Translate(
        CommittedAuditTranslationContext context,
        Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(AgentProfileStateChangedEvent.Descriptor))
            return [];

        var evt = eventPayload.Unpack<AgentProfileStateChangedEvent>();
        var operationName = evt.ChangeKind switch
        {
            "initialized" => "agent_profile.created",
            "draft-updated" => "agent_profile.draft.updated",
            "published" => "agent_profile.published",
            _ => string.Empty,
        };
        if (operationName.Length == 0 ||
            evt.State?.Identity is not { } identity ||
            string.IsNullOrWhiteSpace(identity.ProfileId))
        {
            return [];
        }

        var mutation = evt.State.LastMutation;
        var operation = mutation?.Operation;
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner_kind"] = AgentProfileAuditFields.OwnerKind(identity.Owner),
            ["profile_slug"] = identity.ProfileSlug ?? string.Empty,
            ["mutation_code"] = mutation?.Code ?? string.Empty,
            ["mutation_status"] = AgentProfileAuditFields.MutationStatus(mutation?.Status ??
                AgentProfileMutationStatus.Unspecified),
            ["authority_state_version"] = AgentProfileAuditFields.Number(
                mutation?.AuthorityStateVersion ?? context.StateEvent.Version),
            ["draft_revision"] = AgentProfileAuditFields.Number(evt.State.DraftRevision),
            ["published_revision"] = AgentProfileAuditFields.Number(evt.State.PublishedRevision),
            ["operation_id"] = operation?.OperationId ?? string.Empty,
        };

        var seed = new CommittedAuditSeed(
            operationName,
            "agent_profile",
            identity.ProfileId,
            ScopeId: AgentProfileAuditFields.ScopeId(identity.Owner),
            SensitivityLevel: AuditSensitivityLevel.Confidential,
            CommandId: operation?.CommandId ?? string.Empty,
            CorrelationId: operation?.CorrelationId ?? string.Empty,
            ResultSummary: AgentProfileAuditFields.ProfileResultSummary(operationName, identity.ProfileId),
            Annotations: annotations,
            OmittedFields: ["source_event.payload"]);

        return [CommittedAuditRecordFactory.CreateSystemRecord(context, seed)];
    }
}

public sealed class AgentProfileNamespaceStateChangedAuditTranslator : IAuditCommittedEventTranslator
{
    public string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(AgentProfileNamespaceStateChangedEvent.Descriptor);

    public IReadOnlyList<AuditRecord> Translate(
        CommittedAuditTranslationContext context,
        Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(AgentProfileNamespaceStateChangedEvent.Descriptor))
            return [];

        var evt = eventPayload.Unpack<AgentProfileNamespaceStateChangedEvent>();
        if (!string.Equals(evt.ChangeKind, "default-binding-set", StringComparison.Ordinal) ||
            evt.State is null)
        {
            return [];
        }

        // Set replaces any existing entry and appends the changed binding last.
        var binding = evt.State.DefaultBindings.LastOrDefault();
        if (binding?.Target is null || string.IsNullOrWhiteSpace(binding.Target.ProfileId))
            return [];

        return [CommittedAuditRecordFactory.CreateSystemRecord(
            context,
            BuildSeed(context, evt.State, binding))];
    }

    private static CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        AgentProfileNamespaceState state,
        AgentProfileDefaultBinding binding)
    {
        var admissionKind = binding.AdmissionCase switch
        {
            AgentProfileDefaultBinding.AdmissionOneofCase.Scope => "scope",
            AgentProfileDefaultBinding.AdmissionOneofCase.System => "system",
            _ => "unspecified",
        };
        var isSystemRollout =
            state.Owner?.OwnerCase == AgentProfileOwner.OwnerOneofCase.System &&
            binding.AdmissionCase == AgentProfileDefaultBinding.AdmissionOneofCase.System;
        var operationName = isSystemRollout
            ? "agent_profile.system_rollout.updated"
            : "agent_profile.default_binding.set";
        var mutation = state.LastMutation;
        var operation = mutation?.Operation;
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["owner_kind"] = AgentProfileAuditFields.OwnerKind(state.Owner),
            ["agent_kind"] = binding.AgentKind ?? string.Empty,
            ["target_profile_id"] = binding.Target.ProfileId,
            ["target_owner_kind"] = AgentProfileAuditFields.OwnerKind(binding.Target.Owner),
            ["target_published_revision"] = AgentProfileAuditFields.Number(binding.Target.PublishedRevision),
            ["admission_kind"] = admissionKind,
            ["mutation_code"] = mutation?.Code ?? string.Empty,
            ["mutation_status"] = AgentProfileAuditFields.MutationStatus(mutation?.Status ??
                AgentProfileMutationStatus.Unspecified),
            ["authority_state_version"] = AgentProfileAuditFields.Number(
                mutation?.AuthorityStateVersion ?? context.StateEvent.Version),
            ["operation_id"] = operation?.OperationId ?? string.Empty,
        };
        if (binding.AdmissionCase == AgentProfileDefaultBinding.AdmissionOneofCase.System)
        {
            annotations["enabled"] = binding.System.Enabled ? "true" : "false";
            annotations["cohort_basis_points"] = AgentProfileAuditFields.Number(
                binding.System.CohortBasisPoints);
            if (binding.System.PreviousReviewedTarget is { } previous)
            {
                annotations["previous_reviewed_profile_id"] = previous.ProfileId;
                annotations["previous_reviewed_published_revision"] =
                    AgentProfileAuditFields.Number(previous.PublishedRevision);
            }
        }

        return new CommittedAuditSeed(
            operationName,
            "agent_profile_default_binding",
            binding.Target.ProfileId,
            ScopeId: AgentProfileAuditFields.ScopeId(state.Owner),
            SensitivityLevel: AuditSensitivityLevel.Confidential,
            CommandId: operation?.CommandId ?? string.Empty,
            CorrelationId: operation?.CorrelationId ?? string.Empty,
            ResultSummary: isSystemRollout
                ? $"System Agent Profile rollout updated for {binding.AgentKind}."
                : $"Default Agent Profile binding set for {binding.AgentKind}.",
            Annotations: annotations,
            OmittedFields: ["source_event.payload"]);
    }
}

internal static class AgentProfileAuditFields
{
    public static string OwnerKind(AgentProfileOwner? owner) => owner?.OwnerCase switch
    {
        AgentProfileOwner.OwnerOneofCase.Scope => "scope",
        AgentProfileOwner.OwnerOneofCase.System => "system",
        _ => "unspecified",
    };

    public static string ScopeId(AgentProfileOwner? owner) =>
        owner?.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope
            ? owner.Scope.ScopeId ?? string.Empty
            : string.Empty;

    public static string MutationStatus(AgentProfileMutationStatus status) => status switch
    {
        AgentProfileMutationStatus.Succeeded => "succeeded",
        AgentProfileMutationStatus.NoChange => "no_change",
        AgentProfileMutationStatus.Rejected => "rejected",
        _ => "unspecified",
    };

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string ProfileResultSummary(string operationName, string profileId) => operationName switch
    {
        "agent_profile.created" => $"Agent Profile {profileId} created.",
        "agent_profile.draft.updated" => $"Agent Profile {profileId} draft updated.",
        "agent_profile.published" => $"Agent Profile {profileId} published.",
        _ => string.Empty,
    };
}
